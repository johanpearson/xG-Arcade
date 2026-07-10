using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace XGArcade.Api.Auth;

// Supabase's JWT Signing Keys system (rotating asymmetric keys, identified
// by a `kid` header claim — see ADR-0017) exposes only a bare JWKS document
// at {Supabase:Url}/auth/v1/.well-known/jwks.json, not a full OIDC discovery
// document. The framework's stock OpenIdConnectConfigurationRetriever
// expects a discovery document containing a "jwks_uri" field, so it can't
// be pointed at the JWKS endpoint directly — this retriever fetches the
// JWKS itself and wraps it in a minimal OpenIdConnectConfiguration (setting
// .JsonWebKeySet auto-populates .SigningKeys), which is exactly what
// OpenIdConnectConfigurationRetriever does internally once it has resolved
// a jwks_uri. Used via a ConfigurationManager<OpenIdConnectConfiguration>
// (Program.cs) so JwtBearerHandler gets the framework's own async
// caching/refresh machinery instead of a hand-rolled synchronous resolver.
public class SupabaseJwksConfigurationRetriever : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
        string address, IDocumentRetriever retriever, CancellationToken cancel)
    {
        string jwksJson;
        try
        {
            jwksJson = await retriever.GetDocumentAsync(address, cancel);
        }
        catch (Exception ex)
        {
            // Production's only diagnostic tool for this is the log stream
            // (NOTES.md, 2026-07-10) — this must be unambiguous on first
            // read, not another bare signature failure to re-diagnose from
            // scratch (see ADR-0017's rollout-risk note).
            throw new InvalidOperationException(
                $"Failed to fetch the Supabase JWKS document from '{address}'. " +
                "If Supabase's JWKS endpoint path has changed, override it via " +
                "the Auth:SupabaseJwksPath configuration value.", ex);
        }

        JsonWebKeySet jwks;
        try
        {
            jwks = new JsonWebKeySet(jwksJson);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Fetched a document from '{address}' but it isn't a valid JWKS " +
                "(expected a JSON object with a \"keys\" array).", ex);
        }

        // Setting JsonWebKeySet alone does not populate SigningKeys (verified
        // directly against this project's resolved
        // Microsoft.IdentityModel.Protocols.OpenIdConnect 8.0.1 — it's not
        // documented behavior, just what the setter actually does) — the
        // JwtBearer signature-validation path reads .SigningKeys, not
        // .JsonWebKeySet, so this must be populated explicitly.
        var configuration = new OpenIdConnectConfiguration { JsonWebKeySet = jwks };
        foreach (var signingKey in jwks.GetSigningKeys())
        {
            configuration.SigningKeys.Add(signingKey);
        }

        return configuration;
    }
}
