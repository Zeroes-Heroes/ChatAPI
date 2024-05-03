FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER app
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Development
WORKDIR /src
COPY ["./ChatAPI.WebAPI/WebAPI.csproj", "ChatAPI.WebAPI/"]
COPY ["./ChatAPI.Infrastructure/Infrastructure.csproj", "ChatAPI.Infrastructure/"]
COPY ["./ChatAPI.Domain/Domain.csproj", "ChatAPI.Domain/"]
COPY ["./ChatAPI.Application/Application.csproj", "ChatAPI.Application/"]

RUN dotnet restore "./ChatAPI.WebAPI/ChatAPI.WebAPI.csproj"
COPY . .
WORKDIR "/src/ChatAPI.WebAPI"
RUN dotnet build "./ChatAPI.WebAPI.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Development
RUN dotnet publish "./ChatAPI.WebAPI.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ChatAPI.WebAPI.dll"]