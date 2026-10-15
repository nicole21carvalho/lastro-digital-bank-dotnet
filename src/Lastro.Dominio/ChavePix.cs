using System.Text.RegularExpressions;

namespace Lastro.Dominio;

public enum TipoDeChave
{
    Cpf,
    Email,
    Celular,
    Aleatoria,
}

/// <summary>
/// Uma chave Pix, já no formato do DICT (o diretório de chaves do Banco Central): CPF só
/// com números, e-mail em minúsculas, celular em +55DDNNNNNNNNN e chave aleatória como UUID.
/// </summary>
public sealed partial record ChavePix(TipoDeChave Tipo, string Valor)
{
    /// <summary>Pessoa física pode ter até 5 chaves por conta.</summary>
    public const int MaximoPorConta = 5;

    [GeneratedRegex(@"^[a-z0-9._%+-]+@[a-z0-9-]+(\.[a-z0-9-]+)*\.[a-z]{2,}$")]
    private static partial Regex FormatoDeEmail();

    // +55, DDD de 11 a 99 e celular de 9 dígitos começando por 9.
    [GeneratedRegex(@"^\+55[1-9][1-9]9\d{8}$")]
    private static partial Regex FormatoDeCelular();

    public static ChavePix DeCpf(Cpf cpf)
    {
        ArgumentNullException.ThrowIfNull(cpf);
        return new(TipoDeChave.Cpf, cpf.Numeros);
    }

    public static ChavePix DeEmail(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        var normalizado = email.Trim().ToLowerInvariant();
        return normalizado.Length <= 77 && FormatoDeEmail().IsMatch(normalizado)
            ? new(TipoDeChave.Email, normalizado)
            : throw Invalida("E-mail inválido.");
    }

    public static ChavePix DeCelular(string celular)
    {
        ArgumentNullException.ThrowIfNull(celular);
        var numeros = new string([.. celular.Where(char.IsAsciiDigit)]);
        var normalizado = "+" + (numeros.Length == 11 ? "55" + numeros : numeros);
        return FormatoDeCelular().IsMatch(normalizado)
            ? new(TipoDeChave.Celular, normalizado)
            : throw Invalida("Celular inválido: use DDD e número, como (11) 91234-5678.");
    }

    public static ChavePix Aleatoria() => new(TipoDeChave.Aleatoria, Guid.NewGuid().ToString());

    /// <summary>
    /// A chave que alguém digitou para pagar. Como no DICT, o celular precisa vir com +55:
    /// sem ele, 11 números são um CPF.
    /// </summary>
    public static ChavePix Interpretar(string texto)
    {
        ArgumentNullException.ThrowIfNull(texto);
        var limpo = texto.Trim();
        if (limpo.StartsWith('+'))
        {
            return DeCelular(limpo);
        }
        if (limpo.Contains('@', StringComparison.Ordinal))
        {
            return DeEmail(limpo);
        }
        if (Guid.TryParseExact(limpo, "D", out var aleatoria))
        {
            return new(TipoDeChave.Aleatoria, aleatoria.ToString());
        }
        try
        {
            return DeCpf(Cpf.Criar(limpo));
        }
        catch (RegraVioladaException)
        {
            throw Invalida("Chave Pix inválida: use CPF, e-mail, celular com +55 ou chave aleatória.");
        }
    }

    /// <summary>Para mostrar na tela de confirmação sem expor a chave inteira.</summary>
    public string Mascarada => Tipo switch
    {
        TipoDeChave.Cpf => $"***.{Valor[3..6]}.{Valor[6..9]}-**",
        TipoDeChave.Email => $"{Valor[0]}***{Valor[Valor.IndexOf('@', StringComparison.Ordinal)..]}",
        TipoDeChave.Celular => $"{Valor[..5]}*****{Valor[^4..]}",
        _ => Valor,
    };

    private static RegraVioladaException Invalida(string mensagem) => new(Erros.ChavePixInvalida, mensagem);
}
