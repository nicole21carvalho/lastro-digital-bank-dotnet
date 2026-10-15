namespace Lastro.Dominio;

/// <summary>Resultado da conferência: o livro-razão fecha quando débitos e créditos batem.</summary>
public sealed record Conferencia(
    Dinheiro TotalDebitos,
    Dinheiro TotalCreditos,
    int Lancamentos,
    IReadOnlyList<Guid> Desbalanceados)
{
    public bool Fecha => TotalDebitos == TotalCreditos && Desbalanceados.Count == 0;
}

public static class LivroRazao
{
    /// <summary>
    /// Confere o livro inteiro: cada lançamento precisa ter exatamente um débito e um
    /// crédito do mesmo valor. Qualquer diferença é dinheiro criado ou sumido.
    /// </summary>
    public static Conferencia Conferir(IEnumerable<object> eventos)
    {
        ArgumentNullException.ThrowIfNull(eventos);
        var debitos = new Dictionary<Guid, List<Dinheiro>>();
        var creditos = new Dictionary<Guid, List<Dinheiro>>();
        foreach (var evento in eventos)
        {
            switch (evento)
            {
                case ValorDebitado d:
                    Partidas(debitos, d.LancamentoId).Add(d.Valor);
                    break;
                case ValorCreditado c:
                    Partidas(creditos, c.LancamentoId).Add(c.Valor);
                    break;
            }
        }

        var ids = debitos.Keys.Union(creditos.Keys).ToList();
        var desbalanceados = ids
            .Where(id => !(debitos.TryGetValue(id, out var d) && creditos.TryGetValue(id, out var c)
                && d.Count == 1 && c.Count == 1 && d[0] == c[0]))
            .ToList();
        return new Conferencia(
            Dinheiro.Somar(debitos.Values.SelectMany(v => v)),
            Dinheiro.Somar(creditos.Values.SelectMany(v => v)),
            ids.Count,
            desbalanceados);
    }

    private static List<Dinheiro> Partidas(Dictionary<Guid, List<Dinheiro>> mapa, Guid id)
    {
        if (!mapa.TryGetValue(id, out var lista))
        {
            mapa[id] = lista = [];
        }
        return lista;
    }
}
