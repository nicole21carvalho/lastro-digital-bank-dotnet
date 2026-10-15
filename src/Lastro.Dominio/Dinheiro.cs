using System.Globalization;

namespace Lastro.Dominio;

/// <summary>
/// Valor em reais, guardado em centavos inteiros. Sem <c>double</c> e sem arredondamento:
/// R$ 0,10 + R$ 0,20 é exatamente R$ 0,30, e uma fração de centavo é recusada na entrada.
/// </summary>
public readonly record struct Dinheiro(long Centavos) : IComparable<Dinheiro>
{
    public static readonly Dinheiro Zero = new(0);

    public static Dinheiro DeReais(decimal reais)
    {
        var centavos = reais * 100m;
        if (centavos != decimal.Truncate(centavos))
        {
            throw new RegraVioladaException(Erros.ValorInvalido, "O valor tem mais de duas casas decimais.");
        }
        if (centavos is > long.MaxValue or < long.MinValue)
        {
            throw new RegraVioladaException(Erros.ValorInvalido, "Valor fora do limite.");
        }
        return new Dinheiro((long)centavos);
    }

    public decimal Reais => Centavos / 100m;

    public bool Positivo => Centavos > 0;

    public static Dinheiro operator +(Dinheiro a, Dinheiro b) => new(checked(a.Centavos + b.Centavos));

    public static Dinheiro operator -(Dinheiro a, Dinheiro b) => new(checked(a.Centavos - b.Centavos));

    public static Dinheiro operator -(Dinheiro a) => new(checked(-a.Centavos));

    public static bool operator <(Dinheiro a, Dinheiro b) => a.Centavos < b.Centavos;

    public static bool operator >(Dinheiro a, Dinheiro b) => a.Centavos > b.Centavos;

    public static bool operator <=(Dinheiro a, Dinheiro b) => a.Centavos <= b.Centavos;

    public static bool operator >=(Dinheiro a, Dinheiro b) => a.Centavos >= b.Centavos;

    public int CompareTo(Dinheiro other) => Centavos.CompareTo(other.Centavos);

    public static Dinheiro Somar(IEnumerable<Dinheiro> valores) =>
        valores.Aggregate(Zero, (total, valor) => total + valor);

    /// <summary>No formato brasileiro, sem depender da cultura da máquina: "R$ 1.234,56".</summary>
    public override string ToString()
    {
        var absoluto = Centavos < 0 ? (ulong)-(Centavos + 1) + 1 : (ulong)Centavos;
        var reais = (absoluto / 100).ToString("#,0", CultureInfo.InvariantCulture).Replace(',', '.');
        return $"{(Centavos < 0 ? "-" : "")}R$ {reais},{absoluto % 100:00}";
    }
}
