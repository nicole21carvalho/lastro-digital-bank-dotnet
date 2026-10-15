using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lastro.Api.Operacoes;

namespace Lastro.Api.Testes;

public class ContasEPixTestes(BancoDeTeste banco)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deposito_e_pix_mexem_nos_saldos_e_aparecem_no_extrato()
    {
        var (ana, bia) = (await banco.NovoCliente("Ana"), await banco.NovoCliente("Bia"));
        Assert.Equal(HttpStatusCode.Created, (await banco.Operador.Depositar(ana.Id, 100m)).StatusCode);

        var pix = await ana.Pix(bia, 30.50m, descricao: "Almoço");

        Assert.Equal(HttpStatusCode.Created, pix.StatusCode);
        Assert.Equal(69.50m, await ana.Saldo());
        Assert.Equal(30.50m, await bia.Saldo());
        var valores = (await ana.Extrato()).GetProperty("itens").EnumerateArray().Select(i => i.GetProperty("valor").GetDecimal());
        Assert.Equal([-30.50m, 100m], valores); // mais recente primeiro
    }

    [Fact]
    public async Task Extrato_sai_na_ordem_exata_dos_lancamentos()
    {
        // Pega o bug do modo rápido do Marten: sem a sequência, a ordem vinha embaralhada.
        var ana = await banco.NovoCliente();
        for (var valor = 1; valor <= 10; valor++)
        {
            await banco.Operador.Depositar(ana.Id, valor);
        }

        var valores = (await ana.Extrato()).GetProperty("itens").EnumerateArray().Select(i => i.GetProperty("valor").GetDecimal());

        Assert.Equal([10m, 9m, 8m, 7m, 6m, 5m, 4m, 3m, 2m, 1m], valores);
    }

    [Fact]
    public async Task Lancamento_tem_duas_partidas_que_somam_zero()
    {
        var (ana, bia) = (await banco.NovoCliente(), await banco.NovoCliente());
        await banco.Operador.Depositar(ana.Id, 10m);
        var pix = await (await ana.Pix(bia, 4m)).Ler<Api.Movimentacao>();

        var lancamento = await banco.Operador.GetFromJsonAsync<JsonElement>($"/lancamentos/{pix.LancamentoId}", Ct);

        var partidas = lancamento.GetProperty("partidas").EnumerateArray()
            .Select(p => (p.GetProperty("contaId").GetGuid(), p.GetProperty("valor").GetDecimal())).ToList();
        Assert.Equal([(ana.Id, -4m), (bia.Id, 4m)], partidas);
    }

    [Fact]
    public async Task Pix_sem_saldo_e_recusado_e_nada_muda()
    {
        var (ana, bia) = (await banco.NovoCliente(), await banco.NovoCliente());
        await banco.Operador.Depositar(ana.Id, 10m);

        var resposta = await ana.Pix(bia, 10.01m);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resposta.StatusCode);
        Assert.Equal("saldo-insuficiente", (await resposta.Ler<Api.Problema>()).Type);
        Assert.Equal(10m, await ana.Saldo());
        Assert.Equal(0m, await bia.Saldo());
    }

    [Fact]
    public async Task Pix_para_conta_que_nao_existe_e_404()
    {
        var ana = await banco.NovoCliente();
        await banco.Operador.Depositar(ana.Id, 10m);
        var resposta = await ana.Pix(Guid.CreateVersion7(), 1m);
        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.Equal("conta-nao-encontrada", (await resposta.Ler<Api.Problema>()).Type);
    }

    [Fact]
    public async Task Ninguem_manda_pix_direto_para_uma_conta_interna()
    {
        var ana = await banco.NovoCliente();
        await banco.Operador.Depositar(ana.Id, 10m);
        foreach (var interna in ContasInternas.Todas)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await ana.Pix(interna.Id, 1m)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await banco.Operador.Depositar(interna.Id, 1m)).StatusCode);
        }
        Assert.Equal(10m, await ana.Saldo());
    }

    [Fact]
    public async Task Um_cpf_uma_conta()
    {
        var cpf = Api.NovoCpf();
        var primeira = await banco.Principal.Como($"p-{Guid.NewGuid():N}").PostAsJsonAsync("/contas", new { nome = "Ana", cpf }, Ct);
        var segunda = await banco.Principal.Como($"p-{Guid.NewGuid():N}").PostAsJsonAsync("/contas", new { nome = "Outra Ana", cpf }, Ct);
        Assert.Equal(HttpStatusCode.Created, primeira.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, segunda.StatusCode);
        Assert.Equal("cpf-ja-cadastrado", (await segunda.Ler<Api.Problema>()).Type);
    }

    [Fact]
    public async Task Um_login_uma_conta()
    {
        var ana = await banco.NovoCliente();
        var outra = await ana.Http.PostAsJsonAsync("/contas", new { nome = "Ana de novo", cpf = Api.NovoCpf() }, Ct);
        Assert.Equal("conta-ja-aberta", (await outra.Ler<Api.Problema>()).Type);
        var minha = await ana.Http.GetFromJsonAsync<JsonElement>("/contas/minha", Ct);
        Assert.Equal(ana.Id, minha.GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("Ana", "123.456.789-00", HttpStatusCode.UnprocessableEntity)] // dígito verificador errado
    [InlineData("", "123.456.789-09", HttpStatusCode.BadRequest)] // nome vazio, barrado pela validação
    public async Task Cadastro_invalido_e_recusado(string nome, string cpf, HttpStatusCode esperado)
    {
        var resposta = await banco.Principal.Como($"p-{Guid.NewGuid():N}").PostAsJsonAsync("/contas", new { nome, cpf }, Ct);
        Assert.Equal(esperado, resposta.StatusCode);
    }

    [Fact]
    public async Task Numero_da_conta_tem_digito_verificador_valido() =>
        Assert.True(Dominio.NumeroDaConta.Valido((await banco.NovoCliente()).Numero));

    [Fact]
    public async Task Saldo_em_qualquer_instante_do_passado()
    {
        var (ana, bia) = (await banco.NovoCliente(), await banco.NovoCliente());
        var antesDeTudo = DateTimeOffset.UtcNow.AddMinutes(-1);
        await banco.Operador.Depositar(ana.Id, 50m);
        await Task.Delay(50, Ct);
        var depoisDoDeposito = DateTimeOffset.UtcNow;
        await Task.Delay(50, Ct);
        await ana.Pix(bia, 20m);

        Assert.Equal(30m, await ana.Saldo());
        Assert.Equal(50m, await ana.Saldo(depoisDoDeposito));
        Assert.Equal(0m, await ana.Saldo(antesDeTudo));
    }
}
