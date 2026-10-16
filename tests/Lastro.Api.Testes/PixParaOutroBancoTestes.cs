using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lastro.Api.Spi;
using Lastro.Dominio;

namespace Lastro.Api.Testes;

/// <summary>
/// Pix para outros bancos, pelo Banco Central simulado: "maria" é liquidado, "joao.recusa"
/// é recusado pelo banco dela, e "rita.instavel" fica sem resposta duas vezes antes de liquidar.
/// </summary>
public class PixParaOutroBancoTestes(BancoDeTeste banco)
{
    private const string Maria = "maria@banco-exemplo.example";
    private const string JoaoRecusa = "joao.recusa@banco-teste.example";
    private const string RitaInstavel = "rita.instavel@banco-teste.example";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Cliente> ClienteCom(decimal saldo)
    {
        var ana = await banco.NovoCliente("Ana");
        await banco.Operador.Depositar(ana.Id, saldo);
        return ana;
    }

    /// <summary>A liquidação acontece em segundo plano: espera a saga chegar a um fim.</summary>
    private static async Task<JsonElement> EsperarConclusao(Cliente dono, string endToEndId)
    {
        for (var i = 0; i < 150; i++)
        {
            var pix = await dono.Http.GetFromJsonAsync<JsonElement>($"/pix/{endToEndId}", Ct);
            if (pix.GetProperty("situacao").GetString() != "EmLiquidacao")
            {
                return pix;
            }
            await Task.Delay(100, Ct);
        }
        throw new TimeoutException($"O Pix {endToEndId} não saiu de EmLiquidacao.");
    }

    private static async Task<string> EndToEndIdDe(HttpResponseMessage resposta) =>
        (await resposta.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("paraOutroBanco")
            .GetProperty("endToEndId").GetString()!;

    [Fact]
    public async Task Pix_para_outro_banco_sai_na_hora_e_e_liquidado_em_seguida()
    {
        var ana = await ClienteCom(100m);

        var resposta = await ana.PixPorChave(Maria, 40m);

        Assert.Equal(HttpStatusCode.Accepted, resposta.StatusCode);
        Assert.Equal(60m, await ana.Saldo()); // o dinheiro já saiu da conta
        var pix = await EsperarConclusao(ana, await EndToEndIdDe(resposta));
        Assert.Equal("Liquidado", pix.GetProperty("situacao").GetString());
        Assert.Equal(60m, await ana.Saldo());
        await LivroFecha();
    }

    [Fact]
    public async Task Recusado_pelo_outro_banco_o_dinheiro_volta()
    {
        var ana = await ClienteCom(100m);

        var pix = await EsperarConclusao(ana, await EndToEndIdDe(await ana.PixPorChave(JoaoRecusa, 40m)));

        Assert.Equal("Devolvido", pix.GetProperty("situacao").GetString());
        Assert.Equal("Recusado pelo banco de destino", pix.GetProperty("motivo").GetString());
        Assert.Equal(100m, await ana.Saldo());
        var tipos = (await ana.Extrato()).GetProperty("itens").EnumerateArray()
            .Select(i => (i.GetProperty("tipo").GetString(), i.GetProperty("valor").GetDecimal()));
        Assert.Equal([("Devolucao", 40m), ("Pix", -40m), ("Deposito", 100m)], tipos);
        await LivroFecha();
    }

    [Fact]
    public async Task Sem_resposta_do_outro_banco_consulta_e_reenvia_sem_pagar_duas_vezes()
    {
        var ana = await ClienteCom(100m);

        var pix = await EsperarConclusao(ana, await EndToEndIdDe(await ana.PixPorChave(RitaInstavel, 25m)));

        Assert.Equal("Liquidado", pix.GetProperty("situacao").GetString());
        Assert.Equal(3, pix.GetProperty("tentativas").GetInt32());
        Assert.Equal(75m, await ana.Saldo());
        await LivroFecha();
    }

    [Fact]
    public async Task Chave_de_outro_banco_aparece_com_a_instituicao_antes_de_enviar()
    {
        var ana = await banco.NovoCliente();
        var dono = await ana.Http.GetFromJsonAsync<JsonElement>($"/pix/chaves/{Maria}", Ct);
        Assert.Equal("Banco Exemplo", dono.GetProperty("instituicao").GetString());
        Assert.Equal("Maria C. L.", dono.GetProperty("nome").GetString());
    }

    [Fact]
    public async Task Repetir_a_chave_de_idempotencia_nao_manda_dois_pix_para_fora()
    {
        var ana = await ClienteCom(100m);
        var chave = Api.NovaChave();

        var (a, b) = (await ana.PixPorChave(Maria, 10m, chave), await ana.PixPorChave(Maria, 10m, chave));

        Assert.Equal(await EndToEndIdDe(a), await EndToEndIdDe(b));
        Assert.Equal(90m, await ana.Saldo());
    }

    [Fact]
    public async Task Ninguem_acompanha_o_pix_de_outra_pessoa()
    {
        var (ana, bia) = (await ClienteCom(100m), await banco.NovoCliente());
        var endToEndId = await EndToEndIdDe(await ana.PixPorChave(Maria, 10m));
        Assert.Equal(HttpStatusCode.NotFound, (await bia.Http.GetAsync($"/pix/{endToEndId}", Ct)).StatusCode);
    }

    private async Task LivroFecha()
    {
        var conferencia = await banco.Operador.GetFromJsonAsync<JsonElement>("/livro-razao/conferencia", Ct);
        Assert.True(conferencia.GetProperty("fecha").GetBoolean(), conferencia.ToString());
    }
}

/// <summary>Pix chegando de outro banco: o aviso do SPI, assinado (e sem login).</summary>
public class RecebimentoDePixTestes(BancoDeTeste banco)
{
    private readonly HttpClient _spi = banco.Principal.Anonimo();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Cliente Bia, string Email)> ContaComChave()
    {
        var bia = await banco.NovoCliente("Bia");
        var email = $"bia.{Guid.NewGuid():N}@exemplo.com";
        (await bia.CadastrarChave("Email", email)).EnsureSuccessStatusCode();
        return (bia, email);
    }

