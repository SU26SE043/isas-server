FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/services/Issa.AuthService/Issa.AuthService.csproj", "src/services/Issa.AuthService/"]
COPY ["src/shared/Issa.Shared/Issa.Shared.csproj", "src/shared/Issa.Shared/"]
RUN dotnet restore "src/services/Issa.AuthService/Issa.AuthService.csproj"
COPY . .
WORKDIR "/src/src/services/Issa.AuthService"
RUN dotnet build "./Issa.AuthService.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Issa.AuthService.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Issa.AuthService.dll"]
