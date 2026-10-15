using Lastro.Dominio;

namespace Lastro.Api.Armazenamento;

/// <summary>
/// Dados pessoais do titular. Ficam num documento, e não nos eventos: o event store é
/// imutável, e a LGPD exige poder anonimizar. Os eventos só guardam o id do titular.
/// </summary>
public sealed record Titular(Guid Id, string Nome, string Cpf);

/// <summary>Uma linha do extrato (modelo de leitura do CQRS), gerada dos eventos na mesma transação.</summary>
public sealed record LinhaDoExtrato(
    Guid Id,
    Guid ContaId,
    Guid LancamentoId,
    TipoDeLancamento Tipo,
    long Centavos,
    Guid Contraparte,
    string Historico,
    DateTimeOffset Em,
    long Sequencia);

/// <summary>
/// O que já foi respondido para uma chave de idempotência. Gravado na mesma transação do
/// lançamento: ou os dois existem, ou nenhum.
/// </summary>
public sealed record RegistroDeIdempotencia(
    string Id,
    string Hash,
    MovimentacaoFeita? Feita,
    string? Recusa,
    string? MotivoDaRecusa,
    DateTimeOffset Em);

public sealed record MovimentacaoFeita(
    Guid LancamentoId,
    TipoDeLancamento Tipo,
    decimal Valor,
    Guid Origem,
    Guid Destino,
    string Descricao,
    DateTimeOffset Em);
