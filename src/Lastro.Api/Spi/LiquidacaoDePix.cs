using JasperFx;
using JasperFx.Events;
using Lastro.Api.Armazenamento;
using Lastro.Api.Operacoes;
using Lastro.Dominio;
using Marten;

namespace Lastro.Api.Spi;

/// <summary>
/// Leva os Pix da caixa de saída até o outro banco, de tempos em tempos. Para cada um:
/// envia (ou consulta, a partir da segunda vez) e decide pela saga do domínio. A chamada ao
/// SPI acontece fora da transação; o resultado é gravado depois, com o documento conferindo a
/// versão: se dois trabalhadores pegarem o mesmo Pix, só um conclui.
/// </summary>
public sealed partial class LiquidacaoDePix(
    IDocumentStore loja, ISpi spi, TimeProvider relogio, IConfiguration configuracao, ILogger<LiquidacaoDePix> log)
    : BackgroundService
{
    private readonly TimeSpan _intervalo = configuracao.GetValue("Spi:Intervalo", TimeSpan.FromSeconds(1));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var temporizador = new PeriodicTimer(_intervalo, relogio);
        do
        {
            try
            {
                await Rodada(stoppingToken);
            }
            catch (Exception erro) when (erro is not OperationCanceledException)
            {
                // Uma rodada que falha não derruba o trabalhador: a próxima tenta de novo.
                Falhou(log, erro);
            }
        }
        while (await temporizador.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task<int> Rodada(CancellationToken ct)
    {
        IReadOnlyList<PixEmLiquidacao> pendentes;
        await using (var consulta = loja.QuerySession())
        {
            var agora = relogio.GetUtcNow();
            pendentes = await consulta.Query<PixEmLiquidacao>()
                .Where(p => p.Situacao == SituacaoDoPix.EmLiquidacao && p.ProximaTentativa <= agora)
                .OrderBy(p => p.ProximaTentativa).Take(20).ToListAsync(ct);
        }
        foreach (var pix in pendentes)
        {
            await Processar(pix, ct);
        }
        return pendentes.Count;
    }

    private async Task Processar(PixEmLiquidacao pix, CancellationToken ct)
    {
        var tentativa = pix.Tentativas + 1;
        var resposta = await Perguntar(pix, tentativa, ct);
        var decisao = SagaDoPixExterno.Decidir(resposta, tentativa);

        await using var sessao = loja.LightweightSession();
        sessao.LastModifiedBy = "sistema:liquidacao"; // na auditoria, quem mexeu foi o trabalhador
        sessao.CorrelationId = pix.Id;
        var atual = await sessao.LoadAsync<PixEmLiquidacao>(pix.Id, ct);
        if (atual is not { Situacao: SituacaoDoPix.EmLiquidacao })
        {
            return; // outro trabalhador já concluiu
        }
        var agora = relogio.GetUtcNow();
        var valor = new Dinheiro(atual.Centavos);
        switch (decisao)
        {
            case SagaDoPixExterno.Decisao.Liquidar:
                await Lancar(sessao, ContasInternas.PixEmTransito, ContasInternas.Liquidacao, TipoDeLancamento.Liquidacao,
                    valor, $"Pix {atual.Id} liquidado", agora, ct);
                sessao.Store(atual with { Situacao = SituacaoDoPix.Liquidado, Tentativas = tentativa, ConcluidoEm = agora });
                break;
            case SagaDoPixExterno.Decisao.Devolver:
                var motivo = resposta == RespostaDoSpi.Rejeitado ? "Recusado pelo banco de destino" : "O banco de destino não recebeu";
                await Lancar(sessao, ContasInternas.PixEmTransito, atual.Origem, TipoDeLancamento.Devolucao,
                    valor, $"Pix devolvido: {motivo}", agora, ct);
                sessao.Store(atual with
                {
                    Situacao = SituacaoDoPix.Devolvido, Tentativas = tentativa, Motivo = motivo, ConcluidoEm = agora,
                });
                break;
            default:
                sessao.Store(atual with
                {
                    Tentativas = tentativa, ProximaTentativa = agora + SagaDoPixExterno.Espera(tentativa),
                });
                break;
        }
        try
        {
            await sessao.SaveChangesAsync(ct);
            if (decisao != SagaDoPixExterno.Decisao.TentarDeNovo)
            {
                Telemetria.PixParaOutroBanco.Add(1, new KeyValuePair<string, object?>(
                    "lastro.desfecho", decisao == SagaDoPixExterno.Decisao.Liquidar ? "liquidado" : "devolvido"));
            }
        }
        catch (ConcurrencyException)
        {
            // Outro trabalhador gravou antes: a transação inteira (lançamento inclusive) foi desfeita.
        }
    }

    /// <summary>Na primeira vez envia; depois consulta, e só reenvia se o SPI nunca recebeu.</summary>
    private async Task<RespostaDoSpi> Perguntar(PixEmLiquidacao pix, int tentativa, CancellationToken ct)
    {
        var saindo = new PixSaindo(pix.Id, pix.IspbDestino, pix.Chave, new Dinheiro(pix.Centavos));
        try
        {
            var resposta = pix.Tentativas == 0 ? await spi.Enviar(saindo, ct) : await spi.Consultar(pix.Id, ct);
            return resposta == RespostaDoSpi.Desconhecido && tentativa < SagaDoPixExterno.MaximoDeTentativas
                ? await spi.Enviar(saindo, ct)
                : resposta;
        }
        catch (Exception erro) when (erro is HttpRequestException or TimeoutException
            || (erro is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // Rede ou tempo esgotado: não dá para saber se o outro banco recebeu.
            return RespostaDoSpi.Indefinida;
        }
    }

    private static async Task Lancar(
        IDocumentSession sessao, Guid de, Guid para, TipoDeLancamento tipo, Dinheiro valor, string historico,
        DateTimeOffset em, CancellationToken ct)
    {
        var contas = new Dictionary<Guid, IEventStream<Conta>>();
        foreach (var id in new[] { de, para }.Order())
        {
            contas[id] = await sessao.Events.FetchForExclusiveWriting<Conta>(id, ct);
        }
        var lancamento = Lancamento.Transferir(
            contas[de].Aggregate!, contas[para].Aggregate!, valor, tipo, historico, Guid.CreateVersion7(), em);
        contas[de].AppendOne(lancamento.Debito);
        contas[para].AppendOne(lancamento.Credito);
    }

    [LoggerMessage(LogLevel.Error, "Falha numa rodada da liquidação de Pix")]
    private static partial void Falhou(ILogger log, Exception erro);
}
