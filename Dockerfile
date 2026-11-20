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
# I .so finiscono nel processo di produzione: hash fissati (digest della release su GitHub), da aggiornare con la versione.
RUN if [ "$TARGETARCH" = arm64 ]; then a=aarch64 h=745752f5f07dd51a6d0fbbcfe4d5a0e0a2d7f9a9286eb661a84b2370a595e87d; \
    else a=x86_64 h=06bc3888e45d53cecefc3263db957833b1c0cb082f9129d1b1e5f63c84b88d18; fi \
    && curl -fsSLo /tmp/pyroscope.tgz https://github.com/grafana/pyroscope-dotnet/releases/download/pyroscope-1.5.1/pyroscope.1.5.1-glibc-$a.tar.gz \
    && echo "$h  /tmp/pyroscope.tgz" | sha256sum -c - \
    && mkdir /pyroscope && tar xzf /tmp/pyroscope.tgz -C /pyroscope && rm /tmp/pyroscope.tgz

FROM mcr.microsoft.com/dotnet/aspnet:11.0
WORKDIR /app
COPY --from=build /app .
COPY --from=build /pyroscope /pyroscope
ENV ASPNETCORE_URLS=http://+:8080 AgentSettings__Path=/data/agentsettings.json
# Profiling spento di default; per attivarlo: CORECLR_ENABLE_PROFILING=1 PYROSCOPE_PROFILING_ENABLED=1
# LD_PRELOAD=/pyroscope/Pyroscope.Linux.ApiWrapper.x64.so (stesso nome anche su aarch64; fuori dall'ENV per non caricarlo sempre)
# PYROSCOPE_SERVER_ADDRESS=http://<host>:4040 (Grafana Cloud: anche PYROSCOPE_BASIC_AUTH_USER/PASSWORD).
ENV CORECLR_ENABLE_PROFILING=0 \
    CORECLR_PROFILER={BD1A650D-AC5D-4896-B64F-D6FA25D6B26A} \
    CORECLR_PROFILER_PATH=/pyroscope/Pyroscope.Profiler.Native.so \
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
