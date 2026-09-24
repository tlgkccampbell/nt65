namespace Norristown;

/// <summary>
/// Represents a heading in the catalogue, which groups the diagnostics about one part of the
/// language. The diagnostics in an area are listed together and printed together, and
/// <c>nt65 explain --markdown</c> groups its output by area.
/// </summary>
/// <param name="Name">The heading, such as <c>Reading a line</c>.</param>
/// <param name="About">The sentence under the heading, which says what the area covers.</param>
public sealed record DiagnosticArea(string Name, string About);
