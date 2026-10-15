using System.Text.Json.Serialization;
using Lastro.Api;
using Lastro.Api.Armazenamento;
using Lastro.Api.Operacoes;
using Lastro.Api.Seguranca;
using Lastro.Api.Spi;
using Lastro.Dominio;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<TratadorDeErros>();
builder.Services.AddValidation();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();

// Login: a API é um "resource server" OAuth. O token vem do provedor de identidade (no
// compose, um Keycloak com passkeys) e precisa ter sido emitido para ela (audiência).
var autenticacao = builder.Configuration.GetSection("Autenticacao");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.Authority = autenticacao["Emissor"];
    o.Audience = autenticacao["Audiencia"] ?? "lastro-api";
    o.RequireHttpsMetadata = autenticacao.GetValue("ExigirHttps", true);
    o.MapInboundClaims = false;
    o.TokenValidationParameters.ValidAlgorithms = ["RS256"];
    o.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
    o.TokenValidationParameters.NameClaimType = "sub";
    // No Keycloak, um mapeador de papéis do realm põe a lista em "roles".
    o.TokenValidationParameters.RoleClaimType = "roles";
});
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Politicas.Cliente, p => p.RequireAuthenticatedUser().RequireClaim("sub"))
    .AddPolicy(Politicas.Operador, p => p.RequireAuthenticatedUser().RequireRole("operador"));
builder.Services.AddLimitesDeUso(builder.Configuration);

builder.Services.AddArmazenamento(
    builder.Configuration.GetConnectionString("Lastro")
        ?? throw new InvalidOperationException("Defina ConnectionStrings:Lastro (PostgreSQL)."),
    aplicarEsquema: builder.Configuration.GetValue("Armazenamento:AplicarEsquemaNaSubida", true));
builder.Services.AddHostedService<CriarContasInternas>();
builder.Services.AddScoped<Cadastro>();
builder.Services.AddScoped<Movimentacoes>();
builder.Services.AddScoped<ChavesPix>();
builder.Services.AddScoped<Recebimentos>();
builder.Services.AddScoped<Donos>();
builder.Services.AddSingleton(builder.Configuration.GetValue("Antifraude:Ligada", true)
    ? PoliticaAntifraude.Padrao
    : PoliticaAntifraude.Desligada);

// O Banco Central de mentira (DICT e SPI). Num banco de verdade, aqui entraria o cliente da API dele.
builder.Services.AddSingleton<BancoCentralSimulado>();
builder.Services.AddSingleton<IDiretorioDeChaves>(s => s.GetRequiredService<BancoCentralSimulado>());
builder.Services.AddSingleton<ISpi>(s => s.GetRequiredService<BancoCentralSimulado>());
if (builder.Configuration.GetValue("Spi:Trabalhador", true))
{
    builder.Services.AddHostedService<LiquidacaoDePix>();
}

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference("/docs").AllowAnonymous();
}
app.MapEndpoints();
app.MapGet("/saude", () => TypedResults.Ok(new { status = "ok" })).AllowAnonymous().ExcludeFromDescription();

await app.RunAsync();

/// <summary>Visível para os testes de integração (WebApplicationFactory).</summary>
public partial class Program;
