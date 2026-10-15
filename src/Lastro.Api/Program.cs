using System.Text.Json.Serialization;
using Lastro.Api;
using Lastro.Api.Armazenamento;
using Lastro.Api.Operacoes;
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
