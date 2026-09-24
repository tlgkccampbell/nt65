using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Represents what a whole program means: every file's model, and the table of what the files
/// can see of one another.
/// <para>
/// Files are read in two passes, because a name may be used before the file that declares it
/// has been read, and in another file as well. The first pass collects only declarations. The
/// export table is then built from them, and the second pass resolves every name against it.
/// Evaluation runs once for the program, so a constant in one file may be defined in terms of a
/// constant in another, and a cycle between two files is still one cycle.
/// </para>
/// <para>
/// A program in which some files changed can be built from the previous one by reading only
/// those files again, together with every file the change can reach. Such a file either looked
/// up a name whose meaning changed, or has constants that the changed files' constants are
/// evaluated from, when the edit could close a cycle through them. Nothing any other file
/// resolved or evaluated can have changed.
/// </para>
/// </summary>
public sealed class ProgramModel
{
    private readonly IReadOnlyList<ProgramSymbols.Module> modules;
    private readonly SymbolMap resolved;
    private readonly SymbolMap declared;
    private readonly Forwarding forwarding;
    private readonly Cpu cpu;

    // The names each file looked for in the others, found or not.
    private readonly IReadOnlyDictionary<string, IReadOnlySet<LookedUpName>> lookedUp;

    // The names each file uses that the file declaring them does not export. They are reported
    // where they are used, and the declaring file does not also report that nothing uses them.
    // A file that starts or stops using such a name therefore affects the file that declares it.
    private readonly IReadOnlyDictionary<string, IReadOnlySet<UnexportedName>> unexported;

    // The diagnostics each file's own analysis found, by file, from binding, evaluating its
    // symbols, and checking its macros and signatures. The other two lists hold the diagnostics
    // that only the whole program can report.
    private readonly IReadOnlyDictionary<string, IReadOnlyList<Diagnostic>> byFile;
    private readonly IReadOnlyList<Diagnostic> tables;
    private readonly IReadOnlyList<Diagnostic> segmentValues;

    private ProgramModel(
        IReadOnlyList<SemanticModel> files, SegmentTable segments, ProgramSymbols symbols, Cpu cpu,
        IReadOnlyList<ProgramSymbols.Module> modules, SymbolMap resolved, SymbolMap declared, Forwarding forwarding,
        IReadOnlyDictionary<string, IReadOnlySet<LookedUpName>> lookedUp,
        IReadOnlyDictionary<string, IReadOnlySet<UnexportedName>> unexported,
        IReadOnlyDictionary<string, IReadOnlyList<Diagnostic>> byFile, IReadOnlyList<Diagnostic> tables,
        IReadOnlyList<Diagnostic> segmentValues)
    {
        Files = files;
        Segments = segments;
        Symbols = symbols;
        this.cpu = cpu;
        this.modules = modules;
        this.resolved = resolved;
        this.declared = declared;
        this.forwarding = forwarding;
        this.lookedUp = lookedUp;
        this.unexported = unexported;
        this.byFile = byFile;
        this.tables = tables;
        this.segmentValues = segmentValues;
        Diagnostics = Norristown.Diagnostics.Ordered(
            byFile.Values.SelectMany(file => file).Concat(tables).Concat(segmentValues));
    }

    /// <summary>Gets one model per file, in the order the files were given.</summary>
    public IReadOnlyList<SemanticModel> Files { get; }

    /// <summary>Gets the program's segments.</summary>
    public SegmentTable Segments { get; }

    /// <summary>Gets the table of what each file may name in the others.</summary>
    public ProgramSymbols Symbols { get; }

