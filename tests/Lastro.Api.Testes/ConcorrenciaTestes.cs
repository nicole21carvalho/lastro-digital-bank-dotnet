using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Lastro.Api.Testes;

/// <summary>Muita gente mexendo nas mesmas contas ao mesmo tempo: o dinheiro não some nem duplica.</summary>
public class ConcorrenciaTestes(BancoDeTeste banco)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Mil_pix_simultaneos_com_saldo_para_cem_passam_exatamente_cem()
    {
        var ana = await banco.NovoCliente("Ana");
        var destinos = new List<Cliente>();
        for (var i = 0; i < 10; i++)
        {
            destinos.Add(await banco.NovoCliente($"Destino {i}"));
        }
        await banco.Operador.Depositar(ana.Id, 100m);

        var respostas = await Task.WhenAll(Enumerable.Range(0, 1000).Select(i => ana.Pix(destinos[i % 10], 1m)));

        var porStatus = respostas.GroupBy(r => r.StatusCode).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(100, porStatus.GetValueOrDefault(HttpStatusCode.Created));
        Assert.Equal(900, porStatus.GetValueOrDefault(HttpStatusCode.UnprocessableEntity));
        Assert.Equal(0m, await ana.Saldo());
        var recebido = 0m;
        foreach (var destino in destinos)
        {
            recebido += await destino.Saldo();
        }
        Assert.Equal(100m, recebido);
        await LivroFecha();
    }

    [Fact]
    public async Task Pix_cruzados_ao_mesmo_tempo_nao_travam_e_o_total_se_mantem()
    {
        // A→B e B→A ao mesmo tempo: travando sempre na ordem do id, ninguém espera ninguém em círculo.
        var (ana, bia) = (await banco.NovoCliente("Ana"), await banco.NovoCliente("Bia"));
        await banco.Operador.Depositar(ana.Id, 500m);
        await banco.Operador.Depositar(bia.Id, 500m);

        var idas = Enumerable.Range(0, 200).Select(_ => ana.Pix(bia, 1m));
        var voltas = Enumerable.Range(0, 200).Select(_ => bia.Pix(ana, 1m));
        var respostas = await Task.WhenAll(idas.Concat(voltas));

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(500m, await ana.Saldo());
        Assert.Equal(500m, await bia.Saldo());
        await LivroFecha();
    }

    [Fact]
    public async Task Depositos_simultaneos_de_varios_clientes_somam_certo()
    {
        var contas = new List<Cliente>();
        for (var i = 0; i < 5; i++)
        {
            contas.Add(await banco.NovoCliente());
        }

        var respostas = await Task.WhenAll(Enumerable.Range(0, 250).Select(i => banco.Operador.Depositar(contas[i % 5].Id, 2m)));

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        foreach (var conta in contas)
        {
            Assert.Equal(100m, await conta.Saldo());
        }
        await LivroFecha();
    }

    private async Task LivroFecha()
    {
        var conferencia = await banco.Operador.GetFromJsonAsync<JsonElement>("/livro-razao/conferencia", Ct);
        Assert.True(conferencia.GetProperty("fecha").GetBoolean(), conferencia.ToString());
        Assert.Equal(0m, conferencia.GetProperty("somaDosSaldos").GetDecimal());
    }
}
