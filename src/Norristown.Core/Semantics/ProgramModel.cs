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
/// <para>
/// A program in which one file changed can be built from the one before it, reading only that
/// file again, when the change leaves the file's interface as it was: nothing another file
/// resolved or evaluated can then have changed.
/// </para>
/// </summary>
public sealed class ProgramModel
{
    private readonly IReadOnlyList<ProgramSymbols.Module> modules;
    private readonly SymbolMap resolved;
    private readonly SymbolMap declared;
    private readonly Forwarding forwarding;

    // What each file's own analysis found — binding, evaluating its symbols, checking its
    // macros and signatures — by file, and what only the whole program can say.
    private readonly IReadOnlyDictionary<string, IReadOnlyList<Diagnostic>> byFile;
    private readonly IReadOnlyList<Diagnostic> tables;
    private readonly IReadOnlyList<Diagnostic> segmentValues;

    private ProgramModel(
        IReadOnlyList<SemanticModel> files, SegmentTable segments, ProgramSymbols symbols,
        IReadOnlyList<ProgramSymbols.Module> modules, SymbolMap resolved, SymbolMap declared, Forwarding forwarding,
        IReadOnlyDictionary<string, IReadOnlyList<Diagnostic>> byFile, IReadOnlyList<Diagnostic> tables,
        IReadOnlyList<Diagnostic> segmentValues)
    {
        Files = files;
        Segments = segments;
        Symbols = symbols;
        this.modules = modules;
        this.resolved = resolved;
        this.declared = declared;
        this.forwarding = forwarding;
        this.byFile = byFile;
        this.tables = tables;
        this.segmentValues = segmentValues;
        Diagnostics = Norristown.Diagnostics.Ordered(
            byFile.Values.SelectMany(file => file).Concat(tables).Concat(segmentValues));
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
        // a file shadowing a define.
        var tables = new List<Diagnostic>();
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

        var symbols = ProgramSymbols.Build(modules, tables);
        var bound = binders.Select(binder => binder.Resolve(symbols)).ToList();
        var byFile = new Dictionary<string, List<Diagnostic>>(StringComparer.Ordinal);
        foreach (var tree in trees)
            byFile.TryAdd(tree.Path, []);
        for (var i = 0; i < trees.Count; i++)
            byFile[trees[i].Path].AddRange(bound[i].Diagnostics);

        // Whether a macro can reach itself is a question about the program: a body in one
        // file may call a macro in another, and a cycle between the two is still one cycle.
        // What is found belongs to the file the call that closes the cycle is written in.
        var macros = new List<Diagnostic>();
        Macros.CheckRecursion(binders.SelectMany(binder => binder.DeclaredMacros()), macros);
        Macros.CheckExportedUses(binders.SelectMany(binder => binder.DeclaredMacros()), symbols.IsExported, macros);
        foreach (var diagnostic in macros)
            byFile[diagnostic.Span.File].Add(diagnostic);

        // One map for the program, keyed by file as well as position: evaluating a constant
        // in one file may follow a name into another, where the same offsets mean something
        // else entirely.
        var resolvedNames = new Dictionary<SyntaxTree, Dictionary<int, Symbol>>();
        var declaredNames = new Dictionary<SyntaxTree, Dictionary<int, Symbol>>();
        for (var i = 0; i < trees.Count; i++)
            (resolvedNames[trees[i]], declaredNames[trees[i]]) = Names(bound[i]);
        var resolved = new SymbolMap(resolvedNames);
        var declared = new SymbolMap(declaredNames);

        var evaluation = new List<Diagnostic>();
        var owners = new List<string>();
        Evaluator.EvaluateSymbols(
            segments, [.. bound.SelectMany(result => result.Symbols)], resolved, evaluation, owners, null, binaryLength);
        for (var i = 0; i < evaluation.Count; i++)
            byFile[owners[i]].Add(evaluation[i]);

        // A segment's `dp` and `bank`, and a signature's `dp = e` and `dbr = e`, are expressions
        // that nothing before the analysis reads, so they are worked out once the constants are.
        var segmentValues = new List<Diagnostic>();
        segments.Evaluate(expression => Evaluator.ValueOf(expression, segments, resolved).AsNumber(), segmentValues);
        foreach (var result in bound)
            Value(result.Symbols, segments, resolved, byFile);
        CheckDefineNames(modules, defines, tables);

        var forwarding = new Forwarding(trees.ToDictionary(tree => tree.Path, StringComparer.Ordinal), _ => []);
        var all = byFile.Values.SelectMany(file => file).Concat(tables).Concat(segmentValues).ToList();
        var files = new List<SemanticModel>();
        for (var i = 0; i < trees.Count; i++)
        {
            var path = trees[i].Path;
            files.Add(new SemanticModel(trees[i], segments, configuration, bound[i], resolved, declared,
                Expanded(binders[i]), all.Where(d => d.Span.File == path), binaryLength));
        }
        return new ProgramModel(
            files, segments, symbols, modules, resolved, declared, forwarding,
            byFile.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<Diagnostic>)pair.Value, StringComparer.Ordinal),
            tables, segmentValues);
    }

    /// <summary>
    /// What <paramref name="symbol"/> stands for in this program. A file that was not read
    /// again after another file changed still names what that file declared before, which is
    /// the same declaration under the same name; this is the symbol for it now.
    /// </summary>
    public Symbol Current(Symbol symbol) => forwarding.Current(symbol);

    /// <summary>
    /// The program in which <paramref name="before"/> became <paramref name="after"/>, built
    /// from this one by reading only that file again, or null, with the <paramref name="reason"/>,
    /// when that is not enough and the whole program has to be read. <paramref name="configuration"/> already answers the
    /// new file's conditions, and <paramref name="moved"/> carries a diagnostic about the old
    /// file to where it is now, or answers null when the edit rewrote the place it names.
    /// </summary>
    internal ProgramModel? Replacing(
        SyntaxTree before,
        SyntaxTree after,
        Configuration configuration,
        SyntaxTree? defines,
        Func<Diagnostic, Diagnostic?> moved,
        Func<string, long?>? binaryLength,
        out WholeProgramReason reason)
    {
        var path = before.Path;
        var index = Files.ToList().FindIndex(file => file.Tree == before);
        reason = WholeProgramReason.FilesAddedOrRemoved;
        if (index < 0 || before == defines)
            return null;
        var old = Files[index];

        // Another file's symbol that holds on to one of this file's, rather than naming it
        // where it is written, would hold on to the old one: a type, a macro called or used.
        var others = Files.Where(file => file.Tree != before).ToList();
        reason = WholeProgramReason.SymbolsHeldElsewhere;
        foreach (var symbol in others.SelectMany(file => file.Symbols))
        {
            if (symbol.Type?.Tree.Path == path
                || symbol.Calls.Any(call => call.Callee.Tree.Path == path)
                || symbol.Uses.Any(use => use.Used.Tree.Path == path))
            {
                return null;
            }
        }

        var binder = Binder.Collect(after, Segments, configuration);
        var replaced = modules.ToList();
        replaced[replaced.FindIndex(module => module.Tree == before)] =
            new ProgramSymbols.Module(after, binder.FileScope, binder.Exported());
        var tables = new List<Diagnostic>();
        var symbols = ProgramSymbols.Build(replaced, tables);
        var bound = binder.Resolve(symbols);
        reason = WholeProgramReason.DuplicateNames;
        if (!Forwarding.HasDistinctNames(old.Symbols) || !Forwarding.HasDistinctNames(bound.Symbols))
            return null;

        var trees = replaced.ToDictionary(module => module.Tree.Path, module => module.Tree, StringComparer.Ordinal);
        var forwarding = new Forwarding(trees, of => of == path ? bound.Symbols : Files.First(file => file.Tree.Path == of).Symbols);
        var (resolvedNames, declaredNames) = Names(bound);
        var resolved = new SymbolMap(this.resolved.Replacing(before, after, resolvedNames), forwarding.Current);
        var declared = new SymbolMap(this.declared.Replacing(before, after, declaredNames), forwarding.Current);

        // Every other file's symbols keep the values they have. One of them that is worth what
        // it is only through this file could close a cycle the edit made, which reading this
        // file alone would never see.
        var file = new List<Diagnostic>(bound.Diagnostics);
        var owners = new List<string>();
        var reads = Evaluator.EvaluateSymbols(
            Segments, bound.Symbols, resolved, file, owners, symbol => symbol.Tree.Path != path, binaryLength);
        var visited = new HashSet<Symbol>();
        reason = WholeProgramReason.EvaluationReachesBack;
        if (reads.Any(read => Reaches(read, path, resolved, visited)))
            return null;

        var macros = new List<Diagnostic>();
        Macros.CheckRecursion(binder.DeclaredMacros(), macros);
        Macros.CheckExportedUses(binder.DeclaredMacros(), symbols.IsExported, macros);
        file.AddRange(macros.Where(diagnostic => diagnostic.Span.File == path));
        var values = new Dictionary<string, List<Diagnostic>>(StringComparer.Ordinal) { [path] = file };
        Value(bound.Symbols, Segments, resolved, values);
        CheckDefineNames(replaced, defines, tables);

        // The interface is what decides. A name another file wrote is described in full on both
        // sides, whether or not it is exported, because that file used what it means.
        var namedElsewhere = others
            .SelectMany(other => other.References)
            .Where(reference => reference.Symbol.Tree.Path == path)
            .Select(reference => reference.Symbol.QualifiedName)
            .ToHashSet(StringComparer.Ordinal);
        reason = WholeProgramReason.InterfaceChanged;
        if (FileInterface.Of(old.Symbols, Symbols.IsExported, namedElsewhere)
            != FileInterface.Of(bound.Symbols, symbols.IsExported, namedElsewhere))
        {
            return null;
        }

        reason = WholeProgramReason.DiagnosticInEditedText;
        var byFile = new Dictionary<string, IReadOnlyList<Diagnostic>>(StringComparer.Ordinal) { [path] = file };
        foreach (var (other, found) in this.byFile)
        {
            if (other != path && Moved(found, moved) is { } kept)
                byFile[other] = kept;
            else if (other != path)
                return null;
        }
        if (Moved(segmentValues, moved) is not { } segmentValuesNow)
            return null;

        var all = byFile.Values.SelectMany(diagnostics => diagnostics).Concat(tables).Concat(segmentValuesNow);
        var model = new SemanticModel(after, Segments, configuration, bound, resolved, declared,
            Expanded(binder), all.Where(d => d.Span.File == path), binaryLength);
        var files = Files.ToList();
        files[index] = model;
        return new ProgramModel(
            files, Segments, symbols, replaced, resolved, declared, forwarding, byFile, tables, segmentValuesNow);
    }

    /// <summary>
    /// What the macros <paramref name="binder"/>'s file calls use, their own calls included. An
    /// expansion lands in the calling file, so this file's output is what has to bring those
    /// names in.
    /// </summary>
    private static List<Symbol> Expanded(Binder binder) =>
        [.. Macros.Reachable(binder.CalledMacros()).SelectMany(macro => macro.Uses.Select(use => use.Used))];

    /// <summary>Where one file's names resolve to and where it declares them, by position.</summary>
    private static (Dictionary<int, Symbol> Resolved, Dictionary<int, Symbol> Declared) Names(Binder.Result bound)
    {
        var resolved = new Dictionary<int, Symbol>();
        var declared = new Dictionary<int, Symbol>();
        foreach (var reference in bound.References)
            (reference.IsDeclaration ? declared : resolved)[reference.Span.Start] = reference.Symbol;
        return (resolved, declared);
    }

    /// <summary>Works out the values a signature's items write, now that the constants are known.</summary>
    private static void Value(
        IEnumerable<Symbol> symbols, SegmentTable segments, SymbolMap resolved, Dictionary<string, List<Diagnostic>> byFile)
    {
        long? ValueOf(SyntaxNode expression) => Evaluator.ValueOf(expression, segments, resolved).AsNumber();
        foreach (var symbol in symbols)
        {
            void Report(TextSpan span, string message) =>
                byFile[symbol.Tree.Path].Add(new Diagnostic(symbol.Tree.GetSpan(span), Severity.Error, message));
            symbol.Signature = symbol.Signature?.Valued(ValueOf, Report);
            symbol.MacroSignature = symbol.MacroSignature?.Valued(ValueOf, Report);
        }
    }

    /// <summary>
    /// Whether what <paramref name="symbol"/> is worth could depend on a symbol of
    /// <paramref name="path"/>: whether a name in anything it is evaluated from, followed as
    /// far as it goes, reaches that file.
    /// </summary>
    private static bool Reaches(Symbol symbol, string path, SymbolMap resolved, HashSet<Symbol> visited)
    {
        if (symbol.Tree.Path == path)
            return true;
        if (!visited.Add(symbol))
            return false;

        var written = new[] { symbol.ValueExpression, symbol.Data, symbol.TypeExpression }
            .Concat(symbol.Items)
            .Concat(symbol.Entries)
            .OfType<SyntaxNode>();
        foreach (var node in written.SelectMany(node => node.DescendantNodes().Prepend(node)))
        {
            foreach (var token in node.ChildTokens)
            {
                if (resolved.TryGetValue((node.Tree, token.Span.Start), out var named)
                    && Reaches(named, path, resolved, visited))
                {
                    return true;
                }
            }
        }
        var linked = new[] { symbol.Type, symbol.PreviousMember, symbol.Scope.Owner }
            .Concat(symbol.Body?.Symbols ?? [])
            .OfType<Symbol>();
        return linked.Any(other => Reaches(other, path, resolved, visited));
    }

    /// <summary>
    /// <paramref name="diagnostics"/> carried through an edit, or null when one of them names a
    /// place the edit rewrote.
    /// </summary>
    private static List<Diagnostic>? Moved(IEnumerable<Diagnostic> diagnostics, Func<Diagnostic, Diagnostic?> moved)
    {
        var kept = new List<Diagnostic>();
        foreach (var diagnostic in diagnostics)
        {
            if (moved(diagnostic) is not { } now)
                return null;
            kept.Add(now);
        }
        return kept;
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
