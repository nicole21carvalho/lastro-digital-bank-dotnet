using JasperFx.Events;
using Lastro.Dominio;
using Marten.Events.Aggregation;
using Marten.Events.Projections;

namespace Lastro.Api.Armazenamento;

/// <summary>
/// O estado da conta, refeito a partir dos eventos pelas mesmas funções do domínio. Roda
/// "inline": o saldo é atualizado na mesma transação dos eventos, então nunca fica atrás.
/// </summary>
public sealed partial class ContaProjection : SingleStreamProjection<Conta, Guid>
{
    public static Conta Create(ContaAberta aberta) => Conta.Criar(aberta);

    public static Conta Apply(ValorCreditado credito, Conta conta) => conta.Aplicar(credito);

    public static Conta Apply(ValorDebitado debito, Conta conta) => conta.Aplicar(debito);
}

/// <summary>Cada crédito ou débito vira uma linha do extrato (com sinal: débito é negativo).</summary>
public sealed partial class ExtratoProjection : EventProjection
{
    public static LinhaDoExtrato Create(IEvent<ValorCreditado> e) =>
        new(e.Id, e.Data.ContaId, e.Data.LancamentoId, e.Data.Tipo, e.Data.Valor.Centavos, e.Data.Contraparte,
            e.Data.Historico, e.Data.Em, e.Sequence);

    public static LinhaDoExtrato Create(IEvent<ValorDebitado> e) =>
        new(e.Id, e.Data.ContaId, e.Data.LancamentoId, e.Data.Tipo, -e.Data.Valor.Centavos, e.Data.Contraparte,
            e.Data.Historico, e.Data.Em, e.Sequence);
}
