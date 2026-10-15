namespace Lastro.Dominio;

/// <summary>CPF validado pelos dígitos verificadores, guardado só com os 11 números.</summary>
public sealed record Cpf
{
    private Cpf(string numeros) => Numeros = numeros;

    public string Numeros { get; }

    /// <summary>Para logs e telas: "***.456.789-**". O número inteiro nunca vai para log.</summary>
    public string Mascarado => $"***.{Numeros[3..6]}.{Numeros[6..9]}-**";

    public static Cpf Criar(string texto)
    {
        ArgumentNullException.ThrowIfNull(texto);
        var numeros = new string([.. texto.Where(char.IsAsciiDigit)]);
        if (numeros.Length != 11 || numeros.Distinct().Count() == 1 || !DigitosConferem(numeros))
        {
            throw new RegraVioladaException(Erros.CpfInvalido, "CPF inválido.");
        }
        return new Cpf(numeros);
    }

    public override string ToString() => Mascarado;

    private static bool DigitosConferem(string numeros) =>
        Digito(numeros, 9) == numeros[9] - '0' && Digito(numeros, 10) == numeros[10] - '0';

    // Módulo 11: pesos decrescentes a partir de (posição + 1) sobre os dígitos anteriores.
    private static int Digito(string numeros, int posicao)
    {
        var soma = 0;
        for (var i = 0; i < posicao; i++)
        {
            soma += (numeros[i] - '0') * (posicao + 1 - i);
        }
        var resto = soma % 11;
        return resto < 2 ? 0 : 11 - resto;
    }
}
