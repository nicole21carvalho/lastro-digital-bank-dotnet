namespace Lastro.Dominio.Testes;

public class AntifraudeTestes
{
    private static readonly DateTimeOffset Abertura = new(2026, 10, 15, 9, 0, 0, PeriodoNoturno.Brasilia);

    private static Conta ContaAbertaComSaldo(decimal reais)
    {
        var conta = Conta.Criar(new ContaAberta(Guid.CreateVersion7(), "100001-5", TipoDeConta.Cliente, null, Abertura));
        var liquidacao = LancamentoTestes.NovaConta(TipoDeConta.Interna);
        var deposito = Lancamento.Transferir(
            liquidacao, conta, Dinheiro.DeReais(reais), TipoDeLancamento.Deposito, "", Guid.CreateVersion7(), Abertura);
        return conta.Aplicar(deposito.Credito);
    }

    /// <summary>Um Pix que passa pelas regras do lançamento e do antifraude, como na API.</summary>
    private static Conta Pix(Conta origem, decimal reais, DateTimeOffset em, PoliticaAntifraude? politica = null)
    {
        var valor = Dinheiro.DeReais(reais);
        var lancamento = Lancamento.Transferir(
            origem, LancamentoTestes.NovaConta(), valor, TipoDeLancamento.Pix, "", Guid.CreateVersion7(), em);
        Antifraude.Avaliar(origem, valor, em, politica ?? PoliticaAntifraude.Padrao);
        return origem.Aplicar(lancamento.Debito);
    }

    [Fact]
    public void Conta_nova_tem_teto_de_pix_nas_primeiras_24_horas()
    {
        var ana = Pix(ContaAbertaComSaldo(5_000m), 400m, Abertura.AddHours(1));

        var erro = Assert.Throws<RegraVioladaException>(() => Pix(ana, 100.01m, Abertura.AddHours(2)));

        Assert.Equal(Erros.LimiteDaContaNova, erro.Codigo);
        _ = Pix(ana, 100m, Abertura.AddHours(2)); // até R$ 500,00 no total
        _ = Pix(ana, 2_000m, Abertura.AddHours(25)); // passou o primeiro dia
    }

    [Fact]
    public void Rajada_de_pix_e_barrada_ate_a_janela_passar()
    {
        var ana = ContaAbertaComSaldo(5_000m);
        var depoisDoPrimeiroDia = Abertura.AddDays(2);
        for (var i = 0; i < 10; i++)
        {
            ana = Pix(ana, 1m, depoisDoPrimeiroDia.AddSeconds(i * 30.0));
        }

        var erro = Assert.Throws<RegraVioladaException>(() => Pix(ana, 1m, depoisDoPrimeiroDia.AddMinutes(9)));

        Assert.Equal(Erros.PixEmRajada, erro.Codigo);
        _ = Pix(ana, 1m, depoisDoPrimeiroDia.AddMinutes(10)); // janela nova
    }

    [Fact]
    public void Politica_desligada_nao_barra_nada()
    {
        var ana = ContaAbertaComSaldo(5_000m);
        for (var i = 0; i < 30; i++)
        {
            ana = Pix(ana, 100m, Abertura.AddMinutes(i), PoliticaAntifraude.Desligada);
        }
        Assert.Equal(Dinheiro.DeReais(2_000m), ana.Saldo);
    }

    [Fact]
    public void Deposito_nao_conta_para_o_antifraude()
    {
        var ana = ContaAbertaComSaldo(5_000m);
        Assert.Equal(Dinheiro.Zero, ana.EnviadoNoPrimeiroDia);
        Assert.Null(ana.Rajada);
    }
}
