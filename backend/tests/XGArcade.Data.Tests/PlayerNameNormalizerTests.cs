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

    // Bug fix regression (2026-08-02, reported via xG Path user testing):
    // Ø is NOT decomposable under NFKD (unlike é/ñ/etc.), so before this fix
    // "Ødegaard" normalized to "ødegaard" and never matched what a real
    // player types. Martin Ødegaard is a real, currently-active player —
    // this is the exact reported shape, not a synthetic example.
    [Test]
    public void Normalize_NonDecomposableLetter_MatchesPlayerTypedAsciiSpelling()
    {
        Assert.That(PlayerNameNormalizer.Normalize("Ødegaard"), Is.EqualTo(PlayerNameNormalizer.Normalize("Odegaard")));
        Assert.That(PlayerNameNormalizer.Normalize("Ødegaard"), Is.EqualTo("odegaard"));
    }

    // Deliberately narrow scope check: this fix maps distinct non-decomposable
    // LETTERS to their ASCII transliteration only — it must never grow into
    // fuzzy/edit-distance typo tolerance (REQ-208's deliberately-deferred
    // scope, MVP-SCOPE.md). An extra vowel is a genuine typo, not a letter
    // substitution, and must still normalize to something different.
    [Test]
    public void Normalize_NonDecomposableLetter_DoesNotToleratePlainTypos()
    {
        Assert.That(PlayerNameNormalizer.Normalize("Ødegaard"), Is.Not.EqualTo(PlayerNameNormalizer.Normalize("Oodegaard")));
    }

    // The rest of the non-decomposable-letter list this fix covers —
    // Æ/Œ/Đ/Ł/ß/Þ — each a distinct Unicode letter in its own right, not a
    // base+combining-mark pair, so each needs its own explicit map entry
    // rather than being covered "for free" by the existing NFKD/
    // NonSpacingMark pass.
    [TestCase("Æ", "ae")]
    [TestCase("æ", "ae")]
    [TestCase("Œuvre", "oeuvre")]
    [TestCase("Đorđe", "dorde")]
    [TestCase("Łukasz", "lukasz")]
    [TestCase("Straße", "strasse")]
    [TestCase("Þór", "thor")]
    public void Normalize_OtherNonDecomposableLetters_TransliteratesToAscii(string input, string expected)
    {
        Assert.That(PlayerNameNormalizer.Normalize(input), Is.EqualTo(expected));
    }
}
