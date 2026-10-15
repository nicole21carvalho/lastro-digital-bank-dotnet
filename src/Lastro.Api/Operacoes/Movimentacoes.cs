using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using JasperFx.Events;
using Lastro.Api.Armazenamento;
using Lastro.Api.Spi;
using Lastro.Dominio;
using Marten;

namespace Lastro.Api.Operacoes;

public sealed record PedidoDeMovimentacao(
    Guid Origem,
    Guid Destino,
    Dinheiro Valor,
    TipoDeLancamento Tipo,
    string Descricao,
    string ChaveDeIdempotencia,
    ChaveDeOutroBanco? ParaOutroBanco = null)
{
    /// <summary>
    /// A chave pertence à conta do cliente: no Pix, a de origem; no depósito, a que recebe
    /// (a origem do depósito é a liquidação, a mesma para todo mundo).
    /// </summary>
    public Guid DonoDaChave => Tipo == TipoDeLancamento.Deposito ? Destino : Origem;
}

/// <summary>O que aconteceu: feita, recusada por uma regra, ou a repetição de uma resposta anterior.</summary>
public sealed record ResultadoDaMovimentacao(MovimentacaoFeita? Feita, RegraVioladaException? Recusa, bool Repetida);

/// <summary>
/// Move dinheiro entre duas contas. Tudo numa transação do PostgreSQL:
/// 1. trava as duas contas, sempre na ordem do id (duas transferências cruzadas, A→B e
///    B→A, não ficam esperando uma pela outra para sempre);
/// 2. confere a chave de idempotência (com as contas travadas, duas requisições iguais
///    ao mesmo tempo não passam juntas);
/// 3. decide pelas regras do domínio e grava débito, crédito e a resposta juntos.
/// </summary>
public sealed class Movimentacoes(IDocumentSession sessao, TimeProvider relogio, PoliticaAntifraude antifraude)
{
    /// <summary>Um span por movimentação, com o tipo e o resultado; nada do valor nem das contas.</summary>
    public async Task<ResultadoDaMovimentacao> Executar(PedidoDeMovimentacao pedido, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pedido);
        var tipo = pedido.ParaOutroBanco is null ? pedido.Tipo.ToString() : "PixParaOutroBanco";
        using var atividade = Telemetria.Fonte.StartActivity("movimentar");
        atividade?.SetTag("lastro.tipo", tipo);
        var resultado = "erro";
        try
        {
            var feito = await ExecutarNaTransacao(pedido, ct);
            resultado = feito.Recusa?.Codigo ?? "feito";
            atividade?.SetTag("lastro.repetida", feito.Repetida);
            if (feito is { Feita: { } movimentacao, Repetida: false })
            {
                Telemetria.Valores.Record((double)movimentacao.Valor, new KeyValuePair<string, object?>("lastro.tipo", tipo));
            }
            return feito;
        }
        catch (RegraVioladaException recusa)
        {
            resultado = recusa.Codigo;
            throw;
        }
        catch (Exception erro)
        {
            resultado = "erro";
            atividade?.SetStatus(ActivityStatusCode.Error, erro.GetType().Name);
            throw;
        }
        finally
        {
            atividade?.SetTag("lastro.resultado", resultado);
            Telemetria.Movimentacoes.Add(1,
                new KeyValuePair<string, object?>("lastro.tipo", tipo),
                new KeyValuePair<string, object?>("lastro.resultado", resultado));
        }
    }

    private async Task<ResultadoDaMovimentacao> ExecutarNaTransacao(PedidoDeMovimentacao pedido, CancellationToken ct)
    {
        var contas = new Dictionary<Guid, IEventStream<Conta>>();
        foreach (var id in new[] { pedido.Origem, pedido.Destino }.Distinct().Order())
        {
            contas[id] = await sessao.Events.FetchForExclusiveWriting<Conta>(id, ct);
        }
        var (origem, destino) = (Existente(contas[pedido.Origem]), Existente(contas[pedido.Destino]));
        // Conta interna não confere saldo (pode ficar negativa). Então nenhum pedido de fora pode
        // tirar dinheiro dela por Pix, nem mandar para ela: quem mexe nas internas é só o próprio
        // banco (depósito, liquidação e devolução). Para quem pergunta, ela nem existe.
        if (pedido.Tipo == TipoDeLancamento.Pix && origem.Tipo != TipoDeConta.Cliente)
        {
            throw new RegraVioladaException(Erros.ContaNaoEncontrada, "Conta não encontrada.");
        }
        if (pedido.ParaOutroBanco is null && destino.Tipo != TipoDeConta.Cliente)
        {
            throw new RegraVioladaException(Erros.ContaNaoEncontrada, "Conta não encontrada.");
        }

        // A mesma chave em contas diferentes não colide.
        var registroId = $"{pedido.DonoDaChave}:{pedido.ChaveDeIdempotencia}";
        var hash = Hash(pedido);
        var anterior = await sessao.LoadAsync<RegistroDeIdempotencia>(registroId, ct);
        if (anterior is not null)
        {
            if (anterior.Hash != hash)
            {
                throw new RegraVioladaException(
                    Erros.ChaveDeIdempotenciaReutilizada, "Esta chave já foi usada para outra operação.");
            }
            var recusa = anterior.Recusa is null
                ? null
                : new RegraVioladaException(anterior.Recusa, anterior.MotivoDaRecusa ?? "");
            return new ResultadoDaMovimentacao(anterior.Feita, recusa, Repetida: true);
        }

        var agora = relogio.GetUtcNow();
        Lancamento lancamento;
        try
        {
            lancamento = Lancamento.Transferir(
                origem, destino, pedido.Valor, pedido.Tipo, pedido.Descricao, Guid.CreateVersion7(), agora);
            if (pedido.Tipo == TipoDeLancamento.Pix)
            {
                Antifraude.Avaliar(origem, pedido.Valor, agora, antifraude);
            }
        }
        catch (RegraVioladaException recusa)
        {
            // A recusa também fica registrada: a mesma chave sempre devolve a mesma resposta.
            sessao.Insert(new RegistroDeIdempotencia(registroId, hash, null, recusa.Codigo, recusa.Message, agora));
            await sessao.SaveChangesAsync(ct);
            return new ResultadoDaMovimentacao(null, recusa, Repetida: false);
        }

        var paraFora = pedido.ParaOutroBanco is { } externa
            ? new PixParaFora(EndToEndId.Gerar(Participantes.IspbDoLastro, agora), externa.Instituicao,
                externa.Chave.Mascarada, SituacaoDoPix.EmLiquidacao)
            : null;
        var feita = new MovimentacaoFeita(
            lancamento.Id, pedido.Tipo, lancamento.Valor.Reais, origem.Id, destino.Id, pedido.Descricao, agora, paraFora);
        contas[origem.Id].AppendOne(lancamento.Debito);
        contas[destino.Id].AppendOne(lancamento.Credito);
        if (paraFora is not null && pedido.ParaOutroBanco is { } outro)
        {
            // A caixa de saída: o Pix a enviar nasce junto com o débito, na mesma transação.
            sessao.Insert(new PixEmLiquidacao(
                paraFora.EndToEndId, lancamento.Id, origem.Id, outro.Ispb, outro.Instituicao, outro.Chave.Valor,
                lancamento.Valor.Centavos, SituacaoDoPix.EmLiquidacao, 0, agora, null, agora, null));
        }
        sessao.Insert(new RegistroDeIdempotencia(registroId, hash, feita, null, null, agora));
        await sessao.SaveChangesAsync(ct);
        return new ResultadoDaMovimentacao(feita, null, Repetida: false);
    }

    private static Conta Existente(IEventStream<Conta> stream) =>
        stream.Aggregate ?? throw new RegraVioladaException(Erros.ContaNaoEncontrada, "Conta não encontrada.");

    private static string Hash(PedidoDeMovimentacao p) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{p.Tipo}|{p.Origem}|{p.Destino}|{p.Valor.Centavos}|{p.Descricao}|{p.ParaOutroBanco?.Ispb}|{p.ParaOutroBanco?.Chave.Valor}")));
}
