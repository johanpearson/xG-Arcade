namespace XGArcade.Api.CompositionRoot;

// Single source of truth for WikidataClient's HttpClient config — shared by
// the real AddHttpClient<IWikidataClient, WikidataClient> DI registration
// (ServiceRegistration.cs) and every CLI verb in CliVerbDispatcher.cs that
// builds a WikidataClient directly (they can't use that DI registration,
// since they run before WebApplication.CreateBuilder ever runs).
public static class WikidataHttpClientConfiguration
{
    public static void Configure(HttpClient client)
    {
        client.BaseAddress = new Uri("https://query.wikidata.org/");
        // WDQS's own etiquette guidance asks for an identifiable User-Agent
        // rather than a generic HttpClient default.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "xG-Arcade/1.0 (Tier 0 grid data sync; see docs/decisions/0011-wikidata-first-lookup-waterfall.md)");
    }
}
