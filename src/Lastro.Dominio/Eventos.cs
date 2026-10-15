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

    /// <summary>O Pix para outro banco foi liquidado: o dinheiro sai da conta de trânsito para a liquidação.</summary>
    Liquidacao,

    /// <summary>O outro banco recusou: o dinheiro volta da conta de trânsito para o cliente.</summary>
    Devolucao,

    /// <summary>Pix vindo de outro banco: entra pela liquidação.</summary>
    PixRecebido,
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

/// <summary>O cliente mudou o limite noturno. Se foi um aumento, só vale a partir de <c>ValeDesde</c>.</summary>
public sealed record LimiteNoturnoAlterado(Guid ContaId, Dinheiro Limite, DateTimeOffset ValeDesde, DateTimeOffset Em);

/// <summary>Metade de um lançamento: sempre há um <see cref="ValorCreditado"/> com o mesmo id e valor.</summary>
public sealed record ValorDebitado(
    Guid ContaId,
    Guid LancamentoId,
    TipoDeLancamento Tipo,
    Dinheiro Valor,
    Guid Contraparte,
    string Historico,
    DateTimeOffset Em);
