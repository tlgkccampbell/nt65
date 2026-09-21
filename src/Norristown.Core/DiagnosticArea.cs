namespace Norristown;

/// <summary>
/// A heading in the catalogue: the diagnostics about one part of the language, written
/// together and printed together. It is what <c>nt65 explain --markdown</c> groups by, and so
/// what <c>docs/DIAGNOSTICS.md</c> is laid out under.
/// </summary>
/// <param name="Name">The heading, such as <c>Reading a line</c>.</param>
/// <param name="About">The sentence under the heading, saying what the area covers.</param>
public sealed record DiagnosticArea(string Name, string About);
