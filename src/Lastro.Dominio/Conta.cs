namespace Lastro.Dominio;

/// <summary>
/// O estado de uma conta, que nunca é gravado direto: sai dos eventos, aplicados em ordem.
/// O saldo é créditos menos débitos. Como todo lançamento debita e credita o mesmo valor,
/// a soma dos saldos de todas as contas (as internas do banco inclusive) é sempre zero.
/// </summary>
public sealed record Conta(Guid Id, string Numero, TipoDeConta Tipo, Guid? TitularId, Dinheiro Saldo)
{
    public static Conta Criar(ContaAberta aberta)
    {
        ArgumentNullException.ThrowIfNull(aberta);
        return new(aberta.ContaId, aberta.Numero, aberta.Tipo, aberta.TitularId, Dinheiro.Zero);
    }

    public Conta Aplicar(ValorCreditado credito)
    {
        ArgumentNullException.ThrowIfNull(credito);
        return this with { Saldo = Saldo + credito.Valor };
    }

    public Conta Aplicar(ValorDebitado debito)
    {
        ArgumentNullException.ThrowIfNull(debito);
        return this with { Saldo = Saldo - debito.Valor };
    }

    /// <summary>Reconstrói a conta a partir do histórico (o mesmo que o event store faz).</summary>
    public static Conta Reconstruir(IEnumerable<object> eventos)
    {
        ArgumentNullException.ThrowIfNull(eventos);
        Conta? conta = null;
        foreach (var evento in eventos)
        {
            conta = evento switch
            {
                ContaAberta aberta => Criar(aberta),
                ValorCreditado credito => Exigir(conta).Aplicar(credito),
                ValorDebitado debito => Exigir(conta).Aplicar(debito),
                _ => conta,
            };
        }
        return Exigir(conta);
    }

    private static Conta Exigir(Conta? conta) =>
        conta ?? throw new InvalidOperationException("O histórico precisa começar pela abertura da conta.");
}
