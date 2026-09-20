# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

ARG NUGET_USERNAME
ARG NUGET_TOKEN

COPY NuGet.Config ./
COPY .ccf-feed /ccf-feed
COPY src ./src

# Prefer the locally packed CustomCodeFramework feed produced by CI/deploy.
# Fall back to GitHub Packages only for developer builds that explicitly pass a token.
RUN if find /ccf-feed -maxdepth 1 -type f -name '*.nupkg' | grep -q .; then \
      dotnet nuget add source /ccf-feed --name ccf-local --configfile NuGet.Config && \
      dotnet nuget disable source github --configfile NuGet.Config && \
      sed -i 's/packageSource key="github"/packageSource key="ccf-local"/' NuGet.Config; \
    elif [ -n "$NUGET_TOKEN" ]; then \
      dotnet nuget update source github \
        --username "${NUGET_USERNAME:-github}" \
        --password "$NUGET_TOKEN" \
        --store-password-in-clear-text \
        --configfile NuGet.Config; \
    else \
      echo "CustomCodeFramework local feed is empty and no GitHub Packages token was provided." >&2; \
      exit 1; \
    fi

RUN dotnet restore src/Dhole.Agent.Api/Dhole.Agent.Api.csproj --configfile NuGet.Config \
    && dotnet restore src/Dhole.Agent.Workers/Dhole.Agent.Workers.csproj --configfile NuGet.Config

FROM build AS publish-api
RUN dotnet publish src/Dhole.Agent.Api/Dhole.Agent.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish/api

FROM build AS publish-worker
RUN dotnet publish src/Dhole.Agent.Workers/Dhole.Agent.Workers.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish/worker

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api-final
WORKDIR /app
COPY --from=publish-api /app/publish/api ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Dhole.Agent.Api.dll"]

# Includes Chromium and its Linux dependencies for background Playwright automation.
FROM mcr.microsoft.com/playwright/dotnet:v1.55.0-noble AS worker-final
WORKDIR /app
COPY --from=publish-worker /app/publish/worker ./
ENV Browser__ProfilesPath=/data/browser-profiles
VOLUME ["/data/browser-profiles"]
ENTRYPOINT ["dotnet", "Dhole.Agent.Workers.dll"]
