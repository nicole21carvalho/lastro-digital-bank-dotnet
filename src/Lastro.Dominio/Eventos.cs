namespace Lastro.Dominio;

/// <summary>Conta de cliente não fica negativa; conta interna do banco (liquidação, por exemplo) pode.</summary>
public enum TipoDeConta
{
    Cliente,
    Interna,
}

public enum TipoDeLancamento
{
    Deposito,
    Pix,
}

// Os fatos do livro-razão. São imutáveis e guardados para sempre no event store: o saldo,
// o extrato e a auditoria são todos derivados deles. Por isso não levam dado pessoal
// (nome, CPF): esses ficam no cadastro do titular, que pode ser anonimizado (LGPD).

public sealed record ContaAberta(Guid ContaId, string Numero, TipoDeConta Tipo, Guid? TitularId, DateTimeOffset Em);

/// <summary>Metade de um lançamento: sempre há um <see cref="ValorDebitado"/> com o mesmo id e valor.</summary>
public sealed record ValorCreditado(
    Guid ContaId,
    Guid LancamentoId,
    TipoDeLancamento Tipo,
    Dinheiro Valor,
    Guid Contraparte,
    string Historico,
    DateTimeOffset Em);

/// <summary>Metade de um lançamento: sempre há um <see cref="ValorCreditado"/> com o mesmo id e valor.</summary>
public sealed record ValorDebitado(
    Guid ContaId,
    Guid LancamentoId,
    TipoDeLancamento Tipo,
    Dinheiro Valor,
    Guid Contraparte,
    string Historico,
    DateTimeOffset Em);
