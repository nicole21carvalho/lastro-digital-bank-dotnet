namespace Lastro.Dominio;

/// <summary>Número da conta com dígito verificador (módulo 11), como "100001-5". Agência única: 0001.</summary>
public static class NumeroDaConta
{
    public const string Agencia = "0001";

    public static string Formatar(long sequencial)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequencial);
        var numeros = sequencial.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"{numeros}-{Digito(numeros)}";
    }

    public static bool Valido(string numero)
    {
        ArgumentNullException.ThrowIfNull(numero);
        var partes = numero.Split('-');
        return partes.Length == 2 && partes[0].Length > 0 && partes[0].All(char.IsAsciiDigit)
            && partes[1] == Digito(partes[0]).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // Pesos 2, 3, ..., 9, 2, 3... da direita para a esquerda; resto 0 ou 1 vira dígito 0.
    private static int Digito(string numeros)
    {
        var soma = 0;
        for (var i = 0; i < numeros.Length; i++)
        {
            soma += (numeros[numeros.Length - 1 - i] - '0') * (2 + (i % 8));
        }
        var digito = 11 - (soma % 11);
        return digito >= 10 ? 0 : digito;
    }
}
