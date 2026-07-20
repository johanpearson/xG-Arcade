namespace XGArcade.Data.Tests;

// REQ-208: the edit-distance metric behind guess-time fuzzy typo tolerance
// (GridGameModule.FindFuzzyCandidatesAsync/MaxEditDistance). This class only
// tests the metric itself — the "which threshold, and against which
// candidate pool" decisions built on top of it are GridGameModuleTests'
// job (see its "---- REQ-208" section).
public class NameEditDistanceTests
{
    [Test]
    public void Distance_IdenticalStrings_IsZero()
    {
        Assert.That(NameEditDistance.Distance("zidane", "zidane"), Is.EqualTo(0));
    }

    [Test]
    public void Distance_EmptyStrings_IsZero()
    {
        Assert.That(NameEditDistance.Distance("", ""), Is.EqualTo(0));
    }

    [Test]
    public void Distance_OneEmptyString_IsLengthOfTheOther()
    {
        Assert.That(NameEditDistance.Distance("", "kaka"), Is.EqualTo(4));
        Assert.That(NameEditDistance.Distance("kaka", ""), Is.EqualTo(4));
    }

    [TestCase("zidane", "zidan", 1, TestName = "Distance_SingleDeletion")]
    [TestCase("henry", "henri", 1, TestName = "Distance_SingleSubstitution")]
    [TestCase("kaeka", "kaka", 1, TestName = "Distance_SingleInsertion")]
    [TestCase("ronaldinho", "ronaldinoh", 2, TestName = "Distance_TrailingTransposition")]
    [TestCase("ronaldo", "rivaldo", 2, TestName = "Distance_TwoSubstitutions")]
    [TestCase("thierry henry", "nicolas anelka", 12, TestName = "Distance_GenuinelyDifferentNames_IsLarge")]
    public void Distance_MatchesExpectedEditCount(string a, string b, int expected)
    {
        Assert.That(NameEditDistance.Distance(a, b), Is.EqualTo(expected));
    }

    [Test]
    public void Distance_IsSymmetric()
    {
        Assert.That(NameEditDistance.Distance("ronaldo", "rivaldo"), Is.EqualTo(NameEditDistance.Distance("rivaldo", "ronaldo")));
    }
}
