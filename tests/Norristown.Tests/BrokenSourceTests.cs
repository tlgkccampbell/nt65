using Norristown.LanguageServer;
using Norristown.Project;
using Norristown.Semantics;
using Norristown.Syntax;

// The protocol types define their own Range and Position, and requests take those.
using Position = Norristown.LanguageServer.Protocol.Position;
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests;

/// <summary>
/// Puts every source in the repository, whole and with each line cut short, through everything
/// above the parser. Its names are bound, its code is laid out, its control flow is analysed,
/// and every editor request is answered at a caret on every line. Nothing about the answers is
/// asserted, because a half-written file has no right answer. The test checks only that
/// answering does not throw.
/// <para>
/// This is the safety net under the analysis, as <see cref="Syntax.TypedNodeTests"/> is under the
/// node accessors. An editor runs all of this at every keystroke over a file that may not parse,
/// so code that assumes a child element the parser did not find fails here rather than under
/// someone's caret.
/// </para>
/// </summary>
public sealed class BrokenSourceTests
{
    /// <summary>
    /// The number of problems one variant may report before its remaining lines are skipped as
    /// likely repeats.
    /// </summary>
    private const int MaximumProblems = 5;

    /// <summary>
    /// The share of a cut variant's lines that the everyday run makes requests on: one line in
    /// this many. The thorough run makes them on every line.
    /// </summary>
    private const int Stride = 8;

    [Fact]
    public void EveryRequestAnswersOnWholeAndBrokenLines()
    {
        // The unit of parallel work is one variant of one source, not a whole source. A long file
        // costs more than a short one, and the uncut file more than a cut one, so smaller units
        // keep every core busy until the end.
        var variants = Repo.Sources()
            .SelectMany(path => BrokenLines.Of(path)
                .Select((text, cut) => (Where: $"{Repo.Named(path)} cut {cut}", Path: Repo.Named(path), Text: text, Cut: cut)))
            .ToList();
        Assert.True(variants.Count > 1000, $"{variants.Count} variants is too few to be every source's");

        var failures = Repo.CollectFailures(variants, variant =>
        {
            var problems = new List<string>();
            Sweep(variant.Path, variant.Where, variant.Text, variant.Cut, problems);
            return problems;
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    /// <summary>
    /// Analyzes one variant as a program of its own, and then makes requests about it line by
    /// line. The everyday run makes them on every line of the uncut source but only on every
    /// <see cref="Stride"/>th line of a cut one. Each cut starts at a different line, so together
    /// the cuts still reach most lines. The thorough run makes them on every line of every variant.
    /// </summary>
    private static void Sweep(string path, string where, string text, int cut, List<string> problems)
    {
        var tree = SyntaxTree.Parse(path, text);
        ProgramAnalysis analysis;
        try
        {
            // An `.incbin` is taken to be of unknown length instead of read from disk. Its length
            // only decides where the code after it is laid out, and nothing here depends on that.
            analysis = Compiler.Analyze([tree], ProjectSettings.None, _ => (long?)null);
        }
        catch (Exception e)
        {
            problems.Add($"{where}: analyzing throws {Describe(e)}");
            return;
        }
        if (analysis.ModelFor(path) is not { } model)
        {
            problems.Add($"{where}: the analysis holds no model for the file");
            return;
        }

        // A program with errors is still emitted, so the emitter has to cope with half-typed
        // lines just as every stage before it does.
        try
        {
            Compiler.Emit(analysis, ProjectSettings.None);
        }
        catch (Exception e)
        {
            problems.Add($"{where}: emitting throws {Describe(e)}");
        }

        WholeFile(analysis, model, where, problems);
        var step = cut == 0 || Fixtures.FixtureRunner.ThoroughMode ? 1 : Stride;
        for (var line = step == 1 ? 0 : cut % Stride; line < tree.LineCount && problems.Count < MaximumProblems; line += step)
            OnLine(analysis, model, line, where, problems);
    }

    /// <summary>
    /// Makes the requests about a whole file, which an editor makes once the file is opened or
    /// edited.
    /// </summary>
    private static void WholeFile(ProgramAnalysis analysis, SemanticModel model, string where, List<string> problems)
    {
        var tree = model.Tree;
        void Answer(string request, Action work) => Answered(problems, () => $"{where}: {request}", work);

        Answer("diagnostics", () => Lsp.ToDiagnostics(analysis.DiagnosticsFor(tree.Path), tree, analysis.Configuration));
        Answer("document symbols", () => Lsp.ToSymbols(tree));
        Answer("folding ranges", () => Lsp.ToFoldingRanges(tree));
        Answer("formatting", () => Lsp.ToFormatting(tree, 0, tree.LineCount - 1));
        Answer("code lenses", () => CodeLenses.In(tree, model.Families, analysis.FlowFor(tree.Path)));
        Answer("document links", () => DocumentLinks.In(model));
        Answer("semantic tokens", () => NameHighlighting.In(model));
    }

    /// <summary>
    /// Makes every request an editor makes about one line. The requests that take a position are
    /// made at the caret. The code actions are requested over the whole line, because selecting
    /// the line is what offers the fixes for its diagnostics and the refactorings that apply to it.
    /// </summary>
    private static void OnLine(
        ProgramAnalysis analysis, SemanticModel model, int line, string where, List<string> problems)
    {
        var program = analysis.Program;
        var caret = Caret(model.Tree, line);
        var at = Lsp.ToPosition(model.Tree, caret);
        void Answer(string request, Action work) =>
            Answered(problems, () => $"{where}: {request} at {at.Line + 1}:{at.Character + 1}", work);

        Answer("hover", () => Hovers.At(analysis, model, caret));
        Answer("definition", () => Lsp.ToDefinition(program, model, caret));
        Answer("references", () => Lsp.ToReferences(program, model, caret, includeDeclaration: true));
        Answer("highlights", () => Lsp.ToHighlights(model, caret));
        Answer("prepare rename", () => Rename.RangeAt(model, caret));
        Answer("rename", () => Rename.EditAt(program, model, caret, "renamed"));
        Answer("completion", () => Completion.At(program, model, analysis.Cpu, caret, snippets: true));
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
    /// Returns where the caret is while a line is being typed, which is just past the last token
    /// on it. That is where the next keystroke goes, and because it touches that token, it is
    /// still on a name ending there. The last token differs from one cut variant to the next, so
    /// across the variants the requests are made at many different tokens of each line.
    /// </summary>
    private static int Caret(SyntaxTree tree, int index)
    {
        var tokens = tree.GetLine(index).Tokens;
        return tokens.Count < 2 ? tree.LineStarts[index] : tokens[^2].Span.End;
    }

    /// <summary>
    /// Runs one request, and records a problem if it throws. <paramref name="what"/> is called to
    /// build the message only in that case, because this runs a few hundred thousand times.
    /// </summary>
    private static void Answered(List<string> problems, Func<string> what, Action work)
    {
        try
        {
            work();
        }
        catch (Exception e)
        {
            problems.Add($"{what()} throws {Describe(e)}");
        }
    }

    /// <summary>
    /// Formats an exception for a problem report, with its type, its message and where it was
    /// thrown.
    /// </summary>
    private static string Describe(Exception e) =>
        $"{e.GetType().Name}: {e.Message}{(e.StackTrace is { } stack ? "\n" + stack : "")}";
}
