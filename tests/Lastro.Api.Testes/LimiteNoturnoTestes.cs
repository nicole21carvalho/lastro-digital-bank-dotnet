using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;

namespace Lastro.Api.Testes;

/// <summary>A API com o relógio do teste: dá para "ser 21h" e pular 24 horas na hora.</summary>
public class LimiteNoturnoTestes(BancoDeTeste banco)
{
    private static readonly DateTimeOffset Noite = new(2026, 10, 15, 21, 0, 0, TimeSpan.FromHours(-3));

    private readonly FakeTimeProvider _relogio = new(Noite);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Cliente Ana, Cliente Bia)> Preparar(decimal saldo = 10_000m)
    {
        var api = banco.Variante(_relogio);
        var (ana, bia) = (await api.NovoCliente("Ana"), await api.NovoCliente("Bia"));
        await api.Operador.Depositar(ana.Id, saldo);
        return (ana, bia);
    }

    [Fact]
    public async Task Das_20h_as_6h_sai_no_maximo_mil_reais_por_noite()
    {
        var (ana, bia) = await Preparar();
        Assert.Equal(HttpStatusCode.Created, (await ana.Pix(bia, 600m)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ana.Pix(bia, 400m)).StatusCode);

        var passou = await ana.Pix(bia, 0.01m);

        Assert.Equal("limite-noturno-excedido", (await passou.Ler<Api.Problema>()).Type);
        _relogio.Advance(TimeSpan.FromHours(13)); // 10h: de dia, sem o limite noturno
        Assert.Equal(HttpStatusCode.Created, (await ana.Pix(bia, 5_000m)).StatusCode);
    }

    [Fact]
    public async Task Vinte_pix_simultaneos_a_noite_param_no_limite()
    {
        var (ana, bia) = await Preparar();
        var respostas = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => ana.Pix(bia, 100m)));
        Assert.Equal(10, respostas.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1_000m, await bia.Saldo());
    }

    [Fact]
    public async Task Aumento_pedido_a_noite_so_vale_24_horas_depois()
    {
        var (ana, bia) = await Preparar();

        var limite = await ana.Http.PutAsJsonAsync($"/contas/{ana.Id}/limite-noturno", new { valor = 5_000m }, Ct);
        var corpo = await limite.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(1_000m, corpo.GetProperty("limiteNoturno").GetDecimal());
        Assert.Equal(Noite.AddHours(24), corpo.GetProperty("aumentoPendente").GetProperty("valeDesde").GetDateTimeOffset());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ana.Pix(bia, 1_500m)).StatusCode);

        _relogio.Advance(TimeSpan.FromHours(24));
        Assert.Equal(HttpStatusCode.Created, (await ana.Pix(bia, 1_500m)).StatusCode);
    }

    [Fact]
    public async Task Reducao_vale_na_hora()
    {
        var (ana, bia) = await Preparar();
        await ana.Http.PutAsJsonAsync($"/contas/{ana.Id}/limite-noturno", new { valor = 100m }, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ana.Pix(bia, 100.01m)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ana.Pix(bia, 100m)).StatusCode);
    }
}
