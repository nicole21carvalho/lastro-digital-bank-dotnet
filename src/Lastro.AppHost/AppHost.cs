// O Lastro inteiro na máquina: PostgreSQL, o provedor de identidade (Keycloak, com passkeys)
// e a API, com o painel do Aspire mostrando logs, traces e métricas de tudo.
var builder = DistributedApplication.CreateBuilder(args);

// Senhas geradas na primeira execução e guardadas nos user-secrets desta máquina.
var senhaDoKeycloak = builder.AddParameter(
    "senha-admin-keycloak", new GenerateParameterDefault { MinLength = 24, Special = false }, secret: true, persist: true);
var segredoDoSpi = builder.AddParameter(
    "segredo-spi", new GenerateParameterDefault { MinLength = 48, Special = false }, secret: true, persist: true);

var banco = builder.AddPostgres("postgres")
    .WithImageTag("18-alpine")
    .WithDataVolume("lastro-dados")
    .AddDatabase("lastro");

// O realm "lastro" (app com PKCE, papel "operador", passkeys) vem de infra/keycloak.
var keycloak = builder.AddContainer("keycloak", "quay.io/keycloak/keycloak", "26.8.0")
    .WithArgs("start-dev", "--import-realm")
    .WithBindMount("../../infra/keycloak", "/opt/keycloak/data/import", isReadOnly: true)
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", senhaDoKeycloak)
    .WithHttpEndpoint(port: 8180, targetPort: 8080, name: "http");

builder.AddProject<Projects.Lastro_Api>("api")
    .WithReference(banco, connectionName: "Lastro")
    .WaitFor(banco)
    .WaitFor(keycloak)
    .WithEnvironment("Autenticacao__Emissor", "http://localhost:8180/realms/lastro")
    .WithEnvironment("Autenticacao__ExigirHttps", "false")
    .WithEnvironment("Spi__Segredo", segredoDoSpi)
    .WithHttpHealthCheck("/pronto");

builder.Build().Run();
