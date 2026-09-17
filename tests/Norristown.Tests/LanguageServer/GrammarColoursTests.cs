using Norristown.LanguageServer;
using Norristown.Project;
using Norristown.Syntax;
using Norristown.Tests.Syntax;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// The grammar colours a file before the server answers, and the server's semantic tokens are
/// drawn over it when it does. Where the grammar gives a name more than the plain scope, it gives
/// the scope VS Code maps the server's token to, so the name keeps its colour; and it gives every
/// declaration one, except the few its line cannot tell apart.
/// </summary>
public sealed class GrammarColoursTests
{
    /// <summary>
    /// The declarations whose kind their line does not say, as the grammar's scope and the token
    /// type's: a constant whose expression turns out to name an address, <c>HERE = *</c>.
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
                    // A name `.use ... as` gives is whatever the module it comes from says it is.
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

    /// <summary>The scope VS Code colours a token type by, or null for one it gives none, a label.</summary>
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

    /// <summary>Every fixture, corpus program and example, each a program of the sources beneath its folder.</summary>
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
