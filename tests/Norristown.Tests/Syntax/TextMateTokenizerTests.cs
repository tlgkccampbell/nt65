using Norristown.LanguageServer;

namespace Norristown.Tests.Syntax;

/// <summary>
/// Checks <see cref="TextMateTokenizer"/> against small grammars, one TextMate feature at a time,
/// so that the grammar tests built on it check what VS Code would colour. Each expectation is the
/// behavior of vscode-textmate. Every scope is one letter, and each line's scopes are shown as a
/// string with one letter per character, or <c>.</c> for a character no rule scopes.
/// </summary>
public sealed class TextMateTokenizerTests
{
    [Fact]
    public void AMatchScopesWhatItMatchesAndNothingElse()
    {
        var grammar = Grammar(new TextMateRule(Match: "b+", Name: "b"));
        Assert.Equal(["..bb..b"], Scope(grammar, "a bb cb"));
    }

    [Fact]
    public void TheLeftmostMatchWinsOverARuleListedEarlier()
    {
        var grammar = Grammar(new TextMateRule(Match: "y", Name: "y"), new TextMateRule(Match: "x+", Name: "x"));
        Assert.Equal(["xx.y"], Scope(grammar, "xx y"));
    }

    [Fact]
    public void OnATieTheRuleListedEarlierWinsEvenWhenALaterOneIsLonger()
    {
        var grammar = Grammar(new TextMateRule(Match: "a", Name: "s"), new TextMateRule(Match: "ab+", Name: "l"));
        Assert.Equal(["s.."], Scope(grammar, "abb"));
    }

    [Fact]
    public void ScanningResumesAfterTheMatchSoMatchesDoNotOverlap()
    {
        var grammar = Grammar(new TextMateRule(Match: "aa", Name: "a"));
        Assert.Equal(["aaaa."], Scope(grammar, "aaaaa"));
    }

    [Fact]
    public void CapturesScopeTheirGroupsAndLeaveTheRestOfTheMatchUnscoped()
    {
        var grammar = Grammar(TextMateRule.Scoped(@"(k)\s+(n)(?:\s+(o))?", "k", "n", "o"));
        Assert.Equal(["k..n", "k.n.o"], Scope(grammar, "k  n", "k n o"));
    }

    [Fact]
    public void ANestedGroupsScopeIsTheOneKeptForItsCharacters()
    {
        var grammar = Grammar(TextMateRule.Scoped("(a(b)a)", "o", "i"));
        Assert.Equal(["oio"], Scope(grammar, "aba"));
    }

    [Fact]
    public void AGroupWithNoScopeLeavesItsCharactersUnscoped()
    {
        var grammar = Grammar(TextMateRule.Scoped("(a)(b)(c)", "a", null, "c"));
        Assert.Equal(["a.c"], Scope(grammar, "abc"));
    }

    [Fact]
    public void ALookbehindSeesTextAnEarlierMatchConsumed()
    {
        var grammar = Grammar(new TextMateRule(Match: "k", Name: "k"), TextMateRule.Scoped(@"(?<=k)(n)", "n"));
        Assert.Equal(["kn"], Scope(grammar, "kn"));
    }

    [Fact]
    public void ACaretMatchesOnlyAtTheStartOfTheLineNotWhereScanningResumes()
    {
        var grammar = Grammar(new TextMateRule(Match: "x", Name: "x"), TextMateRule.Scoped(@"^\s*(w)", "s"));
        Assert.Equal([".s.x.", "x.."], Scope(grammar, " w x ", "x w"));
    }

    [Fact]
    public void InsideABlockOnlyItsOwnRulesApplyAndItReachesAcrossLines()
    {
        var grammar = Grammar(
            TextMateRule.Block(@"(\{)", @"\}", ["o"], [new TextMateRule(Match: "i", Name: "i")]),
            new TextMateRule(Match: "[a-z]", Name: "w"));
        Assert.Equal(["w.o.i", "i...", "w.w"], Scope(grammar, "a {bi", "i b}", "i b"));
    }

