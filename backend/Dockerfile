# Build context is the backend/ directory itself (see .github/workflows/deploy.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY XGArcade.sln ./
COPY src/XGArcade.Api/XGArcade.Api.csproj src/XGArcade.Api/
COPY src/XGArcade.Core/XGArcade.Core.csproj src/XGArcade.Core/
COPY src/XGArcade.Games.XGGrid/XGArcade.Games.XGGrid.csproj src/XGArcade.Games.XGGrid/
COPY src/XGArcade.Data/XGArcade.Data.csproj src/XGArcade.Data/
COPY src/XGArcade.DataSync/XGArcade.DataSync.csproj src/XGArcade.DataSync/
RUN dotnet restore src/XGArcade.Api/XGArcade.Api.csproj

COPY src/ src/
RUN dotnet publish src/XGArcade.Api/XGArcade.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Matches infra/bicep/modules/backend-container-app.bicep's targetPort.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "XGArcade.Api.dll"]
