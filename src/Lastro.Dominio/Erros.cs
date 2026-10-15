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
    public const string LimiteNoturnoExcedido = "limite-noturno-excedido";
    public const string LimiteInvalido = "limite-invalido";
    public const string ChavePixInvalida = "chave-pix-invalida";
    public const string ChavePixJaCadastrada = "chave-pix-ja-cadastrada";
    public const string ChavePixNaoEncontrada = "chave-pix-nao-encontrada";
    public const string LimiteDeChaves = "limite-de-chaves";
    public const string ChaveDeOutroTitular = "chave-de-outro-titular";
    public const string LimiteDaContaNova = "limite-da-conta-nova";
    public const string PixEmRajada = "pix-em-rajada";
    public const string ContaJaAberta = "conta-ja-aberta";
}
