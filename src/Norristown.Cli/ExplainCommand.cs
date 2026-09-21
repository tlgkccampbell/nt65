using System.Globalization;

namespace Norristown.Cli;

/// <summary>
/// <c>nt65 explain &lt;name&gt;</c>: what a diagnostic is about, which its one line has no room
/// for. Named nothing, it lists what there is to ask about; named something nt65 has no entry
/// for, it says the name it is nearly.
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

        // The entry, as it reads: what it is called and how much it matters, the sentence it
        // says with a mark where each piece of the program goes, and then what it is about.
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

    /// <summary>Every name there is, with how much each matters, for someone who has one to find.</summary>
    private static void List(TextWriter output)
    {
        output.WriteLine("every diagnostic nt65 reports; `nt65 explain <name>` says what one is about.");
        output.WriteLine();
        var width = Catalogue.All.Max(descriptor => descriptor.Id.Length);
        foreach (var descriptor in Catalogue.All)
            output.WriteLine($"  {descriptor.Id.PadRight(width)}  {Reported(descriptor.Severity)}");
    }

    /// <summary>How much a diagnostic matters, as this command says it.</summary>
    private static string Reported(Severity severity) =>
        severity.ToString().ToLowerInvariant() is "info" ? "a note" : $"a{(severity == Severity.Error ? "n" : "")} "
            + severity.ToString().ToLowerInvariant();

    /// <summary>
    /// The sentence with a mark where each piece of the program goes, since the holes a format
    /// leaves are numbered for nt65 rather than for a reader.
    /// </summary>
    private static string Said(string format)
    {
        var said = format;
        for (var i = 0; said.Contains('{', StringComparison.Ordinal) && i < 16; i++)
            said = said.Replace($"{{{i.ToString(CultureInfo.InvariantCulture)}}}", "...", StringComparison.Ordinal);
        return said.Replace("{{", "{", StringComparison.Ordinal).Replace("}}", "}", StringComparison.Ordinal);
    }

    /// <summary>What a project would most likely want to say about it, which is the other way round.</summary>
    private static string Turned(DiagnosticDescriptor descriptor) =>
        descriptor.Severity == Severity.Error ? "error" : "off";

    /// <summary>The explanation in lines a terminal holds, broken between words.</summary>
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
