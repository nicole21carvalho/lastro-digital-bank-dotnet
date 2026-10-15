using System.Diagnostics;
using System.Diagnostics.Metrics;
using Marten;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Lastro.Api;

/// <summary>
/// Os sinais do próprio banco: um span por movimentação e métricas de negócio. Nenhum valor
/// de uma transação vai para um span, e nenhum id de cliente vai para as métricas (que são
/// agregadas). Dá para ver a saúde do Pix sem expor o Pix de ninguém.
/// </summary>
public static class Telemetria
{
    public const string Nome = "Lastro";

    public static readonly ActivitySource Fonte = new(Nome);

    private static readonly Meter Medidor = new(Nome);

    /// <summary>Pix e depósitos, pelo resultado: "feito" ou o código da recusa.</summary>
    public static readonly Counter<long> Movimentacoes = Medidor.CreateCounter<long>(
        "lastro.movimentacoes", description: "Movimentações pedidas, pelo tipo e pelo resultado");

    /// <summary>Distribuição dos valores (em reais), sem ligar valor a pessoa.</summary>
    public static readonly Histogram<double> Valores = Medidor.CreateHistogram<double>(
        "lastro.movimentacoes.valor", unit: "BRL", description: "Valor das movimentações feitas");

    /// <summary>Como terminam as sagas de Pix para outros bancos.</summary>
    public static readonly Counter<long> PixParaOutroBanco = Medidor.CreateCounter<long>(
        "lastro.pix.outro_banco", description: "Pix para outros bancos, pelo desfecho");
}

public static class Observabilidade
{
    public static WebApplicationBuilder AddObservabilidade(this WebApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(logs =>
        {
            logs.IncludeFormattedMessage = true;
            logs.IncludeScopes = true;
        });
        var otel = builder.Services.AddOpenTelemetry()
            .WithTracing(traces => traces
                .AddSource(Telemetria.Nome, "Marten")
                .AddAspNetCoreInstrumentation(o => o.Filter = http =>
                    !http.Request.Path.StartsWithSegments("/saude") && !http.Request.Path.StartsWithSegments("/pronto"))
                .AddHttpClientInstrumentation()
                .AddNpgsql())
            .WithMetrics(metricas => metricas
                .AddMeter(Telemetria.Nome, "Marten")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation());
        // Com o endereço do coletor (o Aspire define sozinho), exporta por OTLP; sem ele, nada sai.
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        builder.Services.AddHealthChecks().AddCheck<BancoDeDadosResponde>("postgres", tags: ["pronto"]);
        return builder;
    }

    /// <summary>"/saude": o processo está de pé. "/pronto": e consegue falar com o banco de dados.</summary>
    public static WebApplication MapSaude(this WebApplication app)
    {
        app.MapHealthChecks("/saude", new() { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/pronto", new() { Predicate = c => c.Tags.Contains("pronto") }).AllowAnonymous();
        return app;
    }
}

internal sealed class BancoDeDadosResponde(IDocumentStore loja) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var consulta = loja.QuerySession();
            _ = await consulta.QueryAsync<int>("select 1", cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (NpgsqlException erro)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL fora do ar", erro);
        }
    }
}
