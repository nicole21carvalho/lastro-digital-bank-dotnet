using System.Collections.Concurrent;
using Lastro.Dominio;

namespace Lastro.Api.Spi;

/// <summary>Os participantes do Pix. Os ISPBs aqui são fictícios.</summary>
public static class Participantes
{
    public const string IspbDoLastro = "90000000";
}

/// <summary>Quem recebe um Pix em outro banco, segundo o diretório de chaves (DICT).</summary>
public sealed record ChaveDeOutroBanco(ChavePix Chave, string Ispb, string Instituicao, string Nome, string Cpf);

/// <summary>Um Pix saindo do Lastro para outro banco.</summary>
public sealed record PixSaindo(string EndToEndId, string IspbDestino, string Chave, Dinheiro Valor);

/// <summary>O diretório de chaves do Banco Central, para as chaves que não são do Lastro.</summary>
public interface IDiretorioDeChaves
{
    Task<ChaveDeOutroBanco?> Consultar(ChavePix chave, CancellationToken ct);
}

/// <summary>O SPI: o sistema do Banco Central por onde os Pix passam de um banco para outro.</summary>
public interface ISpi
{
    /// <summary>Envia (ou reenvia: o EndToEndId faz o reenvio não pagar duas vezes).</summary>
    Task<RespostaDoSpi> Enviar(PixSaindo pix, CancellationToken ct);

    Task<RespostaDoSpi> Consultar(string endToEndId, CancellationToken ct);
}

/// <summary>
/// Um Banco Central de mentira, para rodar o Lastro sozinho. Os bancos e as pessoas são
/// fictícios. O comportamento depende da chave: "recusa" no e-mail é recusada pelo outro
/// banco, e "instavel" responde "não sei" duas vezes antes de liquidar.
/// </summary>
public sealed class BancoCentralSimulado : IDiretorioDeChaves, ISpi
{
    private static readonly ChaveDeOutroBanco[] Diretorio =
    [
        Externa("maria@banco-exemplo.example", "90000001", "Banco Exemplo", "Maria C. L."),
        Externa("joao.recusa@banco-teste.example", "90000002", "Banco Teste", "João P."),
        Externa("rita.instavel@banco-teste.example", "90000002", "Banco Teste", "Rita A. M."),
    ];

    private readonly ConcurrentDictionary<string, RespostaDoSpi> _recebidos = new();
    private readonly ConcurrentDictionary<string, int> _vezes = new();

    public Task<ChaveDeOutroBanco?> Consultar(ChavePix chave, CancellationToken ct) =>
        Task.FromResult(Diretorio.FirstOrDefault(d => d.Chave == chave));

    public Task<RespostaDoSpi> Enviar(PixSaindo pix, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pix);
        var vez = _vezes.AddOrUpdate(pix.EndToEndId, 1, (_, n) => n + 1);
        if (pix.Chave.Contains("instavel", StringComparison.Ordinal) && vez <= 2)
        {
            return Task.FromResult(RespostaDoSpi.Indefinida);
        }
        // O mesmo EndToEndId sempre recebe a mesma resposta: reenviar não paga de novo.
        var resposta = _recebidos.GetOrAdd(
            pix.EndToEndId,
            _ => pix.Chave.Contains("recusa", StringComparison.Ordinal) ? RespostaDoSpi.Rejeitado : RespostaDoSpi.Liquidado);
        return Task.FromResult(resposta);
    }

    public Task<RespostaDoSpi> Consultar(string endToEndId, CancellationToken ct) =>
        Task.FromResult(_recebidos.TryGetValue(endToEndId, out var resposta) ? resposta : RespostaDoSpi.Desconhecido);

    private static ChaveDeOutroBanco Externa(string email, string ispb, string instituicao, string nome) =>
        new(ChavePix.DeEmail(email), ispb, instituicao, nome, "***.***.***-**");
}
