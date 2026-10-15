using System.Net;

namespace Lastro.Api.Testes;

/// <summary>O app perdeu a resposta e mandou de novo: o Pix não pode sair duas vezes.</summary>
public class IdempotenciaTestes(BancoDeTeste banco)
{
    private readonly HttpClient _http = banco.Cliente();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Repetir_a_chave_repete_a_resposta_sem_debitar_de_novo()
    {
        var (ana, bia) = (await _http.AbrirConta(), await _http.AbrirConta());
        await _http.Depositar(ana.Id, 100m);
        var chave = Api.NovaChave();

        var primeira = await _http.Pix(ana.Id, bia.Id, 25m, chave);
        var segunda = await _http.Pix(ana.Id, bia.Id, 25m, chave);

        Assert.Equal(HttpStatusCode.Created, segunda.StatusCode);
        Assert.True(segunda.Headers.Contains("Idempotent-Replayed"));
        Assert.False(primeira.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(
            (await primeira.Ler<Api.Movimentacao>()).LancamentoId, (await segunda.Ler<Api.Movimentacao>()).LancamentoId);
        Assert.Equal(75m, await _http.Saldo(ana.Id));
    }

    [Fact]
    public async Task Mesma_chave_com_outro_valor_e_recusada()
    {
        var (ana, bia) = (await _http.AbrirConta(), await _http.AbrirConta());
        await _http.Depositar(ana.Id, 100m);
        var chave = Api.NovaChave();
        await _http.Pix(ana.Id, bia.Id, 25m, chave);

        var outra = await _http.Pix(ana.Id, bia.Id, 99m, chave);

        Assert.Equal(HttpStatusCode.Conflict, outra.StatusCode);
        Assert.Equal("chave-de-idempotencia-reutilizada", (await outra.Ler<Api.Problema>()).Type);
        Assert.Equal(75m, await _http.Saldo(ana.Id));
    }

    [Fact]
    public async Task Recusa_tambem_e_lembrada()
    {
        var (ana, bia) = (await _http.AbrirConta(), await _http.AbrirConta());
        var chave = Api.NovaChave();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await _http.Pix(ana.Id, bia.Id, 5m, chave)).StatusCode);
        await _http.Depositar(ana.Id, 100m);

        // Mesma chave depois do depósito: devolve a recusa guardada, não faz um Pix novo.
        var repetida = await _http.Pix(ana.Id, bia.Id, 5m, chave);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, repetida.StatusCode);
        Assert.True(repetida.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(100m, await _http.Saldo(ana.Id));
    }

    [Fact]
    public async Task Vinte_envios_simultaneos_da_mesma_chave_viram_um_pix_so()
    {
        var (ana, bia) = (await _http.AbrirConta(), await _http.AbrirConta());
        await _http.Depositar(ana.Id, 100m);
        var chave = Api.NovaChave();

        var respostas = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => _http.Pix(ana.Id, bia.Id, 10m, chave)));

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var lancamentos = await Task.WhenAll(respostas.Select(r => r.Ler<Api.Movimentacao>()));
        Assert.Single(lancamentos.Select(l => l.LancamentoId).Distinct());
        Assert.Equal(90m, await _http.Saldo(ana.Id));
        Assert.Equal(10m, await _http.Saldo(bia.Id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("curta")]
    [InlineData("tem espaço no meio")]
    public async Task Sem_chave_valida_nao_ha_pix(string? chave)
    {
        var (ana, bia) = (await _http.AbrirConta(), await _http.AbrirConta());
        using var pedido = new HttpRequestMessage(HttpMethod.Post, "/pix")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { origem = ana.Id, destino = bia.Id, valor = 1m }),
        };
        if (chave is not null)
        {
            pedido.Headers.Add("Idempotency-Key", chave);
        }
        var resposta = await _http.SendAsync(pedido, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task A_mesma_chave_em_contas_diferentes_nao_colide()
    {
        var (ana, bia, caio) = (await _http.AbrirConta(), await _http.AbrirConta(), await _http.AbrirConta());
        var (doDeposito, doPix) = (Api.NovaChave(), Api.NovaChave());
        // Dois clientes, por acaso, com a mesma chave: cada um tem a sua operação.
        Assert.Equal(HttpStatusCode.Created, (await _http.Depositar(ana.Id, 10m, doDeposito)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _http.Depositar(bia.Id, 10m, doDeposito)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _http.Pix(ana.Id, caio.Id, 1m, doPix)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _http.Pix(bia.Id, caio.Id, 1m, doPix)).StatusCode);
        Assert.Equal(9m, await _http.Saldo(ana.Id));
        Assert.Equal(9m, await _http.Saldo(bia.Id));
        Assert.Equal(2m, await _http.Saldo(caio.Id));
    }

    [Fact]
    public async Task O_mesmo_cliente_nao_reaproveita_a_chave_de_outra_operacao()
    {
        var (ana, bia) = (await _http.AbrirConta(), await _http.AbrirConta());
        var chave = Api.NovaChave();
        Assert.Equal(HttpStatusCode.Created, (await _http.Depositar(ana.Id, 10m, chave)).StatusCode);
        var pix = await _http.Pix(ana.Id, bia.Id, 1m, chave);
        Assert.Equal(HttpStatusCode.Conflict, pix.StatusCode);
        Assert.Equal(10m, await _http.Saldo(ana.Id));
    }
}
