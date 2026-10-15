namespace Lastro.Dominio.Testes;

public class DinheiroTestes
{
    [Fact]
    public void Centavos_somam_exato_sem_erro_de_ponto_flutuante() =>
        Assert.Equal(Dinheiro.DeReais(0.3m), Dinheiro.DeReais(0.1m) + Dinheiro.DeReais(0.2m));

    [Fact]
    public void Fracao_de_centavo_e_recusada()
    {
        var erro = Assert.Throws<RegraVioladaException>(() => Dinheiro.DeReais(10.001m));
        Assert.Equal(Erros.ValorInvalido, erro.Codigo);
    }

    [Theory]
    [InlineData(0, "R$ 0,00")]
    [InlineData(5, "R$ 0,05")]
    [InlineData(123456, "R$ 1.234,56")]
    [InlineData(100000000, "R$ 1.000.000,00")]
    [InlineData(-500, "-R$ 5,00")]
    [InlineData(long.MinValue, "-R$ 92.233.720.368.547.758,08")]
    public void Formato_brasileiro_sem_depender_da_cultura(long centavos, string esperado) =>
        Assert.Equal(esperado, new Dinheiro(centavos).ToString());

    [Fact]
    public void Soma_que_estoura_o_limite_falha_em_vez_de_virar_negativa() =>
        Assert.Throws<OverflowException>(() => new Dinheiro(long.MaxValue) + new Dinheiro(1));
}

public class CpfTestes
{
    [Theory]
    [InlineData("123.456.789-09")]
    [InlineData("12345678909")]
    public void Cpf_valido_fica_so_com_os_numeros(string texto) =>
        Assert.Equal("12345678909", Cpf.Criar(texto).Numeros);

    [Theory]
    [InlineData("123.456.789-08")] // dígito verificador errado
    [InlineData("111.111.111-11")] // passa na conta, mas não existe
    [InlineData("1234567890")] // faltando um número
    [InlineData("")]
    public void Cpf_invalido_e_recusado(string texto)
    {
        var erro = Assert.Throws<RegraVioladaException>(() => Cpf.Criar(texto));
        Assert.Equal(Erros.CpfInvalido, erro.Codigo);
    }

    [Fact]
    public void Texto_do_cpf_sai_sempre_mascarado()
    {
        var cpf = Cpf.Criar("123.456.789-09");
        Assert.Equal("***.456.789-**", cpf.ToString());
        Assert.DoesNotContain("12345678909", $"{cpf}");
    }
}
