using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Lastro.Api.Testes.BancoDeTeste))]

namespace Lastro.Api.Testes;

/// <summary>A API de verdade, num PostgreSQL de verdade (num container), para todos os testes.</summary>
public sealed class BancoDeTeste : IAsyncDisposable, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private WebApplicationFactory<Program>? _fabrica;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public HttpClient Cliente() =>
        (_fabrica ?? throw new InvalidOperationException("O banco de teste ainda não subiu.")).CreateClient();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        // Fila grande e paciência: os testes de concorrência disparam mil requisições de uma vez.
        var conexao = $"{_postgres.GetConnectionString()};Maximum Pool Size=40;Timeout=300;Command Timeout=300";
        _fabrica = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseEnvironment("Testes")
            .UseSetting("ConnectionStrings:Lastro", conexao));
        _ = _fabrica.Server;
    }

    public async ValueTask DisposeAsync()
    {
        if (_fabrica is not null)
        {
            await _fabrica.DisposeAsync();
        }
        await _postgres.DisposeAsync();
    }
}

/// <summary>Atalhos para os testes falarem com a API como um app falaria.</summary>
public static class Api
{
    public sealed record Conta(Guid Id, string Agencia, string Numero);

    public sealed record Movimentacao(Guid LancamentoId, decimal Valor, Guid Origem, Guid Destino);

    public sealed record Problema(string Type, string Title, int Status);

    public static string NovaChave() => Guid.NewGuid().ToString();

    /// <summary>Um CPF válido diferente a cada chamada (dígitos calculados).</summary>
    public static string NovoCpf()
    {
        var numeros = Random.Shared.Next(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var posicao in new[] { 9, 10 })
        {
            var soma = numeros.Select((c, i) => (c - '0') * (posicao + 1 - i)).Sum();
            numeros += (soma % 11 < 2 ? 0 : 11 - (soma % 11)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return numeros;
    }

    public static async Task<Conta> AbrirConta(this HttpClient http, string nome = "Cliente de teste")
    {
        var resposta = await http.PostAsJsonAsync("/contas", new { nome, cpf = NovoCpf() });
        resposta.EnsureSuccessStatusCode();
        return (await resposta.Content.ReadFromJsonAsync<Conta>(BancoDeTeste.Json))!;
    }

    public static Task<HttpResponseMessage> Depositar(this HttpClient http, Guid conta, decimal valor, string? chave = null) =>
        Enviar(http, $"/contas/{conta}/depositos", new { valor }, chave ?? NovaChave());

    public static Task<HttpResponseMessage> Pix(
        this HttpClient http, Guid origem, Guid destino, decimal valor, string? chave = null, string? descricao = null) =>
        Enviar(http, "/pix", new { origem, destino, valor, descricao }, chave ?? NovaChave());

    public static async Task<decimal> Saldo(this HttpClient http, Guid conta, DateTimeOffset? em = null)
    {
        var url = em is null ? $"/contas/{conta}/saldo" : $"/contas/{conta}/saldo?em={Uri.EscapeDataString(em.Value.ToString("O"))}";
        var json = await http.GetFromJsonAsync<JsonElement>(url);
        return json.GetProperty("saldo").GetDecimal();
    }

    public static async Task<T> Ler<T>(this HttpResponseMessage resposta) =>
        (await resposta.Content.ReadFromJsonAsync<T>(BancoDeTeste.Json))!;

    private static async Task<HttpResponseMessage> Enviar(HttpClient http, string url, object corpo, string chave)
    {
        using var pedido = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(corpo) };
        pedido.Headers.Add("Idempotency-Key", chave);
        return await http.SendAsync(pedido);
    }
}
