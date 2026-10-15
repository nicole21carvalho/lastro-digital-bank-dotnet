using Lastro.Dominio;
using Marten.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Lastro.Api;

/// <summary>
/// Regra violada vira um erro no formato RFC 9457 (Problem Details), com o código estável
/// em "type". Qualquer outra exceção vira 500 sem detalhe nenhum para fora.
/// </summary>
public sealed class TratadorDeErros(IProblemDetailsService problemas) : IExceptionHandler
{
    public static int StatusDe(string codigo) => codigo switch
    {
        Erros.ContaNaoEncontrada or Erros.ChavePixNaoEncontrada => StatusCodes.Status404NotFound,
        Erros.ChaveDeIdempotenciaInvalida => StatusCodes.Status400BadRequest,
        Erros.CpfJaCadastrado or Erros.ChaveDeIdempotenciaReutilizada or Erros.ContaOcupada
            or Erros.ChavePixJaCadastrada or Erros.ContaJaAberta => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status422UnprocessableEntity,
    };

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var (codigo, titulo) = exception switch
        {
            RegraVioladaException regra => (regra.Codigo, regra.Message),
            // A trava da conta não saiu a tempo (muita operação na mesma conta ao mesmo tempo).
            StreamLockedException => (Erros.ContaOcupada, "A conta está ocupada com outra operação. Tente de novo."),
            _ => (null, null),
        };
        if (codigo is null)
        {
            return false;
        }

        var status = StatusDe(codigo);
        httpContext.Response.StatusCode = status;
        if (codigo == Erros.ContaOcupada)
        {
            httpContext.Response.Headers.RetryAfter = "1";
        }
        return await problemas.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Type = codigo, Title = titulo, Status = status },
        });
    }
}
