using Spotlight.Search;
using Xunit;

namespace Spotlight.Tests;

public class FuzzyMatcherTests
{
    private static int Score(string query, string name) => FuzzyMatcher.Score(FuzzyMatcher.NormalizeQuery(query), name);

    [Fact]
    public void NoMatchReturnsMinusOne()
    {
        Assert.Equal(-1, Score("xyz", "Google Chrome"));
        Assert.Equal(-1, Score("chromes", "Chrome"));
    }

    [Fact]
    public void ExactBeatsPrefixBeatsWordStartBeatsSubstringBeatsFuzzy()
    {
        int exact = Score("paint", "Paint");
        int prefix = Score("paint", "Paint 3D");
        int wordStart = Score("paint", "Microsoft Paint");
        int substring = Score("aint", "Paint");
        int fuzzy = Score("pnt", "Paint");
        Assert.True(exact > prefix, $"{exact} > {prefix}");
        Assert.True(prefix > wordStart, $"{prefix} > {wordStart}");
        Assert.True(wordStart > substring, $"{wordStart} > {substring}");
        Assert.True(substring > fuzzy, $"{substring} > {fuzzy}");
        Assert.True(fuzzy > 0);
    }

    [Theory]
    [InlineData("vsc", "Visual Studio Code")]
    [InlineData("gc", "Google Chrome")]
    [InlineData("ps", "PowerShell")]
    public void AcronymsMatchStrongly(string query, string name)
    {
        Assert.True(Score(query, name) >= 700, $"{query} → {name} scored {Score(query, name)}");
    }

    [Fact]
    public void AcronymBeatsLooseMatch()
    {
        Assert.True(Score("vsc", "Visual Studio Code") > Score("vsc", "csv_viewer_screens"));
    }

    [Fact]
    public void CaseInsensitive()
    {
        Assert.Equal(Score("chrome", "chrome"), Score("CHROME", "Chrome"));
    }

    [Fact]
    public void ShorterNamesWinTies()
    {
        Assert.True(Score("note", "Notepad") > Score("note", "Notepad++ Portable Edition"));
    }

    [Fact]
    public void HebrewNamesMatch()
    {
        Assert.True(Score("חשבונית", "חשבונית מרץ.pdf") > 800);
        Assert.True(Score("מרץ", "חשבונית מרץ.pdf") > 700);
    }

    [Fact]
    public void HebrewFinalLettersMatchRegularForms()
    {
        // "חשבון" ends in final nun (ן); inside "חשבונית" it's a regular nun (נ).
        Assert.True(Score("חשבון", "חשבונית מרץ.pdf") > 800);
    }

    [Theory]
    [InlineData("Google Chrome (2).lnk", "Google Chrome")]
    [InlineData("Google Chrome - Shortcut.lnk", "Google Chrome")]
    [InlineData("Steam.lnk", "Steam")]
    [InlineData("Movie (Director's Cut).lnk", "Movie (Director's Cut)")]
    public void ShortcutBaseNameStripsWindowsSuffixes(string file, string expected)
    {
        Assert.Equal(expected, SearchEngine.ShortcutBaseName(file));
    }
}
