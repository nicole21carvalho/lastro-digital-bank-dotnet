namespace Lastro.Dominio;

/// <summary>
/// Um lançamento de partidas dobradas: o mesmo valor sai de uma conta e entra em outra,
/// no mesmo instante. As duas metades são gravadas na mesma transação, ou nenhuma é.
/// </summary>
public sealed record Lancamento(ValorDebitado Debito, ValorCreditado Credito)
{
    /// <summary>Limite do campo "informações adicionais" do Pix.</summary>
    public const int TamanhoMaximoDoHistorico = 140;

    public Guid Id => Debito.LancamentoId;

    public Dinheiro Valor => Debito.Valor;

    /// <summary>A decisão: confere as regras e devolve os fatos a gravar. Não grava nada.</summary>
    public static Lancamento Transferir(
        Conta origem,
        Conta destino,
        Dinheiro valor,
        TipoDeLancamento tipo,
        string historico,
        Guid lancamentoId,
        DateTimeOffset em)
    {
        ArgumentNullException.ThrowIfNull(origem);
        ArgumentNullException.ThrowIfNull(destino);
        ArgumentNullException.ThrowIfNull(historico);

        if (!valor.Positivo)
        {
            throw new RegraVioladaException(Erros.ValorInvalido, "O valor precisa ser maior que zero.");
        }
        if (origem.Id == destino.Id)
        {
            throw new RegraVioladaException(Erros.MesmaConta, "A origem e o destino são a mesma conta.");
        }
        if (historico.Length > TamanhoMaximoDoHistorico)
        {
            throw new RegraVioladaException(
                Erros.HistoricoLongo, $"A descrição pode ter até {TamanhoMaximoDoHistorico} caracteres.");
        }
        if (origem.Tipo == TipoDeConta.Cliente && origem.Saldo < valor)
        {
            throw new RegraVioladaException(Erros.SaldoInsuficiente, "Saldo insuficiente.");
        }
        if (tipo == TipoDeLancamento.Pix && origem.Tipo == TipoDeConta.Cliente && PeriodoNoturno.Contem(em))
        {
            var limite = origem.LimiteNoturnoEm(em);
            var disponivel = limite - origem.UsadoNaNoite(em);
            if (valor > disponivel)
            {
                throw new RegraVioladaException(
                    Erros.LimiteNoturnoExcedido,
                    $"Das 20h às 6h, o limite de Pix é de {limite} por noite. Ainda dá para enviar "
                    + $"{(disponivel > Dinheiro.Zero ? disponivel : Dinheiro.Zero)}.");
            }
        }

        return new Lancamento(
            new ValorDebitado(origem.Id, lancamentoId, tipo, valor, destino.Id, historico, em),
            new ValorCreditado(destino.Id, lancamentoId, tipo, valor, origem.Id, historico, em));
    }
}
