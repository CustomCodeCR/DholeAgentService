# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

ARG NUGET_USERNAME
ARG NUGET_TOKEN

COPY NuGet.Config ./
COPY DholeAgentService.slnx ./
COPY src ./src
COPY tests ./tests

RUN if [ -n "$NUGET_TOKEN" ]; then       dotnet nuget update source github         --username "${NUGET_USERNAME:-github}"         --password "$NUGET_TOKEN"         --store-password-in-clear-text         --configfile NuGet.Config;     fi

RUN dotnet restore DholeAgentService.slnx

FROM build AS publish-api
RUN dotnet publish src/Dhole.Agent.Api/Dhole.Agent.Api.csproj     --configuration Release     --no-restore     --output /app/publish/api

FROM build AS publish-worker
RUN dotnet publish src/Dhole.Agent.Workers/Dhole.Agent.Workers.csproj     --configuration Release     --no-restore     --output /app/publish/worker

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api-final
WORKDIR /app
COPY --from=publish-api /app/publish/api ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Dhole.Agent.Api.dll"]

# Playwright image contains Chromium and its Linux dependencies.
# Version MUST stay aligned with Microsoft.Playwright in Dhole.Agent.Infrastructure.
FROM mcr.microsoft.com/playwright/dotnet:v1.55.0-noble AS worker-final
WORKDIR /app
COPY --from=publish-worker /app/publish/worker ./
ENV Browser__ProfilesPath=/data/browser-profiles
VOLUME ["/data/browser-profiles"]
ENTRYPOINT ["dotnet", "Dhole.Agent.Workers.dll"]