    private static string Aviso(string chave, decimal valor, string? endToEndId = null) => JsonSerializer.Serialize(
        new AvisoDePix(endToEndId ?? EndToEndId.Gerar("90000001", DateTimeOffset.UtcNow), chave, valor,
            new Pagador("Maria Clara Lima", "Banco Exemplo")),
        JsonSerializerOptions.Web);

    private async Task<HttpResponseMessage> Avisar(string corpo, DateTimeOffset? em = null, string? assinatura = null)
    {
        var data = (em ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var pedido = new HttpRequestMessage(HttpMethod.Post, "/spi/recebimentos")
        {
            Content = new StringContent(corpo, Encoding.UTF8, "application/json"),
        };
        pedido.Headers.Add(AssinaturaDoSpi.CabecalhoDaData, data);
        pedido.Headers.Add(AssinaturaDoSpi.Cabecalho, assinatura ?? AssinaturaDoSpi.Calcular(BancoDeTeste.SegredoDoSpi, data, corpo));
        return await _spi.SendAsync(pedido, Ct);
    }

    [Fact]
    public async Task Aviso_assinado_credita_e_aparece_no_extrato_com_quem_pagou()
    {
        var (bia, email) = await ContaComChave();

        var resposta = await Avisar(Aviso(email, 75.25m));

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(75.25m, await bia.Saldo());
        Assert.Equal("Pix de Maria Clara Lima (Banco Exemplo)",
            (await bia.Extrato()).GetProperty("itens")[0].GetProperty("descricao").GetString());
    }

    [Fact]
    public async Task O_mesmo_aviso_dez_vezes_ao_mesmo_tempo_credita_uma_vez()
    {
        var (bia, email) = await ContaComChave();
        var corpo = Aviso(email, 10m);

        var respostas = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Avisar(corpo)));

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(10m, await bia.Saldo());
    }

    [Fact]
    public async Task Assinatura_errada_nao_credita()
    {
        var (bia, email) = await ContaComChave();
        var resposta = await Avisar(Aviso(email, 1_000m), assinatura: "sha256=" + new string('0', 64));
        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        Assert.Equal(0m, await bia.Saldo());
    }

    [Fact]
    public async Task Corpo_alterado_depois_de_assinado_nao_credita()
    {
        var (bia, email) = await ContaComChave();
        var data = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var assinadoPara10 = AssinaturaDoSpi.Calcular(BancoDeTeste.SegredoDoSpi, data, Aviso(email, 10m, "E90000001202610151430ABCDEFGHIJK"));

        var resposta = await Avisar(Aviso(email, 10_000m, "E90000001202610151430ABCDEFGHIJK"), assinatura: assinadoPara10);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        Assert.Equal(0m, await bia.Saldo());
    }

    [Fact]
    public async Task Aviso_antigo_reenviado_nao_credita()
    {
        var (bia, email) = await ContaComChave();
        var resposta = await Avisar(Aviso(email, 10m), em: DateTimeOffset.UtcNow.AddMinutes(-10));
        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        Assert.Equal(0m, await bia.Saldo());
    }

    [Fact]
    public async Task Chave_que_nao_e_do_lastro_e_404() =>
        Assert.Equal(HttpStatusCode.NotFound, (await Avisar(Aviso($"ninguem.{Guid.NewGuid():N}@exemplo.com", 10m))).StatusCode);
}