    /// <summary>Gets the diagnostics for the program's names and constants, in order.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>
    /// Builds the program from <paramref name="trees"/>. <paramref name="cpu"/> is the processor
    /// the program is built for, which decides the mnemonics no name may take. Without it, the
    /// files' <c>.cpu</c> items decide.
    /// </summary>
    public static ProgramModel Create(
        IReadOnlyList<SyntaxTree> trees,
        SegmentTable segments,
        Configuration? configuration = null,
        Func<string, long?>? binaryLength = null,
        Cpu? cpu = null)
    {
        configuration ??= Configuration.Everything;
        var target = cpu ?? ProgramCpu.Resolve(trees, null, []);
        var binders = trees.Select(tree => Binder.Collect(tree, segments, configuration, target)).ToList();
        var modules = binders.Select(binder => binder.Module).ToList();

        var segmentValues = new List<Diagnostic>();
        var read = AnalyzeFiles(
            binders, modules, segments, configuration, target, binaryLength, unchanged: null, segmentValues,
            bound =>
            {
                // The program has one map, keyed by file as well as position. Evaluating a
                // constant in one file may follow a name into another, where the same offsets
                // mean something else entirely.
                var resolvedNames = new Dictionary<SyntaxTree, Dictionary<int, Symbol>>();
                var declaredNames = new Dictionary<SyntaxTree, Dictionary<int, Symbol>>();
                for (var i = 0; i < binders.Count; i++)
                    (resolvedNames[binders[i].Tree], declaredNames[binders[i].Tree]) = Names(bound[i]);
                return (Forwarding.None, new SymbolMap(resolvedNames), new SymbolMap(declaredNames));
            });

        var unexported = new Dictionary<string, IReadOnlySet<UnexportedName>>(StringComparer.Ordinal);
        foreach (var binder in binders)
            unexported.TryAdd(binder.Tree.Path, Unexported(binder));
        var named = NamedElsewhere(unexported);

        var all = read.ByFile.Values.SelectMany(file => file).Concat(read.Tables).Concat(segmentValues).ToList();
        var files = new List<SemanticModel>();
        for (var i = 0; i < trees.Count; i++)
        {
            var path = trees[i].Path;
            files.Add(new SemanticModel(trees[i], segments, configuration, read.Symbols, read.Bound[i], read.Resolved,
                read.Declared, Expanded(binders[i], read.Forwarding.Current), all.Where(d => d.Span.File == path),
                Names(named, path), binaryLength));
        }
        var lookedUp = new Dictionary<string, IReadOnlySet<LookedUpName>>(StringComparer.Ordinal);
        foreach (var binder in binders)
            lookedUp.TryAdd(binder.Tree.Path, binder.LookedUp);
        Freeze(read.Bound);
        return new ProgramModel(
            files, segments, read.Symbols, target, modules, read.Resolved, read.Declared, read.Forwarding, lookedUp,
            unexported,
            read.ByFile.ToDictionary(pair => pair.Key, IReadOnlyList<Diagnostic> (pair) => pair.Value, StringComparer.Ordinal),
            read.Tables, segmentValues);
    }

    /// <summary>
    /// Returns the symbol in this program that corresponds to <paramref name="symbol"/>. A file
    /// that was not read again after another file changed still refers to what that file
    /// declared before, which is the same declaration under the same name. This method returns
    /// the current symbol for it.
    /// </summary>
    public Symbol Current(Symbol symbol) => forwarding.Current(symbol);

    /// <summary>
    /// Returns every reference to <paramref name="symbol"/> in every file of the program,
    /// including its declaration, in file and source order. A file kept from before an edit
    /// elsewhere refers to what the edited file declared then, so each reference's symbol is
    /// compared by its current version.
    /// </summary>
    public IReadOnlyList<(SemanticModel File, SymbolReference Reference)> ReferencesTo(Symbol symbol) =>
        ReferencesTo([symbol]);

    /// <summary>
    /// Returns every reference to any of <paramref name="symbols"/> in every file of the program,
    /// including their declarations, in file and source order. A name that covers several
    /// symbols needs this, such as an enum member and the instances of a <see cref="Family"/>
    /// named after it.
    /// </summary>
    public IReadOnlyList<(SemanticModel File, SymbolReference Reference)> ReferencesTo(
        IReadOnlyCollection<Symbol> symbols)
    {
        var wanted = symbols.Select(Current).ToHashSet();
        return [.. Files
            .OrderBy(file => file.Tree.Path, StringComparer.Ordinal)
            .SelectMany(file => file.References
                .Where(reference => wanted.Contains(Current(reference.Symbol)))
                .Select(reference => (file, reference)))];
    }

