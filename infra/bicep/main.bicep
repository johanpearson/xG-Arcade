// Main orchestration template. Composes the modules under ./modules/
// rather than defining resources flat, so each concern (environment,
// backend, frontend) can be reasoned about and changed independently.
// See ADR-0004 and docs/architecture-document.md §9 for the rationale.

targetScope = 'resourceGroup'

@description('Azure region for all resources')
param location string = resourceGroup().location

@description('Base name used to derive all resource names, e.g. "xg-arcade"')
param appName string

@description('Environment tag, e.g. "prod" or "dev"')
param environmentTag string = 'prod'

@description('Full backend image reference, e.g. ghcr.io/org/xg-arcade-api:sha-abc123')
param containerImage string

@description('GHCR username used for registry authentication')
param registryUsername string

@secure()
param registryPassword string

@secure()
param databaseConnectionString string

@secure()
param supabaseJwtSecret string

@description('Minimum backend replica count. See modules/backend-container-app.bicep for guidance.')
param minReplicas int = 0

module containerAppsEnvironment 'modules/container-apps-environment.bicep' = {
  name: 'containerAppsEnvironment'
  params: {
    location: location
    appName: appName
    environmentTag: environmentTag
  }
}

module backendApi 'modules/backend-container-app.bicep' = {
  name: 'backendApi'
  params: {
    location: location
    appName: appName
    environmentTag: environmentTag
    containerAppsEnvironmentId: containerAppsEnvironment.outputs.environmentId
    containerImage: containerImage
    registryUsername: registryUsername
    registryPassword: registryPassword
    databaseConnectionString: databaseConnectionString
    supabaseJwtSecret: supabaseJwtSecret
    minReplicas: minReplicas
  }
}

module staticWebApp 'modules/static-web-app.bicep' = {
  name: 'staticWebApp'
  params: {
    location: location
    appName: appName
    environmentTag: environmentTag
  }
}

output backendFqdn string = backendApi.outputs.fqdn
output frontendHostname string = staticWebApp.outputs.defaultHostname
output staticWebAppName string = staticWebApp.outputs.staticWebAppName
