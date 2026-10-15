using System.Diagnostics;
using System.Security.Claims;
using Lastro.Api.Armazenamento;
using Lastro.Dominio;
using Marten;

namespace Lastro.Api.Seguranca;

public static class Politicas
{
    /// <summary>Cliente logado (token com "sub").</summary>
    public const string Cliente = "cliente";

    /// <summary>Funcionário do banco: depósito em caixa, conferência e auditoria.</summary>
    public const string Operador = "operador";
}

public static class Usuarios
{
    /// <summary>O "sub" do token: o id estável da pessoa no provedor de identidade.</summary>
    public static string Id(ClaimsPrincipal usuario)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        return usuario.FindFirstValue("sub") ?? throw new InvalidOperationException("Token sem \"sub\".");
    }
}

/// <summary>
/// Confere que a conta é de quem está logado. Conta de outra pessoa responde igual a uma
/// conta que não existe (404, e não 403): ninguém descobre quais contas existem tentando.
/// </summary>
public sealed class Donos(IQuerySession consulta)
{
    public async Task<Conta> Garantir(Guid contaId, ClaimsPrincipal usuario, CancellationToken ct)
    {
        var conta = await consulta.LoadAsync<Conta>(contaId, ct);
        var titular = conta?.TitularId is { } titularId ? await consulta.LoadAsync<Titular>(titularId, ct) : null;
        return titular is not null && titular.Usuario == Usuarios.Id(usuario)
            ? conta!
            : throw new RegraVioladaException(Erros.ContaNaoEncontrada, "Conta não encontrada.");
    }

    public async Task<Conta?> DoUsuario(ClaimsPrincipal usuario, CancellationToken ct)
    {
        var sub = Usuarios.Id(usuario);
        var titular = await consulta.Query<Titular>().FirstOrDefaultAsync(t => t.Usuario == sub, ct);
        return titular is null
            ? null
            : await consulta.Query<Conta>().FirstOrDefaultAsync(c => c.TitularId == titular.Id, ct);
    }
}

/// <summary>
/// Abre a sessão do Marten já dizendo quem está agindo: cada evento gravado leva o "sub" do
/// usuário e o id do trace da requisição nos metadados. É a trilha de auditoria, sem colocar
/// dado pessoal nos eventos do domínio.
/// </summary>
public sealed class SessaoComAutoria(IDocumentStore loja, IHttpContextAccessor contexto) : ISessionFactory
{
    public const string Sistema = "sistema";

    public IQuerySession QuerySession() => loja.QuerySession();

    public IDocumentSession OpenSession()
    {
        var sessao = loja.LightweightSession();
        var usuario = contexto.HttpContext?.User;
        sessao.LastModifiedBy = usuario?.FindFirstValue("sub") ?? Sistema;
        sessao.CorrelationId = Activity.Current?.TraceId.ToString() ?? contexto.HttpContext?.TraceIdentifier;
        return sessao;
    }
}
