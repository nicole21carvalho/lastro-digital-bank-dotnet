using JasperFx;
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

    // aplicarEsquema: cria ou atualiza tabelas e funções na subida. Com várias réplicas, deixe só
    // uma (ou um passo de migração) fazer isso: mudar o esquema com transações rodando dá erro nelas.
    public static IServiceCollection AddArmazenamento(this IServiceCollection services, string conexao, bool aplicarEsquema = true)
    {
        var marten = services.AddMarten(opcoes =>
        {
            opcoes.Connection(conexao);
            opcoes.DatabaseSchemaName = Esquema;
            if (!aplicarEsquema)
            {
                // Nem na primeira consulta: esta réplica usa o esquema que já existe, e nunca roda DDL.
                opcoes.AutoCreateSchemaObjects = AutoCreate.None;
            }
            opcoes.Events.AddEventTypes(
                [typeof(ContaAberta), typeof(ValorCreditado), typeof(ValorDebitado), typeof(LimiteNoturnoAlterado)]);
            // No modo "rich", o número de sequência de cada evento é reservado antes de as
            // projeções rodarem. No modo rápido (o padrão), ele só nasce dentro do banco, e o
            // extrato não saberia a ordem dos lançamentos. Num banco, a ordem vale mais.
            opcoes.Events.AppendMode = EventAppendMode.Rich;

            // CQRS: o lado de escrita são os eventos; saldo e extrato são projeções deles.
            opcoes.Projections.Add<ContaProjection>(ProjectionLifecycle.Inline);
            opcoes.Projections.Add<ExtratoProjection>(ProjectionLifecycle.Inline);

            // Um CPF, um titular; um login, uma conta: o próprio banco garante, mesmo com dois cadastros ao mesmo tempo.
            opcoes.Schema.For<Titular>().UniqueIndex(t => t.Cpf).UniqueIndex(t => t.Usuario);

            // Spans das conexões e contadores de eventos gravados (fonte "Marten" no OpenTelemetry).
            opcoes.OpenTelemetry.TrackConnections = JasperFx.OpenTelemetry.TrackLevel.Normal;
            opcoes.OpenTelemetry.TrackEventCounters();

            // Auditoria: cada evento guarda quem fez (o "sub") e o id do trace da requisição.
            opcoes.Events.MetadataConfig.UserNameEnabled = true;
            opcoes.Events.MetadataConfig.CorrelationIdEnabled = true;
            opcoes.Schema.For<LinhaDoExtrato>().Index(l => l.ContaId);
            opcoes.Schema.For<RegistroDeChave>().Index(c => c.ContaId);
            // Todo documento registrado aqui: assim o passo de esquema cria todas as tabelas, e uma
            // réplica que não roda DDL nunca encontra uma faltando (sem registro, o Marten só criaria
            // a tabela no primeiro uso, e um teste pegou isso).
            opcoes.Schema.For<RegistroDeIdempotencia>();
            opcoes.Schema.For<PixRecebidoRegistro>();
            // Dois trabalhadores de liquidação no mesmo Pix: o segundo a gravar falha (e não liquida duas vezes).
            opcoes.Schema.For<PixEmLiquidacao>().UseOptimisticConcurrency(true)
                .Index(p => p.Situacao).Index(p => p.Origem);

            // Números de conta sem repetição e sem buraco por corrida: uma sequência do PostgreSQL.
            opcoes.Storage.ExtendedSchemaObjects.Add(new Sequence(
                new PostgresqlObjectName(Esquema, "numero_da_conta", SchemaUtils.IdentifierUsage.General), 100_001));
        }).BuildSessionsWith<Seguranca.SessaoComAutoria>();
        if (aplicarEsquema)
        {
            marten.ApplyAllDatabaseChangesOnStartup();
        }
        return services;
    }
}
