using System.Globalization;
using System.Text;
using Norristown.Syntax;

namespace Norristown.Cli;

/// <summary>
/// <c>nt65 import-inc</c>: an nt65 module of constants written once from a ca65 include file
/// of them. It is run by a person, and what it writes is the module's own source from then on:
/// nt65 reads no ca65 at build time, and this does not change that.
/// <para>
/// A line it understands — <c>NAME = expr</c> or <c>NAME := expr</c>, with its comment — becomes
/// a constant; a comment line is carried over as it stands. Anything else is written out as a
/// comment saying it was not converted, and counted on standard error, so that nothing in the
/// file is dropped silently and a person can see what is left to do by hand.
/// </para>
/// </summary>
public static class ImportIncCommand
{
    /// <summary>How wide an <c>.export</c> list is allowed to get before the next one starts.</summary>
    private const int ExportWidth = 92;

    /// <summary>
    /// Converts the file <paramref name="arguments"/> names and returns the exit code: 0 when it
    /// wrote a module, 1 when the file could not be read or the output could not be written, 2
    /// when the command is wrong. Lines it could not convert are notes, not failures.
    /// </summary>
    public static int Run(IReadOnlyList<string> arguments, string directory, TextWriter output, TextWriter error)
    {
        string? source = null, destination = null, module = null;
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            var value = i + 1 < arguments.Count ? arguments[i + 1] : null;
            switch (argument)
            {
                case "-o" or "--out":
                    if (value is null)
                        return Wrong(error, $"`{argument}` needs a file to write");
                    destination = Path.GetFullPath(value, directory);
                    i++;
                    break;
                case "--module":
                    if (value is null)
                        return Wrong(error, "`--module` needs the module's name");
                    module = value;
                    i++;
                    break;
                default:
                    if (argument.StartsWith('-'))
                        return Wrong(error, $"`{argument}` is not an option");
                    if (source is not null)
                        return Wrong(error, "import-inc converts one file");
                    source = Path.GetFullPath(argument, directory);
                    break;
            }
        }
        if (source is null)
            return Wrong(error, "import-inc needs the `.inc` file to convert");

