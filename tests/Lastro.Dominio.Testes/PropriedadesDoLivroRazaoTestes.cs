using FsCheck;
using FsCheck.Xunit;

namespace Lastro.Dominio.Testes;

/// <summary>
/// Testes de propriedade: o FsCheck gera centenas de sequências aleatórias de depósitos e
/// Pix entre quatro clientes e confere o que precisa valer para QUALQUER sequência.
/// Quando uma falha, ele reduz a sequência ao menor exemplo que ainda quebra.
/// </summary>
public class PropriedadesDoLivroRazaoTestes
{
    private static readonly DateTimeOffset Inicio = new(2026, 10, 15, 9, 0, 0, TimeSpan.FromHours(-3));

    private sealed class Banco
    {
        public Banco()
        {
            Liquidacao = Abrir(TipoDeConta.Interna);
            Clientes = [.. Enumerable.Range(0, 4).Select(_ => Abrir(TipoDeConta.Cliente))];
        }

        public Guid Liquidacao { get; }

        public List<Guid> Clientes { get; }

        public Dictionary<Guid, Conta> Contas { get; } = [];

        public List<object> Eventos { get; } = [];

        public Dictionary<Guid, List<object>> PorConta { get; } = [];

        private Guid Abrir(TipoDeConta tipo)
        {
            var aberta = new ContaAberta(Guid.CreateVersion7(), $"0001-{Contas.Count}", tipo, null, Inicio);
            Contas[aberta.ContaId] = Conta.Criar(aberta);
            PorConta[aberta.ContaId] = [aberta];
            return aberta.ContaId;
        }

        /// <summary>Tenta o lançamento; se uma regra recusar, nada muda.</summary>
        public RegraVioladaException? Lancar(Guid origem, Guid destino, long centavos, TipoDeLancamento tipo)
        {
            try
            {
                var lancamento = Lancamento.Transferir(
                    Contas[origem], Contas[destino], new Dinheiro(centavos), tipo, "", Guid.CreateVersion7(),
                    Inicio.AddSeconds(Eventos.Count));
                Contas[origem] = Contas[origem].Aplicar(lancamento.Debito);
                Contas[destino] = Contas[destino].Aplicar(lancamento.Credito);
                Eventos.AddRange([lancamento.Debito, lancamento.Credito]);
                PorConta[origem].Add(lancamento.Debito);
                PorConta[destino].Add(lancamento.Credito);
                return null;
            }
            catch (RegraVioladaException erro)
            {
                return erro;
            }
        }
    }

    private static Banco Simular(PositiveInt[] depositos, (int Origem, int Destino, PositiveInt Centavos)[] pix)
    {
        var banco = new Banco();
        foreach (var (valor, i) in depositos.Select((v, i) => (v, i)))
        {
            Assert.Null(banco.Lancar(banco.Liquidacao, banco.Clientes[i % 4], valor.Get, TipoDeLancamento.Deposito));
        }
        foreach (var (o, d, valor) in pix)
        {
            var (origem, destino) = (banco.Clientes[Math.Abs(o % 4)], banco.Clientes[Math.Abs(d % 4)]);
            var saldoAntes = banco.Contas[origem].Saldo;
            var recusa = banco.Lancar(origem, destino, valor.Get, TipoDeLancamento.Pix);

            // Recusa só pelo motivo certo: faltou saldo de verdade, ou era a mesma conta.
            switch (recusa?.Codigo)
            {
                case Erros.SaldoInsuficiente:
                    Assert.True(saldoAntes < new Dinheiro(valor.Get));
                    break;
                case Erros.MesmaConta:
                    Assert.Equal(origem, destino);
                    break;
                case null:
                    break;
                default:
                    Assert.Fail($"Recusa inesperada: {recusa.Codigo}");
                    break;
            }
        }
        return banco;
    }

    [Property(MaxTest = 300)]
    public void Dinheiro_nao_e_criado_nem_some(
        PositiveInt[] depositos, (int Origem, int Destino, PositiveInt Centavos)[] pix)
    {
        var banco = Simular(depositos, pix);
        // A soma de todos os saldos, contando a liquidação, é zero...
        Assert.Equal(Dinheiro.Zero, Dinheiro.Somar(banco.Contas.Values.Select(c => c.Saldo)));
        // ...ou seja, o dinheiro dos clientes é exatamente o que entrou por depósito.
        var depositado = Dinheiro.Somar(depositos.Select(d => new Dinheiro(d.Get)));
        Assert.Equal(depositado, Dinheiro.Somar(banco.Clientes.Select(c => banco.Contas[c].Saldo)));
    }

    [Property(MaxTest = 300)]
    public void Nenhum_cliente_fica_negativo(PositiveInt[] depositos, (int, int, PositiveInt)[] pix)
    {
        var banco = Simular(depositos, pix);
        Assert.All(banco.Clientes, c => Assert.True(banco.Contas[c].Saldo >= Dinheiro.Zero));
    }

    [Property(MaxTest = 300)]
    public void O_livro_razao_sempre_fecha(PositiveInt[] depositos, (int, int, PositiveInt)[] pix)
    {
        var conferencia = LivroRazao.Conferir(Simular(depositos, pix).Eventos);
        Assert.True(conferencia.Fecha);
        Assert.Equal(conferencia.TotalDebitos, conferencia.TotalCreditos);
    }

    [Property(MaxTest = 300)]
    public void Repassar_o_historico_reconstroi_o_mesmo_saldo(PositiveInt[] depositos, (int, int, PositiveInt)[] pix)
    {
        // É o que o event store faz: o estado é só os eventos aplicados em ordem.
        var banco = Simular(depositos, pix);
        Assert.All(banco.Contas, par => Assert.Equal(par.Value, Conta.Reconstruir(banco.PorConta[par.Key])));
    }
}
