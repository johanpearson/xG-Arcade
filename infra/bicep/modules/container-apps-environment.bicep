// Container Apps environment + the Log Analytics workspace it requires.
// Kept as its own module because it's shared infrastructure that other
// container apps (e.g. a future second game's own job) could reuse without
// duplicating the environment.

@description('Azure region for all resources in this module')
param location string

@description('Base name used to derive resource names, e.g. "xg-arcade"')
param appName string

@description('Environment tag, e.g. "prod" or "dev"')
param environmentTag string = 'prod'

var logAnalyticsName = '${appName}-logs-${environmentTag}'
var containerAppsEnvName = '${appName}-env-${environmentTag}'

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2025-07-01' = {
  name: logAnalyticsName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2026-01-01' = {
  name: containerAppsEnvName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

output environmentId string = containerAppsEnvironment.id
output environmentName string = containerAppsEnvironment.name
