using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Lastro.Api.Testes;

public class ContasEPixTestes(BancoDeTeste banco)
{
    private readonly HttpClient _http = banco.Cliente();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deposito_e_pix_mexem_nos_saldos_e_aparecem_no_extrato()
    {
        var (ana, bia) = (await _http.AbrirConta("Ana"), await _http.AbrirConta("Bia"));
        Assert.Equal(HttpStatusCode.Created, (await _http.Depositar(ana.Id, 100m)).StatusCode);

        var pix = await _http.Pix(ana.Id, bia.Id, 30.50m, descricao: "Almoço");

        Assert.Equal(HttpStatusCode.Created, pix.StatusCode);
        Assert.Equal(69.50m, await _http.Saldo(ana.Id));
        Assert.Equal(30.50m, await _http.Saldo(bia.Id));

        var extrato = await _http.GetFromJsonAsync<JsonElement>($"/contas/{ana.Id}/extrato", Ct);
        var valores = extrato.GetProperty("itens").EnumerateArray().Select(i => i.GetProperty("valor").GetDecimal());
        Assert.Equal([-30.50m, 100m], valores); // mais recente primeiro
    }

    [Fact]
    public async Task Extrato_sai_na_ordem_exata_dos_lancamentos()
    {
        // Pega o bug do modo rápido do Marten: sem a sequência, a ordem vinha embaralhada.
        var ana = await _http.AbrirConta();
        for (var valor = 1; valor <= 10; valor++)
        {
            await _http.Depositar(ana.Id, valor);
        }

        var extrato = await _http.GetFromJsonAsync<JsonElement>($"/contas/{ana.Id}/extrato", Ct);

        var valores = extrato.GetProperty("itens").EnumerateArray().Select(i => i.GetProperty("valor").GetDecimal());
        Assert.Equal([10m, 9m, 8m, 7m, 6m, 5m, 4m, 3m, 2m, 1m], valores);
    }

    [Fact]
    public async Task Lancamento_tem_duas_partidas_que_somam_zero()
    {
        var (ana, bia) = (await _http.AbrirConta(), await _http.AbrirConta());
        await _http.Depositar(ana.Id, 10m);
        var pix = await (await _http.Pix(ana.Id, bia.Id, 4m)).Ler<Api.Movimentacao>();

        var lancamento = await _http.GetFromJsonAsync<JsonElement>($"/lancamentos/{pix.LancamentoId}", Ct);

        var partidas = lancamento.GetProperty("partidas").EnumerateArray()
            .Select(p => (p.GetProperty("contaId").GetGuid(), p.GetProperty("valor").GetDecimal())).ToList();
        Assert.Equal([(ana.Id, -4m), (bia.Id, 4m)], partidas);
    }

    [Fact]
    public async Task Pix_sem_saldo_e_recusado_e_nada_muda()
    {
        var (ana, bia) = (await _http.AbrirConta(), await _http.AbrirConta());
        await _http.Depositar(ana.Id, 10m);

        var resposta = await _http.Pix(ana.Id, bia.Id, 10.01m);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resposta.StatusCode);
        Assert.Equal("saldo-insuficiente", (await resposta.Ler<Api.Problema>()).Type);
        Assert.Equal(10m, await _http.Saldo(ana.Id));
        Assert.Equal(0m, await _http.Saldo(bia.Id));
    }

    [Fact]
    public async Task Pix_para_conta_que_nao_existe_e_404()
    {
        var ana = await _http.AbrirConta();
        await _http.Depositar(ana.Id, 10m);
        var resposta = await _http.Pix(ana.Id, Guid.CreateVersion7(), 1m);
        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.Equal("conta-nao-encontrada", (await resposta.Ler<Api.Problema>()).Type);
    }

    [Fact]
    public async Task Ninguem_tira_dinheiro_de_uma_conta_interna_do_banco()
    {
        // A liquidação pode ficar negativa, então não confere saldo: um Pix saindo dela seria dinheiro infinito.
        var ana = await _http.AbrirConta();
        var liquidacao = Lastro.Api.Operacoes.ContasInternas.Liquidacao;

        var resposta = await _http.Pix(liquidacao, ana.Id, 1_000_000m);

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.Equal(0m, await _http.Saldo(ana.Id));
    }

    [Fact]
    public async Task Ninguem_manda_pix_direto_para_uma_conta_interna()
    {
        var ana = await _http.AbrirConta();
        await _http.Depositar(ana.Id, 10m);
        foreach (var interna in Lastro.Api.Operacoes.ContasInternas.Todas)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await _http.Pix(ana.Id, interna.Id, 1m)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await _http.Depositar(interna.Id, 1m)).StatusCode);
        }
        Assert.Equal(10m, await _http.Saldo(ana.Id));
    }

    [Fact]
    public async Task Um_cpf_uma_conta()
    {
        var cpf = Api.NovoCpf();
        var primeira = await _http.PostAsJsonAsync("/contas", new { nome = "Ana", cpf }, Ct);
        var segunda = await _http.PostAsJsonAsync("/contas", new { nome = "Outra Ana", cpf }, Ct);
        Assert.Equal(HttpStatusCode.Created, primeira.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, segunda.StatusCode);
        Assert.Equal("cpf-ja-cadastrado", (await segunda.Ler<Api.Problema>()).Type);
    }

    [Theory]
    [InlineData("Ana", "123.456.789-00", HttpStatusCode.UnprocessableEntity)] // dígito verificador errado
    [InlineData("", "123.456.789-09", HttpStatusCode.BadRequest)] // nome vazio, barrado pela validação
    public async Task Cadastro_invalido_e_recusado(string nome, string cpf, HttpStatusCode esperado)
    {
        var resposta = await _http.PostAsJsonAsync("/contas", new { nome, cpf }, Ct);
        Assert.Equal(esperado, resposta.StatusCode);
    }

    [Fact]
    public async Task Numero_da_conta_tem_digito_verificador_valido()
    {
        var conta = await _http.AbrirConta();
        Assert.Equal("0001", conta.Agencia);
        Assert.True(Dominio.NumeroDaConta.Valido(conta.Numero), conta.Numero);
    }

    [Fact]
    public async Task Saldo_em_qualquer_instante_do_passado()
    {
        var (ana, bia) = (await _http.AbrirConta(), await _http.AbrirConta());
        var antesDeTudo = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _http.Depositar(ana.Id, 50m);
        await Task.Delay(50, Ct);
        var depoisDoDeposito = DateTimeOffset.UtcNow;
        await Task.Delay(50, Ct);
        await _http.Pix(ana.Id, bia.Id, 20m);

        Assert.Equal(30m, await _http.Saldo(ana.Id));
        Assert.Equal(50m, await _http.Saldo(ana.Id, depoisDoDeposito));
        Assert.Equal(0m, await _http.Saldo(ana.Id, antesDeTudo));
    }
}
