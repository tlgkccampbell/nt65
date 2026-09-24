namespace Norristown.LanguageServer;

/// <summary>
/// Represents one rule of a TextMate grammar. A rule is a <c>match</c> scoped by its name or by
/// its groups, a <c>begin</c>/<c>end</c> block with the rules inside it, or an include of a rule
/// list in the grammar's repository. <c>Captures</c> holds the scope of each group by number, and
/// its index 0 is unused.
/// </summary>
internal sealed record TextMateRule(
    string? Match = null, string? Name = null, IReadOnlyList<string?>? Captures = null,
    string? Begin = null, string? End = null, IReadOnlyList<TextMateRule>? Patterns = null, string? Include = null)
{
    /// <summary>Gets the rules tried inside a block, which are empty for any other rule.</summary>
    public IReadOnlyList<TextMateRule> Patterns { get; } = Patterns ?? [];

    /// <summary>Returns a rule that includes the repository's rule list of that name.</summary>
    public static TextMateRule Including(string name) => new(Include: name);

    /// <summary>Returns a match rule that gives each of its groups, by number from 1, a scope.</summary>
    public static TextMateRule Scoped(string match, params string?[] groups) => new(Match: match, Captures: [null, .. groups]);

    /// <summary>
    /// Returns a block that gives each group of its <c>begin</c>, by number from 1, a scope, and
    /// tries <paramref name="patterns"/> until its <c>end</c> matches.
    /// </summary>
    public static TextMateRule Block(string begin, string end, IReadOnlyList<string?> groups, IReadOnlyList<TextMateRule> patterns) =>
        new(Begin: begin, End: end, Captures: [null, .. groups], Patterns: patterns);
}
