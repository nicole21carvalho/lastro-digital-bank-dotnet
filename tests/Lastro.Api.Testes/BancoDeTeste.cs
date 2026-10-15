using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Lastro.Api.Testes.BancoDeTeste))]

namespace Lastro.Api.Testes;

/// <summary>
/// A API de verdade, num PostgreSQL de verdade (num container), para todos os testes. Os
/// tokens são assinados aqui, com uma chave RSA criada na hora: é o "provedor de identidade"
/// dos testes. A API confere assinatura, emissor, audiência e validade como em produção.
/// </summary>
public sealed class BancoDeTeste : IAsyncDisposable, IAsyncLifetime
{
    public const string Emissor = "https://identidade.lastro.teste/realms/lastro";
    public const string Audiencia = "lastro-api";

    /// <summary>Segredo combinado com o SPI de teste, para assinar os avisos de Pix recebido.</summary>
    public const string SegredoDoSpi = "segredo-de-teste-do-spi-com-mais-de-32-caracteres";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly RsaSecurityKey Chave = new(RSA.Create(2048)) { KeyId = "chave-de-teste" };

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private WebApplicationFactory<Program>? _fabrica;

    private Host? _principal;

    /// <summary>A API principal: antifraude e limites de uso desligados, para os testes de carga.</summary>
    public Host Principal => _principal ?? throw new InvalidOperationException("O banco de teste ainda não subiu.");

    public Task<Cliente> NovoCliente(string nome = "Cliente de teste") => Principal.NovoCliente(nome);

    public HttpClient Operador => Principal.Operador;

    /// <summary>
    /// Outra instância da API no mesmo banco de dados, com ajustes (relógio, configuração). Sem
    /// o trabalhador de liquidação: só o da API principal leva os Pix para outros bancos.
    /// </summary>
    public Host Variante(TimeProvider? relogio = null, params (string Chave, string Valor)[] ajustes) =>
        new(Fabrica.WithWebHostBuilder(b =>
        {
            b.UseSetting("Spi:Trabalhador", "false");
            // O esquema já foi criado pela API principal; mudá-lo com transações rodando dá erro nelas.
            b.UseSetting("Armazenamento:AplicarEsquemaNaSubida", "false");
            foreach (var (chave, valor) in ajustes)
            {
                b.UseSetting(chave, valor);
            }
            if (relogio is not null)
            {
                b.ConfigureTestServices(s => s.AddSingleton(relogio));
            }
        }));

    public static string Token(string sub, IEnumerable<string>? papeis = null, string emissor = Emissor,
        string audiencia = Audiencia, DateTime? expira = null)
    {
        var claims = new List<Claim> { new("sub", sub) };
        claims.AddRange((papeis ?? []).Select(p => new Claim("roles", p)));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = emissor,
            Audience = audiencia,
            Subject = new ClaimsIdentity(claims),
            NotBefore = (expira ?? DateTime.UtcNow.AddMinutes(15)).AddHours(-1),
            Expires = expira ?? DateTime.UtcNow.AddMinutes(15),
            SigningCredentials = new SigningCredentials(Chave, SecurityAlgorithms.RsaSha256),
        });
    }

    private WebApplicationFactory<Program> Fabrica =>
        _fabrica ?? throw new InvalidOperationException("O banco de teste ainda não subiu.");

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        // Fila grande e paciência: os testes de concorrência disparam mil requisições de uma vez.
        var conexao = $"{_postgres.GetConnectionString()};Maximum Pool Size=40;Timeout=300;Command Timeout=300";
        _fabrica = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseEnvironment("Testes")
            .UseSetting("ConnectionStrings:Lastro", conexao)
            .UseSetting("Spi:Intervalo", "00:00:00.200")
            .UseSetting("Spi:Segredo", SegredoDoSpi)
            .UseSetting("Antifraude:Ligada", "false")
            .UseSetting("LimitesDeUso:PixPorMinuto", "1000000")
            .UseSetting("LimitesDeUso:ConsultasDeChavePorMinuto", "1000000")
            // Sem buscar chaves na internet: a chave pública dos testes é a do token.
            .ConfigureTestServices(s => s.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                o.Authority = null;
                o.TokenValidationParameters.ValidIssuer = Emissor;
                o.TokenValidationParameters.IssuerSigningKey = Chave;
            })));
        // Sobe a API principal já: é ela que cria o esquema, antes de qualquer variante.
        _ = _fabrica.Server;
        _principal = new Host(_fabrica);
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

/// <summary>Uma instância da API, e quem fala com ela.</summary>
public sealed class Host(WebApplicationFactory<Program> fabrica)
{
    private HttpClient? _operador;

    /// <summary>O "sub" do funcionário do banco desta instância.</summary>
    public string OperadorId { get; } = $"operador-{Guid.NewGuid():N}";

    /// <summary>Um funcionário do banco (papel "operador"): depósito, conferência e auditoria.</summary>
    public HttpClient Operador => _operador ??= Como(OperadorId, "operador");

    public HttpClient Anonimo() => fabrica.CreateClient();

    public HttpClient Como(string sub, params string[] papeis) => ComToken(BancoDeTeste.Token(sub, papeis));

    public HttpClient ComToken(string token)
    {
        var http = fabrica.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    /// <summary>Uma pessoa nova: faz login (ganha um token) e abre a conta dela.</summary>
    public async Task<Cliente> NovoCliente(string nome = "Cliente de teste")
    {
        var sub = $"pessoa-{Guid.NewGuid():N}";
        var http = Como(sub);
        var cpf = Api.NovoCpf();
        var resposta = await http.PostAsJsonAsync("/contas", new { nome, cpf });
        resposta.EnsureSuccessStatusCode();
        var conta = (await resposta.Content.ReadFromJsonAsync<Api.Conta>(BancoDeTeste.Json))!;
        return new Cliente(http, conta.Id, conta.Numero, cpf, sub);
    }
}

/// <summary>Uma pessoa logada, com a conta dela.</summary>
public sealed record Cliente(HttpClient Http, Guid Id, string Numero, string Cpf, string Usuario)
{
    public Task<HttpResponseMessage> Pix(Guid destino, decimal valor, string? chave = null, string? descricao = null) =>
        Http.Pix(Id, destino, valor, chave, descricao);

    public Task<HttpResponseMessage> Pix(Cliente destino, decimal valor, string? chave = null, string? descricao = null) =>
        Pix(destino.Id, valor, chave, descricao);

    public Task<HttpResponseMessage> PixPorChave(string chavePix, decimal valor, string? idempotencia = null) =>
        Api.Enviar(Http, "/pix", new { origem = Id, chave = chavePix, valor }, idempotencia ?? Api.NovaChave());

    public Task<decimal> Saldo(DateTimeOffset? em = null) => Http.Saldo(Id, em);

    public Task<JsonElement> Extrato() =>
        Http.GetFromJsonAsync<JsonElement>($"/contas/{Id}/extrato", TestContext.Current.CancellationToken);

    public Task<HttpResponseMessage> CadastrarChave(string tipo, string? valor = null) =>
        Http.PostAsJsonAsync($"/contas/{Id}/chaves", new { tipo, valor }, TestContext.Current.CancellationToken);
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

    public static async Task<HttpResponseMessage> Enviar(HttpClient http, string url, object corpo, string chave)
    {
        using var pedido = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(corpo) };
        pedido.Headers.Add("Idempotency-Key", chave);
        return await http.SendAsync(pedido);
    }
}
