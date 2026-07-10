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

    // REQ-208: "punctuation ... ignored" — stripped outright, not replaced
    // with a space, so a punctuation mark never introduces a word split that
    // wasn't already there.
    [TestCase("O'Neil", "oneil")]
    [TestCase("Jean-Pierre", "jeanpierre")]
    [TestCase("Sane.", "sane")]
    [TestCase("D'Angelo, Jr.", "dangelo jr")]
    public void Normalize_StripsPunctuation(string input, string expected)
    {
        Assert.That(PlayerNameNormalizer.Normalize(input), Is.EqualTo(expected));
    }

    // Edge cases: punctuation stripping runs before the existing
    // trim/collapse-whitespace steps, so a name that's entirely (or only
    // leading/trailing) punctuation must still resolve cleanly rather than
    // leaving stray whitespace or throwing.
    [TestCase("...", "")]
    [TestCase("-", "")]
    [TestCase("'Pele'", "pele")]
    public void Normalize_HandlesPunctuationOnlyOrSurroundingInput(string input, string expected)
    {
        Assert.That(PlayerNameNormalizer.Normalize(input), Is.EqualTo(expected));
    }
}
