using System.Net;

namespace Lastro.Api.Testes;

/// <summary>O app perdeu a resposta e mandou de novo: o Pix não pode sair duas vezes.</summary>
public class IdempotenciaTestes(BancoDeTeste banco)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Cliente Ana, Cliente Bia)> AnaComSaldoEBia(decimal saldo = 100m)
    {
        var (ana, bia) = (await banco.NovoCliente("Ana"), await banco.NovoCliente("Bia"));
        await banco.Operador.Depositar(ana.Id, saldo);
        return (ana, bia);
    }

    [Fact]
    public async Task Repetir_a_chave_repete_a_resposta_sem_debitar_de_novo()
    {
        var (ana, bia) = await AnaComSaldoEBia();
        var chave = Api.NovaChave();

        var primeira = await ana.Pix(bia, 25m, chave);
        var segunda = await ana.Pix(bia, 25m, chave);

        Assert.Equal(HttpStatusCode.Created, segunda.StatusCode);
        Assert.True(segunda.Headers.Contains("Idempotent-Replayed"));
        Assert.False(primeira.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(
            (await primeira.Ler<Api.Movimentacao>()).LancamentoId, (await segunda.Ler<Api.Movimentacao>()).LancamentoId);
        Assert.Equal(75m, await ana.Saldo());
    }

    [Fact]
    public async Task Mesma_chave_com_outro_valor_e_recusada()
    {
        var (ana, bia) = await AnaComSaldoEBia();
        var chave = Api.NovaChave();
        await ana.Pix(bia, 25m, chave);

        var outra = await ana.Pix(bia, 99m, chave);

        Assert.Equal(HttpStatusCode.Conflict, outra.StatusCode);
        Assert.Equal("chave-de-idempotencia-reutilizada", (await outra.Ler<Api.Problema>()).Type);
        Assert.Equal(75m, await ana.Saldo());
    }

    [Fact]
    public async Task Recusa_tambem_e_lembrada()
    {
        var (ana, bia) = await AnaComSaldoEBia(saldo: 0m);
        var chave = Api.NovaChave();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ana.Pix(bia, 5m, chave)).StatusCode);
        await banco.Operador.Depositar(ana.Id, 100m);

        // Mesma chave depois do depósito: devolve a recusa guardada, não faz um Pix novo.
        var repetida = await ana.Pix(bia, 5m, chave);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, repetida.StatusCode);
        Assert.True(repetida.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(100m, await ana.Saldo());
    }

    [Fact]
    public async Task Vinte_envios_simultaneos_da_mesma_chave_viram_um_pix_so()
    {
        var (ana, bia) = await AnaComSaldoEBia();
        var chave = Api.NovaChave();

        var respostas = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => ana.Pix(bia, 10m, chave)));

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var lancamentos = await Task.WhenAll(respostas.Select(r => r.Ler<Api.Movimentacao>()));
        Assert.Single(lancamentos.Select(l => l.LancamentoId).Distinct());
        Assert.Equal(90m, await ana.Saldo());
        Assert.Equal(10m, await bia.Saldo());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("curta")]
    [InlineData("tem espaço no meio")]
    public async Task Sem_chave_valida_nao_ha_pix(string? chave)
    {
        var (ana, bia) = await AnaComSaldoEBia();
        using var pedido = new HttpRequestMessage(HttpMethod.Post, "/pix")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { origem = ana.Id, destino = bia.Id, valor = 1m }),
        };
        if (chave is not null)
        {
            pedido.Headers.Add("Idempotency-Key", chave);
        }
        var resposta = await ana.Http.SendAsync(pedido, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal(100m, await ana.Saldo());
    }

    [Fact]
    public async Task A_mesma_chave_em_contas_diferentes_nao_colide()
    {
        var (ana, bia, caio) = (await banco.NovoCliente(), await banco.NovoCliente(), await banco.NovoCliente());
        var (doDeposito, doPix) = (Api.NovaChave(), Api.NovaChave());
        // Dois clientes, por acaso, com a mesma chave: cada um tem a sua operação.
        Assert.Equal(HttpStatusCode.Created, (await banco.Operador.Depositar(ana.Id, 10m, doDeposito)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await banco.Operador.Depositar(bia.Id, 10m, doDeposito)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ana.Pix(caio, 1m, doPix)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await bia.Pix(caio, 1m, doPix)).StatusCode);
        Assert.Equal(9m, await ana.Saldo());
        Assert.Equal(9m, await bia.Saldo());
        Assert.Equal(2m, await caio.Saldo());
    }

    [Fact]
    public async Task O_mesmo_cliente_nao_reaproveita_a_chave_de_outra_operacao()
    {
        var (ana, bia) = (await banco.NovoCliente(), await banco.NovoCliente());
        var chave = Api.NovaChave();
        Assert.Equal(HttpStatusCode.Created, (await banco.Operador.Depositar(ana.Id, 10m, chave)).StatusCode);
        var pix = await ana.Pix(bia, 1m, chave);
        Assert.Equal(HttpStatusCode.Conflict, pix.StatusCode);
        Assert.Equal(10m, await ana.Saldo());
    }
}
