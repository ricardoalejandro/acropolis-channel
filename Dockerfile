# Node is used only to build and test the React frontend.
FROM node:22-bookworm-slim AS node
WORKDIR /source/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci --no-fund --no-audit
COPY frontend/ ./

FROM node AS frontend
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS sdk
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /source
COPY . .
RUN dotnet restore AcropolisChannel.slnx --locked-mode --disable-parallel

FROM sdk AS publish
ARG REVISION=development
RUN dotnet publish src/Acropolis.Api/Acropolis.Api.csproj --no-restore -c Release -o /out/web -m:1 -p:InformationalVersion=0.1.0+$REVISION
RUN dotnet publish src/Acropolis.Migrations/Acropolis.Migrations.csproj --no-restore -c Release -o /out/migrations -m:1 -p:InformationalVersion=0.1.0+$REVISION

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 DOTNET_EnableDiagnostics=0 DOTNET_CLI_TELEMETRY_OPTOUT=1
USER app

FROM runtime AS migrations
ARG REVISION=development
LABEL org.opencontainers.image.revision=$REVISION
COPY --from=publish --chown=app:app /out/migrations/ ./
ENTRYPOINT ["dotnet", "Acropolis.Migrations.dll"]

FROM mcr.microsoft.com/playwright:v1.63.0-noble AS playwright
COPY --from=node /usr/local/ /usr/local/
ENV PATH=/usr/local/bin:$PATH
COPY --from=node /source/frontend/ /source/frontend/
WORKDIR /source/frontend

FROM runtime AS web
ARG REVISION=development
LABEL org.opencontainers.image.revision=$REVISION
COPY --from=publish --chown=app:app /out/web/ ./
COPY --from=frontend --chown=app:app /source/frontend/dist/ ./wwwroot/
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=4s --start-period=20s --retries=3 CMD curl --fail --silent http://127.0.0.1:8080/health > /dev/null || exit 1
ENTRYPOINT ["dotnet", "Acropolis.Api.dll"]
