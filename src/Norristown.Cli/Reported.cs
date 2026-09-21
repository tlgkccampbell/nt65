using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Norristown.Cli;

/// <summary>
/// A diagnostic as the command line says it: one line for the person running nt65, or one JSON
/// object for whatever is reading nt65's output that is not an editor.
/// <para>
/// Both say the same things, and the line is the one an editor's problem matcher reads
/// (<c>file:line:column: severity: message</c>), so the JSON is for tools that would otherwise
/// have to parse it. A related span is a second place a diagnostic points at; the line form
/// leaves those to the editor, which can show both ends, and the JSON form carries them.
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
    /// The diagnostic as one line. <paramref name="colour"/> marks what it is when the terminal
    /// can show it; everything else on the line is left plain, so the position stays selectable
    /// and the message is not competing with it. The catalogue name goes last, in brackets,
    /// where compilers put it: it is what a project file switches and what CI matches on, and
    /// nobody reads it first.
    /// </summary>
    public static string Line(Diagnostic diagnostic, string file, bool colour)
    {
        var said = diagnostic.Severity.ToString().ToLowerInvariant();
        var marked = !colour ? $"{said}:"
            : diagnostic.Severity switch
            {
                Severity.Error => $"{Red}{said}:{Plain}",
                Severity.Warning => $"{Yellow}{said}:{Plain}",
                _ => $"{said}:",
            };
        return $"{file}:{diagnostic.Span.Line}:{diagnostic.Span.StartColumn}: {marked} {diagnostic.Message} "
            + $"[{diagnostic.Id}]";
    }

    /// <summary>
    /// The diagnostic as one JSON object, on one line, so that a stream of them is read a line
    /// at a time. <paramref name="named"/> spells a span's file as the caller would write it.
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
