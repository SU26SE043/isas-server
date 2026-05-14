FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/gateway/Isas.Gateway/Isas.Gateway.csproj", "src/gateway/Isas.Gateway/"]
COPY ["src/shared/Isas.Shared/Isas.Shared.csproj", "src/shared/Isas.Shared/"]
RUN dotnet restore "src/gateway/Isas.Gateway/Isas.Gateway.csproj"
COPY . .
WORKDIR "/src/src/gateway/Isas.Gateway"
RUN dotnet build "./Isas.Gateway.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Isas.Gateway.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Isas.Gateway.dll"]
