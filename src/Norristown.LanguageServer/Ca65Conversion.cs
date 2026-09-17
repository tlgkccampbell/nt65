using System.Text;
using System.Text.RegularExpressions;
using Norristown.Semantics;

namespace Norristown.LanguageServer;

/// <summary>
/// ca65 in a selection, read as nt65: the rewrites that are one line's business and mean the
/// same thing afterwards. A block that was opened by a word and closed by another is opened by
/// a brace and closed by one, segments and data directives take their nt65 spellings, ca65's
/// operator words become the symbols, and what only ca65 needed goes.
/// <para>
/// A line it cannot read is left exactly as it was: an unnamed label, a macro call, an
/// <c>.include</c> and anything else that needs a decision rather than a spelling stays for the
/// programmer, and the diagnostics say so. Converting most of a paste and saying which lines are
/// still ca65 beats guessing at the rest.
/// </para>
/// </summary>
internal static partial class Ca65Conversion
{
    /// <summary>The directives that close a block, which a <c>}</c> closes instead.</summary>
    private static readonly HashSet<string> closers = new(StringComparer.OrdinalIgnoreCase)
    {
        ".endproc", ".endscope", ".endmacro", ".endmac", ".endstruct", ".endunion", ".endenum",
        ".endif", ".endrep", ".endrepeat",
    };

    /// <summary>The directives nt65 has no use for, whose lines go.</summary>
    private static readonly HashSet<string> dropped = new(StringComparer.OrdinalIgnoreCase)
    {
        ".macpack", ".feature", ".smart", ".autoimport", ".case", ".debuginfo", ".linecont",
    };

    /// <summary>ca65's segment directives, each naming the segment it puts things in.</summary>
    private static readonly Dictionary<string, string> segments = new(StringComparer.OrdinalIgnoreCase)
    {
        [".zeropage"] = "ZEROPAGE",
        [".code"] = "CODE",
        [".bss"] = "BSS",
        [".data"] = "DATA",
        [".rodata"] = "RODATA",
    };

    /// <summary>ca65's words for the operators nt65 writes as symbols.</summary>
    private static readonly Dictionary<string, string> operators = new(StringComparer.OrdinalIgnoreCase)
    {
        [".bitand"] = "&",
        [".bitor"] = "|",
        [".bitxor"] = "^",
        [".and"] = "&&",
        [".or"] = "||",
        [".xor"] = "^^",
        [".not"] = "!",
        [".shl"] = "<<",
        [".shr"] = ">>",
        [".asciiz"] = ".strz",
        [".dbyt"] = ".beword",
        [".tag"] = ".type",
    };

    /// <summary>The selection written as nt65, where any of it is ca65 that one line can be read from.</summary>
    public static IEnumerable<Change> In(SemanticModel model, Protocol.Range range)
    {
        var tree = model.Tree;
        var first = Math.Clamp(range.Start.Line, 0, tree.LineStarts.Length - 1);
        var last = range.End.Line > first && range.End.Character == 0 ? range.End.Line - 1 : range.End.Line;
        last = Math.Clamp(last, first, tree.LineStarts.Length - 1);
        if (last == first && range.Start.Character == range.End.Character)
            yield break;

        var written = new List<string>();
        var changed = false;
        for (var line = first; line <= last; line++)
        {
            var end = line + 1 < tree.LineStarts.Length ? tree.LineStarts[line + 1] : tree.Text.Length;
            var text = tree.Text[tree.LineStarts[line]..end].TrimEnd('\r', '\n');
            var read = Converted(text);
            changed |= read != text;
            if (read is not null)
                written.Add(read);
        }
        if (!changed)
            yield break;

        var body = string.Join("\n", written);
        yield return new Change("Read the selection as nt65", CodeActionKinds.Rewrite,
            [Edits.RemoveLines(tree, first, last) with { Text = body.Length == 0 ? "" : body + "\n" }]);
    }

    /// <summary>
    /// One line as nt65: the same line where nothing about it is ca65's, and null for a line
    /// that says something only ca65 needed.
    /// </summary>
    private static string? Converted(string line)
    {
        var (indent, code, comment) = Split(line);
        if (code.Length == 0)
            return line;

        var first = Word(code);
        if (first is not null && dropped.Contains(first))
            return null;
        if (first is not null && closers.Contains(first))
            return indent + "}" + comment;

        var written = code;
        if (first is not null && segments.TryGetValue(first, out var segment) && Rest(code, first).Length == 0)
            written = $".segment {segment}";
        else if (first is not null)
            written = Opened(Renamed(code, first), first);

        return indent + Operators(written) + comment;
    }

