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
/// A program in which some files changed can be built from the one before it by reading only
/// those files again, together with every file the change can reach: a file that looked up a
/// name whose meaning changed, and a file whose constants the changed files' constants are
/// worth something through. Nothing any other file resolved or evaluated can have changed.
/// </para>
/// </summary>
public sealed class ProgramModel
{
    private readonly IReadOnlyList<ProgramSymbols.Module> modules;
    private readonly SymbolMap resolved;
    private readonly SymbolMap declared;
    private readonly Forwarding forwarding;

    // The names each file looked for in the others, found or not.
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> lookedUp;

    // What each file's own analysis found — binding, evaluating its symbols, checking its
    // macros and signatures — by file, and what only the whole program can say.
    private readonly IReadOnlyDictionary<string, IReadOnlyList<Diagnostic>> byFile;
    private readonly IReadOnlyList<Diagnostic> tables;
    private readonly IReadOnlyList<Diagnostic> segmentValues;

    private ProgramModel(
        IReadOnlyList<SemanticModel> files, SegmentTable segments, ProgramSymbols symbols,
        IReadOnlyList<ProgramSymbols.Module> modules, SymbolMap resolved, SymbolMap declared, Forwarding forwarding,
        IReadOnlyDictionary<string, IReadOnlySet<string>> lookedUp,
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
        this.lookedUp = lookedUp;
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
        // What is found belongs to the file of the macro it is reported for.
        var declaredMacros = binders.SelectMany(binder => binder.DeclaredMacros()).ToList();
        Macros.CheckRecursion(declaredMacros, symbol => symbol, (macro, found) => byFile[macro.Tree.Path].Add(found));
        var macros = new List<Diagnostic>();
        Macros.CheckExportedUses(declaredMacros, symbols.IsExported, macros);
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

        var all = byFile.Values.SelectMany(file => file).Concat(tables).Concat(segmentValues).ToList();
        var files = new List<SemanticModel>();
        for (var i = 0; i < trees.Count; i++)
        {
            var path = trees[i].Path;
            files.Add(new SemanticModel(trees[i], segments, configuration, bound[i], resolved, declared,
                Expanded(binders[i], symbol => symbol), all.Where(d => d.Span.File == path), binaryLength));
        }
        var lookedUp = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var binder in binders)
            lookedUp.TryAdd(binder.Tree.Path, binder.LookedUp);
        return new ProgramModel(
            files, segments, symbols, modules, resolved, declared, Forwarding.None, lookedUp,
            byFile.ToDictionary(pair => pair.Key, IReadOnlyList<Diagnostic> (pair) => pair.Value, StringComparer.Ordinal),
            tables, segmentValues);
    }

    /// <summary>
    /// What <paramref name="symbol"/> stands for in this program. A file that was not read
    /// again after another file changed still names what that file declared before, which is
    /// the same declaration under the same name; this is the symbol for it now.
    /// </summary>
    public Symbol Current(Symbol symbol) => forwarding.Current(symbol);

    /// <summary>
    /// The program with the files at <paramref name="dirty"/> read again, as
    /// <paramref name="trees"/> now has them, and every other file's model kept. Null when
    /// that is not enough: <paramref name="affected"/> then names the other files that have to
    /// be read again with them, or is empty when no set of files would do and the whole
    /// program has to be read. <paramref name="configuration"/> already answers the changed
    /// files' conditions, and <paramref name="moved"/> carries a diagnostic about a changed
    /// file to where it is now, or answers null when an edit rewrote the place it names.
    /// </summary>
    internal ProgramModel? Reanalyzing(
        IReadOnlyDictionary<string, SyntaxTree> trees,
        IReadOnlySet<string> dirty,
        Configuration configuration,
        SyntaxTree? defines,
        Func<Diagnostic, Diagnostic?> moved,
        Func<string, long?>? binaryLength,
        out HashSet<string> affected)
    {
        affected = new HashSet<string>(StringComparer.Ordinal);
        var byPath = new Dictionary<string, SemanticModel>(StringComparer.Ordinal);
        foreach (var file in Files)
            byPath.TryAdd(file.Tree.Path, file);
        var others = Files.Where(file => !dirty.Contains(file.Tree.Path)).ToList();

        var binders = dirty.ToDictionary(path => path, path => Binder.Collect(trees[path], Segments, configuration), StringComparer.Ordinal);
        List<ProgramSymbols.Module> replaced = [.. modules.Select(module =>
            binders.TryGetValue(module.Tree.Path, out var binder)
                ? new ProgramSymbols.Module(binder.Tree, binder.FileScope, binder.Exported())
                : module)];
        var tables = new List<Diagnostic>();
        var symbols = ProgramSymbols.Build(replaced, tables);
        var bound = binders.ToDictionary(pair => pair.Key, pair => pair.Value.Resolve(symbols), StringComparer.Ordinal);

        // Another file's symbol may hold on to one of these files' symbols itself, rather than
        // naming it where it is written: a type, a macro called or used. It goes on holding the
        // old one, and where that matters — a macro's expansion, the recursion check, whether a
        // cycle closes — the forwarding answers the new one for it, by name.
        var forwarding = new Forwarding(path =>
            bound.TryGetValue(path, out var result) ? result.Symbols : byPath.GetValueOrDefault(path)?.Symbols);
        var names = bound.ToDictionary(pair => pair.Key, pair => Names(pair.Value), StringComparer.Ordinal);
        var resolved = new SymbolMap(
            this.resolved.Replacing(dirty.Select(path => (byPath[path].Tree, trees[path], names[path].Resolved))),
            forwarding.Current);
        var declared = new SymbolMap(
            this.declared.Replacing(dirty.Select(path => (byPath[path].Tree, trees[path], names[path].Declared))),
            forwarding.Current);

        // Every other file's symbols keep the values they have: one whose value changed because
        // of these files is in a file that looked up a name whose meaning changed, which is read
        // again below. One that could close a cycle with these files is read again with them now.
        var found = dirty.ToDictionary(path => path, path => new List<Diagnostic>(bound[path].Diagnostics), StringComparer.Ordinal);
        var evaluation = new List<Diagnostic>();
        var owners = new List<string>();
        var reads = Evaluator.EvaluateSymbols(
            Segments, [.. dirty.Order(StringComparer.Ordinal).SelectMany(path => bound[path].Symbols)], resolved, evaluation, owners,
            symbol => !dirty.Contains(symbol.Tree.Path), binaryLength);
        for (var i = 0; i < evaluation.Count; i++)
            found[owners[i]].Add(evaluation[i]);
        foreach (var read in reads)
        {
            if (MayCloseACycle(read, dirty, resolved))
                affected.Add(read.Tree.Path);
        }
        if (affected.Count > 0)
            return null;

        var declaredMacros = binders.Values.SelectMany(binder => binder.DeclaredMacros()).ToList();
        Macros.CheckRecursion(declaredMacros, forwarding.Current, (macro, diagnostic) => found[macro.Tree.Path].Add(diagnostic));
        var macros = new List<Diagnostic>();
        Macros.CheckExportedUses(declaredMacros, symbols.IsExported, macros);
        foreach (var diagnostic in macros)
            found[diagnostic.Span.File].Add(diagnostic);
        foreach (var result in bound.Values)
            Value(result.Symbols, Segments, resolved, found);
        CheckDefineNames(replaced, defines, tables);

        // A name whose meaning changed is news to every file that looked it up. A macro, a
        // function or a list that names it in its body has changed too, and is news in turn to
        // every file that looked that up.
        foreach (var path in dirty)
        {
            var before = FileInterface.Of(byPath[path].Symbols, Symbols.IsExported, this.resolved);
            var after = FileInterface.Of(bound[path].Symbols, symbols.IsExported, resolved);
            HashSet<string> changed = [.. FileInterface.Changed(before, after)];

            // Two declarations under one name leave a file that names it with no telling which.
            if (!Forwarding.HasDistinctNames(byPath[path].Symbols) || !Forwarding.HasDistinctNames(bound[path].Symbols))
                changed.UnionWith(before.Keys.Concat(after.Keys));
            if (changed.Count == 0)
                continue;
            var heads = changed.Select(name => name.Split("::")[0]).ToHashSet(StringComparer.Ordinal);
            foreach (var other in others)
            {
                if (lookedUp.GetValueOrDefault(other.Tree.Path) is { } looked && looked.Overlaps(heads))
                    affected.Add(other.Tree.Path);
            }
        }

        // What every other file found stands, carried to where an edit moved it. A file that
        // said something about a place an edit rewrote is read again, and says it afresh.
        var byFile = new Dictionary<string, IReadOnlyList<Diagnostic>>(StringComparer.Ordinal);
        foreach (var (path, diagnostics) in this.byFile)
        {
            if (found.TryGetValue(path, out var again))
                byFile[path] = again;
            else if (EditMap.Moved(diagnostics, moved) is { } kept)
                byFile[path] = kept;
            else
                affected.Add(path);
        }
        if (affected.Count > 0 || EditMap.Moved(segmentValues, moved) is not { } segmentValuesNow)
            return null;

        var all = byFile.Values.SelectMany(diagnostics => diagnostics).Concat(tables).Concat(segmentValuesNow).ToList();
        var files = Files
            .Select(file => dirty.Contains(file.Tree.Path)
                ? new SemanticModel(trees[file.Tree.Path], Segments, configuration, bound[file.Tree.Path], resolved,
                    declared, Expanded(binders[file.Tree.Path], forwarding.Current), all.Where(d => d.Span.File == file.Tree.Path), binaryLength)
                : file)
            .ToList();
        var lookups = new Dictionary<string, IReadOnlySet<string>>(lookedUp, StringComparer.Ordinal);
        foreach (var (path, binder) in binders)
            lookups[path] = binder.LookedUp;
        return new ProgramModel(
            files, Segments, symbols, replaced, resolved, declared, forwarding, lookups, byFile, tables, segmentValuesNow);
    }

    /// <summary>
    /// What the macros <paramref name="binder"/>'s file calls use, their own calls included. An
    /// expansion lands in the calling file, so this file's output is what has to bring those
    /// names in.
    /// </summary>
    private static List<Symbol> Expanded(Binder binder, Func<Symbol, Symbol> current) =>
        [.. Macros.Reachable(binder.CalledMacros()).SelectMany(macro => macro.Uses.Select(use => current(use.Used)))];

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
    /// Whether <paramref name="read"/>, a symbol of a file that is not read again, could be on
    /// a cycle with the files that are: whether something it is evaluated from reaches one of
    /// their symbols that is itself evaluated, however indirectly, from it. Its value was worked
    /// out before, and a cycle an edit closed through it would never be seen from its value.
    /// </summary>
    private static bool MayCloseACycle(Symbol read, IReadOnlySet<string> dirty, SymbolMap resolved)
    {
        var reached = new HashSet<Symbol>();
        var pending = new Stack<Symbol>([read]);
        while (pending.TryPop(out var next))
        {
            foreach (var named in Named(next, resolved))
            {
                if (!reached.Add(named))
                    continue;
                if (dirty.Contains(named.Tree.Path))
                {
                    if (Reaches(named, read, resolved))
                        return true;
                }
                else
                {
                    pending.Push(named);
                }
            }
        }
        return false;
    }

    /// <summary>Whether evaluating <paramref name="from"/> can come to <paramref name="to"/>, through any file.</summary>
    private static bool Reaches(Symbol from, Symbol to, SymbolMap resolved)
    {
        var reached = new HashSet<Symbol> { from };
        var pending = new Stack<Symbol>([from]);
        while (pending.TryPop(out var next))
        {
            foreach (var named in Named(next, resolved))
            {
                if (named == to)
                    return true;
                if (reached.Add(named))
                    pending.Push(named);
            }
        }
        return false;
    }

    /// <summary>
    /// The symbols evaluating <paramref name="symbol"/> reads directly: every name in anything it
    /// is evaluated from, its type, the enum member before it, and what holds or makes it up.
    /// </summary>
    private static IEnumerable<Symbol> Named(Symbol symbol, SymbolMap resolved)
    {
        var written = new[] { symbol.ValueExpression, symbol.Data, symbol.TypeExpression }
            .Concat(symbol.Items)
            .Concat(symbol.Entries)
            .OfType<SyntaxNode>();
        foreach (var node in written.SelectMany(node => node.DescendantNodes().Prepend(node)))
        {
            foreach (var token in node.ChildTokens)
            {
                if (resolved.TryGetValue((node.Tree, token.Span.Start), out var named))
                    yield return named;
            }
        }
        var linked = new[] { symbol.Type, symbol.PreviousMember, symbol.Scope.Owner }
            .Concat(symbol.Body?.Symbols ?? [])
            .OfType<Symbol>();
        foreach (var other in linked)
            yield return resolved.Current(other);
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
