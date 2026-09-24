using Norristown.LanguageServer;
using Norristown.Project;
using Norristown.Syntax;
using Norristown.Tests.Syntax;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Checks that the TextMate grammar colours names as the server's semantic tokens do. The grammar
/// colours a file before the server answers, and the server's tokens are drawn over it once it
/// does. Wherever the grammar gives a name more than the plain scope, it must give the scope that
/// VS Code maps the server's token type to, so that the name keeps its colour when the tokens
/// arrive. The grammar must also give every declaration such a scope, except the few declarations
/// whose kind cannot be determined from their line.
/// </summary>
public sealed class GrammarColoursTests
{
    /// <summary>
    /// The declarations whose kind cannot be determined from their line, as pairs of the grammar's
    /// scope and the token type's scope. The one such declaration is a constant whose expression
    /// turns out to name an address, such as <c>HERE = *</c>, which the grammar colours as a
    /// constant and the server as a variable.
    /// </summary>
    private static readonly HashSet<(string Grammar, string Token)> Undecidable =
    [
        (TextMateGrammar.Constant, TextMateGrammar.Variable),
    ];

    [Fact]
    public void TheGrammarColoursNamesAsTheServerDoes()
    {
        var failures = new List<string>();
        foreach (var program in Programs())
        {
            var analysis = Compiler.Analyze(program.Trees, program.Settings, _ => 16);
            foreach (var tree in program.Trees)
            {
                if (analysis.ModelFor(tree.Path) is not { } model)
                    continue;
                var scopes = TextMateGrammar.Scope([.. tree.Lines.Select(line => line.ToFullString().TrimEnd('\r', '\n'))]);
                var data = NameHighlighting.In(model).Data;
                var (line, character) = (0, 0);
                for (var i = 0; i < data.Count; i += 5)
                {
                    line += data[i];
                    character = data[i] == 0 ? character + data[i + 1] : data[i + 1];
                    var type = NameHighlighting.Legend.TokenTypes[data[i + 3]];
                    var declaration = (data[i + 4] & 1) != 0;
                    if (ScopeOf(type, (data[i + 4] & 2) != 0) is not { } expected)
                        continue;
                    // A name introduced by `.use ... as` has the kind that its source module declares.
                    if (declaration && tree.Lines[line].ToFullString()[..character].TrimEnd().EndsWith(" as", StringComparison.Ordinal))
                        continue;
                    var given = scopes[line][character..(character + data[i + 2])].Distinct().ToList();
                    var grammar = given.Count == 1 ? given[0] : null;
                    var plain = grammar is TextMateGrammar.Identifier or TextMateGrammar.Register or TextMateGrammar.Mnemonic
                        or TextMateGrammar.CheapLocal;
                    if (grammar == expected || (plain && !declaration) || (declaration && Undecidable.Contains((grammar!, expected))))
                        continue;
                    var text = scopes[line].Length >= character + data[i + 2]
                        ? tree.Lines[line].ToFullString().Substring(character, data[i + 2])
                        : "?";
                    failures.Add($"{tree.Path}:{line + 1}: `{text}` is a {type}{(declaration ? " declaration" : "")}, "
                        + $"coloured as {expected}, and the grammar gives {string.Join(" + ", given.Select(s => s ?? "no scope"))}");
                }
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// Returns the scope that VS Code colours a token type by, or null for a type given no scope,
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

    /// <summary>
    /// Returns every fixture, corpus program and example, each as a program made of the sources
    /// beneath its folder.
    /// </summary>
    private static IEnumerable<(IReadOnlyList<SyntaxTree> Trees, ProjectSettings Settings)> Programs()
    {
        var folders = new[] { Repo.Path("tests", "fixtures"), Repo.Path("tests", "corpus"), Repo.Path("examples") }
            .SelectMany(Directory.GetDirectories)
            .Order(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            var build = Path.Combine(folder, "build") + Path.DirectorySeparatorChar;
            var trees = Directory.GetFiles(folder, "*.nt65", SearchOption.AllDirectories)
                .Where(path => !path.StartsWith(build, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .Select(path => SyntaxTree.Parse(Paths.Normalized(Path.GetRelativePath(folder, path)), Repo.ReadText(path)))
                .ToList();
            if (trees.Count == 0)
                continue;
            var project = Path.Combine(folder, ProjectFile.Name);
            yield return (trees, File.Exists(project) ? ProjectFile.Read(ProjectFile.Name, Repo.ReadText(project)) : ProjectSettings.None);
        }
    }
}
