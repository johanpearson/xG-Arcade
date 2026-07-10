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

@description('Supabase project URL (e.g. https://xxxx.supabase.co) — the backend calls its Auth REST API to mediate signup/login (ADR-0013). Not sensitive on its own (the same URL a frontend would use), but grouped with the other Supabase params for clarity.')
param supabaseUrl string

@secure()
@description('Supabase project anon/publishable API key, sent as the "apikey"/Authorization header on Auth REST calls (ADR-0013). Publishable by Supabase\'s own design (safe in frontend bundles too), marked @secure() here only to keep it out of deployment logs, not because it is a true secret.')
param supabaseAnonKey string

@secure()
@description('Shared bearer token authorizing calls to /internal/* endpoints (generate-round.yml, sync-players.yml) — same value as the INTERNAL_JOB_TOKEN GitHub secret.')
param internalJobToken string

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
        {
          name: 'supabase-anon-key'
          value: supabaseAnonKey
        }
        {
          name: 'internal-job-token'
          value: internalJobToken
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
              name: 'Supabase__Url'
              value: supabaseUrl
            }
            {
              name: 'Supabase__AnonKey'
              secretRef: 'supabase-anon-key'
            }
            {
              name: 'Cors__AllowedOrigins'
              value: corsAllowedOrigin
            }
            {
              name: 'Internal__JobToken'
              secretRef: 'internal-job-token'
            }
            {
              // Neither this module nor deploy.yml ever set this before
              // (NOTES.md, 2026-07-09) — ASP.NET Core defaults to
              // "Production" when unset, so the deployed Container App was
              // silently running as Production regardless of environmentTag,
              // meaning every non-Production-only endpoint (COMP-09's
              // force-close-round, S-007's /internal/grid/generate) was
              // unreachable there. "Dev" (not "Development") deliberately:
              // IsProduction() is false either way, but "Development" would
              // also flip IsDevelopment()-gated code (e.g. Auth:Mode=local-e2e)
              // on in a real deployed environment, which must never happen.
              name: 'ASPNETCORE_ENVIRONMENT'
              value: environmentTag == 'prod' ? 'Production' : 'Dev'
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
