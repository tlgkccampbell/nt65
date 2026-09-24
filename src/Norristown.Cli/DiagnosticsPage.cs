using System.Text;

namespace Norristown.Cli;

/// <summary>
/// Builds the whole diagnostic catalogue as one Markdown page, which
/// <c>nt65 explain --markdown</c> prints. None of the page is written by hand, so an entry added
/// to the catalogue appears on it, and a reworded entry reads the same in the catalogue and on the
/// page.
/// </summary>
internal static class DiagnosticsPage
{
    /// <summary>Returns the page, with <c>\n</c> line endings and a newline at the end of it.</summary>
    public static string Text()
    {
        var page = new StringBuilder();
        page.Append(
            $$"""
            # nt65 diagnostics

            Every diagnostic nt65 reports, by area: {{Catalogue.All.Count}} names. The name is what appears in brackets after a
            message in the terminal, as `"id"` in `--json`, as the `code` in an editor, and as the key under
            `"diagnostics"` in `nt65.json`, where a warning can be set to `off`, `warning` or `error`. An error
            cannot be turned down. The names are part of what version 1 promises; the wording is not.

            `nt65 explain <name>` prints the explanation given here, and `nt65 explain` alone lists the names.
            This page is what `nt65 explain --markdown` writes from `src/Norristown.Core/Catalogue.cs`, which is
            the source of truth; `scripts/test.ps1 -Update` writes it here again. In a message, `{0}` and its
            siblings stand for what the diagnostic names at the place it is reported.

            | area | names | not an error by default |
            |---|---|---|

            """);

        foreach (var area in Catalogue.Areas)
        {
            var entries = Under(area);
            var noted = string.Join(", ", entries
                .Where(entry => entry.Severity != Severity.Error)
                .Select(entry => $"`{entry.Id}` ({Reported(entry.Severity)})"));
            page.Append(
                $"| [{area.Name}]({Anchor(area.Name)}) | {entries.Count} | {(noted.Length == 0 ? "—" : noted)} |\n");
        }

        foreach (var area in Catalogue.Areas)
        {
            page.Append($"\n## {area.Name}\n\n{area.About}\n");
            foreach (var entry in Under(area))
            {
                // A diagnostic is an error unless its heading says otherwise, so only the ones a
                // project can turn down have their default severity in the heading.
                var turned = entry.Severity == Severity.Error ? "" : $" — {Reported(entry.Severity)}";
                page.Append($"\n### `{entry.Id}`{turned}\n\n> {entry.Format}\n\n{entry.Explanation}\n");
            }
        }
        return page.ToString();
    }

    /// <summary>
    /// Returns the entries in an area, in name order, which is the order the page prints them in.
    /// </summary>
    private static IReadOnlyList<DiagnosticDescriptor> Under(DiagnosticArea area) =>
        [.. Catalogue.All.Where(entry => entry.Area == area)];

    /// <summary>
    /// Returns the anchor a Markdown renderer gives a heading, which the table uses to link to it.
    /// </summary>
    private static string Anchor(string heading) =>
        "#" + heading.ToLowerInvariant().Replace(' ', '-');

    /// <summary>Returns a severity in lower case, as the table and the headings print it.</summary>
    private static string Reported(Severity severity) => severity.ToString().ToLowerInvariant();
}
