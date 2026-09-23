using System.Globalization;

namespace Norristown.Cli;

/// <summary>
/// <c>nt65 explain &lt;name&gt;</c>: prints the full explanation of a diagnostic, which its
/// one-line message has no room for. Given no name, it lists every diagnostic; given a name nt65
/// does not know, it suggests the closest one. Given <c>--markdown</c>, it writes the whole
/// catalogue as the page checked in as <c>docs/DIAGNOSTICS.md</c>.
/// </summary>
internal static class ExplainCommand
{
    /// <summary>Runs the command with what followed <c>explain</c> on the command line.</summary>
    public static int Run(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        if (arguments is [])
        {
            List(output);
            return 0;
        }
        if (arguments is ["--markdown"])
        {
            output.Write(DiagnosticsPage.Text());
            return 0;
        }
        if (arguments is not [var name] || name.StartsWith('-'))
        {
            error.WriteLine("nt65: explain takes one diagnostic's name");
            error.WriteLine(CommandLine.SeeHelp);
            return 2;
        }
        if (Catalogue.Find(name) is not { } descriptor)
        {
            var nearest = Spelling.Nearest(name, Catalogue.All.Select(entry => entry.Id));
            error.WriteLine(nearest is null
                ? $"nt65: `{name}` is not a diagnostic nt65 reports"
                : $"nt65: `{name}` is not a diagnostic nt65 reports; `{nearest}` is");
            error.WriteLine("nt65: `nt65 explain` lists them");
            return 2;
        }

        // Print the entry: its name and default severity, its message with each placeholder
        // shown as `...`, its explanation, and the project-file setting that changes it.
        output.WriteLine($"{descriptor.Id}, {Reported(descriptor.Severity)} by default");
        output.WriteLine();
        output.WriteLine($"  {Said(descriptor.Format)}");
        output.WriteLine();
        foreach (var line in Wrapped(descriptor.Explanation))
            output.WriteLine(line);
        output.WriteLine();
        output.WriteLine(
            $"in {Project.ProjectFile.Name}: \"diagnostics\": {{ \"{descriptor.Id}\": \"{Turned(descriptor)}\" }}");
        return 0;
    }

    /// <summary>Lists every diagnostic name with its default severity, for someone looking one up.</summary>
    private static void List(TextWriter output)
    {
        output.WriteLine("every diagnostic nt65 reports; `nt65 explain <name>` says what one is about.");
        output.WriteLine();
        var width = Catalogue.All.Max(descriptor => descriptor.Id.Length);
        foreach (var descriptor in Catalogue.All)
            output.WriteLine($"  {descriptor.Id.PadRight(width)}  {Reported(descriptor.Severity)}");
    }

    /// <summary>A severity with its article, as this command prints it: "an error", "a warning" or "a note".</summary>
    private static string Reported(Severity severity) =>
        severity.ToString().ToLowerInvariant() is "info" ? "a note" : $"a{(severity == Severity.Error ? "n" : "")} "
            + severity.ToString().ToLowerInvariant();

    /// <summary>
    /// The message format with each numbered placeholder (<c>{0}</c>, <c>{1}</c>, ...) shown as
    /// <c>...</c>, since the numbers mean something to nt65 but nothing to a reader.
    /// </summary>
    private static string Said(string format)
    {
        var said = format;
        for (var i = 0; said.Contains('{', StringComparison.Ordinal) && i < 16; i++)
            said = said.Replace($"{{{i.ToString(CultureInfo.InvariantCulture)}}}", "...", StringComparison.Ordinal);
        return said.Replace("{{", "{", StringComparison.Ordinal).Replace("}}", "}", StringComparison.Ordinal);
    }

    /// <summary>
    /// The value for the example project-file line: <c>off</c> for a diagnostic that is not an
    /// error, since turning it off is the likeliest change, and <c>error</c> for an error, which
    /// cannot be turned down.
    /// </summary>
    private static string Turned(DiagnosticDescriptor descriptor) =>
        descriptor.Severity == Severity.Error ? "error" : "off";

    /// <summary>The explanation broken between words into lines of about 88 characters, to fit a terminal.</summary>
    private static IEnumerable<string> Wrapped(string text)
    {
        var line = "";
        foreach (var word in text.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > 88)
            {
                yield return line;
                line = "";
            }
            line = line.Length == 0 ? word : $"{line} {word}";
        }
        if (line.Length > 0)
            yield return line;
    }
}
