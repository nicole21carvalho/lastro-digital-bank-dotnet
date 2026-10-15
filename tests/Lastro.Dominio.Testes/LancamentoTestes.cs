namespace Lastro.Dominio.Testes;

public class LancamentoTestes
{
    private static readonly DateTimeOffset Agora = new(2026, 10, 15, 14, 0, 0, TimeSpan.FromHours(-3));

    internal static Conta NovaConta(TipoDeConta tipo = TipoDeConta.Cliente, long saldo = 0) =>
        new(Guid.CreateVersion7(), "0001-1", tipo, null, new Dinheiro(saldo));

    private static Lancamento Pix(Conta origem, Conta destino, long centavos, string historico = "") =>
        Lancamento.Transferir(
            origem, destino, new Dinheiro(centavos), TipoDeLancamento.Pix, historico, Guid.CreateVersion7(), Agora);

    [Fact]
    public void Transferencia_debita_e_credita_o_mesmo_valor_no_mesmo_lancamento()
    {
        var (ana, bia) = (NovaConta(saldo: 10_000), NovaConta());
        var lancamento = Pix(ana, bia, 2_550, "Almoço");

        Assert.Equal(lancamento.Debito.LancamentoId, lancamento.Credito.LancamentoId);
        Assert.Equal(new Dinheiro(7_450), ana.Aplicar(lancamento.Debito).Saldo);
        Assert.Equal(new Dinheiro(2_550), bia.Aplicar(lancamento.Credito).Saldo);
        Assert.Equal(bia.Id, lancamento.Debito.Contraparte);
        Assert.Equal(ana.Id, lancamento.Credito.Contraparte);
    }

    [Fact]
    public void Conta_de_cliente_nao_fica_negativa_nem_por_um_centavo()
    {
        var erro = Assert.Throws<RegraVioladaException>(() => Pix(NovaConta(saldo: 999), NovaConta(), 1_000));
        Assert.Equal(Erros.SaldoInsuficiente, erro.Codigo);
    }

    [Fact]
    public void Pode_transferir_o_saldo_inteiro()
    {
        var ana = NovaConta(saldo: 1_000);
        Assert.Equal(Dinheiro.Zero, ana.Aplicar(Pix(ana, NovaConta(), 1_000).Debito).Saldo);
    }

    [Fact]
    public void Conta_interna_do_banco_pode_ficar_negativa()
    {
        // A liquidação fica negativa quando o dinheiro entra no banco: é a outra metade do depósito.
        var liquidacao = NovaConta(TipoDeConta.Interna);
        var deposito = Pix(liquidacao, NovaConta(), 50_000);
        Assert.Equal(new Dinheiro(-50_000), liquidacao.Aplicar(deposito.Debito).Saldo);
    }

    [Theory]
    [InlineData(0, Erros.ValorInvalido)]
    [InlineData(-100, Erros.ValorInvalido)]
    public void Valor_precisa_ser_positivo(long centavos, string codigo)
    {
        var erro = Assert.Throws<RegraVioladaException>(() => Pix(NovaConta(saldo: 1_000), NovaConta(), centavos));
        Assert.Equal(codigo, erro.Codigo);
    }

    [Fact]
    public void Nao_transfere_para_a_propria_conta()
    {
        var ana = NovaConta(saldo: 1_000);
        Assert.Equal(Erros.MesmaConta, Assert.Throws<RegraVioladaException>(() => Pix(ana, ana, 100)).Codigo);
    }

    [Fact]
    public void Descricao_tem_o_limite_do_pix()
    {
        var (ana, bia) = (NovaConta(saldo: 1_000), NovaConta());
        _ = Pix(ana, bia, 100, new string('x', 140));
        var erro = Assert.Throws<RegraVioladaException>(() => Pix(ana, bia, 100, new string('x', 141)));
        Assert.Equal(Erros.HistoricoLongo, erro.Codigo);
    }

    [Fact]
    public void Conferencia_acha_lancamento_com_uma_metade_so()
    {
        var (ana, bia) = (NovaConta(saldo: 1_000), NovaConta());
        var certo = Pix(ana, bia, 100);
        var quebrado = Pix(ana, bia, 200);

        var conferencia = LivroRazao.Conferir([certo.Debito, certo.Credito, quebrado.Debito]);

        Assert.False(conferencia.Fecha);
        Assert.Equal([quebrado.Id], conferencia.Desbalanceados);
    }
}
