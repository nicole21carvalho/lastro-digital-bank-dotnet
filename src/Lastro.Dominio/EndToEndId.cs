using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Lastro.Dominio;

/// <summary>
/// O identificador de ponta a ponta de um Pix, no formato do Banco Central: "E", o ISPB do
/// banco que envia (8 dígitos), data e hora em UTC (aaaaMMddHHmm) e 11 caracteres aleatórios,
/// 32 no total. Cada Pix tem o seu, e os dois bancos usam o mesmo: reenviar com o mesmo id
/// nunca vira um segundo pagamento.
/// </summary>
public static partial class EndToEndId
{
    private const string Alfabeto = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    [GeneratedRegex("^E[0-9]{8}[0-9]{12}[A-Za-z0-9]{11}$")]
    private static partial Regex Formato();

    public static string Gerar(string ispb, DateTimeOffset em)
    {
        ArgumentNullException.ThrowIfNull(ispb);
        if (ispb.Length != 8 || !ispb.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("O ISPB tem 8 dígitos.", nameof(ispb));
        }
        var aleatorio = new string([.. Enumerable.Range(0, 11).Select(_ => Alfabeto[RandomNumberGenerator.GetInt32(Alfabeto.Length)])]);
        return $"E{ispb}{em.UtcDateTime.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)}{aleatorio}";
    }

    public static bool Valido(string? id) => id is not null && Formato().IsMatch(id);
}