    /// <summary>
    /// Returns the program with the files at <paramref name="dirty"/> read again, as
    /// <paramref name="trees"/> now has them, and every other file's model kept. Returns null
    /// when that is not enough. <paramref name="affected"/> then names the other files that must
    /// be read again with them, or is empty when no set of files would do and the whole program
    /// must be read.
    /// <para>
    /// <paramref name="configuration"/> already decides the changed files' conditions.
    /// <paramref name="moved"/> maps a diagnostic about a changed file to its current position,
    /// or returns null when an edit replaced the text it points at.
    /// </para>
    /// </summary>
    internal ProgramModel? Reanalyzing(
        IReadOnlyDictionary<string, SyntaxTree> trees,
        IReadOnlySet<string> dirty,
        Configuration configuration,
        Func<Diagnostic, Diagnostic?> moved,
        Func<string, long?>? binaryLength,
        out HashSet<string> affected)
    {
        affected = new HashSet<string>(StringComparer.Ordinal);
        var byPath = new Dictionary<string, SemanticModel>(StringComparer.Ordinal);
        foreach (var file in Files)
            byPath.TryAdd(file.Tree.Path, file);
        var others = Files.Where(file => !dirty.Contains(file.Tree.Path)).ToList();

        // The files are read in path order, so that their constants are evaluated in the same
        // order each time.
        var binders = dirty.Order(StringComparer.Ordinal).ToDictionary(
            path => path, path => Binder.Collect(trees[path], Segments, configuration, cpu), StringComparer.Ordinal);
        List<ProgramSymbols.Module> replaced = [.. modules.Select(module =>
            binders.TryGetValue(module.Tree.Path, out var binder) ? binder.Module : module)];

        // Every other file's symbols keep their values, and are read as they stand rather than
        // evaluated again.
        var read = AnalyzeFiles(
            [.. binders.Values], replaced, Segments, configuration, cpu, binaryLength,
            unchanged: symbol => !dirty.Contains(symbol.Tree.Path), segmentValues: null,
            results =>
            {
                // Another file's symbol may hold a reference to one of these files' symbols
                // directly, rather than through a name in the source. Examples are a type, and a
                // macro called or used. It keeps holding the old symbol. Where that matters, as in
                // a macro's expansion, the recursion check and the cycle check, the forwarding
                // finds the new symbol by name.
                var reread = binders.Keys.Zip(results)
                    .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
                var forwarding = new Forwarding(path =>
                    reread.TryGetValue(path, out var result) ? result.Symbols : byPath.GetValueOrDefault(path)?.Symbols);
                var names = reread.ToDictionary(pair => pair.Key, pair => Names(pair.Value), StringComparer.Ordinal);
                return (
                    forwarding,
                    new SymbolMap(
                        this.resolved.Replacing(dirty.Select(path => (byPath[path].Tree, trees[path], names[path].Resolved))),
                        forwarding.Current),
                    new SymbolMap(
                        this.declared.Replacing(dirty.Select(path => (byPath[path].Tree, trees[path], names[path].Declared))),
                        forwarding.Current));
            });

        // A symbol of another file whose value changed because of these files is in a file that
        // looked up a name whose meaning changed, and that file is read again below. A file with a
        // symbol that could close a cycle with these files is read again with them now.
        foreach (var symbol in read.Reads)
        {
            if (MayCloseACycle(symbol, dirty, read.Resolved))
                affected.Add(symbol.Tree.Path);
        }
        if (affected.Count > 0)
            return null;

        var bound = binders.Keys.Zip(read.Bound).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
        var (symbols, tables, found) = (read.Symbols, read.Tables, read.ByFile);
        var (forwarding, resolved, declared) = (read.Forwarding, read.Resolved, read.Declared);

        // A name whose meaning changed affects every file that looked it up in its module. A
        // macro, a function or a list that names it in its body has changed too, and in turn
        // affects every file that looked that up. A module that changed its name or its
        // re-exports changed what paths mean, so every file is read again.
        foreach (var path in dirty)
        {
            var module = modules.First(module => module.Tree.Path == path);
            var before = FileInterface.Of(module, byPath[path].Symbols, this.resolved);
            var after = FileInterface.Of(binders[path].Module, bound[path].Symbols, resolved);
            HashSet<string> changed = [.. FileInterface.Changed(before, after)];

            // When two declarations share a name, a file that uses the name cannot tell which one
            // it means, so every entry counts as changed.
            if (!Forwarding.HasDistinctNames(byPath[path].Symbols) || !Forwarding.HasDistinctNames(bound[path].Symbols))
                changed.UnionWith(before.Keys.Concat(after.Keys));
            if (changed.Count == 0)
                continue;
            var everything = changed.Any(FileInterface.IsModuleWide);
            var heads = changed.Select(name => name.Split("::")[0])
                .SelectMany(head => new[] { new LookedUpName(module.Name, head), new LookedUpName(null, head) })
                .ToHashSet();
            foreach (var other in others)
            {
                if (everything || lookedUp.GetValueOrDefault(other.Tree.Path) is { } looked && looked.Overlaps(heads))
                    affected.Add(other.Tree.Path);
            }
        }

        // A file that has started or stopped using a name that another file does not export
        // affects the file that declares the name, because that file starts or stops reporting
        // that nothing uses it. Nothing else tracks this, because the cause is in one file and the
        // diagnostic it suppresses is in another.
        var unexportedNow =
            new Dictionary<string, IReadOnlySet<UnexportedName>>(this.unexported, StringComparer.Ordinal);
        foreach (var (path, binder) in binders)
        {
            unexportedNow[path] = Unexported(binder);
            var was = this.unexported[path];
            foreach (var name in unexportedNow[path].Except(was).Concat(was.Except(unexportedNow[path])))
            {
                if (!dirty.Contains(name.Path))
                    affected.Add(name.Path);
            }
        }

        // A diagnostic for the program rather than for one file, such as two exports under one
        // linker name or two files that declare one module, is reported on the file that sorts
        // later. That need not be a file that changed. That file's diagnostics then change, and
        // its other diagnostics depend on them, because a file with an error does not report the
        // names it never uses. So that file is read again with the others.
        foreach (var path in Named(this.tables).Union(Named(tables), StringComparer.Ordinal))
        {
            if (!dirty.Contains(path) && !Format(this.tables, path).SequenceEqual(Format(tables, path), StringComparer.Ordinal))
                affected.Add(path);
        }
        if (affected.Count > 0)
            return null;

        // Every other file's diagnostics are kept and moved to follow the edit. A file with a
        // diagnostic in text the edit replaced is read again and reports it afresh.
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

        var named = NamedElsewhere(unexportedNow);
        var all = byFile.Values.SelectMany(diagnostics => diagnostics).Concat(tables).Concat(segmentValuesNow).ToList();
        var files = Files
            .Select(file => dirty.Contains(file.Tree.Path)
                ? new SemanticModel(trees[file.Tree.Path], Segments, configuration, symbols, bound[file.Tree.Path], resolved,
                    declared, Expanded(binders[file.Tree.Path], forwarding.Current), all.Where(d => d.Span.File == file.Tree.Path),
                    Names(named, file.Tree.Path), binaryLength)
                : file)
            .ToList();
        var lookups = new Dictionary<string, IReadOnlySet<LookedUpName>>(lookedUp, StringComparer.Ordinal);
        foreach (var (path, binder) in binders)
            lookups[path] = binder.LookedUp;
        Freeze(read.Bound);
        return new ProgramModel(
            files, Segments, symbols, cpu, replaced, resolved, declared, forwarding, lookups, unexportedNow, byFile, tables,
            segmentValuesNow);
    }

