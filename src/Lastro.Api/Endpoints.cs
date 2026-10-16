using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lastro.Api.Armazenamento;
using Lastro.Api.Operacoes;
using Lastro.Api.Seguranca;
using Lastro.Api.Spi;
using Lastro.Dominio;
using Marten;
using Microsoft.AspNetCore.Mvc;

namespace Lastro.Api;

public sealed record AbrirContaPedido([Required, StringLength(120, MinimumLength = 2)] string Nome, [Required] string Cpf);

public sealed record DepositoPedido(decimal Valor);

/// <summary>Pix para uma chave (o normal) ou direto para o id de uma conta do Lastro.</summary>
public sealed record PixPedido(Guid Origem, Guid? Destino, string? Chave, decimal Valor, [StringLength(140)] string? Descricao);

public sealed record ChavePedido(TipoDeChave Tipo, string? Valor);

public sealed record LimitePedido(decimal Valor);

public sealed record AumentoResposta(decimal Valor, DateTimeOffset ValeDesde);

public sealed record LimiteResposta(decimal LimiteNoturno, AumentoResposta? AumentoPendente);

public sealed record ContaResposta(
    Guid Id, string Agencia, string Numero, TipoDeConta Tipo, decimal Saldo, LimiteResposta Limites);

public sealed record SaldoResposta(Guid ContaId, decimal Saldo, DateTimeOffset? Em);

public sealed record LinhaResposta(
    Guid LancamentoId, TipoDeLancamento Tipo, decimal Valor, Guid Contraparte, string Descricao, DateTimeOffset Em);

public sealed record ExtratoResposta(Guid ContaId, int Pagina, int Tamanho, int Total, IReadOnlyList<LinhaResposta> Itens);

public sealed record PartidaResposta(Guid ContaId, decimal Valor);

public sealed record LancamentoResposta(Guid Id, TipoDeLancamento Tipo, string Descricao, DateTimeOffset Em, IReadOnlyList<PartidaResposta> Partidas);

public sealed record PixParaOutroBancoResposta(
    string EndToEndId, SituacaoDoPix Situacao, string Instituicao, decimal Valor, int Tentativas, string? Motivo,
    DateTimeOffset CriadoEm, DateTimeOffset? ConcluidoEm);

public sealed record RegistroDeAuditoria(
    long Versao, string Evento, DateTimeOffset Em, string? Usuario, string? Correlacao, Guid? LancamentoId);

public sealed record ConferenciaResposta(
    decimal TotalDebitos, decimal TotalCreditos, int Lancamentos, decimal SomaDosSaldos, bool Fecha,
    IReadOnlyList<Guid> Desbalanceados);

public static partial class Endpoints
{
    // Uma chave por operação, gerada pelo app (um UUID serve). Repetir a chave repete a resposta.
    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex FormatoDaChave();

