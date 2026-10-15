namespace Lastro.Dominio;

/// <summary>
/// O período noturno do Pix: das 20h às 6h, no horário de Brasília. Nele, os Pix de pessoa
/// física têm um limite por noite (o padrão do Banco Central é R$ 1.000,00), e aumentar o
/// limite só vale depois de 24 horas, para um golpista não conseguir forçar o aumento na hora.
/// </summary>
public static class PeriodoNoturno
{
    /// <summary>Brasília não tem horário de verão desde 2019: UTC-3 o ano inteiro.</summary>
    public static readonly TimeSpan Brasilia = TimeSpan.FromHours(-3);

    public static readonly Dinheiro LimitePadrao = Dinheiro.DeReais(1_000m);

    public static readonly TimeSpan EsperaParaAumentar = TimeSpan.FromHours(24);

    private const int Comeca = 20;
    private const int Termina = 6;

    public static bool Contem(DateTimeOffset instante)
    {
        var hora = instante.ToOffset(Brasilia).Hour;
        return hora is >= Comeca or < Termina;
    }

    /// <summary>Quando começou a noite que contém o instante (às 2h de quarta, é 20h de terça).</summary>
    public static DateTimeOffset Inicio(DateTimeOffset instante)
    {
        if (!Contem(instante))
        {
            throw new ArgumentOutOfRangeException(nameof(instante), "O instante não está no período noturno.");
        }
        var local = instante.ToOffset(Brasilia);
        var dia = local.Hour >= Comeca ? local.Date : local.Date.AddDays(-1);
        return new DateTimeOffset(dia.AddHours(Comeca), Brasilia);
    }
}

/// <summary>Quanto já saiu em Pix numa noite (a que começou em <see cref="Inicio"/>).</summary>
public sealed record UsoNoturno(DateTimeOffset Inicio, Dinheiro Usado);

/// <summary>Um aumento de limite pedido, que só passa a valer em <see cref="ValeDesde"/>.</summary>
public sealed record LimitePendente(Dinheiro Valor, DateTimeOffset ValeDesde);
