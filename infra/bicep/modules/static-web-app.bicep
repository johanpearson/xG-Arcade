// Frontend hosting on Azure Static Web Apps, Free tier.
// The actual build/deploy is done by the Static Web Apps GitHub Actions
// integration (see /.github/workflows/deploy.yml), not by this template —
// this module only provisions the resource itself.

@description('Azure region for this resource. Static Web Apps only supports a subset of regions.')
param location string

@description('Base name used to derive resource names')
param appName string

@description('Environment tag, e.g. "prod" or "dev"')
param environmentTag string = 'prod'

var staticWebAppName = '${appName}-web-${environmentTag}'

resource staticWebApp 'Microsoft.Web/staticSites@2025-03-01' = {
  name: staticWebAppName
  location: location
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {
    // Repository/branch/build config are intentionally left unset here;
    // the GitHub Actions deploy workflow supplies the deployment token and
    // build output directly. Setting them here would fight the workflow
    // for control of the same deployment.
  }
}

output staticWebAppName string = staticWebApp.name
output defaultHostname string = staticWebApp.properties.defaultHostname