    /// <summary>
    /// Reads the files of <paramref name="binders"/>, once each has collected its declarations,
    /// and runs every step of the analysis that follows, in order. Both a whole program and a
    /// program in which some files changed are read by this method, so the two report the same
    /// diagnostics.
    /// </summary>
    /// <param name="binders">The files to read, in the order their constants are evaluated.</param>
    /// <param name="modules">Every module of the program, including those of the files not read.</param>
    /// <param name="segments">The program's segments.</param>
    /// <param name="configuration">Which <c>.if</c> branches the build takes.</param>
    /// <param name="cpu">The processor the program is built for.</param>
    /// <param name="binaryLength">Returns the length of each file an <c>.incbin</c> names.</param>
    /// <param name="unchanged">
    /// Returns a value indicating whether a symbol belongs to a file that is not read, whose value
    /// is read as it stands, or is null when every file is read.
    /// </param>
    /// <param name="segmentValues">
    /// Receives the diagnostics from evaluating the segments' values, or is null when those were
    /// evaluated before and are kept.
    /// </param>
    /// <param name="link">
    /// Returns the forwarding and the maps of names for the program, given what each file of
    /// <paramref name="binders"/> resolved, in the same order.
    /// </param>
    private static Reading AnalyzeFiles(
        IReadOnlyList<Binder> binders,
        IReadOnlyList<ProgramSymbols.Module> modules,
        SegmentTable segments,
        Configuration configuration,
        Cpu cpu,
        Func<string, long?>? binaryLength,
        Func<Symbol, bool>? unchanged,
        List<Diagnostic>? segmentValues,
        Func<IReadOnlyList<Binder.Result>, (Forwarding Forwarding, SymbolMap Resolved, SymbolMap Declared)> link)
    {
        // These are the diagnostics for the program rather than for one file, such as two files
        // that declare one module, or two exports under one linker name.
        var tables = new List<Diagnostic>();

        // A family declares one name per member of the enum it iterates over, and the enum may
        // belong to another module. The instances are therefore declared once every file has
        // been read, and before the modules' exports, because what a file exports includes them.
        if (binders.Any(binder => binder.HasFamilies))
        {
            var provisional = ProgramSymbols.Build(modules, []);
            foreach (var binder in binders)
                binder.DeclareFamilies(provisional);
        }
        foreach (var binder in binders)
            binder.Export();

        var symbols = ProgramSymbols.Build(modules, tables);
        var bound = binders.Select(binder => binder.Resolve(symbols)).ToList();
        var byFile = new Dictionary<string, List<Diagnostic>>(StringComparer.Ordinal);
        for (var i = 0; i < binders.Count; i++)
        {
            byFile.TryAdd(binders[i].Tree.Path, []);
            byFile[binders[i].Tree.Path].AddRange(bound[i].Diagnostics);
        }
        var (forwarding, resolved, declared) = link(bound);

        // Whether a macro can reach itself is a question about the whole program. A body in one
        // file may call a macro in another, and a cycle between the two is still one cycle. Each
        // diagnostic belongs to the file of the macro it is reported for.
        var declaredMacros = binders.SelectMany(binder => binder.DeclaredMacros()).ToList();
        Macros.CheckRecursion(declaredMacros, forwarding.Current, (macro, found) => byFile[macro.Tree.Path].Add(found));
        var macros = new List<Diagnostic>();
        Macros.CheckExportedUses(declaredMacros, symbol => symbol.IsExported, macros);
        foreach (var diagnostic in macros)
            byFile[diagnostic.Span.File].Add(diagnostic);

        var evaluation = new List<(Diagnostic Diagnostic, string Owner)>();
        var reads = Evaluator.EvaluateSymbols(
            segments, [.. bound.SelectMany(result => result.Symbols)], resolved, evaluation, unchanged,
            binaryLength, configuration);
        foreach (var (diagnostic, owner) in evaluation)
            byFile[owner].Add(diagnostic);

        // A segment's `dp` and `bank`, and a signature's `dp = e` and `dbr = e`, are expressions
        // that nothing before the analysis reads, so they are evaluated after the constants.
        if (segmentValues is not null)
            segments.Evaluate(
                expression => Evaluator.ValueOf(expression, segments, resolved, configuration: configuration).AsNumber(),
                segmentValues);
        foreach (var result in bound)
            Value(result.Symbols, segments, resolved, configuration, byFile);
        foreach (var result in bound)
        {
            CheckAliases(result.Symbols, resolved, byFile);
            CheckExportSizes(result.Symbols, byFile);
        }

        // This check runs after the alias check, because by then an alias that declares no
        // signature has taken the routine's.
        foreach (var result in bound)
            CheckDeclaredSignatures(result.Symbols, byFile, cpu);
        return new Reading(symbols, bound, byFile, tables, forwarding, resolved, declared, reads);
    }

