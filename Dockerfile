# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

ARG GITHUB_ACTOR=github-actions

COPY NuGet.Config ./
COPY src ./src

RUN --mount=type=secret,id=github_token,required=true \
    export NuGetPackageSourceCredentials_github="Username=${GITHUB_ACTOR};Password=$(cat /run/secrets/github_token);ValidAuthenticationTypes=Basic" && \
    dotnet restore src/Dhole.Agent.Api/Dhole.Agent.Api.csproj --configfile /src/NuGet.Config && \
    dotnet restore src/Dhole.Agent.Workers/Dhole.Agent.Workers.csproj --configfile /src/NuGet.Config

FROM build AS publish-api
RUN --mount=type=secret,id=github_token,required=true \
    export NuGetPackageSourceCredentials_github="Username=${GITHUB_ACTOR};Password=$(cat /run/secrets/github_token);ValidAuthenticationTypes=Basic" && \
    dotnet publish src/Dhole.Agent.Api/Dhole.Agent.Api.csproj \
      --configuration Release \
      --no-restore \
      --output /app/publish/api

FROM build AS publish-worker
RUN --mount=type=secret,id=github_token,required=true \
    export NuGetPackageSourceCredentials_github="Username=${GITHUB_ACTOR};Password=$(cat /run/secrets/github_token);ValidAuthenticationTypes=Basic" && \
    dotnet publish src/Dhole.Agent.Workers/Dhole.Agent.Workers.csproj \
      --configuration Release \
      --no-restore \
      --output /app/publish/worker

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api-final
WORKDIR /app
COPY --from=publish-api /app/publish/api ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Dhole.Agent.Api.dll"]

# Playwright 1.55 ships the browsers and Linux dependencies required by the
# worker, but its pinned image only contains .NET 8. Overlay the .NET 10
# ASP.NET runtime so the net10.0 worker and its Microsoft.AspNetCore.App
# framework reference can start without losing the Playwright browser stack.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS worker-dotnet-runtime
FROM mcr.microsoft.com/playwright/dotnet:v1.55.0-noble AS worker-final
COPY --from=worker-dotnet-runtime /usr/share/dotnet /usr/share/dotnet
WORKDIR /app
COPY --from=publish-worker /app/publish/worker ./
ENV Browser__ProfilesPath=/data/browser-profiles
VOLUME ["/data/browser-profiles"]
ENTRYPOINT ["xvfb-run", "-a", "dotnet", "Dhole.Agent.Workers.dll"]
