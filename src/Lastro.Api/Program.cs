using System.Text.Json.Serialization;
using Lastro.Api;
using Lastro.Api.Armazenamento;
using Lastro.Api.Operacoes;
using Lastro.Api.Spi;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<TratadorDeErros>();
builder.Services.AddValidation();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddArmazenamento(builder.Configuration.GetConnectionString("Lastro")
    ?? throw new InvalidOperationException("Defina ConnectionStrings:Lastro (PostgreSQL)."));
builder.Services.AddHostedService<CriarContasInternas>();
builder.Services.AddScoped<Cadastro>();
builder.Services.AddScoped<Movimentacoes>();
builder.Services.AddScoped<ChavesPix>();
builder.Services.AddScoped<Recebimentos>();

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
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference("/docs");
}
app.MapEndpoints();
app.MapGet("/saude", () => TypedResults.Ok(new { status = "ok" })).ExcludeFromDescription();

await app.RunAsync();

/// <summary>Visível para os testes de integração (WebApplicationFactory).</summary>
public partial class Program;