    /// <summary>
    /// Freezes every symbol the files of <paramref name="bound"/> declare, once the model that
    /// holds them is complete. Another thread may read them from then on, and a model built
    /// from this one after an edit keeps them as they are.
    /// </summary>
    private static void Freeze(IEnumerable<Binder.Result> bound)
    {
        foreach (var symbol in bound.SelectMany(result => result.Symbols))
            symbol.Freeze();
    }

    /// <summary>
    /// Returns the symbols used by the macros that <paramref name="binder"/>'s file calls,
    /// including those used by the macros they call. An expansion lands in the calling file, so
    /// this file's output must bring those names in.
    /// </summary>
    private static List<Symbol> Expanded(Binder binder, Func<Symbol, Symbol> current) =>
        [.. Macros.Reachable(binder.CalledMacros()).SelectMany(macro => macro.Uses.Select(use => current(use.Used)))];

    /// <summary>
    /// Returns the names <paramref name="binder"/>'s file uses that the files declaring them do
    /// not export.
    /// </summary>
    private static IReadOnlySet<UnexportedName> Unexported(Binder binder) =>
        binder.Unexported.Select(symbol => new UnexportedName(symbol.Tree.Path, symbol.QualifiedName)).ToHashSet();

    /// <summary>
    /// Inverts <paramref name="unexported"/>. Returns, for each declaring file, the names it
    /// declares and does not export but that another file uses anyway.
    /// </summary>
    private static Dictionary<string, HashSet<string>> NamedElsewhere(
        IReadOnlyDictionary<string, IReadOnlySet<UnexportedName>> unexported) =>
        unexported.Values
            .SelectMany(names => names)
            .GroupBy(name => name.Path, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(name => name.QualifiedName).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

    /// <summary>
    /// Returns the unexported names of <paramref name="path"/> that another file uses. For most
    /// files the set is empty.
    /// </summary>
    private static IReadOnlySet<string> Names(IReadOnlyDictionary<string, HashSet<string>> named, string path) =>
        named.GetValueOrDefault(path) ?? [];

    /// <summary>Returns the files that the program-wide diagnostics in <paramref name="tables"/> point at.</summary>
    private static IEnumerable<string> Named(IEnumerable<Diagnostic> tables) =>
        tables.Select(diagnostic => diagnostic.Span.File).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// Returns the program-wide diagnostics about <paramref name="path"/>, in order, formatted as
    /// text to compare.
    /// </summary>
    private static IEnumerable<string> Format(IEnumerable<Diagnostic> tables, string path) =>
        tables
            .Where(diagnostic => string.Equals(diagnostic.Span.File, path, StringComparison.Ordinal))
            .Select(diagnostic => $"{diagnostic.Span} {diagnostic.Severity} {diagnostic.Message}");

    /// <summary>
    /// Returns the symbols one file's names resolve to and the symbols it declares, each keyed by
    /// position.
    /// </summary>
    private static (Dictionary<int, Symbol> Resolved, Dictionary<int, Symbol> Declared) Names(Binder.Result bound)
    {
        var resolved = new Dictionary<int, Symbol>();
        var declared = new Dictionary<int, Symbol>();
        foreach (var reference in bound.References)
            (reference.IsDeclaration ? declared : resolved)[reference.Span.Start] = reference.Symbol;
        return (resolved, declared);
    }

    /// <summary>
    /// Reads each signature again with the signature sets it names and the values of its items,
    /// now that the names and the constants are known, and reports its problems. Also reports
    /// the problems of each signature set, once, where the set is declared.
    /// </summary>
    private static void Value(
        IEnumerable<Symbol> symbols,
        SegmentTable segments,
        SymbolMap resolved,
        Configuration configuration,
        Dictionary<string, List<Diagnostic>> byFile)
    {
        var names = new BoundNames(resolved);
        Symbol? SetOf(NameExpressionSyntax name) => names.SymbolOf(name);
        foreach (var symbol in symbols)
        {
            // An instance of a family reads its signature with the binding's value for that
            // instance, so `dbr = Bank::b` gives that instance's bank.
            var bound = symbol.Bound is { } held
                ? new Dictionary<Symbol, Expansion.Bound> { [held.Binding] = held.Value }
                : null;
            long? ValueOf(ExpressionSyntax expression) =>
                Evaluator.ValueOf(expression, segments, resolved, bound, configuration: configuration).AsNumber();
            void Report(TextSpan span, DiagnosticMessage message) =>
                byFile[symbol.Tree.Path].Add(new Diagnostic(symbol.Tree.GetSpan(span), Severity.Error, message));
            if (symbol.Kind == SymbolKind.SignatureSet)
                Signature.CheckSet(symbol, ValueOf, SetOf, Report);
            symbol.Signature = symbol.Signature?.Resolved(ValueOf, SetOf, Report);
            symbol.MacroSignature = symbol.MacroSignature?.Resolved(ValueOf, SetOf, Report);
            if (symbol.Kind == SymbolKind.Macro)
            {
                ArgumentChecks.CheckHeader(symbol, ValueOf, SetOf, Report);
                ArgumentChecks.CheckComparisons(symbol, SetOf, (span, message) =>
                    byFile[symbol.Tree.Path].Add(new Diagnostic(symbol.Tree.GetSpan(span), message)));
            }

            // A routine is imported as far when its signature says so, either directly or
            // through a signature set it names.
            if (symbol is { Kind: SymbolKind.ImportedAddress, Signature.IsFar: true })
                symbol.AddressSize = AddressSize.Far;
        }
    }

    /// <summary>
    /// Reports a diagnostic for each extern proc that names a routine but declares a signature
    /// that differs from the routine's. Such an alias, as in <c>.proc r_long = r: far</c>, is
    /// another name for that routine, and every call through it is checked against what it
    /// declares. Without this check, near code could be reached with <c>jsl</c>, or a caller
    /// held to widths the routine never asked for, with no diagnostic. An alias that declares no
    /// signature takes the routine's.
    /// </summary>
    private static void CheckAliases(
        IEnumerable<Symbol> symbols, SymbolMap resolved, Dictionary<string, List<Diagnostic>> byFile)
    {
        var names = new BoundNames(resolved);
        foreach (var alias in symbols)
        {
            if (alias is not { Kind: SymbolKind.ExternProc, Signature: { } declared, ValueExpression: { } value }
                || names.SymbolOf(value) is not { Signature: { } actual } routine
                || routine.Kind == SymbolKind.ExternProc && routine == alias)
            {
                continue;
            }
            if (value.Parent is not ExternProcDeclarationSyntax { Signature: not null })
            {
                alias.Signature = actual;
                continue;
            }
            DiagnosticMessage? mismatch = declared.IsFar != actual.IsFar
                ? Catalogue.AliasDistanceMismatch.Message(
                    alias.Name, declared.Distance, routine.DisplayName, actual.Distance)
                : declared != actual
                    ? Catalogue.AliasSignatureMismatch.Message(alias.Name, declared, routine.DisplayName, actual)
                    : (DiagnosticMessage?)null;
            if (mismatch is { } problem)
                byFile[alias.Tree.Path].Add(new Diagnostic(alias.Tree.GetSpan(value.Span), problem));
        }
    }

    /// <summary>
    /// Returns a value indicating whether <paramref name="read"/>, a symbol of a file that is not
    /// read again, could be on a cycle with the files that are. That is so when something it is
    /// evaluated from reaches one of their symbols that is itself evaluated, directly or
    /// indirectly, from it. Its value was computed before, so a cycle that an edit closed through
    /// it would never show in that value.
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

    /// <summary>
    /// Returns a value indicating whether evaluating <paramref name="from"/> can reach
    /// <paramref name="to"/>, through any file.
    /// </summary>
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
    /// Returns the symbols that evaluating <paramref name="symbol"/> reads directly. These are
    /// every name in anything it is evaluated from, its type, the enum member before it, the
    /// symbol that owns its scope and the members of its body.
    /// </summary>
    private static IEnumerable<Symbol> Named(Symbol symbol, SymbolMap resolved)
    {
        var declaredNodes = new SyntaxNode?[] { symbol.ValueExpression, symbol.Data, symbol.TypeExpression }
            .Concat(symbol.Items)
            .Concat(symbol.Entries)
            .OfType<SyntaxNode>();
        foreach (var node in declaredNodes.SelectMany(node => node.DescendantNodes().Prepend(node)))
        {
            foreach (var token in node.ChildTokens)
            {
                // A missing token starts where the token after it does, so looking up a missing
                // token would find the name at that position.
                if (!token.IsMissing && resolved.TryGetValue((node.Tree, token.Span.Start), out var named))
                    yield return named;
            }
        }
        var linked = new[] { symbol.Type, symbol.PreviousMember, symbol.Scope.Owner }
            .Concat(symbol.Body?.Symbols ?? [])
            .OfType<Symbol>();
        foreach (var other in linked)
            yield return resolved.Current(other);
    }

    /// <summary>
    /// Reports a diagnostic, on the 65816, for each routine with no body whose signature declares
    /// no state. Every call through such a routine is checked against its declaration, and there
    /// is no body to check the declaration itself. An extern proc at a constant address and an
    /// imported routine must therefore declare their state rather than take a default that is only
    /// a guess. An extern proc that names another routine and declares nothing takes that
    /// routine's signature, which counts as a declaration too.
    /// </summary>
    private static void CheckDeclaredSignatures(
        IEnumerable<Symbol> symbols, Dictionary<string, List<Diagnostic>> byFile, Cpu cpu)
    {
        if (cpu != Cpu.Wdc65816)
            return;
        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not (SymbolKind.ExternProc or SymbolKind.ImportedAddress)
                || symbol.Signature is not { DeclaresState: false })
            {
                continue;
            }
            var kind = symbol.Kind == SymbolKind.ExternProc ? "an extern proc" : "an imported routine";
            byFile[symbol.Tree.Path].Add(new Diagnostic(symbol.DeclarationSpan,
                Catalogue.SignatureMissing.Message(symbol.Name, kind)));
        }
    }

    /// <summary>
    /// Reports a diagnostic for each export given an address size narrower than its own. An
    /// export may be given a wider address size, as in <c>.export K: abs</c>, so that importers
    /// are sized for what it may later become. A narrower size would tell the linker, and every
    /// other module, something that is not true.
    /// </summary>
    private static void CheckExportSizes(IEnumerable<Symbol> symbols, Dictionary<string, List<Diagnostic>> byFile)
    {
        foreach (var symbol in symbols)
        {
            if (symbol is not { ExportSize: { } given, ExportSpan: { } at })
                continue;
            var actual = symbol.IsAddress ? symbol.AddressSize : symbol.Value.ImpliedAddressSize();
            if (actual is { } size && given < size)
            {
                byFile[symbol.Tree.Path].Add(new Diagnostic(symbol.Tree.GetSpan(at),
                    Catalogue.ExportNarrowsAddressSize.Message(symbol.Name, Format(size), symbol.Name, Format(size)))
                {
                    Fix = new DiagnosticFix(FixKind.ExportSize, Format(size)),
                });
            }
        }
    }

    /// <summary>Formats an address size as an export or an import gives it.</summary>
    private static string Format(AddressSize size) => size switch
    {
        AddressSize.ZeroPage => "zp",
        AddressSize.Absolute => "abs",
        _ => "far",
    };

    /// <summary>Represents what reading a set of files found.</summary>
    /// <param name="Symbols">The table of what each file may name in the others.</param>
    /// <param name="Bound">What each file resolved, in the order the files were read.</param>
    /// <param name="ByFile">The diagnostics each file's own analysis found, by file.</param>
    /// <param name="Tables">The diagnostics that only the whole program can report.</param>
    /// <param name="Forwarding">The forwarding from earlier versions of the files to the current ones.</param>
    /// <param name="Resolved">The symbols the program's names resolve to.</param>
    /// <param name="Declared">The symbols the program's names declare.</param>
    /// <param name="Reads">The symbols of files not read whose values were read as they stand.</param>
    private sealed record Reading(
        ProgramSymbols Symbols,
        IReadOnlyList<Binder.Result> Bound,
        Dictionary<string, List<Diagnostic>> ByFile,
        List<Diagnostic> Tables,
        Forwarding Forwarding,
        SymbolMap Resolved,
        SymbolMap Declared,
        IReadOnlySet<Symbol> Reads);
}
