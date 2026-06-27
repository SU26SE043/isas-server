FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/services/Isas.InterviewService/Isas.InterviewService.csproj", "src/services/Isas.InterviewService/"]
COPY ["src/shared/Isas.Shared/Isas.Shared.csproj", "src/shared/Isas.Shared/"]
RUN dotnet restore "src/services/Isas.InterviewService/Isas.InterviewService.csproj"
COPY . .
WORKDIR "/src/src/services/Isas.InterviewService"
RUN dotnet build "./Isas.InterviewService.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Isas.InterviewService.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Isas.InterviewService.dll"]
