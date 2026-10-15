namespace Lastro.Dominio;

public enum SituacaoDoPix
{
    /// <summary>Saiu da conta do cliente e está na conta de trânsito, esperando o outro banco.</summary>
    EmLiquidacao,

    /// <summary>O outro banco recebeu: o dinheiro saiu do Lastro.</summary>
    Liquidado,

    /// <summary>O outro banco recusou (ou nunca respondeu): o dinheiro voltou para o cliente.</summary>
    Devolvido,
}

/// <summary>A resposta do SPI (o sistema do Banco Central que liga os bancos) para um Pix.</summary>
public enum RespostaDoSpi
{
    Liquidado,
    Rejeitado,

    /// <summary>Não deu para saber (tempo esgotado, rede): consultar antes de tentar de novo.</summary>
    Indefinida,

    /// <summary>O SPI não conhece este EndToEndId: o Pix nunca chegou lá.</summary>
    Desconhecido,
}

/// <summary>
/// A saga de um Pix para outro banco, como funções puras. Garantia que importa: o dinheiro
/// só volta para o cliente quando é certo que o outro banco NÃO recebeu. Resposta indefinida
/// nunca vira devolução: primeiro se consulta o SPI pelo EndToEndId.
/// </summary>
public static class SagaDoPixExterno
{
    public const int MaximoDeTentativas = 6;

    /// <summary>Espera antes da próxima tentativa: 1 s, 2 s, 4 s, 8 s... (recuo exponencial).</summary>
    public static TimeSpan Espera(int tentativas) => TimeSpan.FromSeconds(Math.Pow(2, Math.Clamp(tentativas - 1, 0, 10)));

    /// <summary>O que fazer depois de uma resposta do SPI.</summary>
    public static Decisao Decidir(RespostaDoSpi resposta, int tentativas) => resposta switch
    {
        RespostaDoSpi.Liquidado => Decisao.Liquidar,
        RespostaDoSpi.Rejeitado => Decisao.Devolver,
        // Desconhecido depois de reenviar até o fim: o SPI nunca recebeu, então é seguro devolver.
        RespostaDoSpi.Desconhecido when tentativas >= MaximoDeTentativas => Decisao.Devolver,
        // Indefinida nunca devolve sozinha: o outro banco pode ter recebido. Continua consultando.
        _ => Decisao.TentarDeNovo,
    };

    public enum Decisao
    {
        Liquidar,
        Devolver,
        TentarDeNovo,
    }
}
