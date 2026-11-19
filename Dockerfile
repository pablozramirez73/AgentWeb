# Server AgentWeb (web + API + hub). Configurazione solo da variabili d'ambiente, mai nell'immagine:
#   ConnectionStrings__Default  Postgres con pgvector (obbligatoria)
#   Redis                       backplane SignalR per più repliche (facoltativa)
#   AgentSettings__Path         file delle impostazioni agenti su un volume, es. /data/agentsettings.json
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish AgentWeb.Web/AgentWeb.Web.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:11.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 AgentSettings__Path=/data/agentsettings.json
RUN mkdir -p /data && chown app /data
VOLUME /data
EXPOSE 8080
USER app
# Health check: GET /healthz (200 se Postgres risponde) — da configurare nell'orchestratore: l'immagine non ha curl/wget.
ENTRYPOINT ["dotnet", "AgentWeb.Web.dll"]
