using System.Globalization;

namespace Norristown;

/// <summary>
/// One entry in the catalogue: the name a diagnostic is reported under, how much it matters
/// where a project says nothing, the sentence it says, and a sentence about it the message
/// does not say.
/// <para>
/// The name is what a user meets — in the Problems panel, in <c>nt65.json</c> and in CI output
/// — so it is stable once released. The format is a composite format string, and a reporting
/// site gives it the pieces of its own sentence rather than writing one.
/// </para>
/// </summary>
/// <param name="Id">The kebab-case name, such as <c>unused-symbol</c>.</param>
/// <param name="Severity">How much it matters where the project file says nothing.</param>
/// <param name="Format">The sentence, with a hole for each piece a site gives it.</param>
/// <param name="Explanation">What the message has no room to say, which <c>nt65 explain</c> prints.</param>
public sealed record DiagnosticDescriptor(string Id, Severity Severity, string Format, string Explanation)
{
    /// <summary>The message this descriptor says, with <paramref name="arguments"/> in its holes.</summary>
    public DiagnosticMessage Says(params object?[] arguments) =>
        new(this, string.Format(CultureInfo.InvariantCulture, Format, arguments));
}
