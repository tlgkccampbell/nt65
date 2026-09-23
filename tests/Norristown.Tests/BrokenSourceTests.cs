using Norristown.LanguageServer;
using Norristown.Project;
using Norristown.Semantics;
using Norristown.Syntax;

// The protocol types define their own Range and Position, which are the ones requests take.
using Position = Norristown.LanguageServer.Protocol.Position;
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests;

/// <summary>
/// Every source in the repository, whole and with each line cut short, put through everything
/// above the parser: its names bound, its code laid out, its control flow analysed, and every
/// editor request answered at a caret on every line. Nothing about the answers is asserted,
/// because a half-written file has no right answer; only that answering does not throw.
/// <para>
/// This is the safety net under the analysis, as <see cref="Syntax.TypedNodeTests"/> is under the
/// node accessors. An editor runs all of this at every keystroke over a file that may not parse,
/// so code that assumes a piece the parser did not find fails here rather than under
/// someone's caret.
/// </para>
/// </summary>
public sealed class BrokenSourceTests
{
    /// <summary>How many problems one variant may report before its remaining lines are skipped, as likely repeats.</summary>
    private const int Most = 5;

    [Fact]
    public void EveryRequestAnswersOnWholeAndBrokenLines()
    {
        // The unit of parallel work is one variant of one source, not a whole source: a long file
        // costs more than a short one, and the uncut file more than a cut one, so smaller units
        // keep every core busy until the end.
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
            // An `.incbin` is taken to be of unknown length rather than read from disk: its length
            // only decides where the code after it is placed, and nothing here depends on that.
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

        // A program with errors is still emitted, so the emitter has to cope with half-typed
        // lines just as every stage before it does.
        try
        {
            Compiler.Emit(analysis, ProjectSettings.None);
        }
        catch (Exception e)
        {
            problems.Add($"{where}: emitting throws {Told(e)}");
        }

        WholeFile(analysis, model, where, problems);
        for (var line = 0; line < tree.LineCount && problems.Count < Most; line++)
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
        Answer("formatting", () => Lsp.ToFormatting(tree, 0, tree.LineCount - 1));
        Answer("code lenses", () => CodeLenses.In(tree, analysis.FlowFor(tree.Path)));
        Answer("document links", () => DocumentLinks.In(model));
        Answer("semantic tokens", () => NameHighlighting.In(model));
    }

    /// <summary>
    /// Every request an editor makes about one line: the ones that take a position, asked at the
    /// caret, and the code actions, asked over the whole line, since selecting the line is what
    /// offers the fixes for its diagnostics and the refactorings that apply to it.
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
    /// Where the caret is while a line is being typed: just past the last token on it, which is
    /// where the next keystroke goes and, since it touches that token, still on a name ending
    /// there. The last token differs from one cut variant to the next, so across the variants
    /// the requests are asked at many different tokens of each line.
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
            problems.Add($"{what()} throws {Told(e)}");
        }
    }

    /// <summary>An exception as a problem reports it: its type and message, and where it was thrown.</summary>
    private static string Told(Exception e) =>
        $"{e.GetType().Name}: {e.Message}{(e.StackTrace is { } stack ? "\n" + stack : "")}";
}