    public static void MapEndpoints(this IEndpointRouteBuilder app)
    {
        // Tudo exige login, a não ser o que diz o contrário (AllowAnonymous).
        var cliente = app.MapGroup("").RequireAuthorization(Politicas.Cliente);
        var operador = app.MapGroup("").RequireAuthorization(Politicas.Operador);

        var contas = cliente.MapGroup("/contas").WithTags("Contas");
        contas.MapPost("/", async (AbrirContaPedido pedido, Cadastro cadastro, ClaimsPrincipal usuario, CancellationToken ct) =>
        {
            var conta = await cadastro.AbrirConta(pedido.Nome, pedido.Cpf, Usuarios.Id(usuario), ct);
            return TypedResults.Created($"/contas/{conta.Id}", conta);
        });
        contas.MapGet("/minha", async (Donos donos, ClaimsPrincipal usuario, CancellationToken ct) =>
            await donos.DoUsuario(usuario, ct) is { } conta ? Results.Ok(Resposta(conta)) : Results.NotFound());
        contas.MapGet("/{id:guid}", async (Guid id, Donos donos, ClaimsPrincipal usuario, CancellationToken ct) =>
            TypedResults.Ok(Resposta(await donos.Garantir(id, usuario, ct))));
        contas.MapPut("/{id:guid}/limite-noturno", async (
            Guid id, LimitePedido pedido, Donos donos, Cadastro cadastro, ClaimsPrincipal usuario, CancellationToken ct) =>
        {
            await donos.Garantir(id, usuario, ct);
            return TypedResults.Ok(Limites(await cadastro.AlterarLimiteNoturno(id, Dinheiro.DeReais(pedido.Valor), ct)));
        });
        contas.MapGet("/{id:guid}/saldo", ConsultarSaldo);
        contas.MapGet("/{id:guid}/extrato", ConsultarExtrato);

        var chaves = contas.MapGroup("/{id:guid}/chaves").WithTags("Chaves Pix");
        chaves.MapPost("/", async (
            Guid id, ChavePedido pedido, Donos donos, ChavesPix dict, ClaimsPrincipal usuario, CancellationToken ct) =>
        {
            await donos.Garantir(id, usuario, ct);
            var chave = await dict.Cadastrar(id, pedido.Tipo, pedido.Valor, ct);
            return TypedResults.Created($"/contas/{id}/chaves/{Uri.EscapeDataString(chave.Chave)}", chave);
        });
        chaves.MapGet("/", async (Guid id, Donos donos, ChavesPix dict, ClaimsPrincipal usuario, CancellationToken ct) =>
        {
            await donos.Garantir(id, usuario, ct);
            return TypedResults.Ok(await dict.Listar(id, ct));
        });
        chaves.MapDelete("/{chave}", async (
            Guid id, string chave, Donos donos, ChavesPix dict, ClaimsPrincipal usuario, CancellationToken ct) =>
        {
            await donos.Garantir(id, usuario, ct);
            await dict.Excluir(id, chave, ct);
            return TypedResults.NoContent();
        });

        cliente.MapPost("/pix", async (
            PixPedido pedido, [FromHeader(Name = "Idempotency-Key")] string? chave, Donos donos,
            Movimentacoes movimentacoes, ChavesPix dict, ClaimsPrincipal usuario, HttpContext http, CancellationToken ct) =>
        {
            // Só sai dinheiro da conta de quem está logado.
            await donos.Garantir(pedido.Origem, usuario, ct);
            var destino = (pedido.Destino, pedido.Chave) switch
            {
                ({ } conta, null) => new DestinoDoPix(conta, null),
                (null, { } chavePix) => await dict.Resolver(chavePix, ct),
                _ => throw new RegraVioladaException(Erros.ValorInvalido, "Informe a chave Pix ou a conta de destino (só uma)."),
            };
            // Para outro banco, o dinheiro vai para a conta de trânsito e a saga leva adiante.
            var resultado = await movimentacoes.Executar(
                new PedidoDeMovimentacao(pedido.Origem, destino.Conta ?? ContasInternas.PixEmTransito,
                    Dinheiro.DeReais(pedido.Valor), TipoDeLancamento.Pix, pedido.Descricao?.Trim() ?? "",
                    Chave(chave), destino.OutroBanco), ct);
            return Responder(resultado, http);
        }).WithTags("Pix").RequireRateLimiting(LimitesDeUso.Pix);

        cliente.MapGet("/pix/{endToEndId}", async (
            string endToEndId, IQuerySession consulta, Donos donos, ClaimsPrincipal usuario, CancellationToken ct) =>
        {
            var pix = await consulta.LoadAsync<PixEmLiquidacao>(endToEndId, ct);
            if (pix is null)
            {
                return Results.NotFound();
            }
            await donos.Garantir(pix.Origem, usuario, ct);
            return Results.Ok(new PixParaOutroBancoResposta(pix.Id, pix.Situacao, pix.Instituicao,
                new Dinheiro(pix.Centavos).Reais, pix.Tentativas, pix.Motivo, pix.CriadoEm, pix.ConcluidoEm));
        }).WithTags("Pix");

        // A tela "confira antes de enviar": quem recebe, sem expor o titular inteiro. Com limite
        // de uso, para ninguém varrer chaves e montar uma lista de nomes.
        cliente.MapGet("/pix/chaves/{chave}", async (string chave, ChavesPix dict, CancellationToken ct) =>
            TypedResults.Ok(await dict.Consultar(chave, ct)))
            .WithTags("Pix").RequireRateLimiting(LimitesDeUso.ConsultaDeChave);

        // Funções do banco, não do cliente.
        operador.MapPost("/contas/{id:guid}/depositos", async (
            Guid id, DepositoPedido pedido, [FromHeader(Name = "Idempotency-Key")] string? chave,
            Movimentacoes movimentacoes, HttpContext http, CancellationToken ct) =>
        {
            var resultado = await movimentacoes.Executar(
                new PedidoDeMovimentacao(ContasInternas.Liquidacao, id, Dinheiro.DeReais(pedido.Valor),
                    TipoDeLancamento.Deposito, "Depósito", Chave(chave)), ct);
            return Responder(resultado, http);
        }).WithTags("Operação");
        operador.MapGet("/lancamentos/{id:guid}", ConsultarLancamento).WithTags("Operação");
        operador.MapGet("/livro-razao/conferencia", Conferir).WithTags("Operação");
        operador.MapGet("/contas/{id:guid}/auditoria", Auditar).WithTags("Operação");

        // O SPI avisa que chegou um Pix de outro banco. Não tem login: quem prova a origem é a
        // assinatura. Sem assinatura válida, o corpo nem é lido como aviso.
        app.MapPost("/spi/recebimentos", async (
            HttpRequest requisicao, Recebimentos recebimentos, IConfiguration configuracao, TimeProvider relogio,
            CancellationToken ct) =>
        {
            if (configuracao["Spi:Segredo"] is not { Length: >= 32 } segredo)
            {
                return Results.Problem(title: "Recebimento de Pix desligado (sem Spi:Segredo).", statusCode: 503);
            }
            using var leitor = new StreamReader(requisicao.Body);
            var corpo = await leitor.ReadToEndAsync(ct);
            if (!AssinaturaDoSpi.Confere(segredo, requisicao.Headers[AssinaturaDoSpi.CabecalhoDaData],
                    requisicao.Headers[AssinaturaDoSpi.Cabecalho], corpo, relogio.GetUtcNow()))
            {
                return Results.Unauthorized();
            }
            var aviso = JsonSerializer.Deserialize<AvisoDePix>(corpo, JsonSerializerOptions.Web)
                ?? throw new RegraVioladaException(Erros.ValorInvalido, "Aviso vazio.");
            return Results.Ok(await recebimentos.Receber(aviso, ct));
        }).AllowAnonymous().WithTags("SPI").ExcludeFromDescription();
    }

