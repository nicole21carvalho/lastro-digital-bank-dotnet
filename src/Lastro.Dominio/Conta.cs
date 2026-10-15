namespace Lastro.Dominio;

/// <summary>
/// O estado de uma conta, que nunca é gravado direto: sai dos eventos, aplicados em ordem.
/// O saldo é créditos menos débitos. Como todo lançamento debita e credita o mesmo valor,
/// a soma dos saldos de todas as contas (as internas do banco inclusive) é sempre zero.
/// </summary>
public sealed record Conta(Guid Id, string Numero, TipoDeConta Tipo, Guid? TitularId, Dinheiro Saldo)
{
    /// <summary>Teto que o próprio cliente pode escolher para o limite noturno.</summary>
    public static readonly Dinheiro LimiteNoturnoMaximo = Dinheiro.DeReais(50_000m);

    public Dinheiro LimiteNoturno { get; init; } = PeriodoNoturno.LimitePadrao;

    public LimitePendente? AumentoPendente { get; init; }

    public UsoNoturno? Noite { get; init; }

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
        var conta = this with { Saldo = Saldo - debito.Valor };
        if (debito.Tipo != TipoDeLancamento.Pix || Tipo != TipoDeConta.Cliente || !PeriodoNoturno.Contem(debito.Em))
        {
            return conta;
        }
        // Soma o Pix à noite em que ele saiu; numa noite nova, a conta recomeça do zero.
        return conta with { Noite = new UsoNoturno(PeriodoNoturno.Inicio(debito.Em), UsadoNaNoite(debito.Em) + debito.Valor) };
    }

    public Conta Aplicar(LimiteNoturnoAlterado alterado)
    {
        ArgumentNullException.ThrowIfNull(alterado);
        // Uma redução vale na hora (e cancela um aumento que estava esperando).
        if (alterado.ValeDesde <= alterado.Em)
        {
            return this with { LimiteNoturno = alterado.Limite, AumentoPendente = null };
        }
        return this with
        {
            LimiteNoturno = LimiteNoturnoEm(alterado.Em),
            AumentoPendente = new LimitePendente(alterado.Limite, alterado.ValeDesde),
        };
    }

    /// <summary>O limite que vale num instante: um aumento pendente só conta depois da espera.</summary>
    public Dinheiro LimiteNoturnoEm(DateTimeOffset instante) =>
        AumentoPendente is { } aumento && instante >= aumento.ValeDesde ? aumento.Valor : LimiteNoturno;

    /// <summary>Quanto já saiu em Pix na noite que contém o instante (zero de dia).</summary>
    public Dinheiro UsadoNaNoite(DateTimeOffset instante) =>
        PeriodoNoturno.Contem(instante) && Noite is { } noite && noite.Inicio == PeriodoNoturno.Inicio(instante)
            ? noite.Usado
            : Dinheiro.Zero;

    /// <summary>A decisão de mudar o limite noturno: aumento espera 24 horas, redução vale na hora.</summary>
    public LimiteNoturnoAlterado AlterarLimiteNoturno(Dinheiro novo, DateTimeOffset agora)
    {
        if (Tipo != TipoDeConta.Cliente)
        {
            throw new InvalidOperationException("Conta interna não tem limite noturno.");
        }
        if (novo < Dinheiro.Zero || novo > LimiteNoturnoMaximo)
        {
            throw new RegraVioladaException(
                Erros.LimiteInvalido, $"O limite noturno vai de R$ 0,00 a {LimiteNoturnoMaximo}.");
        }
        var valeDesde = novo > LimiteNoturnoEm(agora) ? agora + PeriodoNoturno.EsperaParaAumentar : agora;
        return new LimiteNoturnoAlterado(Id, novo, valeDesde, agora);
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
                LimiteNoturnoAlterado alterado => Exigir(conta).Aplicar(alterado),
                _ => conta,
            };
        }
        return Exigir(conta);
    }

    private static Conta Exigir(Conta? conta) =>
        conta ?? throw new InvalidOperationException("O histórico precisa começar pela abertura da conta.");
}
