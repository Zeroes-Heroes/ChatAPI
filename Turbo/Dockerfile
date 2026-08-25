FROM mcr.microsoft.com/dotnet/aspnet:8.0.30 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

RUN apt-get update \
    && apt-get install -y --no-install-recommends tini \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:8.0.424 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Turbo/Turbo.csproj", "Turbo/"]
RUN dotnet restore "./Turbo/Turbo.csproj"
COPY . .
WORKDIR "/src/Turbo"
RUN dotnet build "./Turbo.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Turbo.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

USER app

STOPSIGNAL SIGTERM

HEALTHCHECK --interval=30s --timeout=3s --start-period=20s --retries=3 \
    CMD ["/bin/bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080"]

ENTRYPOINT ["/usr/bin/tini", "--", "dotnet", "Turbo.dll"]