    private static ContaResposta Resposta(Conta conta) =>
        new(conta.Id, NumeroDaConta.Agencia, conta.Numero, conta.Tipo, conta.Saldo.Reais, Limites(conta));

    private static string Chave(string? chave) =>
        chave is not null && FormatoDaChave().IsMatch(chave)
            ? chave
            : throw new RegraVioladaException(
                Erros.ChaveDeIdempotenciaInvalida,
                "Envie o cabeçalho Idempotency-Key, de 8 a 64 letras, números, '-' ou '_'.");

    private static LimiteResposta Limites(Conta conta) => new(
        conta.LimiteNoturno.Reais,
        conta.AumentoPendente is { } aumento ? new AumentoResposta(aumento.Valor.Reais, aumento.ValeDesde) : null);

    private static IResult Responder(ResultadoDaMovimentacao resultado, HttpContext http)
    {
        if (resultado.Repetida)
        {
            // Avisa o app que esta é a resposta guardada, e não uma operação nova.
            http.Response.Headers["Idempotent-Replayed"] = "true";
        }
        if (resultado.Feita is { ParaOutroBanco: { } fora } paraOutroBanco)
        {
            // Aceito: o dinheiro já saiu da conta, e a liquidação segue em segundo plano.
            return TypedResults.Accepted($"/pix/{fora.EndToEndId}", paraOutroBanco);
        }
        if (resultado.Feita is { } feita)
        {
            return TypedResults.Created($"/lancamentos/{feita.LancamentoId}", feita);
        }
        var recusa = resultado.Recusa!;
        return TypedResults.Problem(
            type: recusa.Codigo, title: recusa.Message, statusCode: TratadorDeErros.StatusDe(recusa.Codigo));
    }