    /// <summary>What a line says once its directive is written the nt65 way.</summary>
    private static string Renamed(string code, string first)
    {
        var rest = Rest(code, first);
        switch (first.ToLowerInvariant())
        {
            case ".segment" when rest.StartsWith('"'):
                var quoted = rest.IndexOf('"', 1);
                return quoted > 0 ? $".segment {rest[1..quoted]}{rest[(quoted + 1)..]}" : code;

            case ".define" when rest.Length > 0:
                if (Word(rest) is not { } name)
                    return code;

                // A `.define` with parameters is a function, and one without is a constant:
                // nt65 has both, and neither of them substitutes text.
                var after = rest[name.Length..];
                if (!after.StartsWith('('))
                    return $"{name} = {after.Trim()}";
                var close = after.IndexOf(')');
                return close < 0 ? code : $".func {name}{after[..(close + 1)]} = {after[(close + 1)..].Trim()}";

            case ".ifdef":
                return $".if .defined({rest.Trim()})";

            case ".ifndef":
                return $".if !.defined({rest.Trim()})";

            case ".else":
                return "} .else";

            case ".elseif":
                return $"}} .elseif {rest.Trim()}";

            case ".a8" or ".a16" or ".i8" or ".i16":
                return $".ensure {first[1..]}";

            default:
                return code;
        }
    }

    /// <summary>
    /// A line that opens a block in ca65, opened with a brace: what followed the word is what
    /// the block is of, and a macro's parameters go in parentheses.
    /// </summary>
    private static string Opened(string code, string first)
    {
        if (code.EndsWith('{'))
            return code;
        switch (first.ToLowerInvariant())
        {
            case ".proc" or ".scope" or ".struct" or ".union" or ".enum" or ".repeat":
                return code.TrimEnd() + " {";

            case ".macro" or ".mac":
                var rest = Rest(code, first).Trim();
                var name = Word(rest);
                if (name is null)
                    return code;
                var parameters = Rest(rest, name).Trim();
                return $".macro {name}({parameters}) {{";

            case ".if" or ".ifdef" or ".ifndef" or ".elseif" or ".else":
                return code.TrimEnd() + " {";

            default:
                return LabelledData(code);
        }
    }

    /// <summary>A label written in front of data, as the declaration that names it.</summary>
    private static string LabelledData(string code)
    {
        var match = Labelled().Match(code);
        if (!match.Success)
            return code;
        var name = match.Groups["name"].Value;
        var directive = match.Groups["directive"].Value;
        var rest = match.Groups["rest"].Value.Trim();
        return directive.Equals(".res", StringComparison.OrdinalIgnoreCase) && rest.Length > 0
            ? $".data {name}: .byte[{rest}]"
            : $".data {name}: {directive}{(rest.Length > 0 ? " " + rest : "")}";
    }

    /// <summary>ca65's operator words and directive spellings, written as nt65 writes them.</summary>
    private static string Operators(string code)
    {
        var written = new StringBuilder();
        var at = 0;
        while (at < code.Length)
        {
            var c = code[at];
            if (c is '"' or '\'')
            {
                var end = at + 1;
                while (end < code.Length && code[end] != c)
                    end += code[end] == '\\' ? 2 : 1;
                end = Math.Min(end + 1, code.Length);
                written.Append(code[at..end]);
                at = end;
                continue;
            }
            if (c == '.' || char.IsLetter(c))
            {
                var end = at + (c == '.' ? 1 : 0);
                while (end < code.Length && (char.IsLetterOrDigit(code[end]) || code[end] == '_'))
                    end++;
                var word = code[at..end];
                written.Append(operators.TryGetValue(word, out var symbol) ? symbol : word);
                at = end;
                continue;
            }

            // ca65 compares with `=` and `<>`, which mean assignment and nothing in nt65.
            if (c == '<' && at + 1 < code.Length && code[at + 1] == '>')
            {
                written.Append("!=");
                at += 2;
                continue;
            }
            written.Append(c);
            at++;
        }
        return written.ToString();
    }

    /// <summary>A line as its indent, its code and whatever follows the code, comment and all.</summary>
    private static (string Indent, string Code, string Comment) Split(string line)
    {
        var start = 0;
        while (start < line.Length && line[start] is ' ' or '\t')
            start++;
        var comment = Comment(line);
        var code = line[start..(line.Length - comment.Length)].TrimEnd();
        return (line[..start], code, line[(start + code.Length)..]);
    }

    /// <summary>What follows a line's code: the whitespace and the comment, if any.</summary>
    private static string Comment(string line)
    {
        var quote = '\0';
        for (var at = 0; at < line.Length; at++)
        {
            if (quote != '\0')
            {
                if (line[at] == '\\')
                    at++;
                else if (line[at] == quote)
                    quote = '\0';
            }
            else if (line[at] is '"' or '\'')
            {
                quote = line[at];
            }
            else if (line[at] == ';')
            {
                return line[at..];
            }
        }
        return "";
    }

    /// <summary>The first word of a line's code: a directive, a mnemonic or a name.</summary>
    private static string? Word(string code)
    {
        var at = 0;
        if (at < code.Length && code[at] == '.')
            at++;
        var start = at;
        while (at < code.Length && (char.IsLetterOrDigit(code[at]) || code[at] == '_'))
            at++;
        return at > start ? code[..at] : null;
    }

    /// <summary>What a line says after <paramref name="first"/>.</summary>
    private static string Rest(string code, string first) => code[first.Length..].TrimStart();

    /// <summary>A name in front of a data directive, which nt65 declares rather than labels.</summary>
    [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*(?<directive>\.(?:byte|word|dword|addr|faraddr|res|asciiz|strz|byt|dbyt|lobytes|hibytes|bankbytes|incbin))\b(?<rest>.*)$")]
    private static partial Regex Labelled();
}
