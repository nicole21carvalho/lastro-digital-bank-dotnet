namespace Lastro.Dominio;

/// <summary>
/// Os limites contra os golpes mais comuns com Pix. Mudam por banco (e por ambiente), por
/// isso são uma política, e não constantes:
/// - conta nova: contas "laranja" são abertas para receber e repassar golpes logo em seguida,
///   então nas primeiras 24 horas o Pix tem um teto baixo;
/// - rajada: muitos Pix em poucos minutos é o padrão de quem esvazia uma conta invadida.
/// </summary>
public sealed record PoliticaAntifraude(bool Ligada, Dinheiro LimiteDaContaNova, int PixPorJanela)
{
    public static readonly PoliticaAntifraude Padrao = new(Ligada: true, Dinheiro.DeReais(500m), PixPorJanela: 10);

    public static readonly PoliticaAntifraude Desligada = Padrao with { Ligada = false };
}

/// <summary>Quantos Pix saíram na janela que começou em <see cref="Inicio"/> (janela fixa).</summary>
public sealed record JanelaDePix(DateTimeOffset Inicio, int Quantidade);

public static class Antifraude
{
    /// <summary>Quanto tempo uma conta é "nova".</summary>
    public static readonly TimeSpan PeriodoDaContaNova = TimeSpan.FromHours(24);

    /// <summary>A janela em que se contam os Pix de uma rajada.</summary>
    public static readonly TimeSpan Janela = TimeSpan.FromMinutes(10);

    /// <summary>Recusa o Pix se ele cair numa das regras. Não grava nada: só decide.</summary>
    public static void Avaliar(Conta origem, Dinheiro valor, DateTimeOffset em, PoliticaAntifraude politica)
    {
        ArgumentNullException.ThrowIfNull(origem);
        ArgumentNullException.ThrowIfNull(politica);
        if (!politica.Ligada || origem.Tipo != TipoDeConta.Cliente)
        {
            return;
        }
        if (em - origem.AbertaEm < PeriodoDaContaNova && origem.EnviadoNoPrimeiroDia + valor > politica.LimiteDaContaNova)
        {
            throw new RegraVioladaException(
                Erros.LimiteDaContaNova,
                $"Nas primeiras 24 horas da conta, os Pix somam no máximo {politica.LimiteDaContaNova}. "
                + "É uma proteção contra golpes com contas recém-abertas.");
        }
        if (origem.Rajada is { } janela && em - janela.Inicio < Janela && janela.Quantidade >= politica.PixPorJanela)
        {
            throw new RegraVioladaException(
                Erros.PixEmRajada,
                $"Foram {janela.Quantidade} Pix em menos de 10 minutos. Espere um pouco: "
                + "é uma proteção para o caso de a conta ter sido invadida.");
        }
    }

    /// <summary>A janela depois de mais um Pix: soma na atual ou começa uma nova.</summary>
    public static JanelaDePix Somar(JanelaDePix? janela, DateTimeOffset em) =>
        janela is not null && em - janela.Inicio < Janela
            ? janela with { Quantidade = janela.Quantidade + 1 }
            : new JanelaDePix(em, 1);
}
