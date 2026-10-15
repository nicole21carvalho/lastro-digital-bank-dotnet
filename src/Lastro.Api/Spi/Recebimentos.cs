using System.Security.Cryptography;
using System.Text;
using JasperFx.Events;
using Lastro.Api.Armazenamento;
using Lastro.Api.Operacoes;
using Lastro.Dominio;
using Marten;

namespace Lastro.Api.Spi;

public sealed record Pagador(string Nome, string Instituicao);

/// <summary>O aviso do SPI: chegou um Pix de outro banco para uma chave do Lastro.</summary>
public sealed record AvisoDePix(string EndToEndId, string Chave, decimal Valor, Pagador Pagador);

public sealed record PixCreditado(Guid LancamentoId, bool Repetido);

/// <summary>
/// Confere que o aviso veio mesmo do SPI: HMAC-SHA256 do "data.corpo" com o segredo
/// combinado, comparado em tempo constante (sem vazar, pelo tempo de resposta, quantos
/// caracteres acertou). A data assinada impede reaproveitar um aviso antigo.
/// </summary>
public static class AssinaturaDoSpi
{
    public const string Cabecalho = "X-Spi-Assinatura";
    public const string CabecalhoDaData = "X-Spi-Data";
    public static readonly TimeSpan Tolerancia = TimeSpan.FromMinutes(5);

    public static string Calcular(string segredo, string data, string corpo) =>
        "sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(segredo), Encoding.UTF8.GetBytes($"{data}.{corpo}")));

    public static bool Confere(string segredo, string? data, string? assinatura, string corpo, DateTimeOffset agora)
    {
        if (data is null || assinatura is null || !long.TryParse(data, out var segundos))
        {
            return false;
        }
        if ((agora - DateTimeOffset.FromUnixTimeSeconds(segundos)).Duration() > Tolerancia)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Calcular(segredo, data, corpo)), Encoding.ASCII.GetBytes(assinatura));
    }
}

/// <summary>Credita um Pix vindo de outro banco: o dinheiro entra pela liquidação.</summary>
public sealed class Recebimentos(IDocumentSession sessao, ChavesPix dict, TimeProvider relogio)
{
    public async Task<PixCreditado> Receber(AvisoDePix aviso, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(aviso);
        if (!EndToEndId.Valido(aviso.EndToEndId))
        {
            throw new RegraVioladaException(Erros.ValorInvalido, "EndToEndId inválido.");
        }
        var valor = Dinheiro.DeReais(aviso.Valor);
        var destino = await dict.ContaInterna(aviso.Chave, ct);

        var contas = new Dictionary<Guid, IEventStream<Conta>>();
        foreach (var id in new[] { ContasInternas.Liquidacao, destino }.Order())
        {
            contas[id] = await sessao.Events.FetchForExclusiveWriting<Conta>(id, ct);
        }
        // Com a liquidação travada, dois avisos iguais ao mesmo tempo passam um de cada vez.
        if (await sessao.LoadAsync<PixRecebidoRegistro>(aviso.EndToEndId, ct) is { } anterior)
        {
            return new PixCreditado(anterior.LancamentoId, Repetido: true);
        }

        var agora = relogio.GetUtcNow();
        var historico = $"Pix de {aviso.Pagador.Nome.Trim()} ({aviso.Pagador.Instituicao.Trim()})";
        var lancamento = Lancamento.Transferir(
            contas[ContasInternas.Liquidacao].Aggregate!, contas[destino].Aggregate!, valor, TipoDeLancamento.PixRecebido,
            historico[..Math.Min(historico.Length, Lancamento.TamanhoMaximoDoHistorico)], Guid.CreateVersion7(), agora);
        contas[ContasInternas.Liquidacao].AppendOne(lancamento.Debito);
        contas[destino].AppendOne(lancamento.Credito);
        sessao.Insert(new PixRecebidoRegistro(aviso.EndToEndId, lancamento.Id, destino, agora));
        await sessao.SaveChangesAsync(ct);
        return new PixCreditado(lancamento.Id, Repetido: false);
    }
}
