using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What a whole program means: every file's model, and the table of what they can see of
/// one another.
/// <para>
/// Files are read in two passes, because a name may be used before the file that declares
/// it has been read, and in another file besides. The first pass collects declarations only;
/// the export table is then built from them; the second pass resolves every name against it.
/// Evaluation runs once for the program, so a constant in one file may be defined in terms
/// of a constant in another and a cycle between two files is still one cycle.
/// </para>
/// </summary>
public sealed class ProgramModel
{
    private ProgramModel(
        IReadOnlyList<SemanticModel> files, SegmentTable segments, ProgramSymbols symbols,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        Files = files;
        Segments = segments;
        Symbols = symbols;
        Diagnostics = diagnostics;
    }

    /// <summary>One model per file, in the order the files were given.</summary>
    public IReadOnlyList<SemanticModel> Files { get; }

    /// <summary>The program's segments.</summary>
    public SegmentTable Segments { get; }

    /// <summary>What each file may name in the others.</summary>
    public ProgramSymbols Symbols { get; }

    /// <summary>Everything wrong with the program's names and constants, ordered.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>
    /// Builds the program from <paramref name="trees"/>. <paramref name="defines"/>, where
    /// there is one, is the file the build configuration was read as: everything it
    /// declares is a define, visible everywhere.
    /// </summary>
    public static ProgramModel Create(
        IReadOnlyList<SyntaxTree> trees,
        SegmentTable segments,
        Configuration? configuration = null,
        SyntaxTree? defines = null,
        Func<string, long?>? binaryLength = null)
    {
        configuration ??= Configuration.Everything;
        // What is wrong with the program rather than with one file: a name two files export,
        // a constant defined in terms of itself across files, a file shadowing a define.
        var program = new List<Diagnostic>();
        var binders = trees.Select(tree => Binder.Collect(tree, segments, configuration)).ToList();

        var modules = new List<ProgramSymbols.Module>();
        foreach (var binder in binders)
        {
            // A define is exported by being one: it is visible in every file, as if
            // declared and exported once.
            var exported = binder.Tree == defines ? binder.FileScope.Symbols : binder.Exported();
            if (binder.Tree == defines)
            {
                foreach (var symbol in exported)
                    symbol.IsDefine = true;
            }
            modules.Add(new ProgramSymbols.Module(binder.Tree, binder.FileScope, exported));
        }

        var symbols = ProgramSymbols.Build(modules, program);
        var bound = binders.Select(binder => binder.Resolve(symbols)).ToList();

        // Whether a macro can reach itself is a question about the program: a body in one
        // file may call a macro in another, and a cycle between the two is still one cycle.
        Macros.CheckRecursion(binders.SelectMany(binder => binder.DeclaredMacros()), program);

        // One map for the program, keyed by file as well as position: evaluating a constant
        // in one file may follow a name into another, where the same offsets mean something
        // else entirely.
        var resolved = new Dictionary<(SyntaxTree Tree, int Position), Symbol>();
        for (var i = 0; i < trees.Count; i++)
        {
            foreach (var reference in bound[i].References)
            {
                if (!reference.IsDeclaration)
                    resolved[(trees[i], reference.Span.Start)] = reference.Symbol;
            }
        }

        Evaluator.EvaluateSymbols(
            segments, [.. bound.SelectMany(result => result.Symbols)], resolved, program, binaryLength);
        CheckDefineNames(modules, defines, program);

        var files = new List<SemanticModel>();
        for (var i = 0; i < trees.Count; i++)
        {
            files.Add(new SemanticModel(trees[i], segments, configuration, bound[i], resolved,
                program.Where(d => d.Span.File == trees[i].Path), binaryLength));
        }
        return new ProgramModel(files, segments, symbols, Norristown.Diagnostics.Ordered(
            bound.SelectMany(result => result.Diagnostics).Concat(program)));
    }

    /// <summary>A file may not declare a name the build configuration already gives it.</summary>
    private static void CheckDefineNames(
        IReadOnlyList<ProgramSymbols.Module> modules, SyntaxTree? defines, List<Diagnostic> diagnostics)
    {
        if (defines is null)
            return;
        var configured = modules
            .First(module => module.Tree == defines)
            .FileScope.Symbols
            .ToDictionary(symbol => symbol.Name, StringComparer.Ordinal);

        foreach (var module in modules.Where(module => module.Tree != defines))
        {
            foreach (var symbol in module.FileScope.Symbols)
            {
                if (!symbol.IsCheapLocal && configured.ContainsKey(symbol.Name))
                {
                    diagnostics.Add(new Diagnostic(symbol.DeclarationSpan, Severity.Error,
                        $"`{symbol.Name}` is a define, and a file may not declare one"));
                }
            }
        }
    }
}
