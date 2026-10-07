# Node is used only to build and test the React frontend.
FROM node:22-bookworm-slim AS node
WORKDIR /source/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci --no-fund --no-audit
COPY frontend/ ./

FROM node AS frontend
RUN npm run build

FROM node AS frontend-preview
RUN npm run build:preview

FROM node:22-bookworm-slim AS qa-pki
RUN apt-get update && apt-get install -y --no-install-recommends openssl bash ca-certificates && rm -rf /var/lib/apt/lists/*

FROM caddy:2.11.6-alpine AS preview
COPY infra/qa/preview.Caddyfile /etc/caddy/Caddyfile
COPY --from=frontend-preview /source/frontend/dist-preview/ /srv/
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS sdk
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_PROCESSOR_COUNT=1
WORKDIR /source
# Copy restore inputs first so code changes reuse the locked NuGet dependency layer.
COPY AcropolisChannel.slnx global.json Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/Acropolis.Api/Acropolis.Api.csproj src/Acropolis.Api/packages.lock.json src/Acropolis.Api/
COPY src/Acropolis.Migrations/Acropolis.Migrations.csproj src/Acropolis.Migrations/packages.lock.json src/Acropolis.Migrations/
COPY src/Modules/Catalog/Acropolis.Catalog.Application/Acropolis.Catalog.Application.csproj src/Modules/Catalog/Acropolis.Catalog.Application/packages.lock.json src/Modules/Catalog/Acropolis.Catalog.Application/
COPY src/Modules/Catalog/Acropolis.Catalog.Infrastructure/Acropolis.Catalog.Infrastructure.csproj src/Modules/Catalog/Acropolis.Catalog.Infrastructure/packages.lock.json src/Modules/Catalog/Acropolis.Catalog.Infrastructure/
COPY src/Modules/Identity/Acropolis.Identity.Application/Acropolis.Identity.Application.csproj src/Modules/Identity/Acropolis.Identity.Application/packages.lock.json src/Modules/Identity/Acropolis.Identity.Application/
COPY src/Modules/Identity/Acropolis.Identity.Infrastructure/Acropolis.Identity.Infrastructure.csproj src/Modules/Identity/Acropolis.Identity.Infrastructure/packages.lock.json src/Modules/Identity/Acropolis.Identity.Infrastructure/
COPY src/Modules/Platform/Acropolis.Platform.Application/Acropolis.Platform.Application.csproj src/Modules/Platform/Acropolis.Platform.Application/packages.lock.json src/Modules/Platform/Acropolis.Platform.Application/
COPY src/Modules/Platform/Acropolis.Platform.Infrastructure/Acropolis.Platform.Infrastructure.csproj src/Modules/Platform/Acropolis.Platform.Infrastructure/packages.lock.json src/Modules/Platform/Acropolis.Platform.Infrastructure/
COPY src/Modules/Subscriptions/Acropolis.Subscriptions.Application/Acropolis.Subscriptions.Application.csproj src/Modules/Subscriptions/Acropolis.Subscriptions.Application/packages.lock.json src/Modules/Subscriptions/Acropolis.Subscriptions.Application/
COPY src/Modules/Subscriptions/Acropolis.Subscriptions.Infrastructure/Acropolis.Subscriptions.Infrastructure.csproj src/Modules/Subscriptions/Acropolis.Subscriptions.Infrastructure/packages.lock.json src/Modules/Subscriptions/Acropolis.Subscriptions.Infrastructure/
COPY tests/backend/Acropolis.Api.Tests/Acropolis.Api.Tests.csproj tests/backend/Acropolis.Api.Tests/packages.lock.json tests/backend/Acropolis.Api.Tests/
COPY tests/backend/Acropolis.Architecture.Tests/Acropolis.Architecture.Tests.csproj tests/backend/Acropolis.Architecture.Tests/packages.lock.json tests/backend/Acropolis.Architecture.Tests/
COPY tests/backend/Acropolis.Catalog.IntegrationTests/Acropolis.Catalog.IntegrationTests.csproj tests/backend/Acropolis.Catalog.IntegrationTests/packages.lock.json tests/backend/Acropolis.Catalog.IntegrationTests/
COPY tests/backend/Acropolis.Catalog.UnitTests/Acropolis.Catalog.UnitTests.csproj tests/backend/Acropolis.Catalog.UnitTests/packages.lock.json tests/backend/Acropolis.Catalog.UnitTests/
COPY tests/backend/Acropolis.Identity.IntegrationTests/Acropolis.Identity.IntegrationTests.csproj tests/backend/Acropolis.Identity.IntegrationTests/packages.lock.json tests/backend/Acropolis.Identity.IntegrationTests/
COPY tests/backend/Acropolis.Identity.UnitTests/Acropolis.Identity.UnitTests.csproj tests/backend/Acropolis.Identity.UnitTests/packages.lock.json tests/backend/Acropolis.Identity.UnitTests/
COPY tests/backend/Acropolis.Platform.IntegrationTests/Acropolis.Platform.IntegrationTests.csproj tests/backend/Acropolis.Platform.IntegrationTests/packages.lock.json tests/backend/Acropolis.Platform.IntegrationTests/
COPY tests/backend/Acropolis.Platform.UnitTests/Acropolis.Platform.UnitTests.csproj tests/backend/Acropolis.Platform.UnitTests/packages.lock.json tests/backend/Acropolis.Platform.UnitTests/
COPY tests/backend/Acropolis.Subscriptions.IntegrationTests/Acropolis.Subscriptions.IntegrationTests.csproj tests/backend/Acropolis.Subscriptions.IntegrationTests/packages.lock.json tests/backend/Acropolis.Subscriptions.IntegrationTests/
COPY tests/backend/Acropolis.Subscriptions.UnitTests/Acropolis.Subscriptions.UnitTests.csproj tests/backend/Acropolis.Subscriptions.UnitTests/packages.lock.json tests/backend/Acropolis.Subscriptions.UnitTests/
RUN dotnet restore AcropolisChannel.slnx --locked-mode --disable-parallel
COPY . .

FROM sdk AS publish
ARG REVISION=development
RUN dotnet publish src/Acropolis.Api/Acropolis.Api.csproj --no-restore -c Release -o /out/web -m:1 -p:InformationalVersion=0.1.0+$REVISION
RUN dotnet publish src/Acropolis.Migrations/Acropolis.Migrations.csproj --no-restore -c Release -o /out/migrations -m:1 -p:InformationalVersion=0.1.0+$REVISION

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates && rm -rf /var/lib/apt/lists/*
RUN mkdir -p /var/acropolis/keys && chown app:app /var/acropolis/keys && chmod 700 /var/acropolis/keys
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 DOTNET_EnableDiagnostics=0 DOTNET_CLI_TELEMETRY_OPTOUT=1
USER app

FROM runtime AS migrations
ARG REVISION=development
LABEL org.opencontainers.image.revision=$REVISION
COPY --from=publish --chown=app:app /out/migrations/ ./
ENTRYPOINT ["dotnet", "Acropolis.Migrations.dll"]

FROM mcr.microsoft.com/playwright:v1.63.0-noble AS playwright
RUN apt-get update && apt-get install -y --no-install-recommends libnss3-tools openssl bash && rm -rf /var/lib/apt/lists/*
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
