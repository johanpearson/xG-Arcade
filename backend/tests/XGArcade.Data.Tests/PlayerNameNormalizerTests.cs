namespace XGArcade.Data.Tests;

// S-006 (docs/backlog.md): the normalize() function implementation-document.md
// §6 defines for REQ-207/208's guess matching (Tier 1), pulled forward now
// only because PlayerAlias.NormalizedAlias (populated from Wikidata's
// skos:altLabel) needs somewhere to call it from.
public class PlayerNameNormalizerTests
{
    [Test]
    public void Normalize_LowercasesInput()
    {
        Assert.That(PlayerNameNormalizer.Normalize("Thierry Henry"), Is.EqualTo("thierry henry"));
    }

    [Test]
    public void Normalize_StripsDiacritics()
    {
        Assert.That(PlayerNameNormalizer.Normalize("Kaká"), Is.EqualTo("kaka"));
    }

    [Test]
    public void Normalize_CollapsesRepeatedWhitespace()
    {
        Assert.That(PlayerNameNormalizer.Normalize("Thierry   Henry"), Is.EqualTo("thierry henry"));
    }

    [Test]
    public void Normalize_TrimsLeadingAndTrailingWhitespace()
    {
        Assert.That(PlayerNameNormalizer.Normalize("  Pele  "), Is.EqualTo("pele"));
    }
}
