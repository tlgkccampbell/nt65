using Norristown.Project;
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
    private readonly Cpu cpu;

    // The names each file looked for in the others, found or not.
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> lookedUp;

    // What each file's own analysis found — binding, evaluating its symbols, checking its
    // macros and signatures — by file, and what only the whole program can say.
    private readonly IReadOnlyDictionary<string, IReadOnlyList<Diagnostic>> byFile;
    private readonly IReadOnlyList<Diagnostic> tables;
    private readonly IReadOnlyList<Diagnostic> segmentValues;

    private ProgramModel(
        IReadOnlyList<SemanticModel> files, SegmentTable segments, ProgramSymbols symbols, Cpu cpu,
        IReadOnlyList<ProgramSymbols.Module> modules, SymbolMap resolved, SymbolMap declared, Forwarding forwarding,
        IReadOnlyDictionary<string, IReadOnlySet<string>> lookedUp,
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
    /// declares is a define, visible everywhere. <paramref name="cpu"/> is what the program
    /// is built for, which decides the mnemonics no name may take; without one, the files'
    /// <c>.cpu</c> items say.
    /// </summary>
    public static ProgramModel Create(
        IReadOnlyList<SyntaxTree> trees,
        SegmentTable segments,
        Configuration? configuration = null,
        SyntaxTree? defines = null,
        Func<string, long?>? binaryLength = null,
        Cpu? cpu = null)
    {
        configuration ??= Configuration.Everything;
        var target = cpu ?? ProgramCpu.Resolve(trees, null, []);
        // What is wrong with the program rather than with one file: two files that are one
        // module, two exports under one linker name, a file shadowing a define.
        var tables = new List<Diagnostic>();
        var binders = trees.Select(tree => Binder.Collect(tree, segments, configuration, target, tree == defines)).ToList();

        // A define is visible in every file by being one, as if every file brought it in.
        var modules = binders.Select(binder => binder.Module).ToList();
        var defined = binders.FirstOrDefault(binder => binder.Tree == defines)?.FileScope.Symbols ?? [];
        foreach (var symbol in defined)
            symbol.IsDefine = true;

        // A family declares one name per member of the enum it walks, and the enum may be
        // another module's, so the instances are declared once every file has been read and
        // before the modules' exports are: what a file exports includes them.
        if (binders.Any(binder => binder.HasFamilies))
        {
            var provisional = ProgramSymbols.Build(modules, defined, []);
            foreach (var binder in binders)
                binder.DeclareFamilies(provisional);
        }
        foreach (var binder in binders)
            binder.Export();

        var symbols = ProgramSymbols.Build(modules, defined, tables);
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
        Macros.CheckExportedUses(declaredMacros, symbol => symbol.IsExported, macros);
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
            segments, [.. bound.SelectMany(result => result.Symbols)], resolved, evaluation, owners, null, binaryLength,
            configuration);
        for (var i = 0; i < evaluation.Count; i++)
            byFile[owners[i]].Add(evaluation[i]);

        // A segment's `dp` and `bank`, and a signature's `dp = e` and `dbr = e`, are expressions
        // that nothing before the analysis reads, so they are worked out once the constants are.
        var segmentValues = new List<Diagnostic>();
        segments.Evaluate(expression => Evaluator.ValueOf(expression, segments, resolved).AsNumber(), segmentValues);
        foreach (var result in bound)
            Value(result.Symbols, segments, resolved, byFile);
        foreach (var result in bound)
        {
            CheckAliases(result.Symbols, resolved, byFile);
            CheckExportSizes(result.Symbols, byFile);
        }

        // After the aliases, because one that writes nothing has taken the routine's by now.
        foreach (var result in bound)
            CheckDeclaredSignatures(result.Symbols, byFile, target);
        CheckDefineNames(modules, defines, tables);

        var all = byFile.Values.SelectMany(file => file).Concat(tables).Concat(segmentValues).ToList();
        var files = new List<SemanticModel>();
        for (var i = 0; i < trees.Count; i++)
        {
            var path = trees[i].Path;
            files.Add(new SemanticModel(trees[i], segments, configuration, symbols, bound[i], resolved, declared,
                Expanded(binders[i], symbol => symbol), all.Where(d => d.Span.File == path), binaryLength));
        }
        var lookedUp = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var binder in binders)
            lookedUp.TryAdd(binder.Tree.Path, binder.LookedUp);
        return new ProgramModel(
            files, segments, symbols, target, modules, resolved, declared, Forwarding.None, lookedUp,
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
    /// Every place <paramref name="symbol"/> is written, in every file of the program, its
    /// declaration included, in file and source order. A file kept from before an edit
    /// elsewhere names what the edited file declared then, so what each reference stands for
    /// is compared as what it stands for now.
    /// </summary>
    public IReadOnlyList<(SemanticModel File, SymbolReference Reference)> ReferencesTo(Symbol symbol) =>
        ReferencesTo([symbol]);

    /// <summary>
    /// The same for several symbols at once, which is what one name declared under several of
    /// them needs: an enum member and the instances of a family named after it.
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

        var binders = dirty.ToDictionary(
            path => path, path => Binder.Collect(trees[path], Segments, configuration, cpu, trees[path] == defines), StringComparer.Ordinal);
        List<ProgramSymbols.Module> replaced = [.. modules.Select(module =>
            binders.TryGetValue(module.Tree.Path, out var binder) ? binder.Module : module)];

        // A family declares one name per member of the enum it walks, which may be another
        // module's, so the instances are declared before what each module exports is read.
        if (binders.Values.Any(binder => binder.HasFamilies))
        {
            var provisional = ProgramSymbols.Build(
                replaced, replaced.FirstOrDefault(module => module.Tree == defines)?.FileScope.Symbols ?? [], []);
            foreach (var binder in binders.Values)
                binder.DeclareFamilies(provisional);
        }
        foreach (var binder in binders.Values)
            binder.Export();

        var tables = new List<Diagnostic>();
        var symbols = ProgramSymbols.Build(
            replaced, replaced.FirstOrDefault(module => module.Tree == defines)?.FileScope.Symbols ?? [], tables);
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
            symbol => !dirty.Contains(symbol.Tree.Path), binaryLength, configuration);
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
        Macros.CheckExportedUses(declaredMacros, symbol => symbol.IsExported, macros);
        foreach (var diagnostic in macros)
            found[diagnostic.Span.File].Add(diagnostic);
        foreach (var result in bound.Values)
            Value(result.Symbols, Segments, resolved, found);
        foreach (var result in bound.Values)
        {
            CheckAliases(result.Symbols, resolved, found);
            CheckExportSizes(result.Symbols, found);
        }

        // After the aliases, because one that writes nothing has taken the routine's by now.
        foreach (var result in bound.Values)
            CheckDeclaredSignatures(result.Symbols, found, cpu);
        CheckDefineNames(replaced, defines, tables);

        // A name whose meaning changed is news to every file that looked it up in its module. A
        // macro, a function or a list that names it in its body has changed too, and is news in
        // turn to every file that looked that up. A module that changed its name or what it
        // re-exports changed what paths mean, and every file is read again.
        foreach (var path in dirty)
        {
            var module = modules.First(module => module.Tree.Path == path);
            var before = FileInterface.Of(module, byPath[path].Symbols, this.resolved);
            var after = FileInterface.Of(binders[path].Module, bound[path].Symbols, resolved);
            HashSet<string> changed = [.. FileInterface.Changed(before, after)];

            // Two declarations under one name leave a file that names it with no telling which.
            if (!Forwarding.HasDistinctNames(byPath[path].Symbols) || !Forwarding.HasDistinctNames(bound[path].Symbols))
                changed.UnionWith(before.Keys.Concat(after.Keys));
            if (changed.Count == 0)
                continue;
            var everything = changed.Any(FileInterface.IsModuleWide);
            var heads = changed.Select(name => name.Split("::")[0])
                .SelectMany(head => new[] { $"member:{module.Name}::{head}", "name:" + head })
                .ToHashSet(StringComparer.Ordinal);
            foreach (var other in others)
            {
                if (other.Tree == defines)
                    continue;
                if (everything || lookedUp.GetValueOrDefault(other.Tree.Path) is { } looked && looked.Overlaps(heads))
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
                ? new SemanticModel(trees[file.Tree.Path], Segments, configuration, symbols, bound[file.Tree.Path], resolved,
                    declared, Expanded(binders[file.Tree.Path], forwarding.Current), all.Where(d => d.Span.File == file.Tree.Path), binaryLength)
                : file)
            .ToList();
        var lookups = new Dictionary<string, IReadOnlySet<string>>(lookedUp, StringComparer.Ordinal);
        foreach (var (path, binder) in binders)
            lookups[path] = binder.LookedUp;
        return new ProgramModel(
            files, Segments, symbols, cpu, replaced, resolved, declared, forwarding, lookups, byFile, tables, segmentValuesNow);
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

    /// <summary>
    /// Reads each signature again with the signature sets it names and the values its items
    /// write, now that the names and the constants are known, and reports what is wrong with
    /// it; and what is wrong with each signature set, once, where it is declared.
    /// </summary>
    private static void Value(
        IEnumerable<Symbol> symbols, SegmentTable segments, SymbolMap resolved, Dictionary<string, List<Diagnostic>> byFile)
    {
        Symbol? SetOf(NameExpressionSyntax name) => Evaluator.SymbolNamed(name, resolved);
        foreach (var symbol in symbols)
        {
            // An instance of a family reads its signature with what the binding is worth there,
            // so `dbr = Bank::b` is that bank on each of them.
            var bound = symbol.Bound is { } held
                ? new Dictionary<Symbol, Expansion.Bound> { [held.Binding] = held.Value }
                : null;
            long? ValueOf(ExpressionSyntax expression) => Evaluator.ValueOf(expression, segments, resolved, bound).AsNumber();
            void Report(TextSpan span, DiagnosticMessage message) =>
                byFile[symbol.Tree.Path].Add(new Diagnostic(symbol.Tree.GetSpan(span), Severity.Error, message));
            if (symbol.Kind == SymbolKind.SignatureSet)
                Signature.CheckSet(symbol, ValueOf, SetOf, Report);
            symbol.Signature = symbol.Signature?.Resolved(ValueOf, SetOf, Report);
            symbol.MacroSignature = symbol.MacroSignature?.Resolved(ValueOf, SetOf, Report);

            // A routine is imported as far when its signature says so, which a set it names may.
            if (symbol is { Kind: SymbolKind.ImportedAddress, Signature.IsFar: true })
                symbol.AddressSize = AddressSize.Far;
        }
    }

    /// <summary>
    /// An extern proc that names a routine, <c>.proc r_long = r: far</c>, is another name for
    /// that routine, and what it declares is what every call through it is checked against. So
    /// it has to declare what the routine does: otherwise near code is reached with <c>jsl</c>,
    /// or a caller is held to widths the routine never asked for, with nothing said. One that
    /// declares nothing takes the routine's signature.
    /// </summary>
    private static void CheckAliases(
        IEnumerable<Symbol> symbols, SymbolMap resolved, Dictionary<string, List<Diagnostic>> byFile)
    {
        foreach (var alias in symbols)
        {
            if (alias is not { Kind: SymbolKind.ExternProc, Signature: { } declared, ValueExpression: { } value }
                || Evaluator.SymbolNamed(value, resolved) is not { Signature: { } actual } routine
                || routine.Kind == SymbolKind.ExternProc && routine == alias)
            {
                continue;
            }
            if (value.Parent is not ExternProcDeclarationSyntax { Signature: not null })
            {
                alias.Signature = actual;
                continue;
            }
            DiagnosticMessage? said = declared.IsFar != actual.IsFar
                ? Catalogue.AliasDistanceMismatch.Says(
                    alias.Name, declared.Distance, routine.DisplayName, actual.Distance)
                : declared.Entry != actual.Entry || declared.Exit != actual.Exit || declared.Inline != actual.Inline
                    || declared.IsInterrupt != actual.IsInterrupt || declared.NeverReturns != actual.NeverReturns
                    || declared.Arguments != actual.Arguments
                    ? Catalogue.AliasSignatureMismatch.Says(alias.Name, declared, routine.DisplayName, actual)
                    : (DiagnosticMessage?)null;
            if (said is { } problem)
                byFile[alias.Tree.Path].Add(new Diagnostic(alias.Tree.GetSpan(value.Span), problem));
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
        var written = new SyntaxNode?[] { symbol.ValueExpression, symbol.Data, symbol.TypeExpression }
            .Concat(symbol.Items)
            .Concat(symbol.Entries)
            .OfType<SyntaxNode>();
        foreach (var node in written.SelectMany(node => node.DescendantNodes().Prepend(node)))
        {
            foreach (var token in node.ChildTokens)
            {
                // A missing token starts where the token after it does, so looking one up would
                // find whatever is named there.
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
    /// An export may be given a wider address size than its own, <c>.export K: abs</c>, so that
    /// what imports it is sized to what it may later become; a narrower one would tell the
    /// linker, and every other module, something that is not so.
    /// </summary>
    /// <summary>
    /// On the 65816 a routine with no body declares what every call through it is checked
    /// against, and has no body to check that declaration itself. So it has to say something: an
    /// extern proc at a constant address and an imported routine write their state rather than
    /// take a default that is only a guess. An extern proc that names another routine and writes
    /// nothing takes that routine's, which is a declaration too.
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
                Catalogue.SignatureMissing.Says(symbol.Name, kind)));
        }
    }

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
                    Catalogue.ExportNarrowsAddressSize.Says(symbol.Name, Spell(size), symbol.Name, Spell(size)))
                {
                    Fix = new DiagnosticFix(FixKind.ExportSize, Spell(size)),
                });
            }
        }
    }

    /// <summary>An address size as an export or an import writes it.</summary>
    private static string Spell(AddressSize size) => size switch
    {
        AddressSize.ZeroPage => "zp",
        AddressSize.Absolute => "abs",
        _ => "far",
    };

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
                    diagnostics.Add(new Diagnostic(symbol.DeclarationSpan,
                        Catalogue.DefineRedeclared.Says(symbol.Name)));
                }
            }
        }
    }
}
