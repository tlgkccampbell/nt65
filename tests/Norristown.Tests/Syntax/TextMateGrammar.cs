using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

/// <summary>
/// The VS Code grammar, editors/vscode/syntaxes/nt65.tmLanguage.json, is generated from the
/// lexer's tables and checked against the lexer's tokens. Every pattern is a single-line
/// <c>match</c>, since nothing in nt65 spans lines, which also makes it easy to run here.
/// </summary>
internal static class TextMateGrammar
{
    public const string Comment = "comment.line.semicolon.nt65";
    public const string String = "string.quoted.double.nt65";
    public const string Character = "constant.character.nt65";
    public const string OperatorWord = "keyword.operator.word.nt65";
    public const string Directive = "keyword.control.directive.nt65";
    public const string Label = "entity.name.label.nt65";
    public const string CheapLocal = "variable.other.local.nt65";
    public const string Cpu = "constant.language.cpu.nt65";
    public const string Number = "constant.numeric.nt65";
    public const string Mnemonic = "keyword.other.mnemonic.nt65";
    public const string Register = "variable.language.register.nt65";
    public const string Macro = "entity.name.function.macro.nt65";
    public const string Identifier = "variable.other.nt65";
    public const string Operator = "keyword.operator.nt65";

    private const string Word = "[A-Za-z_][A-Za-z0-9_]*";

    public static readonly string Path = Repo.Path("editors", "vscode", "syntaxes", "nt65.tmLanguage.json");

    private static readonly Lazy<List<(string Scope, Regex Regex)>> compiled =
        new(() => [.. Patterns().Select(p => (p.Scope, new Regex(p.Match, RegexOptions.CultureInvariant)))]);

    /// <summary>(scope, regex) in priority order. A pattern with a group scopes only group 1.</summary>
    public static IReadOnlyList<(string Scope, string Match)> Patterns() =>
    [
        (Comment, ";.*$"),
        (String, """
            "(?:[^"\\]|\\.)*"?
            """),
        (Character, """
            '(?:[^'\\]|\\.)*'?
            """),
        (OperatorWord, @"(?i)\.mod\b"),
        (Directive, @"\.[A-Za-z_][A-Za-z0-9_]*"),
        // A label at the start of a line; `name::` is a scoped name, not a label.
        (Label, $@"^\s*(@?{Word})(?=\s*:(?!:))"),
        (CheapLocal, "@" + Word),
        (Cpu, @"(?i)\b65c02\b"),
        (Number, @"\$[A-Za-z0-9_]*|%[A-Za-z0-9_]*|\b[0-9][A-Za-z0-9_]*"),
        (Mnemonic, $@"(?i)\b(?:{string.Join("|", SyntaxFacts.Mnemonics)})\b"),
        (Register, $@"(?i)\b(?:{string.Join("|", SyntaxFacts.Registers)})\b"),
        // After mnemonics and registers, which keep their scope even before a `!`.
        (Macro, $@"\b{Word}(?=\s*!(?!=))"),
        (Identifier, @"\b" + Word),
        (Operator, @"->|\.\.|<<|>>|<=|>=|==|!=|&&|\|\||\^\^|[-+*/&|^~!<>=#?]"),
    ];

    public static string Generate()
    {
        var stream = new MemoryStream();
        var options = new JsonWriterOptions { Indented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using (var json = new Utf8JsonWriter(stream, options))
        {
            json.WriteStartObject();
            json.WriteString("comment", "Generated from the lexer's tables by tests/Norristown.Tests/Syntax/TextMateGrammarTests.cs; " +
                "scripts/test.ps1 -Update rewrites it.");
            json.WriteString("name", "nt65");
            json.WriteString("scopeName", "source.nt65");
            json.WriteStartArray("patterns");
            foreach (var (scope, match) in Patterns())
            {
                json.WriteStartObject();
                json.WriteString("match", match);
                if (new Regex(match).GetGroupNumbers().Length > 1)
                {
                    json.WriteStartObject("captures");
                    json.WriteStartObject("1");
                    json.WriteString("name", scope);
                    json.WriteEndObject();
                    json.WriteEndObject();
                }
                else
                {
                    json.WriteString("name", scope);
                }
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary>
    /// Scopes each character of one line the way a TextMate tokenizer runs a list of match
    /// rules: from the current position, the leftmost match of any pattern wins, and the
    /// earlier pattern breaks a tie.
    /// </summary>
    public static string?[] Scope(string line)
    {
        var patterns = compiled.Value;
        var scopes = new string?[line.Length];
        var pos = 0;
        while (pos < line.Length)
        {
            Match? best = null;
            string? bestScope = null;
            foreach (var (scope, regex) in patterns)
            {
                var m = regex.Match(line, pos);
                if (m.Success && (best is null || m.Index < best.Index))
                    (best, bestScope) = (m, scope);
            }
            if (best is null)
                break;
            var group = best.Groups.Count > 1 ? best.Groups[1] : best.Groups[0];
            for (var i = group.Index; i < group.Index + group.Length; i++)
                scopes[i] = bestScope;
            pos = Math.Max(best.Index + best.Length, pos + (best.Length == 0 ? 1 : 0));
        }
        return scopes;
    }

    /// <summary>The scope the grammar should give a token, or null for none (punctuation).</summary>
    public static string? Expected(GreenLine line, int index)
    {
        var token = line.Tokens[index];
        if (index == 0 && line.LineKind == LineKind.Label)
            return Label;
        return token.Kind switch
        {
            SyntaxKind.Identifier when line.Tokens[index + 1].Kind == SyntaxKind.Bang => Macro,
            SyntaxKind.Identifier => Identifier,
            SyntaxKind.CheapLocal => CheapLocal,
            SyntaxKind.Mnemonic => Mnemonic,
            SyntaxKind.Register => Register,
            SyntaxKind.Directive when token.Text.Equals(".mod", StringComparison.OrdinalIgnoreCase) => OperatorWord,
            SyntaxKind.Directive => Directive,
            SyntaxKind.NumberLiteral => Number,
            SyntaxKind.CharacterLiteral => Character,
            SyntaxKind.StringLiteral => String,
            SyntaxKind.CpuName => Cpu,
            SyntaxKind.ColonColon or SyntaxKind.Colon or SyntaxKind.Comma or SyntaxKind.OpenParen or SyntaxKind.CloseParen
                or SyntaxKind.OpenBracket or SyntaxKind.CloseBracket or SyntaxKind.OpenBrace or SyntaxKind.CloseBrace => null,
            _ => Operator,
        };
    }
}