    [Fact]
    public void ABlocksEndBeatsItsRulesAtTheSamePosition()
    {
        var grammar = Grammar(TextMateRule.Block(@"(\()", @"\)", ["p"], [new TextMateRule(Match: @"\)+", Name: "c")]));
        Assert.Equal(["p.."], Scope(grammar, "())"));
    }

    [Fact]
    public void ARuleThatMatchesEarlierThanTheEndStillWins()
    {
        var grammar = Grammar(TextMateRule.Block(@"(<)", ">", ["b"], [new TextMateRule(Match: ">?x", Name: "x")]));
        Assert.Equal(["bx.."], Scope(grammar, "<x>x"));
    }

    [Fact]
    public void AnEndOfDollarClosesTheBlockAtTheEndOfTheLine()
    {
        var grammar = Grammar(
            TextMateRule.Block("(d)", "$", ["d"], [new TextMateRule(Match: "a", Name: "i")]),
            new TextMateRule(Match: "a", Name: "o"));
        Assert.Equal(["d", "o"], Scope(grammar, "d", "a"));
        Assert.Equal(["dii", "o"], Scope(grammar, "daa", "a"));
    }

    [Fact]
    public void ALookaheadEndClosesTheBlockAndLeavesItsTextToTheRulesOutside()
    {
        var grammar = Grammar(
            TextMateRule.Block("(r)", @"(?=\{)", ["r"], [new TextMateRule(Match: @"\{", Name: "i")]),
            new TextMateRule(Match: @"\{", Name: "o"));
        Assert.Equal(["r.o"], Scope(grammar, "r {"));
    }

    [Fact]
    public void ANestedBlocksEndClosesOnlyThatBlock()
    {
        var parentheses = new TextMateRule(Begin: @"\(", End: @"\)", Patterns: [TextMateRule.Including("words")]);
        var grammar = Grammar(
            [TextMateRule.Block(@"(m)\(", @"\)", ["m"], [parentheses, TextMateRule.Including("words")]),
                new TextMateRule(Match: @"\)", Name: "c")],
            ("words", [new TextMateRule(Match: "[a-z]", Name: "w")]));
        Assert.Equal(["m..w.w.c"], Scope(grammar, "m((a)b))"));
    }

    [Fact]
    public void AnIncludeStandsForItsRulesInPlaceAndIncludesMayNest()
    {
        var grammar = Grammar(
            [new TextMateRule(Match: "a", Name: "f"), TextMateRule.Including("outer"), new TextMateRule(Match: "c", Name: "l")],
            ("outer", [TextMateRule.Including("inner"), new TextMateRule(Match: "a|b", Name: "o")]),
            ("inner", [new TextMateRule(Match: "b|c", Name: "i")]));
        Assert.Equal(["fii"], Scope(grammar, "abc"));
    }

    [Fact]
    public void ARuleThatMatchesNothingIsRefused()
    {
        var grammar = Grammar(new TextMateRule(Match: "x*", Name: "x"));
        Assert.Throws<NotSupportedException>(() => Scope(grammar, "y"));
    }

    [Fact]
    public void BackslashGIsRefused() =>
        Assert.Throws<NotSupportedException>(() => Grammar(TextMateRule.Block("(a)", "$", ["a"], [TextMateRule.Scoped(@"\G(b)", "b")])));

    [Fact]
    public void ANamedBlockIsRefused() =>
        Assert.Throws<NotSupportedException>(() => Grammar(new TextMateRule(Begin: "a", End: "b", Name: "n")));

    private static TextMateTokenizer Grammar(params TextMateRule[] rules) => new(rules, new Dictionary<string, IReadOnlyList<TextMateRule>>());

    private static TextMateTokenizer Grammar(TextMateRule[] root, params (string Name, TextMateRule[] Rules)[] repository) =>
        new(root, repository.ToDictionary(entry => entry.Name, entry => (IReadOnlyList<TextMateRule>)entry.Rules));

    private static string[] Scope(TextMateTokenizer grammar, params string[] lines) =>
        [.. grammar.Scope(lines).Select(scopes => string.Concat(scopes.Select(scope => scope ?? ".")))];
}
