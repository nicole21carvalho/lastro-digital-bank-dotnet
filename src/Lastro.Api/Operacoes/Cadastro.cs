using Lastro.Api.Armazenamento;
using Lastro.Dominio;
using Marten;
using Npgsql;

namespace Lastro.Api.Operacoes;

public sealed record ContaCriada(Guid Id, string Agencia, string Numero);

public sealed class Cadastro(IDocumentSession sessao, TimeProvider relogio)
{
    public async Task<ContaCriada> AbrirConta(string nome, string cpfInformado, CancellationToken ct)
    {
        var cpf = Cpf.Criar(cpfInformado);
        var sequencial = (await sessao.QueryAsync<long>(
            $"select nextval('{ConfiguracaoDoMarten.SequenciaDasContas}')", ct)).Single();
        var numero = NumeroDaConta.Formatar(sequencial);

        var titular = new Titular(Guid.CreateVersion7(), nome.Trim(), cpf.Numeros);
        var contaId = Guid.CreateVersion7();
        // Titular e conta na mesma transação: não existe conta sem dono nem dono sem conta.
        sessao.Insert(titular);
        sessao.Events.StartStream<Conta>(
            contaId, new ContaAberta(contaId, numero, TipoDeConta.Cliente, titular.Id, relogio.GetUtcNow()));
        try
        {
            await sessao.SaveChangesAsync(ct);
        }
        catch (Exception erro) when (ViolouUnicidade(erro))
        {
            throw new RegraVioladaException(Erros.CpfJaCadastrado, "Já existe uma conta para este CPF.");
        }
        return new ContaCriada(contaId, NumeroDaConta.Agencia, numero);
    }

    public async Task<Conta> AlterarLimiteNoturno(Guid contaId, Dinheiro novo, CancellationToken ct)
    {
        var stream = await sessao.Events.FetchForExclusiveWriting<Conta>(contaId, ct);
        var conta = stream.Aggregate is { Tipo: TipoDeConta.Cliente } cliente
            ? cliente
            : throw new RegraVioladaException(Erros.ContaNaoEncontrada, "Conta não encontrada.");
        var alterado = conta.AlterarLimiteNoturno(novo, relogio.GetUtcNow());
        stream.AppendOne(alterado);
        await sessao.SaveChangesAsync(ct);
        return conta.Aplicar(alterado);
    }

    // O Marten embrulha o erro do PostgreSQL (às vezes num AggregateException): procura na cadeia toda.
    // Num Insert com id repetido, ele já troca o erro pela própria exceção, sem o erro original dentro.
    internal static bool ViolouUnicidade(Exception erro) => erro switch
    {
        PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } => true,
        JasperFx.DocumentAlreadyExistsException => true,
        AggregateException agregado => agregado.InnerExceptions.Any(ViolouUnicidade),
        { InnerException: { } interna } => ViolouUnicidade(interna),
        _ => false,
    };
}
