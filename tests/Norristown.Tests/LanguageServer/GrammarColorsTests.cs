using Norristown.LanguageServer;
using Norristown.Project;
using Norristown.Syntax;
using Norristown.Tests.Syntax;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Checks that the TextMate grammar colors names as the server's semantic tokens do. The grammar
/// colors a file before the server answers, and the server's tokens are drawn over it once it
/// does. Wherever the grammar gives a name more than the plain scope, it must give the scope that
/// VS Code maps the server's token type to, so that the name keeps its color when the tokens
/// arrive. The grammar must also give every declaration such a scope, except the few declarations
/// whose kind cannot be determined from their line.
/// </summary>
public sealed class GrammarColorsTests
{
    /// <summary>
    /// The declarations whose kind cannot be determined from their line, as pairs of the grammar's
    /// scope and the token type's scope. The one such declaration is a constant whose expression
    /// turns out to name an address, such as <c>HERE = *</c>, which the grammar colors as a
    /// constant and the server as a variable.
    /// </summary>
    private static readonly HashSet<(string Grammar, string Token)> Undecidable =
    [
        (TextMateGrammar.Constant, TextMateGrammar.Variable),
    ];

    [Fact]
    public void TheGrammarColorsNamesAsTheServerDoes()
    {
        var failures = Repo.CollectFailures(Folders(), Check);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// Returns where the grammar and the server color a name differently in the program of the
    /// sources beneath <paramref name="folder"/>.
    /// </summary>
    private static List<string> Check(string folder)
    {
        var failures = new List<string>();
        if (Program(folder) is not { } program)
            return failures;
        var analysis = Compiler.Analyze(program.Trees, program.Settings, _ => 16);
        foreach (var tree in program.Trees)
        {
            if (analysis.ModelFor(tree.Path) is not { } model)
                continue;
            var lines = tree.PhysicalLines.Select(line => line.ToFullString()).ToList();
            var scopes = TextMateTokenizer.Nt65.Scope([.. lines.Select(line => line.TrimEnd('\r', '\n'))]);
            var data = NameHighlighting.In(model).Data;
            var (line, character) = (0, 0);
            for (var i = 0; i < data.Count; i += 5)
            {
                line += data[i];
                character = data[i] == 0 ? character + data[i + 1] : data[i + 1];
                var token = new Token(lines[line], scopes[line], character, data[i + 2], data[i + 3], data[i + 4]);
                if (Mismatch(token) is { } mismatch)
                    failures.Add($"{tree.Path}:{line + 1}: {mismatch}");
            }
        }
        return failures;
    }

    /// <summary>
    /// Returns how the grammar's scope for <paramref name="token"/> differs from the scope that
    /// VS Code maps the server's token type to, or null where they agree or the difference is one
    /// of the allowed ones.
    /// </summary>
    private static string? Mismatch(Token token)
    {
        var type = NameHighlighting.Legend.TokenTypes[token.Type];
        var declaration = (token.Modifiers & 1) != 0;
        if (ScopeOf(type, (token.Modifiers & 2) != 0) is not { } expected)
            return null;
        // A name introduced by `.use ... as` has the kind that its source module declares.
        if (declaration && token.Line[..token.Character].TrimEnd().EndsWith(" as", StringComparison.Ordinal))
            return null;
        var given = token.Scopes[token.Character..(token.Character + token.Length)].Distinct().ToList();
        var grammar = given.Count == 1 ? given[0] : null;
        var plain = grammar is TextMateGrammar.Identifier or TextMateGrammar.Register or TextMateGrammar.Mnemonic
            or TextMateGrammar.CheapLocal;
        if (grammar == expected || (plain && !declaration) || (declaration && Undecidable.Contains((grammar!, expected))))
            return null;
        var text = token.Scopes.Length >= token.Character + token.Length
            ? token.Line.Substring(token.Character, token.Length)
            : "?";
        return $"`{text}` is a {type}{(declaration ? " declaration" : "")}, "
            + $"colored as {expected}, and the grammar gives {string.Join(" + ", given.Select(s => s ?? "no scope"))}";
    }

    /// <summary>
    /// Returns the scope that VS Code colors a token type by, or null for a type given no scope,
    /// such as a label.
    /// </summary>
    private static string? ScopeOf(string type, bool readOnly) => type switch
    {
        "namespace" => TextMateGrammar.Namespace,
        "type" => TextMateGrammar.Type,
        "enum" => TextMateGrammar.Enum,
        "struct" => TextMateGrammar.Struct,
        "enumMember" => TextMateGrammar.EnumMember,
        "property" => TextMateGrammar.Property,
        "function" => TextMateGrammar.Function,
        "macro" => TextMateGrammar.Macro,
        "parameter" => TextMateGrammar.Parameter,
        "variable" => readOnly ? TextMateGrammar.Constant : TextMateGrammar.Variable,
        _ => null,
    };

    /// <summary>Returns the folder of every fixture, corpus program and example.</summary>
    private static List<string> Folders() =>
        [.. new[] { Repo.Path("tests", "fixtures"), Repo.Path("tests", "corpus"), Repo.Path("examples") }
            .SelectMany(Directory.GetDirectories)
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Returns the program made of the sources beneath <paramref name="folder"/>, or null when
    /// the folder holds none.
    /// </summary>
    private static (IReadOnlyList<SyntaxTree> Trees, ProjectSettings Settings)? Program(string folder)
    {
        var build = Path.Combine(folder, "build") + Path.DirectorySeparatorChar;
        var trees = Directory.GetFiles(folder, "*.nt65", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(build, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(path => SyntaxTree.Parse(Paths.Normalized(Path.GetRelativePath(folder, path)), Repo.ReadText(path)))
            .ToList();
        if (trees.Count == 0)
            return null;
        var project = Path.Combine(folder, ProjectFile.Name);
        return (trees, File.Exists(project) ? Repo.ReadProject(folder) : ProjectSettings.None);
    }

    /// <summary>
    /// Represents one semantic token on a line, with the text of the line and the grammar's scope
    /// for each of its characters.
    /// </summary>
    /// <param name="Line">The text of the line, with its line break.</param>
    /// <param name="Scopes">The grammar's scope for each character of the line, or null where it gives none.</param>
    /// <param name="Character">The column the token starts at.</param>
    /// <param name="Length">The length of the token.</param>
    /// <param name="Type">The index of the token's type in the legend.</param>
    /// <param name="Modifiers">The token's modifiers, one bit each.</param>
    private sealed record Token(string Line, string?[] Scopes, int Character, int Length, int Type, int Modifiers);
}