        string text;
        try
        {
            text = File.ReadAllText(source);
        }
        catch (IOException problem)
        {
            error.WriteLine($"nt65: cannot read {ProjectRoot.Shown(directory, source)}: {problem.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException problem)
        {
            error.WriteLine($"nt65: cannot read {ProjectRoot.Shown(directory, source)}: {problem.Message}");
            return 1;
        }

        var named = ProjectRoot.Shown(directory, source);
        var converted = Convert(text, module ?? ModuleName(source), named);
        foreach (var (line, why) in converted.Refused)
            error.WriteLine($"{named}:{line.ToString(CultureInfo.InvariantCulture)}: not converted: {why}");
        if (converted.Refused.Count > 0)
        {
            var many = converted.Refused.Count;
            error.WriteLine($"nt65: {many.ToString(CultureInfo.InvariantCulture)} "
                + $"line{(many == 1 ? "" : "s")} of {named} left as {(many == 1 ? "a comment" : "comments")}");
        }

        if (destination is null)
        {
            output.Write(converted.Text);
            return 0;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, converted.Text);
        }
        catch (IOException problem)
        {
            error.WriteLine($"nt65: cannot write {ProjectRoot.Shown(directory, destination)}: {problem.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException problem)
        {
            error.WriteLine($"nt65: cannot write {ProjectRoot.Shown(directory, destination)}: {problem.Message}");
            return 1;
        }
        return 0;
    }

    /// <summary>
    /// <paramref name="text"/> as the module <paramref name="module"/>, with what could not be
    /// converted, by line. <paramref name="from"/> is the include file as the command line wrote
    /// it, which the module's header names so that a reader knows where it came from.
    /// </summary>
    public static (string Text, IReadOnlyList<(int Line, string Why)> Refused) Convert(
        string text, string module, string from)
    {
        var refused = new List<(int Line, string Why)>();
        var body = new List<string>();
        var names = new List<string>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            // A trailing blank line is the newline the last line ends with, not a line.
            if (i == lines.Length - 1 && lines[i].Length == 0)
                break;
            var line = lines[i].TrimEnd();
            var bare = line.Trim();
            if (bare.Length == 0)
            {
                body.Add("");
                continue;
            }
            if (bare.StartsWith(';'))
            {
                body.Add(bare);
                continue;
            }
            if (Constant(bare) is var (name, value, comment) && Reads($"{name} = {value}"))
            {
                names.Add(name);
                body.Add(comment is null ? $"{name} = {value}" : $"{name} = {value}{comment}");
                continue;
            }
            refused.Add((i + 1, Why(bare)));
            body.Add($"; not converted: {bare}");
        }

        var built = new StringBuilder();
        built.Append("; Constants from ").Append(from).Append(", written by `nt65 import-inc`.\n");
        built.Append("; This file is the module's own source from here on: nt65 reads no ca65 at build time, so\n");
        built.Append("; a change to the include file is brought over by running the command again.\n");
        built.Append(".module ").Append(module).Append('\n');
        foreach (var exported in Exports(names))
            built.Append(exported).Append('\n');

        // The exports end with a blank line of their own, so a file that opens with one does
        // not give the module two.
        foreach (var line in names.Count > 0 && body is ["", .. var rest] ? rest : body)
            built.Append(line).Append('\n');
        return (Laid(built.ToString()), refused);
    }

    /// <summary>
    /// The name, the expression and the comment of a <c>NAME = expr</c> or <c>NAME := expr</c>
    /// line; the name is empty where the line is neither.
    /// </summary>
    private static (string Name, string Value, string? Comment) Constant(string line)
    {
        var (code, comment) = SplitComment(line);
        var at = code.IndexOf('=', StringComparison.Ordinal);
        if (at <= 0)
            return ("", "", null);
        var name = code[..at].TrimEnd();

        // ca65 spells the same definition `:=` as well, which nt65 has one spelling for.
        if (name.EndsWith(':'))
            name = name[..^1].TrimEnd();
        var value = code[(at + 1)..].Trim();
        return name.Length == 0 || value.Length == 0 ? ("", "", null) : (name, value, comment);
    }

    /// <summary>
    /// The code and the comment of a line, split at the <c>;</c> that is not inside a literal.
    /// The comment keeps the spacing it was written with, so a column of them stays a column
    /// until the layout says otherwise.
    /// </summary>
    private static (string Code, string? Comment) SplitComment(string line)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0')
            {
                if (c == '\\')
                    i++;
                else if (c == quote)
                    quote = '\0';
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == ';')
            {
                return (line[..i].TrimEnd(), line[i..]);
            }
        }
        return (line, null);
    }

    /// <summary>
    /// Whether nt65 reads <paramref name="line"/> as the constant it is meant to be. The reader
    /// is nt65's own, so a ca65 spelling nt65 does not have — <c>.LOBYTE</c>, <c>.SHL</c>, a
    /// local label — and an expression whose order nt65 asks to see in parentheses are both
    /// refused here rather than written out as something that will not build.
    /// </summary>
    private static bool Reads(string line)
    {
        var tree = SyntaxTree.Parse(new SourceFile("import.nt65", line + "\n"));
        return tree.Diagnostics.Count == 0
            && tree.GetLine(0).Statement is ConstantDeclarationSyntax;
    }

    /// <summary>What to say about a line that was left as a comment, as far as it can be told.</summary>
    private static string Why(string line)
    {
        var (code, _) = SplitComment(line);
        if (code.Length == 0)
            return "nothing but a comment on the line";
        if (code.StartsWith('.'))
            return $"`{code.Split(' ', '\t')[0]}` is a ca65 directive, and nt65 has no such line";
        return code.Contains('=', StringComparison.Ordinal)
            ? "nt65 does not read the expression: check the spelling and the parentheses it asks for"
            : "the line defines no constant";
    }

    /// <summary>
    /// The <c>.export</c> lines that make the constants part of the module, one line each while
    /// they fit. Every name is exported: a module of constants nobody else can name is one
    /// nothing in the program could have used it for.
    /// </summary>
    private static IEnumerable<string> Exports(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
            yield break;
        yield return "";
        var line = new StringBuilder();
        foreach (var name in names)
        {
            if (line.Length > 0 && line.Length + 2 + name.Length > ExportWidth)
            {
                yield return line.ToString();
                line.Clear();
            }
            line.Append(line.Length == 0 ? ".export " : ", ").Append(name);
        }
        yield return line.ToString();
        yield return "";
    }

    /// <summary>The module written in the one layout, which is what every other nt65 source is in.</summary>
    private static string Laid(string text) => Formatter.Format(SyntaxTree.Parse(new SourceFile("import.nt65", text)));

    /// <summary>The module name a file gets when the command line does not give one: its own, made a name.</summary>
    private static string ModuleName(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var name = new StringBuilder();
        foreach (var c in stem)
            name.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        return name.Length == 0 || char.IsDigit(name[0]) ? "_" + name : name.ToString();
    }

    /// <summary>Says what is wrong with the command line, and where its usage text is.</summary>
    private static int Wrong(TextWriter error, string problem)
    {
        error.WriteLine($"nt65: {problem}");
        error.WriteLine(CommandLine.SeeHelp);
        return 2;
    }
}
