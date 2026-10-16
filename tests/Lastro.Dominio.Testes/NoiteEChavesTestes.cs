using FsCheck;
using FsCheck.Xunit;

namespace Lastro.Dominio.Testes;

public class PeriodoNoturnoTestes
{
    private static DateTimeOffset Brasilia(int dia, int hora, int minuto = 0) =>
        new(2026, 10, dia, hora, minuto, 0, PeriodoNoturno.Brasilia);

    [Theory]
    [InlineData(19, 59, false)]
    [InlineData(20, 0, true)]
    [InlineData(23, 30, true)]
    [InlineData(2, 0, true)]
    [InlineData(5, 59, true)]
    [InlineData(6, 0, false)]
    [InlineData(12, 0, false)]
    public void Noite_vai_das_20h_as_6h_de_brasilia(int hora, int minuto, bool noite) =>
        Assert.Equal(noite, PeriodoNoturno.Contem(Brasilia(15, hora, minuto)));

    [Fact]
    public void Horario_vem_de_brasilia_mesmo_com_o_instante_em_utc()
    {
        // 23h UTC são 20h em Brasília.
        Assert.True(PeriodoNoturno.Contem(new DateTimeOffset(2026, 10, 15, 23, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void A_madrugada_pertence_a_noite_que_comecou_na_vespera() =>
        Assert.Equal(Brasilia(14, 20), PeriodoNoturno.Inicio(Brasilia(15, 2)));
}

public class LimiteNoturnoTestes
{
    private static readonly DateTimeOffset Noite = new(2026, 10, 15, 21, 0, 0, PeriodoNoturno.Brasilia);

    private static (Conta Ana, Conta Bia) Contas() =>
        (LancamentoTestes.NovaConta(saldo: 1_000_000), LancamentoTestes.NovaConta());

    private static Conta Pix(Conta origem, Conta destino, decimal reais, DateTimeOffset em) =>
        origem.Aplicar(Lancamento.Transferir(
            origem, destino, Dinheiro.DeReais(reais), TipoDeLancamento.Pix, "", Guid.CreateVersion7(), em).Debito);

    [Fact]
    public void A_noite_o_limite_e_por_noite_e_nao_por_pix()
    {
        var (ana, bia) = Contas();
        ana = Pix(ana, bia, 600m, Noite);
        ana = Pix(ana, bia, 400m, Noite.AddHours(1)); // chegou nos R$ 1.000,00

        var erro = Assert.Throws<RegraVioladaException>(() => Pix(ana, bia, 0.01m, Noite.AddHours(2)));
        Assert.Equal(Erros.LimiteNoturnoExcedido, erro.Codigo);
        Assert.Contains("R$ 0,00", erro.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_noite_seguinte_comeca_do_zero_e_de_dia_nao_ha_limite_noturno()
    {
        var (ana, bia) = Contas();
        ana = Pix(ana, bia, 1_000m, Noite);
        ana = Pix(ana, bia, 5_000m, Noite.AddHours(14)); // 11h do dia seguinte
        ana = Pix(ana, bia, 1_000m, Noite.AddDays(1)); // outra noite
        Assert.Equal(Dinheiro.DeReais(1_000m), ana.UsadoNaNoite(Noite.AddDays(1)));
    }

    [Fact]
    public void Aumento_do_limite_so_vale_depois_de_24_horas()
    {
        var (ana, bia) = Contas();
        ana = ana.Aplicar(ana.AlterarLimiteNoturno(Dinheiro.DeReais(5_000m), Noite));

        // Na mesma noite, quem forçar o aumento (num golpe, por exemplo) não consegue usar.
        Assert.Throws<RegraVioladaException>(() => Pix(ana, bia, 1_500m, Noite.AddHours(1)));
        _ = Pix(ana, bia, 1_500m, Noite.AddHours(24));
    }

    [Fact]
    public void Reducao_vale_na_hora_e_cancela_o_aumento_pendente()
    {
        var (ana, bia) = Contas();
        ana = ana.Aplicar(ana.AlterarLimiteNoturno(Dinheiro.DeReais(5_000m), Noite));
        ana = ana.Aplicar(ana.AlterarLimiteNoturno(Dinheiro.DeReais(200m), Noite.AddMinutes(5)));

        Assert.Null(ana.AumentoPendente);
        Assert.Throws<RegraVioladaException>(() => Pix(ana, bia, 200.01m, Noite.AddHours(30)));
    }

    [Fact]
    public void Limite_fora_da_faixa_e_recusado()
    {
        var ana = Contas().Ana;
        var erro = Assert.Throws<RegraVioladaException>(() => ana.AlterarLimiteNoturno(Dinheiro.DeReais(50_000.01m), Noite));
        Assert.Equal(Erros.LimiteInvalido, erro.Codigo);
    }

    [Fact]
    public void Deposito_a_noite_nao_conta_no_limite()
    {
        var liquidacao = LancamentoTestes.NovaConta(TipoDeConta.Interna);
        var ana = LancamentoTestes.NovaConta();
        var deposito = Lancamento.Transferir(
            liquidacao, ana, Dinheiro.DeReais(5_000m), TipoDeLancamento.Deposito, "", Guid.CreateVersion7(), Noite);
        Assert.Equal(Dinheiro.Zero, ana.Aplicar(deposito.Credito).UsadoNaNoite(Noite));
    }

    /// <summary>Para qualquer sequência de Pix na mesma noite, o que saiu nunca passa do limite.</summary>
    [Property(MaxTest = 300)]
    public void Numa_noite_nunca_sai_mais_que_o_limite(PositiveInt[] centavos, NonNegativeInt[] minutos)
    {
        var (ana, bia) = Contas();
        var saiu = Dinheiro.Zero;
        var recusados = 0;
        foreach (var (valor, minuto) in centavos.Zip(minutos))
        {
            var em = Noite.AddMinutes(minuto.Get % 500); // de 21h até antes das 6h
            try
            {
                ana = ana.Aplicar(Lancamento.Transferir(
                    ana, bia, new Dinheiro(valor.Get), TipoDeLancamento.Pix, "", Guid.CreateVersion7(), em).Debito);
                saiu += new Dinheiro(valor.Get);
            }
            catch (RegraVioladaException erro) when (erro.Codigo == Erros.LimiteNoturnoExcedido)
            {
                recusados++; // recusa esperada: passaria do limite da noite
            }
        }
        Assert.True(saiu <= PeriodoNoturno.LimitePadrao, $"saiu {saiu} (recusados: {recusados})");
    }
}

public class ChavePixTestes
{
    [Theory]
    [InlineData("123.456.789-09", TipoDeChave.Cpf, "12345678909")]
    [InlineData("  Ana.Souza@Exemplo.COM ", TipoDeChave.Email, "ana.souza@exemplo.com")]
    [InlineData("+55 (11) 91234-5678", TipoDeChave.Celular, "+5511912345678")]
    [InlineData("7D444840-9DC0-11D1-B245-5FFDCE74FAD2", TipoDeChave.Aleatoria, "7d444840-9dc0-11d1-b245-5ffdce74fad2")]
    public void Chave_digitada_vira_o_formato_do_dict(string texto, TipoDeChave tipo, string valor) =>
        Assert.Equal(new ChavePix(tipo, valor), ChavePix.Interpretar(texto));

    [Theory]
    [InlineData("11912345678")] // celular sem +55: no DICT, 11 números são CPF (e este não é válido)
    [InlineData("+55 11 2345-6789")] // fixo não é chave
    [InlineData("ana@")]
    [InlineData("")]
    public void Chave_invalida_e_recusada(string texto) =>
        Assert.Equal(Erros.ChavePixInvalida, Assert.Throws<RegraVioladaException>(() => ChavePix.Interpretar(texto)).Codigo);

    [Fact]
    public void Celular_sem_ddi_no_cadastro_ganha_o_55() =>
        Assert.Equal("+5511912345678", ChavePix.DeCelular("(11) 91234-5678").Valor);

    [Theory]
    [InlineData("123.456.789-09", "***.456.789-**")]
    [InlineData("ana.souza@exemplo.com", "a***@exemplo.com")]
    [InlineData("+55 11 91234-5678", "+5511*****5678")]
    public void Chave_aparece_mascarada_na_confirmacao(string texto, string mascarada) =>
        Assert.Equal(mascarada, ChavePix.Interpretar(texto).Mascarada);
}
