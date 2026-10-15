using Lastro.Dominio;
using Marten;
using Marten.Exceptions;

namespace Lastro.Api.Operacoes;

/// <summary>Contas do próprio banco, com ids fixos. Podem ficar negativas.</summary>
public static class ContasInternas
{
    /// <summary>Por onde o dinheiro entra e sai do banco (depósitos, e no futuro o Pix de outros bancos).</summary>
    public static readonly Guid Liquidacao = new("0199e6a0-0000-7000-8000-000000000001");

    /// <summary>Onde fica o dinheiro de um Pix para outro banco enquanto ele não é liquidado.</summary>
    public static readonly Guid PixEmTransito = new("0199e6a0-0000-7000-8000-000000000002");

    public static readonly IReadOnlyList<(Guid Id, string Numero)> Todas = [(Liquidacao, "0-0"), (PixEmTransito, "0-1")];
}

/// <summary>Cria as contas internas na subida, se ainda não existirem.</summary>
public sealed class CriarContasInternas(IDocumentStore loja, TimeProvider relogio) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var (id, numero) in ContasInternas.Todas)
        {
            await using var sessao = loja.LightweightSession();
            sessao.LastModifiedBy = "sistema:inicio";
            if (await sessao.Events.FetchStreamStateAsync(id, cancellationToken) is not null)
            {
                continue;
            }
            sessao.Events.StartStream<Conta>(id, new ContaAberta(id, numero, TipoDeConta.Interna, null, relogio.GetUtcNow()));
            try
            {
                await sessao.SaveChangesAsync(cancellationToken);
            }
            catch (ExistingStreamIdCollisionException)
            {
                // Outra réplica subindo ao mesmo tempo criou primeiro: é o que se queria.
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
