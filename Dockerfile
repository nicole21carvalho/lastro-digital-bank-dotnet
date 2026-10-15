# A API do Lastro numa imagem enxuta.
# Etapa 1: compila com o SDK, com as versões travadas pelos packages.lock.json.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /src
# Primeiro só o que define as dependências (camada em cache enquanto elas não mudam)...
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Lastro.Dominio/Lastro.Dominio.csproj src/Lastro.Dominio/packages.lock.json src/Lastro.Dominio/
COPY src/Lastro.Api/Lastro.Api.csproj src/Lastro.Api/packages.lock.json src/Lastro.Api/
RUN dotnet restore src/Lastro.Api/Lastro.Api.csproj --locked-mode
# ...depois o código.
COPY src/Lastro.Dominio/ src/Lastro.Dominio/
COPY src/Lastro.Api/ src/Lastro.Api/
RUN dotnet publish src/Lastro.Api/Lastro.Api.csproj -c Release -o /app --no-restore

# Etapa 2: "chiseled" (Ubuntu sem shell, sem gerenciador de pacotes e com usuário sem
# privilégios por padrão). Sem ICU nem fuso horário: o Lastro não depende de cultura e usa
# UTC-3 fixo para o horário de Brasília.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Lastro.Api.dll"]