    /// <summary>Saldo agora, ou em qualquer instante do passado: o event store guarda tudo.</summary>
    private static async Task<IResult> ConsultarSaldo(
        Guid id, DateTimeOffset? em, IQuerySession consulta, Donos donos, ClaimsPrincipal usuario, CancellationToken ct)
    {
        var atual = await donos.Garantir(id, usuario, ct);
        var conta = em is null ? atual : await consulta.Events.AggregateStreamAsync<Conta>(id, timestamp: em, token: ct);
        // Antes da abertura da conta, o saldo era zero.
        return TypedResults.Ok(new SaldoResposta(id, conta?.Saldo.Reais ?? 0m, em));
    }

    private static async Task<IResult> ConsultarExtrato(
        Guid id, IQuerySession consulta, Donos donos, ClaimsPrincipal usuario, CancellationToken ct,
        int pagina = 1, int tamanho = 20)
    {
        if (pagina < 1 || tamanho is < 1 or > 100)
        {
            return TypedResults.Problem(
                type: Erros.ValorInvalido, title: "Página a partir de 1, com 1 a 100 linhas.", statusCode: 400);
        }
        await donos.Garantir(id, usuario, ct);
        var linhas = consulta.Query<LinhaDoExtrato>().Where(l => l.ContaId == id);
        var total = await linhas.CountAsync(ct);
        var itens = await linhas.OrderByDescending(l => l.Sequencia)
            .Skip((pagina - 1) * tamanho).Take(tamanho).ToListAsync(ct);
        return TypedResults.Ok(new ExtratoResposta(id, pagina, tamanho, total,
            [.. itens.Select(l => new LinhaResposta(
                l.LancamentoId, l.Tipo, new Dinheiro(l.Centavos).Reais, l.Contraparte, l.Historico, l.Em))]));
    }

    private static async Task<IResult> ConsultarLancamento(Guid id, IQuerySession consulta, CancellationToken ct)
    {
        var partidas = await consulta.Query<LinhaDoExtrato>().Where(l => l.LancamentoId == id).ToListAsync(ct);
        if (partidas.Count == 0)
        {
            return TypedResults.NotFound();
        }
        var primeira = partidas[0];
        return TypedResults.Ok(new LancamentoResposta(id, primeira.Tipo, primeira.Historico, primeira.Em,
            [.. partidas.OrderBy(p => p.Centavos).Select(p => new PartidaResposta(p.ContaId, new Dinheiro(p.Centavos).Reais))]));
    }

    /// <summary>Auditoria: refaz a conta do livro inteiro a partir dos eventos.</summary>
    private static async Task<IResult> Conferir(IQuerySession consulta, CancellationToken ct)
    {
        var eventos = await consulta.Events.QueryAllRawEvents().ToListAsync(ct);
        var conferencia = LivroRazao.Conferir(eventos.Select(e => e.Data));
        var saldos = await consulta.Query<Conta>().ToListAsync(ct);
        var soma = Dinheiro.Somar(saldos.Select(c => c.Saldo));
        return TypedResults.Ok(new ConferenciaResposta(
            conferencia.TotalDebitos.Reais, conferencia.TotalCreditos.Reais, conferencia.Lancamentos, soma.Reais,
            conferencia.Fecha && soma == Dinheiro.Zero, conferencia.Desbalanceados));
    }

    /// <summary>Quem fez o quê, e quando: os metadados que o Marten guarda em cada evento.</summary>
    private static async Task<IResult> Auditar(Guid id, IQuerySession consulta, CancellationToken ct)
    {
        var eventos = await consulta.Events.FetchStreamAsync(id, token: ct);
        if (eventos.Count == 0)
        {
            return TypedResults.NotFound();
        }
        return TypedResults.Ok(eventos.Select(e => new RegistroDeAuditoria(
            e.Version, e.EventTypeName, e.Timestamp, e.UserName, e.CorrelationId,
            e.Data switch { ValorDebitado d => d.LancamentoId, ValorCreditado c => c.LancamentoId, _ => null }))
            .ToList());
    }
}
