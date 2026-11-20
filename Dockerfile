# Server AgentWeb (web + API + hub). Configurazione solo da variabili d'ambiente, mai nell'immagine:
#   ConnectionStrings__Default  Postgres con pgvector (obbligatoria)
#   Redis                       backplane SignalR per più repliche (facoltativa)
#   AgentSettings__Path         file delle impostazioni agenti su un volume, es. /data/agentsettings.json
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish AgentWeb.Web/AgentWeb.Web.csproj -c Release -o /app
# Profiler Pyroscope (continuous profiling su Grafana), solo Linux: glibc, x86_64 o aarch64 secondo l'architettura dell'immagine.
ARG TARGETARCH
RUN mkdir /pyroscope && curl -fsSL https://github.com/grafana/pyroscope-dotnet/releases/download/pyroscope-1.5.1/pyroscope.1.5.1-glibc-$([ "$TARGETARCH" = arm64 ] && echo aarch64 || echo x86_64).tar.gz | tar xz -C /pyroscope

FROM mcr.microsoft.com/dotnet/aspnet:11.0
WORKDIR /app
COPY --from=build /app .
COPY --from=build /pyroscope /pyroscope
ENV ASPNETCORE_URLS=http://+:8080 AgentSettings__Path=/data/agentsettings.json
# Profiling spento di default; per attivarlo: CORECLR_ENABLE_PROFILING=1 PYROSCOPE_PROFILING_ENABLED=1
# PYROSCOPE_SERVER_ADDRESS=http://<host>:4040 (Grafana Cloud: anche PYROSCOPE_BASIC_AUTH_USER/PASSWORD).
ENV CORECLR_ENABLE_PROFILING=0 \
    CORECLR_PROFILER={BD1A650D-AC5D-4896-B64F-D6FA25D6B26A} \
    CORECLR_PROFILER_PATH=/pyroscope/Pyroscope.Profiler.Native.so \
    LD_PRELOAD=/pyroscope/Pyroscope.Linux.ApiWrapper.x64.so \
    LD_LIBRARY_PATH=/pyroscope \
    PYROSCOPE_APPLICATION_NAME=agentweb \
    PYROSCOPE_PROFILING_LOG_DIR=/tmp \
    PYROSCOPE_PROFILING_WALLTIME_ENABLED=true \
    PYROSCOPE_PROFILING_ALLOCATION_ENABLED=true \
    PYROSCOPE_PROFILING_LOCK_ENABLED=true \
    PYROSCOPE_PROFILING_EXCEPTION_ENABLED=true \
    DD_INTERNAL_PROFILING_ENABLED_ARM64=1
RUN mkdir -p /data && chown app /data
VOLUME /data
EXPOSE 8080
USER app
# Health check: GET /healthz (200 se Postgres risponde) — da configurare nell'orchestratore: l'immagine non ha curl/wget.
ENTRYPOINT ["dotnet", "AgentWeb.Web.dll"]
