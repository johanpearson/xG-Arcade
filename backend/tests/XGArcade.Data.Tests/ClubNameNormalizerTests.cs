namespace XGArcade.Data.Tests;

// Extracted (2026-09-04, REQ-1406/1407 bug fix) alongside
// XGArcade.Data.ClubNameNormalizer itself — see that class's own doc
// comment for why. These cases mirror WikidataClientTests
// .REQ1203_QueryPlayerCareerStintsByQidsAsync_NormalizesClubLegalSuffix's
// existing end-to-end coverage of the same behavior; this adds direct,
// isolated unit coverage now that the logic is a standalone public class.
public class ClubNameNormalizerTests
{
    [TestCase("Liverpool FC", "Liverpool")]
    [TestCase("Liverpool A.F.C.", "Liverpool")]
    [TestCase("Bournemouth AFC", "Bournemouth")]
    [TestCase("Chelsea FC", "Chelsea")]
    public void StripLegalSuffix_StripsTrailingLegalSuffixToken(string rawClubName, string expected)
    {
        Assert.That(ClubNameNormalizer.StripLegalSuffix(rawClubName), Is.EqualTo(expected));
    }

    [Test]
    public void StripLegalSuffix_LeadingAfcIsNotStripped()
    {
        Assert.That(ClubNameNormalizer.StripLegalSuffix("AFC Bournemouth"), Is.EqualTo("AFC Bournemouth"));
    }

    [Test]
    public void StripLegalSuffix_DoesNotMatchSuffixAsSubstringInsideAWord()
    {
        Assert.That(ClubNameNormalizer.StripLegalSuffix("Deportivo Alavés"), Is.EqualTo("Deportivo Alavés"));
    }

    [TestCase("FC", "FC")]
    [TestCase("AFC", "AFC")]
    public void StripLegalSuffix_LabelThatIsExactlyTheSuffixTokenIsLeftUntouched(string rawClubName, string expected)
    {
        Assert.That(ClubNameNormalizer.StripLegalSuffix(rawClubName), Is.EqualTo(expected));
    }

    [Test]
    public void StripLegalSuffix_TrimsSurroundingWhitespace()
    {
        Assert.That(ClubNameNormalizer.StripLegalSuffix("  Chelsea  "), Is.EqualTo("Chelsea"));
    }

    [Test]
    public void StripLegalSuffix_NameWithNoSuffixIsUnchangedAsideFromTrimming()
    {
        Assert.That(ClubNameNormalizer.StripLegalSuffix("West Bromwich Albion"), Is.EqualTo("West Bromwich Albion"));
    }
}
