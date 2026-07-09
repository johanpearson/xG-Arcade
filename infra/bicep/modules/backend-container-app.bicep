// The backend API as a Container App on the Consumption plan.
// Deliberately has zero Container-Apps-specific application code dependency
// (see ADR-0004 / CLAUDE.md) — this module just runs a plain container.

@description('Azure region for this resource')
param location string

@description('Base name used to derive resource names')
param appName string

@description('Environment tag, e.g. "prod" or "dev"')
param environmentTag string = 'prod'

@description('Resource ID of the Container Apps environment to deploy into')
param containerAppsEnvironmentId string

@description('Full image reference, e.g. ghcr.io/org/xg-arcade-api:sha-abc123')
param containerImage string

@description('GHCR username used for registry authentication')
param registryUsername string

@secure()
@description('GHCR personal access token or GITHUB_TOKEN with read:packages scope')
param registryPassword string

@secure()
@description('Supabase Postgres connection string')
param databaseConnectionString string

@secure()
@description('Supabase project JWT secret, used to validate incoming auth tokens')
param supabaseJwtSecret string

@description('Frontend origin (scheme + host) allowed by CORS, e.g. https://xg-arcade-dev.azurestaticapps.net. Empty until the Static Web App\'s hostname is known (see "post-deploy secrets" in infra/README.md), which means CORS allows nothing yet — safe default, not a functional requirement until the frontend is deployed.')
param corsAllowedOrigin string = ''

@description('Minimum replica count. Keep at 0 for max cost savings; raise to 1 if scheduled-job cold starts (see implementation-document.md open questions) become an issue')
param minReplicas int = 0

var containerAppName = '${appName}-api-${environmentTag}'

resource backendApi 'Microsoft.App/containerApps@2026-01-01' = {
  name: containerAppName
  location: location
  properties: {
    managedEnvironmentId: containerAppsEnvironmentId
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        allowInsecure: false
      }
      registries: [
        {
          server: 'ghcr.io'
          username: registryUsername
          passwordSecretRef: 'registry-password'
        }
      ]
      secrets: [
        {
          name: 'registry-password'
          value: registryPassword
        }
        {
          name: 'database-connection-string'
          value: databaseConnectionString
        }
        {
          name: 'supabase-jwt-secret'
          value: supabaseJwtSecret
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: containerImage
          env: [
            {
              name: 'ConnectionStrings__Database'
              secretRef: 'database-connection-string'
            }
            {
              name: 'Auth__SupabaseJwtSecret'
              secretRef: 'supabase-jwt-secret'
            }
            {
              name: 'Cors__AllowedOrigins'
              value: corsAllowedOrigin
            }
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: 2
      }
    }
  }
}

output fqdn string = backendApi.properties.configuration.ingress.fqdn
