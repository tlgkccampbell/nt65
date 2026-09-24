using System.Globalization;

namespace Norristown;

/// <summary>
/// Represents one entry in the catalogue. An entry holds the name a diagnostic is reported under,
/// its default severity when the project does not override it, its message format, and a longer
/// explanation that does not fit in the message.
/// <para>
/// The name is what a user sees in the Problems panel, in <c>nt65.json</c> and in CI output, so
/// it is stable once released. The format is a composite format string. A reporting site
/// supplies the arguments for it rather than writing its own sentence.
/// </para>
/// </summary>
/// <param name="Id">The kebab-case name, such as <c>unused-symbol</c>.</param>
/// <param name="Area">The heading it is listed and printed under.</param>
/// <param name="Severity">How much it matters when the project file does not set a severity for it.</param>
/// <param name="Format">The message, with a placeholder for each argument a reporting site supplies.</param>
/// <param name="Explanation">
/// The explanation that the message has no room for, which <c>nt65 explain</c> prints.
/// </param>
public sealed record DiagnosticDescriptor(
    string Id, DiagnosticArea Area, Severity Severity, string Format, string Explanation)
{
    /// <summary>
    /// Returns this descriptor's message, with <paramref name="arguments"/> filled into its
    /// placeholders.
    /// </summary>
    public DiagnosticMessage Message(params object?[] arguments) =>
        new(this, string.Format(CultureInfo.InvariantCulture, Format, arguments));
}
