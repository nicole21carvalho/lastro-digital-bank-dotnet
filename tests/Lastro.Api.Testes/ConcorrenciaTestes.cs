using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Lastro.Api.Testes;

/// <summary>Muita gente mexendo nas mesmas contas ao mesmo tempo: o dinheiro não some nem duplica.</summary>
public class ConcorrenciaTestes(BancoDeTeste banco)
{
    private readonly HttpClient _http = banco.Cliente();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Mil_pix_simultaneos_com_saldo_para_cem_passam_exatamente_cem()
    {
        var ana = await _http.AbrirConta("Ana");
        var destinos = new List<Api.Conta>();
        for (var i = 0; i < 10; i++)
        {
            destinos.Add(await _http.AbrirConta($"Destino {i}"));
        }
        await _http.Depositar(ana.Id, 100m);

        var respostas = await Task.WhenAll(
            Enumerable.Range(0, 1000).Select(i => _http.Pix(ana.Id, destinos[i % 10].Id, 1m)));

        var porStatus = respostas.GroupBy(r => r.StatusCode).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(100, porStatus.GetValueOrDefault(HttpStatusCode.Created));
        Assert.Equal(900, porStatus.GetValueOrDefault(HttpStatusCode.UnprocessableEntity));
        Assert.Equal(0m, await _http.Saldo(ana.Id));
        var recebido = 0m;
        foreach (var destino in destinos)
        {
            recebido += await _http.Saldo(destino.Id);
        }
        Assert.Equal(100m, recebido);
        await LivroFecha();
    }

    [Fact]
    public async Task Pix_cruzados_ao_mesmo_tempo_nao_travam_e_o_total_se_mantem()
    {
        // A→B e B→A ao mesmo tempo: travando sempre na ordem do id, ninguém espera ninguém em círculo.
        var (ana, bia) = (await _http.AbrirConta("Ana"), await _http.AbrirConta("Bia"));
        await _http.Depositar(ana.Id, 500m);
        await _http.Depositar(bia.Id, 500m);

        var idas = Enumerable.Range(0, 200).Select(_ => _http.Pix(ana.Id, bia.Id, 1m));
        var voltas = Enumerable.Range(0, 200).Select(_ => _http.Pix(bia.Id, ana.Id, 1m));
        var respostas = await Task.WhenAll(idas.Concat(voltas));

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(500m, await _http.Saldo(ana.Id));
        Assert.Equal(500m, await _http.Saldo(bia.Id));
        await LivroFecha();
    }

    [Fact]
    public async Task Depositos_simultaneos_de_varios_clientes_somam_certo()
    {
        var contas = new List<Api.Conta>();
        for (var i = 0; i < 5; i++)
        {
            contas.Add(await _http.AbrirConta());
        }

        var respostas = await Task.WhenAll(
            Enumerable.Range(0, 250).Select(i => _http.Depositar(contas[i % 5].Id, 2m)));

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        foreach (var conta in contas)
        {
            Assert.Equal(100m, await _http.Saldo(conta.Id));
        }
        await LivroFecha();
    }

    private async Task LivroFecha()
    {
        var conferencia = await _http.GetFromJsonAsync<JsonElement>("/livro-razao/conferencia", Ct);
        Assert.True(conferencia.GetProperty("fecha").GetBoolean(), conferencia.ToString());
        Assert.Equal(0m, conferencia.GetProperty("somaDosSaldos").GetDecimal());
    }
}
