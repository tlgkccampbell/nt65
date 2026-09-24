using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Norristown.Cli;

/// <summary>
/// Formats a diagnostic as the command line prints it, either as one line for the person running
/// nt65 or as one JSON object for a tool other than an editor that reads nt65's output.
/// <para>
/// Both forms say the same things, and the line is the one an editor's problem matcher reads
/// (<c>file:line:column: severity: message</c>), so the JSON is for tools that would otherwise
/// have to parse the line. A related span is a second place a diagnostic points at. The line form
/// leaves related spans to the editor, which can show both ends, and the JSON form includes them.
/// </para>
/// </summary>
internal static class Reported
{
    private const string Red = "[31m";
    private const string Yellow = "[33m";
    private const string Plain = "[0m";

    private static readonly JsonSerializerOptions Json = new()
    {
        // The output is read by a tool, not embedded in a page, so a message keeps the `<`,
        // `>` and `&` it was written with rather than becoming escapes nobody wants to read.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Formats the diagnostic as one line. <paramref name="colour"/> marks its severity when the
    /// terminal can show colour. Everything else on the line is left plain, so the position stays
    /// selectable and the message does not compete with it. The catalogue name goes last, in
    /// brackets, where compilers put it. It is the name a project file uses to change the
    /// diagnostic's severity and the name CI matches on, and nobody needs to read it first.
    /// </summary>
    public static string Line(Diagnostic diagnostic, string file, bool colour)
    {
        var severity = diagnostic.Severity.ToString().ToLowerInvariant();
        var marked = !colour ? $"{severity}:"
            : diagnostic.Severity switch
            {
                Severity.Error => $"{Red}{severity}:{Plain}",
                Severity.Warning => $"{Yellow}{severity}:{Plain}",
                _ => $"{severity}:",
            };
        return $"{file}:{diagnostic.Span.Line}:{diagnostic.Span.StartColumn}: {marked} {diagnostic.Message} "
            + $"[{diagnostic.Id}]";
    }

    /// <summary>
    /// Formats the diagnostic as one JSON object on one line, so that a stream of them can be read
    /// a line at a time. <paramref name="named"/> gives the file name to print for a span.
    /// </summary>
    public static string Object(Diagnostic diagnostic, Func<Span, string> named) =>
        JsonSerializer.Serialize(
            new
            {
                file = named(diagnostic.Span),
                line = diagnostic.Span.Line,
                column = diagnostic.Span.StartColumn,
                endColumn = diagnostic.Span.EndColumn,
                severity = diagnostic.Severity.ToString().ToLowerInvariant(),
                id = diagnostic.Id,
                message = diagnostic.Message,
                related = diagnostic.Related.Count == 0
                    ? null
                    : diagnostic.Related.Select(related => new
                    {
                        file = named(related.Span),
                        line = related.Span.Line,
                        column = related.Span.StartColumn,
                        endColumn = related.Span.EndColumn,
                        message = related.Message,
                    }),
            },
            Json);
}
