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

@description('Supabase project URL (e.g. https://xxxx.supabase.co) — the backend calls its Auth REST API to mediate signup/login (ADR-0013), and validates incoming auth tokens against this project\'s JWKS endpoint (ADR-0017). Not sensitive on its own (the same URL a frontend would use), but grouped with the other Supabase params for clarity.')
param supabaseUrl string

@description('Path appended to supabaseUrl to fetch Supabase\'s JWKS document for JWT validation (ADR-0017) — override only if live testing shows Supabase\'s actual path differs from the documented default. Not secret: a JWKS endpoint publishes public keys by design.')
param supabaseJwksPath string = '/auth/v1/.well-known/jwks.json'

@secure()
@description('Supabase project anon/publishable API key, sent as the "apikey"/Authorization header on Auth REST calls (ADR-0013). Publishable by Supabase\'s own design (safe in frontend bundles too), marked @secure() here only to keep it out of deployment logs, not because it is a true secret.')
param supabaseAnonKey string

@secure()
@description('Supabase project service_role API key (REQ-710, ADR-0026) — a genuinely privileged credential (bypasses Row Level Security entirely), used only by SupabaseAuthClient.DeleteUserAsync\'s call to Supabase\'s Admin API to delete a user\'s identity on account deletion. Unlike supabaseAnonKey above, this is a true secret: never send it to the frontend, never log it.')
param supabaseServiceRoleKey string

@secure()
@description('Shared bearer token authorizing calls to /internal/* endpoints (generate-grid-round.yml, generate-path-round.yml, and the /internal/sync-players endpoint, called by hand or a future re-added workflow) — same value as the INTERNAL_JOB_TOKEN GitHub secret.')
param internalJobToken string

@secure()
@description('Fine-grained GitHub PAT scoped to Issues:write on this one repo only (REQ-903/ADR-0064/COMP-12) — used by Core.IncidentReporting to create GitHub issues from in-app bug reports, same value as the INCIDENT_REPORT_PAT GitHub secret. Optional/defaults to empty: unlike the Supabase secrets above, this Tier 1 pull-forward has no manual secret guaranteed to be provisioned in every environment yet — an empty value means POST /incidents fails closed per-request (GitHubIssueClient\'s own check), never a deploy failure or app crash.')
param githubIncidentReportToken string = ''

@description('Frontend origin (scheme + host) allowed by CORS, e.g. https://xg-arcade-dev.azurestaticapps.net. Empty until the Static Web App\'s hostname is known (see "post-deploy secrets" in infra/README.md), which means CORS allows nothing yet — safe default, not a functional requirement until the frontend is deployed.')
param corsAllowedOrigin string = ''

@description('Comma-separated Supabase auth user ids (the JWT "sub" claim) authorized as admins (S-012, docs/backlog.md) — see AdminAuthorizationHandler and implementation-document.md §4. Config-based, not a database role, per architecture-document.md. Empty by default, which means no admin endpoint succeeds for anyone yet — safe default, matching corsAllowedOrigin\'s pattern above, until an admin\'s id is actually filled in. Not marked @secure(): these are the same auth user ids already visible to that admin themselves, comparable to an email address, not a true secret — kept out of source control anyway since they identify a real person.')
param adminUserIds string = ''

@description('Minimum replica count. Keep at 0 for max cost savings; raise to 1 if scheduled-job cold starts (see implementation-document.md open questions) become an issue')
param minReplicas int = 0

@description('Default RoundSchedulingOptions.RoundDuration in hours (REQ-301, ADR-0027) — change this (no code/image change needed) for a lasting adjustment; generate-grid-round.yml\'s (and, for xg-path, generate-path-round.yml\'s) own workflow_dispatch round_duration_hours input overrides it for a single one-off generation call instead, scoped to that workflow\'s own GameKey only (S-136/ADR-0072).')
param roundDurationHours int = 48

@description('GridLiveLookupOptions.Enabled (REQ-211, ADR-0070) — REQ-211\'s guess-time live-lookup fallback. true (default) preserves existing behavior. Same "edit this default, push to main, deploy.yml redeploys with no code/image change" pattern as roundDurationHours above — the sanctioned way to toggle this operationally.')
param gridLiveLookupEnabled bool = true

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
          name: 'supabase-anon-key'
          value: supabaseAnonKey
        }
        {
          name: 'supabase-service-role-key'
          value: supabaseServiceRoleKey
        }
        {
          name: 'internal-job-token'
          value: internalJobToken
        }
        {
          name: 'github-incident-report-token'
          value: githubIncidentReportToken
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
              name: 'Supabase__Url'
              value: supabaseUrl
            }
            {
              name: 'Auth__SupabaseJwksPath'
              value: supabaseJwksPath
            }
            {
              name: 'Supabase__AnonKey'
              secretRef: 'supabase-anon-key'
            }
            {
              name: 'Supabase__ServiceRoleKey'
              secretRef: 'supabase-service-role-key'
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
              name: 'GitHub__IncidentReportToken'
              secretRef: 'github-incident-report-token'
            }
            {
              name: 'Admin__UserIds'
              value: adminUserIds
            }
            {
              name: 'RoundScheduling__RoundDurationHours'
              value: string(roundDurationHours)
            }
            {
              name: 'GridLiveLookup__Enabled'
              value: string(gridLiveLookupEnabled)
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
