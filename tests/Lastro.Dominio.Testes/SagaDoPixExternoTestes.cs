using static Lastro.Dominio.SagaDoPixExterno;

namespace Lastro.Dominio.Testes;

public class SagaDoPixExternoTestes
{
    [Theory]
    [InlineData(RespostaDoSpi.Liquidado, 1, Decisao.Liquidar)]
    [InlineData(RespostaDoSpi.Rejeitado, 1, Decisao.Devolver)]
    [InlineData(RespostaDoSpi.Desconhecido, 1, Decisao.TentarDeNovo)]
    [InlineData(RespostaDoSpi.Desconhecido, MaximoDeTentativas, Decisao.Devolver)]
    [InlineData(RespostaDoSpi.Indefinida, 1, Decisao.TentarDeNovo)]
    public void Cada_resposta_do_spi_leva_a_uma_decisao(RespostaDoSpi resposta, int tentativas, Decisao esperada) =>
        Assert.Equal(esperada, Decidir(resposta, tentativas));

    [Fact]
    public void Resposta_indefinida_nunca_devolve_o_dinheiro_sozinha()
    {
        // O outro banco pode ter recebido: devolver aqui seria pagar duas vezes.
        foreach (var tentativas in Enumerable.Range(1, 100))
        {
            Assert.Equal(Decisao.TentarDeNovo, Decidir(RespostaDoSpi.Indefinida, tentativas));
        }
    }

    [Fact]
    public void Espera_cresce_em_dobro_e_tem_teto()
    {
        Assert.Equal([1d, 2d, 4d, 8d], Enumerable.Range(1, 4).Select(t => Espera(t).TotalSeconds));
        Assert.Equal(Espera(11), Espera(50));
    }

    [Fact]
    public void EndToEndId_segue_o_formato_do_banco_central()
    {
        var id = EndToEndId.Gerar("90000000", new DateTimeOffset(2026, 10, 15, 14, 30, 0, TimeSpan.Zero));
        Assert.Equal(32, id.Length);
        Assert.StartsWith("E90000000202610151430", id, StringComparison.Ordinal);
        Assert.True(EndToEndId.Valido(id));
        Assert.False(EndToEndId.Valido("E123"));
        Assert.NotEqual(id, EndToEndId.Gerar("90000000", new DateTimeOffset(2026, 10, 15, 14, 30, 0, TimeSpan.Zero)));
    }
}
