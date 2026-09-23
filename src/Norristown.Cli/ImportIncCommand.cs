using System.Globalization;
using System.Text;
using Norristown.Syntax;

namespace Norristown.Cli;

/// <summary>
/// <c>nt65 import-inc</c>: converts a ca65 include file of constants into an nt65 module, once.
/// A person runs it by hand, and the file it writes is maintained as ordinary nt65 source from
/// then on: nt65 reads no ca65 at build time, and this command does not change that.
/// <para>
/// A line it understands — <c>NAME = expr</c> or <c>NAME := expr</c>, with its comment — becomes
/// a constant; a comment line is carried over as it stands. Anything else is written out as a
/// comment saying it was not converted, and counted on standard error, so that nothing in the
/// file is dropped silently and a person can see what is left to do by hand.
/// </para>
/// </summary>
public static class ImportIncCommand
{
    /// <summary>The longest an <c>.export</c> line may get before the names continue on another one.</summary>
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
    /// Converts <paramref name="text"/> into the source of the module <paramref name="module"/>, and
    /// returns it with the lines that could not be converted, by line number and reason.
    /// <paramref name="from"/> is the include file's path as shown to the user, which the module's
    /// header comment names so that a reader knows where it came from.
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

        // The export lines end with a blank line, so a blank line at the start of the include
        // file is dropped rather than leaving two in a row.
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

        // ca65 also accepts `:=` for this definition; nt65 spells it only as `=`, so the colon is
        // dropped.
        if (name.EndsWith(':'))
            name = name[..^1].TrimEnd();
        var value = code[(at + 1)..].Trim();
        return name.Length == 0 || value.Length == 0 ? ("", "", null) : (name, value, comment);
    }

    /// <summary>
    /// The code and the comment of a line, split at the <c>;</c> that is not inside a literal.
    /// The comment keeps the spacing before it, so comments aligned in a column stay aligned
    /// unless the formatter moves them.
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
    /// Whether nt65's own parser reads <paramref name="line"/>, without errors, as a constant
    /// declaration. A ca65 spelling nt65 does not have — <c>.LOBYTE</c>, <c>.SHL</c>, a local
    /// label — and an expression nt65 requires parentheses in to make its order explicit are both
    /// rejected here, rather than written out as source that will not build.
    /// </summary>
    private static bool Reads(string line)
    {
        var tree = SyntaxTree.Parse(new SourceFile("import.nt65", line + "\n"));
        return tree.Diagnostics.Count == 0
            && tree.GetLine(0).Statement is ConstantDeclarationSyntax;
    }

    /// <summary>The reason reported for a line left as a comment, as best it can be determined.</summary>
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
    /// they fit. Every name is exported, since a constant no other module can name would be of no
    /// use to the rest of the program.
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

    /// <summary>The module reformatted in nt65's standard layout, the one <c>nt65 fmt</c> writes.</summary>
    private static string Laid(string text) => Formatter.Format(SyntaxTree.Parse(new SourceFile("import.nt65", text)));

    /// <summary>
    /// The module name used when the command line does not give one: the file's name without its
    /// extension, with each character other than a letter, digit or <c>_</c> replaced by <c>_</c>,
    /// and <c>_</c> put in front when that is empty or starts with a digit.
    /// </summary>
    private static string ModuleName(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var name = new StringBuilder();
        foreach (var c in stem)
            name.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        return name.Length == 0 || char.IsDigit(name[0]) ? "_" + name : name.ToString();
    }

    /// <summary>Reports what is wrong with the command line and how to see the usage text, and returns 2.</summary>
    private static int Wrong(TextWriter error, string problem)
    {
        error.WriteLine($"nt65: {problem}");
        error.WriteLine(CommandLine.SeeHelp);
        return 2;
    }
}
