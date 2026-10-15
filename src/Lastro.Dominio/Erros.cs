namespace Lastro.Dominio;

/// <summary>Uma regra do banco impediu a operação. O código vira o "type" do erro na API.</summary>
public sealed class RegraVioladaException(string codigo, string mensagem) : Exception(mensagem)
{
    public string Codigo { get; } = codigo;
}

/// <summary>Códigos estáveis: o app do cliente pode decidir o que mostrar por eles.</summary>
public static class Erros
{
    public const string ValorInvalido = "valor-invalido";
    public const string SaldoInsuficiente = "saldo-insuficiente";
    public const string MesmaConta = "mesma-conta";
    public const string HistoricoLongo = "historico-longo";
    public const string CpfInvalido = "cpf-invalido";
    public const string CpfJaCadastrado = "cpf-ja-cadastrado";
    public const string ContaNaoEncontrada = "conta-nao-encontrada";
    public const string ChaveDeIdempotenciaReutilizada = "chave-de-idempotencia-reutilizada";
    public const string ChaveDeIdempotenciaInvalida = "chave-de-idempotencia-invalida";
    public const string ContaOcupada = "conta-ocupada";
}
