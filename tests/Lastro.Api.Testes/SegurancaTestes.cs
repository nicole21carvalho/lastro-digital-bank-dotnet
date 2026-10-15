using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lastro.Api.Operacoes;

namespace Lastro.Api.Testes;

/// <summary>Quem pode fazer o quê: login, dono da conta, papéis, limites e trilha de auditoria.</summary>
public class SegurancaTestes(BancoDeTeste banco)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Cliente Ana, Cliente Bia)> AnaComSaldoEBia()
    {
        var (ana, bia) = (await banco.NovoCliente("Ana"), await banco.NovoCliente("Bia"));
        await banco.Operador.Depositar(ana.Id, 100m);
        return (ana, bia);
    }

    [Fact]
    public async Task Sem_login_nada_funciona()
    {
        var (ana, bia) = await AnaComSaldoEBia();
        var anonimo = banco.Principal.Anonimo();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.Pix(ana.Id, bia.Id, 1m)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync($"/contas/{ana.Id}/saldo", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsJsonAsync("/contas", new { nome = "X", cpf = Api.NovoCpf() }, Ct)).StatusCode);
        Assert.Equal(100m, await ana.Saldo());
    }

    public static TheoryData<string> TokensInvalidos => new()
    {
        { "emissor" },
        { "audiencia" },
        { "expirado" },
        { "assinatura" },
    };

    [Theory]
    [MemberData(nameof(TokensInvalidos))]
    public async Task Token_que_nao_vale_e_recusado(string defeito)
    {
        var ana = await banco.NovoCliente();
        var token = defeito switch
        {
            "emissor" => BancoDeTeste.Token(ana.Usuario, emissor: "https://outro.exemplo/realms/x"),
            "audiencia" => BancoDeTeste.Token(ana.Usuario, audiencia: "outra-api"),
            "expirado" => BancoDeTeste.Token(ana.Usuario, expira: DateTime.UtcNow.AddMinutes(-5)),
            // Troca um caractere do meio da assinatura (o último pode só mudar bits de enchimento).
            _ => AdulterarAssinatura(BancoDeTeste.Token(ana.Usuario)),
        };
        var resposta = await banco.Principal.ComToken(token).GetAsync($"/contas/{ana.Id}/saldo", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    private static string AdulterarAssinatura(string token)
    {
        var meio = token.LastIndexOf('.') + 10;
        return token[..meio] + (token[meio] == 'A' ? 'B' : 'A') + token[(meio + 1)..];
    }

    [Fact]
    public async Task Ninguem_mexe_na_conta_de_outra_pessoa_nem_descobre_que_ela_existe()
    {
        var (ana, bia) = await AnaComSaldoEBia();

        // Bia tenta tudo na conta da Ana: a resposta é a mesma de uma conta que não existe.
        Assert.Equal(HttpStatusCode.NotFound, (await bia.Http.Pix(ana.Id, bia.Id, 50m)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bia.Http.GetAsync($"/contas/{ana.Id}/saldo", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bia.Http.GetAsync($"/contas/{ana.Id}/extrato", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bia.Http.GetAsync($"/contas/{ana.Id}/chaves", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bia.Http.PostAsJsonAsync($"/contas/{ana.Id}/chaves", new { tipo = "Aleatoria" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bia.Http.PutAsJsonAsync($"/contas/{ana.Id}/limite-noturno", new { valor = 0m }, Ct)).StatusCode);
        Assert.Equal(100m, await ana.Saldo());
    }

    [Fact]
    public async Task Ninguem_tira_dinheiro_de_uma_conta_interna_do_banco()
    {
        // A liquidação pode ficar negativa, então não confere saldo: um Pix saindo dela seria dinheiro infinito.
        var ana = await banco.NovoCliente();
        foreach (var quem in new[] { ana.Http, banco.Operador })
        {
            var resposta = await quem.Pix(ContasInternas.Liquidacao, ana.Id, 1_000_000m);
            Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        }
        Assert.Equal(0m, await ana.Saldo());
    }

    [Fact]
    public async Task Cliente_nao_faz_o_que_e_do_operador()
    {
        var ana = await banco.NovoCliente();
        Assert.Equal(HttpStatusCode.Forbidden, (await ana.Http.Depositar(ana.Id, 1_000m)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ana.Http.GetAsync("/livro-razao/conferencia", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ana.Http.GetAsync($"/contas/{ana.Id}/auditoria", Ct)).StatusCode);
        Assert.Equal(0m, await ana.Saldo());
    }

    [Fact]
    public async Task Cada_evento_guarda_quem_fez_e_o_trace_da_requisicao()
    {
        var (ana, bia) = await AnaComSaldoEBia();
        await ana.Pix(bia, 10m);

        var auditoria = await banco.Operador.GetFromJsonAsync<JsonElement>($"/contas/{ana.Id}/auditoria", Ct);

        var porEvento = auditoria.EnumerateArray()
            .Select(e => (e.GetProperty("evento").GetString(), e.GetProperty("usuario").GetString())).ToList();
        Assert.Equal(
            [("conta_aberta", ana.Usuario), ("valor_creditado", banco.Principal.OperadorId), ("valor_debitado", ana.Usuario)],
            porEvento);
        Assert.All(auditoria.EnumerateArray(), e => Assert.False(string.IsNullOrEmpty(e.GetProperty("correlacao").GetString())));
    }

    [Fact]
    public async Task Limite_de_uso_barra_quem_varre_chaves_pix()
    {
        var api = banco.Variante(ajustes: ("LimitesDeUso:ConsultasDeChavePorMinuto", "3"));
        var curioso = await api.NovoCliente("Curioso");

        var respostas = new List<HttpResponseMessage>();
        for (var i = 0; i < 4; i++)
        {
            respostas.Add(await curioso.Http.GetAsync($"/pix/chaves/varrendo.{i}@exemplo.com", Ct));
        }

        Assert.All(respostas.Take(3), r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        Assert.Equal(HttpStatusCode.TooManyRequests, respostas[3].StatusCode);
        Assert.True(respostas[3].Headers.Contains("Retry-After"));
        // O limite é por pessoa: outra pessoa continua consultando.
        var outra = await api.NovoCliente("Outra");
        Assert.Equal(HttpStatusCode.NotFound, (await outra.Http.GetAsync("/pix/chaves/alguem@exemplo.com", Ct)).StatusCode);
    }

    [Fact]
    public async Task Limite_de_uso_do_pix_por_pessoa()
    {
        var api = banco.Variante(ajustes: ("LimitesDeUso:PixPorMinuto", "2"));
        var (ana, bia) = (await api.NovoCliente(), await api.NovoCliente());
        await api.Operador.Depositar(ana.Id, 10m);

        Assert.Equal(HttpStatusCode.Created, (await ana.Pix(bia, 1m)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ana.Pix(bia, 1m)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await ana.Pix(bia, 1m)).StatusCode);
        Assert.Equal(8m, await ana.Saldo());
    }

    [Fact]
    public async Task Antifraude_segura_conta_nova_e_rajada()
    {
        var api = banco.Variante(ajustes: ("Antifraude:Ligada", "true"));
        var (ana, bia) = (await api.NovoCliente(), await api.NovoCliente());
        await api.Operador.Depositar(ana.Id, 5_000m);

        var alto = await ana.Pix(bia, 600m);

        Assert.Equal("limite-da-conta-nova", (await alto.Ler<Api.Problema>()).Type);
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await ana.Pix(bia, 1m)).StatusCode);
        }
        Assert.Equal("pix-em-rajada", (await (await ana.Pix(bia, 1m)).Ler<Api.Problema>()).Type);
        Assert.Equal(4_990m, await ana.Saldo());
    }
}
