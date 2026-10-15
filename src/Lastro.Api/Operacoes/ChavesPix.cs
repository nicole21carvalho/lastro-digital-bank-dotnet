using Lastro.Api.Armazenamento;
using Lastro.Api.Spi;
using Lastro.Dominio;
using Marten;

namespace Lastro.Api.Operacoes;

public sealed record ChaveCadastrada(string Chave, TipoDeChave Tipo, DateTimeOffset CriadaEm);

/// <summary>
/// O que aparece antes de confirmar um Pix. Só o necessário para a pessoa reconhecer quem
/// recebe: primeiro nome com as iniciais e CPF mascarado. Uma chave não revela o titular inteiro.
/// </summary>
public sealed record DonoDaChave(string Chave, TipoDeChave Tipo, string Nome, string Cpf, string Instituicao);

/// <summary>Para onde vai um Pix: uma conta do Lastro, ou uma chave de outro banco.</summary>
public sealed record DestinoDoPix(Guid? Conta, ChaveDeOutroBanco? OutroBanco);

/// <summary>
/// As chaves Pix do Lastro. Uma chave que não é daqui é procurada no diretório do Banco
/// Central (simulado), como fazem os bancos de verdade.
/// </summary>
public sealed class ChavesPix(IDocumentSession sessao, TimeProvider relogio, IDiretorioDeChaves diretorio)
{
    public const string Instituicao = "Lastro";

    public async Task<ChaveCadastrada> Cadastrar(Guid contaId, TipoDeChave tipo, string? valor, CancellationToken ct)
    {
        // Trava a conta: dois cadastros ao mesmo tempo não passam juntos do limite de 5 chaves.
        var conta = (await sessao.Events.FetchForExclusiveWriting<Conta>(contaId, ct)).Aggregate;
        if (conta is not { Tipo: TipoDeConta.Cliente, TitularId: { } titularId })
        {
            throw new RegraVioladaException(Erros.ContaNaoEncontrada, "Conta não encontrada.");
        }
        var titular = await sessao.LoadAsync<Titular>(titularId, ct)
            ?? throw new InvalidOperationException($"Conta {contaId} sem titular.");

        var chave = tipo switch
        {
            // A chave CPF é sempre o CPF do próprio titular: não dá para cadastrar o de outra pessoa.
            TipoDeChave.Cpf when valor is null || Cpf.Criar(valor).Numeros == titular.Cpf => ChavePix.DeCpf(Cpf.Criar(titular.Cpf)),
            TipoDeChave.Cpf => throw new RegraVioladaException(Erros.ChaveDeOutroTitular, "A chave CPF precisa ser o CPF do titular da conta."),
            TipoDeChave.Email => ChavePix.DeEmail(valor ?? ""),
            TipoDeChave.Celular => ChavePix.DeCelular(valor ?? ""),
            _ => ChavePix.Aleatoria(),
        };

        var quantas = await sessao.Query<RegistroDeChave>().CountAsync(c => c.ContaId == contaId, ct);
        if (quantas >= ChavePix.MaximoPorConta)
        {
            throw new RegraVioladaException(
                Erros.LimiteDeChaves, $"Cada conta pode ter até {ChavePix.MaximoPorConta} chaves Pix.");
        }
        if (await sessao.LoadAsync<RegistroDeChave>(chave.Valor, ct) is not null)
        {
            throw new RegraVioladaException(Erros.ChavePixJaCadastrada, "Esta chave já está cadastrada.");
        }

        var registro = new RegistroDeChave(chave.Valor, chave.Tipo, contaId, relogio.GetUtcNow());
        // Insert, e não Store: se outra conta gravar a mesma chave ao mesmo tempo, esta falha.
        sessao.Insert(registro);
        try
        {
            await sessao.SaveChangesAsync(ct);
        }
        catch (Exception erro) when (Cadastro.ViolouUnicidade(erro))
        {
            throw new RegraVioladaException(Erros.ChavePixJaCadastrada, "Esta chave já está cadastrada.");
        }
        return new ChaveCadastrada(registro.Id, registro.Tipo, registro.CriadaEm);
    }

    public async Task<IReadOnlyList<ChaveCadastrada>> Listar(Guid contaId, CancellationToken ct)
    {
        var chaves = await sessao.Query<RegistroDeChave>().Where(c => c.ContaId == contaId)
            .OrderBy(c => c.CriadaEm).ToListAsync(ct);
        return [.. chaves.Select(c => new ChaveCadastrada(c.Id, c.Tipo, c.CriadaEm))];
    }

    public async Task Excluir(Guid contaId, string chaveDigitada, CancellationToken ct)
    {
        var chave = ChavePix.Interpretar(chaveDigitada);
        var registro = await sessao.LoadAsync<RegistroDeChave>(chave.Valor, ct);
        // A chave de outra conta responde igual a uma que não existe: nada vaza.
        if (registro is null || registro.ContaId != contaId)
        {
            throw new RegraVioladaException(Erros.ChavePixNaoEncontrada, "Chave não encontrada nesta conta.");
        }
        sessao.Delete(registro);
        await sessao.SaveChangesAsync(ct);
    }

    /// <summary>A conta do Lastro dona desta chave (para receber um Pix de outro banco).</summary>
    public async Task<Guid> ContaInterna(string chaveDigitada, CancellationToken ct)
    {
        var chave = ChavePix.Interpretar(chaveDigitada);
        var registro = await sessao.LoadAsync<RegistroDeChave>(chave.Valor, ct)
            ?? throw new RegraVioladaException(Erros.ChavePixNaoEncontrada, "Chave Pix não encontrada.");
        return registro.ContaId;
    }

    /// <summary>Para onde vai um Pix feito com esta chave: aqui ou em outro banco.</summary>
    public async Task<DestinoDoPix> Resolver(string chaveDigitada, CancellationToken ct)
    {
        var chave = ChavePix.Interpretar(chaveDigitada);
        if (await sessao.LoadAsync<RegistroDeChave>(chave.Valor, ct) is { } registro)
        {
            return new DestinoDoPix(registro.ContaId, null);
        }
        return await diretorio.Consultar(chave, ct) is { } externa
            ? new DestinoDoPix(null, externa)
            : throw new RegraVioladaException(Erros.ChavePixNaoEncontrada, "Chave Pix não encontrada.");
    }

    public async Task<DonoDaChave> Consultar(string chaveDigitada, CancellationToken ct)
    {
        var chave = ChavePix.Interpretar(chaveDigitada);
        var destino = await Resolver(chaveDigitada, ct);
        if (destino.OutroBanco is { } externa)
        {
            return new DonoDaChave(chave.Mascarada, chave.Tipo, externa.Nome, externa.Cpf, externa.Instituicao);
        }
        var conta = await sessao.LoadAsync<Conta>(destino.Conta!.Value, ct);
        var titular = conta?.TitularId is { } id ? await sessao.LoadAsync<Titular>(id, ct) : null;
        if (titular is null)
        {
            throw new RegraVioladaException(Erros.ChavePixNaoEncontrada, "Chave Pix não encontrada.");
        }
        return new DonoDaChave(
            chave.Mascarada, chave.Tipo, NomeResumido(titular.Nome), Cpf.Criar(titular.Cpf).Mascarado, Instituicao);
    }

    /// <summary>"Ana Maria Souza" vira "Ana M. S.".</summary>
    public static string NomeResumido(string nome)
    {
        ArgumentNullException.ThrowIfNull(nome);
        var partes = nome.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return partes.Length == 0 ? "" : string.Join(' ', [partes[0], .. partes.Skip(1).Select(p => $"{p[0]}.")]);
    }
}
