using Norristown.LanguageServer;
using Norristown.Project;
using Norristown.Semantics;
using Norristown.Syntax;

// The protocol has a Range and a Position of their own, which are the ones a request is asked in.
using Position = Norristown.LanguageServer.Protocol.Position;
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests;

/// <summary>
/// Every source in the repository, whole and with each line cut short, put through everything
/// above the parser: its names bound, its code laid out, its control followed, and every editor
/// request answered at every caret on every line. Nothing about the answers is asserted, because
/// a half-written file has no right answer; only that answering does not throw.
/// <para>
/// This is the net under the analysis, as <see cref="Syntax.TypedNodeTests"/> is the net under the
/// node accessors. An editor runs all of this over a file that does not parse at every keystroke,
/// so anything that assumes a piece the parser did not find fails here rather than under
/// someone's caret.
/// </para>
/// </summary>
public sealed class BrokenSourceTests
{
    /// <summary>How much is said about one variant before the rest of it is the same news.</summary>
    private const int Most = 5;

    [Fact]
    public void EveryRequestAnswersOnWholeAndBrokenLines()
    {
        // One variant of one source is the unit of work, rather than a whole source: a long file
        // costs more than a short one and the file as written more than a cut of it, so splitting
        // them apart is what keeps every core busy to the end.
        var variants = Repo.Sources()
            .SelectMany(path => BrokenLines.Variants(Repo.ReadText(path))
                .Select((text, cut) => (Where: $"{Repo.Named(path)} cut {cut}", Path: Repo.Named(path), Text: text)))
            .ToList();
        Assert.True(variants.Count > 1000, $"{variants.Count} variants is too few to be every source's");

        var failures = Repo.CollectFailures(variants, variant =>
        {
            var problems = new List<string>();
            Sweep(variant.Path, variant.Where, variant.Text, problems);
            return problems;
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    /// <summary>One variant, analyzed as a program of its own and then asked about everywhere.</summary>
    private static void Sweep(string path, string where, string text, List<string> problems)
    {
        var tree = SyntaxTree.Parse(path, text);
        ProgramAnalysis analysis;
        try
        {
            // An `.incbin` is taken to be of unknown length rather than read from disk: how long
            // it is says where the code after it goes, and nothing here asks where that is.
            analysis = Compiler.Analyze([tree], ProjectSettings.None, _ => (long?)null);
        }
        catch (Exception e)
        {
            problems.Add($"{where}: analyzing throws {Told(e)}");
            return;
        }
        if (analysis.ModelFor(path) is not { } model)
        {
            problems.Add($"{where}: the analysis holds no model for the file");
            return;
        }

        WholeFile(analysis, model, where, problems);
        for (var line = 0; line < tree.Lines.Length && problems.Count < Most; line++)
            OnLine(analysis, model, line, where, problems);
    }

    /// <summary>The requests about a whole file, which an editor asks once it is opened or edited.</summary>
    private static void WholeFile(ProgramAnalysis analysis, SemanticModel model, string where, List<string> problems)
    {
        var tree = model.Tree;
        void Answer(string request, Action work) => Answered(problems, () => $"{where}: {request}", work);

        Answer("diagnostics", () => Lsp.ToDiagnostics(analysis.DiagnosticsFor(tree.Path), tree, analysis.Configuration));
        Answer("document symbols", () => Lsp.ToSymbols(tree));
        Answer("folding ranges", () => Lsp.ToFoldingRanges(tree));
        Answer("formatting", () => Lsp.ToFormatting(tree, 0, tree.Lines.Length - 1));
        Answer("code lenses", () => CodeLenses.In(tree, analysis.FlowFor(tree.Path)));
        Answer("document links", () => DocumentLinks.In(model));
        Answer("semantic tokens", () => NameHighlighting.In(model));
    }

    /// <summary>
    /// Every request an editor makes about one line: the ones that take a position, asked at the
    /// caret, and the code actions, asked over the line, which is the selection that offers the
    /// fixes its diagnostics name and the rewrites it allows.
    /// </summary>
    private static void OnLine(
        ProgramAnalysis analysis, SemanticModel model, int line, string where, List<string> problems)
    {
        var program = analysis.Program;
        var caret = Caret(model.Tree, line);
        var at = Lsp.ToPosition(model.Tree, caret);
        void Answer(string request, Action work) =>
            Answered(problems, () => $"{where}: {request} at {at.Line + 1}:{at.Character + 1}", work);

        Answer("hover", () => Lsp.ToHover(analysis, model, caret));
        Answer("definition", () => Lsp.ToDefinition(program, model, caret));
        Answer("references", () => Lsp.ToReferences(program, model, caret, includeDeclaration: true));
        Answer("highlights", () => Lsp.ToHighlights(model, caret));
        Answer("prepare rename", () => Lsp.ToRenameRange(model, caret));
        Answer("rename", () => Lsp.ToRename(program, model, caret, "renamed"));
        Answer("completion", () => Completion.At(program, model, analysis.Cpu, caret));
        Answer("signature help", () => CallHelp.At(program, model, caret));
        Answer("code actions", () =>
            CodeActions.In(analysis, model, new Range(new Position(line, 0), new Position(line, int.MaxValue))));
        Answer("call hierarchy", () =>
        {
            foreach (var item in CallHierarchy.Prepare(analysis, model, caret))
            {
                CallHierarchy.Incoming(analysis, item);
                CallHierarchy.Outgoing(analysis, item);
            }
        });
    }

    /// <summary>
    /// Where the caret is while one line is being written: just past the last word written on it,
    /// which is where the next keystroke goes and, a reference ending there, still on that word.
    /// Which word it is moves along the line from one cut variant to the next, so between them
    /// every word of the line is asked about.
    /// </summary>
    private static int Caret(SyntaxTree tree, int index)
    {
        var line = tree.Lines[index];
        var start = tree.LineStarts[index];
        return line.Tokens.Length < 2
            ? start
            : start + line.TextOffset(line.Tokens.Length - 2) + line.Tokens[^2].Text.Length;
    }

    /// <summary>
    /// Runs one request, and says so when it throws. <paramref name="what"/> is asked for the
    /// message only then, because this runs a few hundred thousand times.
    /// </summary>
    private static void Answered(List<string> problems, Func<string> what, Action work)
    {
        try
        {
            work();
        }
        catch (Exception e)
        {
            problems.Add($"{what()} throws {Told(e)}");
        }
    }

    /// <summary>An exception as the message names it: what it was, and where it was thrown.</summary>
    private static string Told(Exception e) =>
        $"{e.GetType().Name}: {e.Message}{(e.StackTrace is { } stack ? "\n" + stack : "")}";
}
