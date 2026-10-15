using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;

namespace Lastro.Api.Seguranca;

/// <summary>
/// Limites de uso por pessoa (pelo "sub" do token; sem login, pelo IP). Os valores vêm da
/// configuração: cada ambiente tem os seus.
/// - Pix: um robô com um token roubado não esvazia a conta em segundos;
/// - consulta de chave: a tela "quem recebe" mostra nome e CPF mascarados, e varrer chaves
///   para montar uma lista de pessoas é um ataque conhecido ao diretório do Pix.
/// </summary>
public static class LimitesDeUso
{
    public const string Pix = "pix";
    public const string ConsultaDeChave = "consulta-de-chave";

    public static IServiceCollection AddLimitesDeUso(this IServiceCollection services, IConfiguration configuracao)
    {
        var pixPorMinuto = configuracao.GetValue("LimitesDeUso:PixPorMinuto", 30);
        var consultasPorMinuto = configuracao.GetValue("LimitesDeUso:ConsultasDeChavePorMinuto", 20);

        return services.AddRateLimiter(opcoes =>
        {
            opcoes.AddPolicy(Pix, http => PorMinuto(http, pixPorMinuto));
            opcoes.AddPolicy(ConsultaDeChave, http => PorMinuto(http, consultasPorMinuto));
            opcoes.OnRejected = async (contexto, ct) =>
            {
                var resposta = contexto.HttpContext.Response;
                resposta.StatusCode = StatusCodes.Status429TooManyRequests;
                if (contexto.Lease.TryGetMetadata(MetadataName.RetryAfter, out var espera))
                {
                    resposta.Headers.RetryAfter = ((int)Math.Ceiling(espera.TotalSeconds)).ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                }
                await resposta.WriteAsJsonAsync(
                    new ProblemDetails
                    {
                        Type = "limite-de-uso",
                        Title = "Muitas operações seguidas. Espere um pouco e tente de novo.",
                        Status = StatusCodes.Status429TooManyRequests,
                    },
                    ct);
            };
        });
    }

    private static RateLimitPartition<string> PorMinuto(HttpContext http, int limite) =>
        RateLimitPartition.GetFixedWindowLimiter(
            http.User.FindFirstValue("sub") ?? $"ip:{http.Connection.RemoteIpAddress}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limite,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
}
