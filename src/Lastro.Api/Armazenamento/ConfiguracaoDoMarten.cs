using JasperFx.Events;
using JasperFx.Events.Projections;
using Lastro.Dominio;
using Marten;
using Weasel.Postgresql;

namespace Lastro.Api.Armazenamento;

public static class ConfiguracaoDoMarten
{
    public const string Esquema = "lastro";

    public const string SequenciaDasContas = $"{Esquema}.numero_da_conta";

    public static IServiceCollection AddArmazenamento(this IServiceCollection services, string conexao)
    {
        services.AddMarten(opcoes =>
        {
            opcoes.Connection(conexao);
            opcoes.DatabaseSchemaName = Esquema;
            opcoes.Events.AddEventTypes(
                [typeof(ContaAberta), typeof(ValorCreditado), typeof(ValorDebitado), typeof(LimiteNoturnoAlterado)]);
            // No modo "rich", o número de sequência de cada evento é reservado antes de as
            // projeções rodarem. No modo rápido (o padrão), ele só nasce dentro do banco, e o
            // extrato não saberia a ordem dos lançamentos. Num banco, a ordem vale mais.
            opcoes.Events.AppendMode = EventAppendMode.Rich;

            // CQRS: o lado de escrita são os eventos; saldo e extrato são projeções deles.
            opcoes.Projections.Add<ContaProjection>(ProjectionLifecycle.Inline);
            opcoes.Projections.Add<ExtratoProjection>(ProjectionLifecycle.Inline);

            // Um CPF, um titular: o próprio banco garante, mesmo com dois cadastros ao mesmo tempo.
            opcoes.Schema.For<Titular>().UniqueIndex(t => t.Cpf);
            opcoes.Schema.For<LinhaDoExtrato>().Index(l => l.ContaId);
            opcoes.Schema.For<RegistroDeChave>().Index(c => c.ContaId);

            // Números de conta sem repetição e sem buraco por corrida: uma sequência do PostgreSQL.
            opcoes.Storage.ExtendedSchemaObjects.Add(new Sequence(
                new PostgresqlObjectName(Esquema, "numero_da_conta", SchemaUtils.IdentifierUsage.General), 100_001));
        }).ApplyAllDatabaseChangesOnStartup();
        return services;
    }
}
