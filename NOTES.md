# Notes

A running log of small, practical context that isn't worth a full ADR but
is worth remembering — gotchas discovered while building, quirks of a
third-party service, things that took longer than expected, "don't do X,
here's why" reminders. Think of this as the difference between:

- **`docs/decisions/*.md` (ADRs)**: formal, durable decisions with
  alternatives considered — "we chose X over Y because Z"
- **`docs/CHANGELOG.md`**: what changed in the *documentation*, dated
- **`NOTES.md` (this file)**: informal, accumulated context that doesn't
  fit either — the stuff you'd otherwise only remember by re-reading old
  chat history

Add an entry whenever something surprises you enough that you'd want to
have known it going in. Prune entries that stop being relevant (e.g. a
workaround for a bug that got fixed upstream) rather than letting this
grow forever — unlike the CHANGELOG, this file doesn't need to preserve
history, just current usefulness.

## Format

```
### YYYY-MM-DD — short title
What happened / what to know. Keep it to a few sentences.
```

## Entries

### 2026-07-09 — Microsoft.AspNetCore.OpenApi dropped from XGArcade.Api (S-001)
The default `dotnet new webapi` template pulls in `Microsoft.AspNetCore.OpenApi`
10.0.9, which transitively depends on `Microsoft.OpenApi` 2.0.0 — flagged by
NuGet (NU1903) as a known high-severity vulnerability
(GHSA-v5pm-xwqc-g5wc) across the *entire* 2.x line (checked every 2.x patch
release up to 2.6.1, all vulnerable). The 3.x line fixes it, but breaks the
AspNetCore.OpenApi 10.0.9 source generator (`OpenApiXmlCommentSupport`),
which was compiled against 2.x's API shape (`CS0200: 'IOpenApiMediaType.Example'
cannot be assigned to`). Since nothing in Tier 0 needs OpenAPI generation yet,
removed the package entirely rather than pinning around it — no
`AddOpenApi()`/`MapOpenApi()` calls, no Swagger UI. **If a future story needs
this** (e.g. `implementation-document.md` §4's planned typed API client,
"possibly generated via OpenAPI"), check whether a compatible
`Microsoft.AspNetCore.OpenApi` version exists yet before re-adding it — don't
just re-run `dotnet add package Microsoft.AspNetCore.OpenApi` and assume it
still works.

### 2026-07-09 — migrate-and-seed is a no-op stub until S-003 (S-002)
`ci.yml`'s e2e-tests job runs `dotnet run -- migrate-and-seed` before
starting the API, but `XGArcade.Data`'s EF Core context/migrations don't
exist until S-003. `Program.cs` special-cases `args is ["migrate-and-seed"]`
to print a message and exit 0 rather than start Kestrel — this is what
stops S-001's original problem (that exact command hanging forever, since
`dotnet run -- migrate-and-seed` used to just start the normal server and
never return) from coming back. **When S-003 lands**, replace the stub body
with the real migration/seed call — don't leave it silently doing nothing
once there's something for it to do.

### 2026-07-09 — `dotnet run`'s launch profile overrides `ASPNETCORE_URLS` (S-002)
`ci.yml`'s e2e-tests job set `ASPNETCORE_URLS: http://localhost:8080` as a
step env var, but the API still bound to `:5028` and the health-wait curl
loop timed out — confirmed via CI logs, not locally (see the next note).
Cause: `dotnet run` without `--no-launch-profile` reads
`Properties/launchSettings.json`'s `applicationUrl` and uses that in
preference to an externally-set `ASPNETCORE_URLS`, even though the env var
is already present in the process environment before `dotnet run` starts.
Fixed by adding `--no-launch-profile` to the "Start API" step's `dotnet run`
command. If a future workflow step starts the API via `dotnet run` and sets
`ASPNETCORE_URLS`/`ASPNETCORE_HTTP_PORTS` to pick the port, add
`--no-launch-profile` there too — this isn't a one-off, it'll bite any
`dotnet run` invocation that also sets the port via env var.

### 2026-07-09 — dotnet SDK isn't installable in this sandbox (S-002)
This session's outbound network policy blocks `builds.dotnet.microsoft.com`
(confirmed via the agent proxy's `/__agentproxy/status`, a 403 policy
denial, not a transient failure) — `dotnet-install.sh` can't run here.
`nuget.org` itself is reachable (used to verify package versions exist
before pinning them), just not the SDK installer. Backend C# changes in
this session were written and manually reviewed but never locally compiled
or test-run; `ci.yml`'s `backend-tests`/`e2e-tests` jobs are the actual
verification once pushed. If a future session hits the same wall, don't
retry the download — this is an org policy boundary, not a flaky network.
