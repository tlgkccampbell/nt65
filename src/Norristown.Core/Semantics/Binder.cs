using System.Collections.Immutable;
using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Builds one file's scopes and declarations and resolves the names it uses.
/// <para>
/// Declarations are collected first and resolved afterwards, so a name may be used before
/// the line that declares it.
/// </para>
/// <para>
/// A macro body is read once, however many times it is expanded. It sees the scope the macro
/// is declared in, and its declarations belong to a scope of its own that nothing outside can
/// reach, so an expansion never declares a name in its caller. The block argument of a call
/// works the other way: it is the caller's own code, and its names resolve there.
/// </para>
/// <para>
/// A file is a module, and a name that another module declares is reached only by its path,
/// such as <c>hw::init</c>, or by bringing it in with <c>.use</c>. A name is looked for in the
/// scopes around it, then among the names the file's <c>.use</c> items bring in, then among the
/// defines, then as the start of a module's path, and last among what a
/// <c>.use module::*</c> brings in. This order ensures that a module adding an export can never
/// change what a name in another module already means.
/// </para>
/// <para>
/// An <c>.if</c> opens no scope. Its conditions were evaluated before binding runs, so a branch
/// the build takes is read as if the <c>.if</c> were not there, and a branch it leaves out is
/// not read at all. This lets the same name be declared under two branches.
/// </para>
/// <para>
/// A <c>.repeat</c> or an <c>.each</c> body is read once, however many times it is emitted,
/// with the name the repetition binds in a scope of its own. What it declares is local to each
/// iteration, as what a macro body declares is local to each <see cref="Expansion"/>.
/// </para>
/// <para>
/// This file performs the walk, which finds what each line declares and where.
/// <c>Binder.Resolution.cs</c> resolves what the collected names refer to, and runs after the
/// walk.
/// </para>
/// </summary>
internal sealed partial class Binder
{
    // The dispatcher that binds a statement, with one method per kind of statement.
    private readonly Statements statements;
    private readonly SyntaxTree tree;
    private readonly SegmentTable segments;
    private readonly Configuration configuration;
    private readonly Cpu cpu;
    private readonly bool isDefines;

    // Where the binder reports its diagnostics. Resolving a name whose diagnostics are dropped
    // points this at a list of its own for as long as that takes.
    private List<Diagnostic> diagnostics = [];

    // The callback a lookup reports its diagnostics through; a lookup that must not report is
    // passed null instead.
    private readonly Action<TextSpan, DiagnosticMessage> report;
    private readonly List<Symbol> symbols = [];
    private readonly List<SymbolReference> references = [];
    private readonly List<Use> uses = [];

    // Where a condition asks `.defined` about a name, which may only be a define.
    private readonly List<(SyntaxToken Name, Scope Scope)> definedAsked = [];
    private readonly List<Invocation> calls = [];
    private readonly List<Symbol> called = [];
    private readonly HashSet<Symbol> resolving = [];
    private readonly HashSet<Symbol> unexported = [];

    // What the file exports. This includes the declarations that follow `.export`, the items
    // of its `.export` lists with the scope each appears in, and everything that exporting
    // those spreads to.
    private readonly List<(Symbol Symbol, TextSpan At)> exportedDeclarations = [];

    // The blocks that open a scope, with the scope each opens, in the order they are opened.
    private readonly List<(TextSpan Span, Scope Scope)> regions = [];
    private readonly List<(ExportItemSyntax Item, Scope Scope)> exportItems = [];
    private readonly List<Symbol> exported = [];

    // The record initializers of `.type T` data declarations, with the `T` each belongs to.
    // The member names they contain become references to `T`'s members once `T` is resolved.
    private readonly List<(NameExpressionSyntax Type, IReadOnlyList<SyntaxNode> Values)> records = [];

    // The file's `.use` items, what they bring in once resolved, and what it re-exports.
    private readonly List<UseDirectiveSyntax> useDirectives = [];
    private readonly Dictionary<string, Resolution> used = new(StringComparer.Ordinal);

    // Where each `.use` gives the name it brings in, so that an item nothing uses can be
    // reported on the item rather than on the whole line.
    private readonly Dictionary<string, (TextSpan At, bool Exported)> broughtAt = new(StringComparer.Ordinal);
    private readonly List<ProgramSymbols.Module> globs = [];
    private readonly List<ProgramSymbols.Reexport> reexports = [];

    // Every name looked for in the other files, found or not: a file that declares or stops
    // declaring one of them, or changes what it means, changes what this file means.
    private readonly HashSet<LookedUpName> lookedUp = [];
    private readonly Scope fileScope;
    private ProgramSymbols program = ProgramSymbols.Empty;
    private Scope scope;
    // The segment the walk is putting declarations in, or null before any region or block
    // names one.
    private string? segment;
    private string? moduleName;
    private TextSpan moduleNameSpan;

    // Whether the walk has passed an item, which `.module` has to come before.
    private bool pastFirstItem;
    private Symbol? previousEnumMember;

    // A label on a line of its own, followed so far by nothing but blank lines. A `.state`
    // here is that label's declaration.
    private Symbol? bareLabel;

    // The run of misplaced instructions the walk is in the middle of, reported when it ends,
    // and whether the line being walked is one of them.
    private CodeRun? codeRun;
    private bool inCodeRun;

    // The declarations named by a repetition's binding, waiting for the enum walked to be
    // known before each becomes one declaration per member, and what they became.
    private readonly List<PendingFamily> pendingFamilies = [];
    private readonly List<Family> families = [];

    // The repetition whose body the walk is directly in, where a declaration named after the
    // repetition's binding is a family (one declaration per member); null everywhere else,
    // including inside a nested block of the body.
    private Repeated? repeated;

    private Binder(
        SyntaxTree tree, SegmentTable segments, Configuration configuration, Cpu cpu, bool isDefines, Scope fileScope)
    {
        statements = new Statements(this);
        this.tree = tree;
        this.segments = segments;
        this.configuration = configuration;
        this.cpu = cpu;
        this.isDefines = isDefines;
        this.fileScope = fileScope;
        scope = fileScope;
        report = (span, message) => Report(span, message);
    }

    /// <summary>Gets the file being bound.</summary>
    public SyntaxTree Tree => tree;

    /// <summary>Gets the file's top-level scope, which is what another file can reach into.</summary>
    public Scope FileScope => fileScope;

    /// <summary>
    /// Gets what resolving this file looked for in other modules, whether it was found or not.
    /// Each name looked for in a module is recorded with that module's name. Each name that no
    /// module declared is recorded with a null module, because another module starting to export
    /// it would change what this file reports about it.
    /// </summary>
    public IReadOnlySet<LookedUpName> LookedUp => lookedUp;

    /// <summary>
    /// Gets the symbols this file named in another module that the module does not export, each
    /// of which has been reported as <c>not-exported</c>. They still count as used, so the file
    /// that declares one does not also report that nothing uses it.
    /// </summary>
    public IReadOnlySet<Symbol> Unexported => unexported;

    /// <summary>
    /// Gets the file as the program sees it, with its module's name, its top level and what it
    /// exports.
    /// </summary>
    public ProgramSymbols.Module Module => new(tree, moduleName, moduleNameSpan, fileScope, exported, reexports);

    /// <summary>
    /// Reads the declarations of <paramref name="tree"/>, leaving the names it uses to be
    /// resolved once every file of the program has been read. What the file exports is known
    /// from here on, because a file exports only what it declares. <paramref name="isDefines"/>
    /// indicates that the file holds the build configuration's defines, which is not a module.
    /// </summary>
    public static Binder Collect(
        SyntaxTree tree, SegmentTable segments, Configuration configuration, Cpu cpu, bool isDefines = false)
    {
        var binder = new Binder(tree, segments, configuration, cpu, isDefines, new Scope(ScopeKind.File, null, null, null));
        binder.WalkContainer(tree.Root);
        binder.EndCodeRun();

        // Everything the build configuration declares at its top level is a define. It is marked
        // here, with the rest of what the file declares, so that a program built again after an
        // edit elsewhere never has to change a define it kept.
        if (isDefines)
        {
            foreach (var symbol in binder.fileScope.Symbols)
                symbol.IsDefine = true;
        }
        return binder;
    }

    /// <summary>
    /// Gets a value indicating whether the file holds any declaration named by a repetition's
    /// binding.
    /// </summary>
    public bool HasFamilies => pendingFamilies.Count > 0;

    /// <summary>
    /// Resolves the names the file uses, looking in <paramref name="program"/> for the ones it
    /// does not declare.
    /// </summary>
    public Result Resolve(ProgramSymbols program)
    {
        this.program = program;
        foreach (var directive in useDirectives)
            ResolveUse(directive);
        ResolveUses(uses);

        // Conditions are evaluated before the program is read, so `.defined` of a name the
        // program declares would be false regardless of the program.
        foreach (var (name, at) in definedAsked)
        {
            if (at.Lookup(name.Text) is { IsDefine: false, Kind: not (SymbolKind.MacroParameter or SymbolKind.Binding) })
            {
                Report(name.Span, Catalogue.DefinedAsksAboutDefines.Message(name.Text));
            }
        }

        // A module exports what it declares. A name brought in from another module belongs to
        // that module, and making it part of this one is a re-export, which states where it came
        // from.
        foreach (var (item, _) in exportItems)
        {
            if (item.Name is not { LastPart: { } innermost } name)
                continue;
            var last = innermost.Name;
            var reference = references.LastOrDefault(found => found.Span.Start == last.Span.Start && !found.IsDeclaration);
            if (reference?.Symbol is { } foreign && foreign.Tree != tree)
            {
                Report(name.Span, Catalogue.ReexportNeeded.Message(foreign.Name, foreign.Module, foreign.PathName));
            }
        }

        // A call's arguments are resolved after everything else, because whether a name in
        // one is a name at all depends on the parameter it binds to. A `one` argument is a
        // word, and words are never looked up.
        ResolveCalls();
        ResolveRecords();
        references.Sort((a, b) => a.Span.Start.CompareTo(b.Span.Start));
        return new Result(fileScope, symbols, references, diagnostics, regions)
        {
            Families = families,
            Brought = used.ToDictionary(
                pair => pair.Key,
                pair => new BroughtName(pair.Value.Symbol, pair.Value.Module,
                    broughtAt.TryGetValue(pair.Key, out var at) ? at.At : default,
                    broughtAt.TryGetValue(pair.Key, out var how) && how.Exported),
                StringComparer.Ordinal),
            Used = used,
            Globs = globs,
        };
    }

    /// <summary>Returns the macros this file declares, which the recursion check reads.</summary>
    public IEnumerable<Symbol> DeclaredMacros() => symbols.Where(symbol => symbol.Kind == SymbolKind.Macro);

    /// <summary>
    /// Returns the macros this file calls directly, rather than from inside another macro's body.
    /// Their bodies are expanded into this file, so the names they use are ones this file has
    /// to bring in.
    /// </summary>
    public IReadOnlyList<Symbol> CalledMacros() => called;

    /// <summary>Determines whether <paramref name="node"/> is a call of <c>.defined</c>.</summary>
    private static bool IsDefinedCall(SyntaxNode node) =>
        node is CallExpressionSyntax { BuiltinKind: BuiltinKind.Defined };

    /// <summary>Returns the first token of a statement that could be a declared name.</summary>
    private static SyntaxToken? NameToken(StatementSyntax statement)
    {
        foreach (var token in statement.ChildTokens)
        {
            // A missing token names nothing, and it starts where the token after it does, so
            // taking it would name whatever follows.
            if (!token.IsMissing && token.Kind is SyntaxKind.Identifier or SyntaxKind.CheapLocal
                or SyntaxKind.Register or SyntaxKind.Mnemonic)
            {
                return token;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the signature a statement gives after its name, or null when it gives none or
    /// takes none.
    /// </summary>
    private static ProcSignatureSyntax? SignatureOf(StatementSyntax declaration) => declaration switch
    {
        ProcDeclarationSyntax proc => proc.Signature,
        ExternProcDeclarationSyntax externProc => externProc.Signature,
        MultiProcDeclarationSyntax multiProc => multiProc.Signature,
        MacroDeclarationSyntax macro => macro.Signature,
        _ => null,
    };

    private void WalkContainer(FileSyntax file)
    {
        foreach (var member in file.Members)
        {
            if (member is BlockSyntax block)
            {
                bareLabel = null;
                EndCodeRun();
                WalkBlock(block);
                pastFirstItem = true;
            }
            else if (member is LineSyntax line)
            {
                WalkLine(line);
            }
        }
    }

    /// <summary>
    /// Walks a block, binding what its opener declares and then its contents in the scope and
    /// segment the opener sets. A segment block changes the segment of its contents, not their
    /// scope.
    /// </summary>
    private void WalkBlock(BlockSyntax block)
    {
        var kind = block.BlockKind;
        var lines = block.Members;
        var opener = block.Opener.Statement;
        var outerRepeated = repeated;
        var declaresInstances = NamedByBinding(opener, outerRepeated) is not null;
        if (!declaresInstances)
            CheckAllowedHere(opener);
        var outerScope = scope;
        var outerSegment = segment;

        // A family is declared directly in a repetition's body; what a nested block inside the
        // body declares stays private to each iteration, as usual.
        repeated = null;

        switch (kind)
        {
            case BlockKind.Macro:
                scope = OpenMacro(opener);
                if (scope.Owner is { Kind: SymbolKind.Macro } macro)
                    macro.Definition = block;
                break;

            // A block argument appears at the call and belongs to it. Its names resolve in the
            // caller, and the cheap locals it declares are private to it, because the same block
            // may be spliced in more than one place.
            case BlockKind.MacroBlock:
                if (opener is MacroCallSyntax or LabeledLineSyntax)
                    BindStatement(opener);
                else if (opener is BlockContinuationSyntax continuation)
                    CheckContinuation(block, continuation);
                scope = new Scope(ScopeKind.BlockArgument, null, scope, null);
                break;

            case BlockKind.Proc:
                scope = NamedByBinding(opener, outerRepeated) is { } familyProc
                    ? OpenFamilyRoutine(familyProc, opener)
                    : OpenScope(ScopeKind.Proc, opener, SymbolKind.Proc);
                break;
            case BlockKind.MultiProc:
                scope = OpenMultiProc(block, opener);
                break;
            case BlockKind.Scope:
                scope = NamedByBinding(opener, outerRepeated) is { } familyScope
                    ? RefuseScopeFamily(familyScope, opener, ScopeKind.Scope)
                    : OpenScope(ScopeKind.Scope, opener, SymbolKind.Scope);
                break;
            case BlockKind.Segment:
            case BlockKind.Region:
                segment = SegmentOf(opener) ?? segment;
                break;
            case BlockKind.Data:
                scope = NamedByBinding(opener, outerRepeated) is { } familyData
                    ? RefuseScopeFamily(familyData, opener, ScopeKind.Data)
                    : OpenData(opener, block);
                break;
            case BlockKind.Enum:
                scope = OpenType(opener, SymbolKind.Enum);
                previousEnumMember = null;
                break;
            case BlockKind.Struct:
                scope = OpenType(opener, SymbolKind.Struct);
                break;
            case BlockKind.Union:
                scope = OpenType(opener, SymbolKind.Union);
                break;
            case BlockKind.Charmap:
                DeclareCollected(opener, SymbolKind.Charmap, lines);
                return;
            case BlockKind.List:
                DeclareCollected(opener, SymbolKind.List, lines);
                return;
            case BlockKind.RecordInitializer:
                BindInitializer(opener, lines);
                return;
            case BlockKind.If:
                // Anything an included branch declares belongs to the scope around it. The
                // condition is read for its names so that an editor can follow a define to
                // the configuration that gives it a value. A `.defined` is recorded regardless
                // of its result, and a false result leaves the branch out.
                foreach (var call in opener.DescendantNodes().Where(IsDefinedCall))
                {
                    foreach (var argument in call.DescendantNodes().OfType<NameExpressionSyntax>())
                    {
                        if (argument.SimpleName is { Kind: SyntaxKind.Identifier } name)
                            definedAsked.Add((name, scope));
                    }
                }
                if (!configuration.Includes(block))
                    return;
                // A condition may compare a `one` parameter or a repetition binding with a
                // bare word, which is never looked up, so a name here that turns out not to be
                // a name is a word rather than a mistake.
                CollectUses(opener, uses, words: true);
                break;
            case BlockKind.Repeat:
            case BlockKind.Each:
                scope = OpenRepetition(opener);
                repeated = RepeatedIn(block, opener, outerScope, kind);
                break;
            default:
                BindStatement(opener);
                break;
        }

        if (scope != outerScope)
            regions.Add((block.Span, scope));
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i] is BlockSyntax inner)
            {
                EndCodeRun();
                WalkBlock(inner);
            }
            else if (lines[i] is LineSyntax line)
            {
                WalkLine(line);
            }
        }
        EndCodeRun();

        scope = outerScope;
        segment = outerSegment;
        repeated = outerRepeated;
        if (kind == BlockKind.Enum)
            previousEnumMember = null;
    }


    /// <summary>
    /// Returns what a repetition's body may declare by the name it binds, or null for a
    /// repetition that binds no name. The result holds the block, the enum or list it walks, and
    /// the scope the instances would go in. Its reason, when not null, explains why no family may
    /// be declared there, and is reported against any family declared there anyway.
    /// </summary>
    private Repeated? RepeatedIn(BlockSyntax block, StatementSyntax opener, Scope around, BlockKind kind)
    {
        if (scope.Symbols is not [{ Kind: SymbolKind.Binding } binding])
            return null;
        DiagnosticMessage? why = kind == BlockKind.Repeat
            ? Catalogue.FamilyMisplaced.Message("a family must be in an `.each` over a named enum, because each routine it "
                + "declares is named after one of the enum's members; a `.repeat` only counts, so it gives no names")
            : around.Kind == ScopeKind.Repetition
                ? Catalogue.FamilyMisplaced.Message("a family cannot be inside another `.repeat` or `.each`: it declares its "
                    + "routines into the scope around its `.each`, and inside a repetition that scope is a new one on every pass")
                : Placement is ScopeKind.File
                    ? (DiagnosticMessage?)null
                    : Catalogue.FamilyMisplaced.Message(
                        "a family declares one routine or data declaration per member into the scope around its `.each`, and this one is "
                        + $"inside {Article(Placement)}: " + (Placement is ScopeKind.Macro or ScopeKind.BlockArgument
                            ? "names declared there cannot reach the caller's scope"
                            : "a routine belongs at file level or in a `.scope`"));
        return new Repeated(block, (opener as RepetitionDirectiveSyntax)?.Expression, binding, around, segment, why);
    }

    /// <summary>Returns the phrase a message uses for a place where a declaration is not allowed.</summary>
    private static string Article(ScopeKind kind) => kind switch
    {
        ScopeKind.Proc => "a routine",
        ScopeKind.Macro => "a macro body",
        ScopeKind.BlockArgument => "a block argument",
        ScopeKind.Data => "a `.data` block",
        _ => "a type",
    };

    /// <summary>
    /// Returns the repetition around <paramref name="opener"/> when the declaration is named
    /// after the name the repetition binds, and so declares one declaration per member rather
    /// than one private to each iteration.
    /// </summary>
    private static Repeated? NamedByBinding(StatementSyntax opener, Repeated? repeated) =>
        repeated is { } found && NameToken(opener) is { } name && name.Text == found.Binding.Name
            ? found
            : null;

    /// <summary>
    /// Opens the body of a <c>.proc</c> named after a repetition's binding, which declares one
    /// routine per member once the enum is known. The body is read once, as every repetition body
    /// is.
    /// </summary>
    private Scope OpenFamilyRoutine(Repeated each, StatementSyntax opener)
    {
        CheckWidthsExist(opener);
        var signature = SignatureOf(opener);
        CollectUses(signature);
        var body = new Scope(ScopeKind.Proc, each.Binding.Name, scope, null);
        AddFamily(each, opener, body, SymbolKind.Proc, signature, null, null);
        return body;
    }

    /// <summary>
    /// Opens a <c>.multiproc E, b: signature { }</c>, which is an <c>.each</c> over <c>E</c> whose
    /// body is one routine, folded into one line. It opens the repetition's scope and the
    /// routine's scope inside it, exactly as the two blocks it abbreviates would.
    /// </summary>
    private Scope OpenMultiProc(BlockSyntax block, StatementSyntax opener)
    {
        if (opener is not MultiProcDeclarationSyntax multiProc)
        {
            BindStatement(opener);
            return new Scope(ScopeKind.Proc, null, scope, null);
        }

        // A routine in a macro body, a block argument or a repetition has already been
        // reported, by the same rules that apply to `.proc`.
        var around = scope;
        var placement = Placement;
        var why = placement is ScopeKind.Proc or ScopeKind.Data or ScopeKind.Type
            ? Catalogue.MultiprocMisplaced.Message(Article(placement))
            : (DiagnosticMessage?)null;
        if (why is { } misplaced)
            Report(multiProc.Keyword.Span, misplaced);

        // A `.multiproc` inside a macro body, a block argument or a repetition has been reported
        // by the rule that forbids `.proc` there. A misplaced one declares nothing.
        var declares = why is null && placement is ScopeKind.File && around.Kind != ScopeKind.Repetition;

        CheckWidthsExist(multiProc);
        var walked = multiProc.Expression;
        var signature = multiProc.Signature;

        // The enum is named outside the repetition and the signature inside it, because a
        // signature may name the binding. For example, `dbr = Bank::b` is that bank on each
        // instance.
        CollectUses(walked);
        var iterations = new Scope(ScopeKind.Repetition, null, around, null);
        var outer = scope;
        scope = iterations;
        var binding = Declare(multiProc.Name, SymbolKind.Binding);
        CollectUses(signature);
        var body = new Scope(ScopeKind.Proc, binding?.Name, iterations, null);
        if (binding is not null && declares)
            AddFamily(new Repeated(block, walked, binding, around, segment, null), multiProc, body, SymbolKind.Proc, signature, null, null);
        scope = outer;
        return body;
    }

    /// <summary>
    /// Reports a <c>.scope</c> or a <c>.data</c> block named after a repetition's binding, which
    /// is not allowed. Everything inside such a block would be declared once per member and
    /// reached through each instance. A family is for routines and data, so this reports the
    /// block with a message that suggests what to use instead.
    /// </summary>
    private Scope RefuseScopeFamily(Repeated each, StatementSyntax opener, ScopeKind kind)
    {
        var what = kind == ScopeKind.Scope ? "scope" : "`.data` block";
        Report(NameToken(opener)?.Span ?? opener.Span,
            Catalogue.FamilyDeclaresTooMuch.Message(each.Binding.Name, what, each.Binding.Name, each.Binding.Name));

        // The block still opens a scope of its own, so its contents have somewhere to be
        // declared and the one error does not lead to others.
        return new Scope(kind, null, scope, null);
    }

    /// <summary>Records a declaration named by a repetition's binding, to be declared once the enum is known.</summary>
    private void AddFamily(
        Repeated each, StatementSyntax declaration, Scope body, SymbolKind kind,
        ProcSignatureSyntax? signature, DataDirectiveSyntax? data, NameExpressionSyntax? type)
    {
        if (each.Why is { } refused)
        {
            if (declaration is not MultiProcDeclarationSyntax)
                Report(NameToken(declaration)?.Span ?? declaration.Span, refused);
            return;
        }
        pendingFamilies.Add(new PendingFamily(declaration, each, body, kind, signature, data, type));
    }


    /// <summary>
    /// Declares the instances of each family in the file, one declaration per member of the enum
    /// it walks, named after the member, in the scope around the repetition. This runs once every
    /// file has been collected, because the enum may belong to another module. It runs before the
    /// modules' exports are read, because the instances are among them.
    /// </summary>
    public void DeclareFamilies(ProgramSymbols provisional)
    {
        if (pendingFamilies.Count == 0)
            return;

        // The `.use` items are read here only to find an enum that one of them brought in. The
        // program they are read against is not complete yet, so a binder of their own reads
        // them, and what it reports and records is dropped with it. `Resolve` reads them again
        // against the complete program. What it looked for is kept, because the enum each
        // family walks depends on it.
        var brought = new Binder(tree, segments, configuration, cpu, isDefines, fileScope) { program = provisional };
        foreach (var directive in useDirectives)
            brought.ResolveUse(directive);

        foreach (var pending in pendingFamilies)
            DeclareFamily(pending, brought);
        pendingFamilies.Clear();
        lookedUp.UnionWith(brought.lookedUp);
    }

    /// <summary>
    /// Declares one family by finding the enum it walks and making its declaration for each
    /// member. The enum is looked for with what <paramref name="brought"/> read from the file's
    /// <c>.use</c> items.
    /// </summary>
    private void DeclareFamily(PendingFamily pending, Binder brought)
    {
        var at = NameToken(pending.Declaration)?.Span ?? pending.Declaration.Span;
        var walked = pending.Each.Walked;
        var walkedText = walked?.GetText().Trim();
        var found = walked is null ? null : brought.NamedByPath(walked, pending.Each.Around);
        if (found is not { Kind: SymbolKind.Enum, Body: { } members })
        {
            Report(walked?.Span ?? at, Catalogue.FamilyNotOverAnEnum.Message(
                walkedText, found is null ? "is not declared" : $"is {Named(found)}"));
            return;
        }

        var outerScope = scope;
        var outerSegment = segment;
        scope = pending.Each.Around;
        segment = pending.Each.Segment;
        var instances = new List<(Symbol Member, Symbol Instance)>();
        foreach (var member in members.Symbols.Where(symbol => symbol.IsEnumMember))
            instances.Add((member, DeclareInstance(pending, member, at)));
        scope = outerScope;
        segment = outerSegment;

        // The family's line contains the repetition's binding, and that is the name declared
        // there. Each instance is looked up by its own member name, but its declaration span is
        // that line, which is where go-to-definition lands. References may not overlap, so no
        // reference to an instance is recorded at that line.
        if (instances.Count > 0)
            pending.Body.Owner = instances[0].Instance;
        families.Add(new Family(pending.Declaration, pending.Each.Block, pending.Each.Binding, found, instances));
    }

    /// <summary>Declares one instance of a family under its member's name.</summary>
    private Symbol DeclareInstance(PendingFamily pending, Symbol member, TextSpan at)
    {
        var instance = new Symbol(member.Name, pending.Kind, scope, tree, at)
        {
            Segment = segment,
            Data = pending.Data,
            TypeExpression = pending.Type,
        };
        if (pending.Kind == SymbolKind.Proc)
            instance.Signature = Signature.Read(pending.Signature);

        // The signature may name the binding, as in `dbr = Bank::b`, and each instance's
        // signature is that expression with its own member's value.
        instance.Bound = (pending.Each.Binding, new Expansion.Bound(member.Value, null, Member: member));
        if (scope.Declare(instance) is { } existing)
        {
            Report(at, Catalogue.FamilyMemberCollides.Message(member.Name, pending.Each.Walked?.GetText().Trim()),
                new RelatedSpan(existing.DeclarationSpan, "declared here"));
        }
        symbols.Add(instance);
        if (pending.Declaration.ExportToken is { } export)
            exportedDeclarations.Add((instance, export.Span));
        return instance;
    }

    /// <summary>Returns how a message describes a symbol that is not the kind that was expected.</summary>
    private static string Named(Symbol symbol) =>
        symbol.Kind == SymbolKind.Enum ? "an anonymous enum, whose members are ordinary names" : $"a {symbol.KindText}";

    /// <summary>
    /// Returns the symbol a path names, resolved from <paramref name="at"/> without reporting
    /// anything. A family uses this to find the enum it walks, which has to be known before the
    /// file's names are resolved, because the declarations the family makes are among them. A
    /// <c>.type</c> uses it to find the type it names.
    /// <para>
    /// The path is walked as far as its first missing part, and the symbol reached before that
    /// part is returned.
    /// </para>
    /// </summary>
    private Symbol? NamedByPath(ExpressionSyntax? expression, Scope at)
    {
        if (expression is not NameExpressionSyntax name)
            return null;
        var parts = name.Parts;
        var present = parts.TakeWhile(part => part.Name is { IsMissing: false }).Select(part => part.Name!.Text).ToList();
        if (present.Count == 0)
            return null;
        var start = Lookup.First(present[0], parts.Count == 1, name.GlobalToken is not null, at, program, used, globs, Touch);
        return Lookup.Walk(start, present, program, BodyOf, Touch)?.Symbol;
    }

    /// <summary>
    /// Opens the scope of a <c>.proc</c> or <c>.scope</c>. A block whose opener is broken, such
    /// as by a missing name or a <c>.proc</c> after a label, still opens a scope. The cheap locals
    /// inside it then have an owner, and one bad line produces one error, not many.
    /// </summary>
    private Scope OpenScope(ScopeKind kind, StatementSyntax opener, SymbolKind symbolKind)
    {
        var (expected, declaredName) = opener switch
        {
            ProcDeclarationSyntax proc when kind == ScopeKind.Proc => (true, proc.Name),
            ScopeDeclarationSyntax named when kind != ScopeKind.Proc => (true, named.Name),
            _ => (false, (SyntaxToken?)null),
        };
        if (!expected)
        {
            BindStatement(opener);
            return new Scope(kind, null, scope, null);
        }

        // `.scope { }` is anonymous, and a `.proc` without a name in the source declares nothing
        // either. The scope it opens is nameless, as the routine is.
        if (declaredName is not { IsMissing: false } name)
            return new Scope(kind, null, scope, null);

        var symbol = Declare(name, symbolKind);
        if (symbol is not null && opener is ProcDeclarationSyntax routine)
        {
            CheckWidthsExist(routine);
            symbol.Signature = ReadSignature(routine);
        }
        var body = new Scope(kind, symbol?.Name ?? name.Text, scope, symbol);
        if (symbol is not null)
            symbol.Body = body;
        return body;
    }

    /// <summary>
    /// Reports a 16-bit width on a CPU whose registers are eight bits, where it cannot hold.
    /// There <c>a16</c> and <c>i16</c> are rejected wherever they appear, whether in a signature,
    /// a set, a macro's signature, a <c>.state</c> or an <c>.ensure</c>. The other items describe
    /// registers the CPU does not have, rather than contradicting the ones it has, so they are
    /// ignored there. This lets one module serve both a 6502 program and a 65816 one.
    /// </summary>
    private void CheckWidthsExist(SyntaxNode statement)
    {
        if (cpu == Cpu.Wdc65816)
            return;
        Walk(statement);

        // A signature's items sit under a `proc(...)` or a `: ... -> ...` rather than directly
        // under the line, so this looks the whole statement over. A state list is left to
        // StateItem.Read, which has already walked into it.
        void Walk(SyntaxNode node)
        {
            foreach (var item in StateItem.Read(node))
            {
                if (item is { Width: Width.Sixteen, Part: StatePart.A or StatePart.Index })
                {
                    Report(item.Node.Span, Catalogue.SignatureItemNeeds65816.Message(item.Text, CpuNames.Format(cpu)));
                }
            }
            foreach (var child in node.ChildNodes)
            {
                if (child is not StateListSyntax)
                    Walk(child);
            }
        }
    }

    /// <summary>Reads the signature a proc or an extern proc gives after its name, or the default.</summary>
    private Signature ReadSignature(StatementSyntax declaration)
    {
        var declaredSignature = SignatureOf(declaration);
        CollectUses(declaredSignature);
        return Signature.Read(declaredSignature);
    }

    /// <summary>
    /// Opens the scope of a mixed-data <c>.data</c> block, which holds its named members, reached
    /// as <c>name::member</c>, and the <c>@</c> positions private to it. A block whose opener is
    /// broken still opens a scope, so its contents have an owner.
    /// </summary>
    private Scope OpenData(StatementSyntax opener, BlockSyntax block)
    {
        if (opener is not DataDeclarationSyntax { Name: { IsMissing: false } name })
            return new Scope(ScopeKind.Data, null, scope, null);
        var symbol = Declare(name, SymbolKind.Data);
        var body = new Scope(ScopeKind.Data, symbol?.Name ?? name.Text, scope, symbol);
        if (symbol is not null)
        {
            symbol.Body = body;
            symbol.Definition = block;
        }
        return body;
    }

    /// <summary>
    /// Opens the scope of an <c>.enum</c>, <c>.struct</c> or <c>.union</c>. An anonymous type
    /// opens no scope, and its members are declared where it appears. This is how an anonymous
    /// enum names constants and an anonymous struct groups fields.
    /// </summary>
    private Scope OpenType(StatementSyntax opener, SymbolKind kind)
    {
        if (NameToken(opener) is not { } name)
            return scope;
        var symbol = Declare(name, kind);
        var body = new Scope(ScopeKind.Type, symbol?.Name ?? name.Text, scope, symbol);
        if (symbol is not null)
            symbol.Body = body;
        return body;
    }

    /// <summary>
    /// Opens the scope of a <c>.repeat</c> or an <c>.each</c>, which holds the one name it binds
    /// and nothing else. The body is read in it once. The binding's value differs in each
    /// iteration, but the symbol each name refers to does not, so one reading serves them all.
    /// </summary>
    private Scope OpenRepetition(StatementSyntax opener)
    {
        var body = new Scope(ScopeKind.Repetition, null, scope, null);
        CollectUses(opener);
        if (NameToken(opener) is not { } name)
            return body;

        var outer = scope;
        scope = body;
        Declare(name, SymbolKind.Binding);
        scope = outer;
        return body;
    }

    /// <summary>
    /// Opens the scope of a <c>.macro</c>, which holds its parameters and everything its body
    /// declares. The scope carries the macro's name, so a label in the body is named after it in
    /// the output. Nothing outside can reach into the scope, which makes each expansion's locals
    /// its own.
    /// </summary>
    private Scope OpenMacro(StatementSyntax opener)
    {
        if (opener is not MacroDeclarationSyntax declaration)
        {
            BindStatement(opener);
            return new Scope(ScopeKind.Macro, null, scope, null);
        }

        CheckMacroPlacement(declaration);
        var macroName = declaration.Name;
        var symbol = Declare(macroName, SymbolKind.Macro);

        // The declarations in a body are named after the macro, so a macro without a name in
        // the source opens a nameless scope, as a routine with no name does.
        var body = new Scope(
            ScopeKind.Macro, symbol?.Name ?? (macroName.IsMissing ? null : macroName.Text), scope, symbol);
        if (symbol is not null)
            symbol.Body = body;
        if (symbol is not null && declaration.Signature is { } signature)
        {
            CheckWidthsExist(signature);
            symbol.MacroSignature = Signature.ReadMacro(signature);
            CollectUses(signature);
        }

        // A default appears in the header, so it resolves where the macro is declared rather
        // than in the body where it is used.
        IReadOnlyList<MacroParameterSyntax> declarations =
            declaration.Parameters is { } list ? list.Parameters : [];
        foreach (var parameter in declarations)
        {
            CollectUses(Macros.DefaultOf(parameter));
            CollectKindUses(parameter.ParameterKind);
        }

        var outer = scope;
        scope = body;
        var parameters = new List<MacroParameter>();
        foreach (var parameter in declarations)
        {
            if (Declare(parameter.Name, SymbolKind.MacroParameter) is not { } declared)
                continue;
            declared.Parameter = Macros.Describe(parameter, declared);
            parameters.Add(declared.Parameter);
        }
        scope = outer;
        if (symbol is not null)
            symbol.Parameters = parameters;
        CheckParameterOrder(declarations, parameters);
        return body;
    }

    /// <summary>
    /// Collects the names in a parameter's kind, which are the bounds of a <c>const</c> range and
    /// the enum of an enum kind. These resolve where the macro is declared, as a default does. The
    /// words of a <c>one</c> and the modes of an <c>operand</c> are never looked up.
    /// </summary>
    private void CollectKindUses(ParameterKindSyntax? kind)
    {
        if (kind is null)
            return;
        CollectUses(kind.Type);
        CollectUses(kind.Low);
        CollectUses(kind.High);
        CollectKindUses(kind.Element);
    }

    /// <summary>
    /// Reports a <c>.macro</c> declared inside a routine or another macro, since a macro belongs
    /// at file level or in a <c>.scope</c> outside any routine. A macro declared in a proc would
    /// see that proc's cheap locals, and an expansion in another proc would branch into them, out
    /// of sight of the first proc's flow analysis. Which surroundings rule a macro out comes from
    /// its placement, which the editor reads too.
    /// </summary>
    private void CheckMacroPlacement(MacroDeclarationSyntax opener)
    {
        var placement = SyntaxFacts.PlacementOf(DirectiveKind.Macro);
        for (var around = scope; around is { Kind: not ScopeKind.File }; around = around.Parent)
        {
            var nesting = around.Kind switch
            {
                ScopeKind.Proc => DirectiveNesting.Routine,
                ScopeKind.Macro => DirectiveNesting.MacroBody,
                _ => DirectiveNesting.None,
            };
            if (!placement.IsBarredBy(nesting))
                continue;
            Report(opener.Keyword.Span, Catalogue.MacroMisplaced.Message(
                around.Kind == ScopeKind.Proc ? "a routine" : "another macro"));
            return;
        }
    }

    /// <summary>
    /// Reports parameters declared in the wrong order. A macro has at most one <c>list</c>, which
    /// takes every remaining positional argument, and only blocks may follow it. Blocks come
    /// last, because they appear after the parentheses and so cannot be positional at all.
    /// </summary>
    private void CheckParameterOrder(
        IReadOnlyList<MacroParameterSyntax> declaredParameters, IReadOnlyList<MacroParameter> parameters)
    {
        MacroParameter? list = null;
        MacroParameter? block = null;
        for (var i = 0; i < parameters.Count && i < declaredParameters.Count; i++)
        {
            var parameter = parameters[i];
            var at = declaredParameters[i].Span;
            if (parameter.IsBlock)
            {
                block = parameter;
                continue;
            }
            if (block is not null)
            {
                Report(at, Catalogue.ParameterAfterBlock.Message(parameter.Name, block.Name));
            }
            if (list is not null)
            {
                Report(at, Catalogue.ParameterAfterList.Message(parameter.Name, list.Name));
            }
            if (parameter.Kind == ParameterKind.List)
                list = list is null ? parameter : list;
        }
    }

    /// <summary>
    /// Checks <c>} name {</c>, which continues the block argument above it. The parameter it
    /// names is checked with the call. The only check here is for a continuation with no block
    /// argument above it, which no call would ever see.
    /// </summary>
    private void CheckContinuation(BlockSyntax block, BlockContinuationSyntax opener)
    {
        if (block.Parent is { } container
            && container.ChildNodes.IndexOf(block) is > 0 and var at
            && container.ChildNodes[at - 1] is BlockSyntax { BlockKind: BlockKind.MacroBlock })
        {
            return;
        }
        Report(opener.Name.Span, Catalogue.BlockContinuesNothing);
    }

    /// <summary>
    /// Reports a statement that a macro body, a block argument or a repetition may not hold. In a
    /// macro body, such a statement would declare in the caller or make something program-wide
    /// depend on how often the macro is called. A block argument is spliced wherever the body
    /// names it, so anything it declared would be declared once per splice.
    /// </summary>
    private void CheckAllowedHere(StatementSyntax statement)
    {
        var why = InMacroBody ? Macros.Forbidden(statement) : null;
        if (why is null && InRepetition)
            why = Repetitions.Forbidden(statement);
        if (why is not { } refused)
            return;
        Report(statement.ChildTokens.Length > 0 ? statement.ChildTokens[0].Span : statement.Span, refused);
    }

    /// <summary>
    /// Reports an annotation with no statement above it, on its keyword. An annotation goes
    /// between the statement it applies to and whatever follows.
    /// </summary>
    private void CheckAnnotation(LineSyntax line, StatementSyntax statement)
    {
        SyntaxToken? keyword = statement switch
        {
            NextDirectiveSyntax next => next.Keyword,
            PatchDirectiveSyntax patch => patch.Keyword,
            _ => null,
        };
        if (keyword is { } present && Annotations.Misplaced(line, statement) is { } why)
            Report(present.Span, why);
    }

    /// <summary>
    /// Gets a value indicating whether the walk is inside a <c>.repeat</c> or <c>.each</c> body,
    /// however many scopes deep.
    /// </summary>
    private bool InRepetition => scope.Enclosing(ScopeKind.Repetition) is not null;

    /// <summary>
    /// Gets a value indicating whether the walk is inside a macro body, however many scopes deep.
    /// </summary>
    private bool InMacroBody => scope.Enclosing(ScopeKind.Macro) is not null;

    /// <summary>
    /// Gets a value indicating whether a declaration here would land in a block argument. A
    /// routine or a macro inside a block argument owns what it declares, so the search stops at
    /// the first of those.
    /// </summary>
    private bool InABlockArgument
    {
        get
        {
            for (var around = scope; around is not null; around = around.Parent)
            {
                if (around.Kind == ScopeKind.BlockArgument)
                    return true;
                if (around.Kind is ScopeKind.Proc or ScopeKind.Macro or ScopeKind.Type)
                    return false;
            }
            return false;
        }
    }

    /// <summary>
    /// Declares a <c>.charmap</c> or a <c>.list</c>, whose lines are entries rather than
    /// declarations, as one symbol holding them. A list's items name symbols, and those names
    /// belong to the scope the list appears in.
    /// </summary>
    private void DeclareCollected(StatementSyntax opener, SymbolKind kind, ImmutableArray<SyntaxNode> lines)
    {
        var bodies = new List<StatementSyntax>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i] is LineSyntax { Statement: CharmapEntrySyntax or ListItemsSyntax } line)
                bodies.Add(line.Statement);
        }

        if (NameToken(opener) is not { } name)
            return;
        if (kind == SymbolKind.List)
        {
            Declare(name, kind, value: null, items: [.. bodies.SelectMany(line => line.ChildNodes)]);
            foreach (var line in bodies)
                CollectUses(line);
            return;
        }
        Declare(name, kind, value: null, entries: [.. bodies.OfType<CharmapEntrySyntax>()]);
        foreach (var line in bodies)
            CollectUses(line);
    }

    /// <summary>
    /// Binds a record that spans several lines. The opener declares data of the type. The member
    /// names its values give are checked against that type once it is known, so only the values
    /// themselves are names to resolve here.
    /// </summary>
    private void BindInitializer(StatementSyntax opener, ImmutableArray<SyntaxNode> lines)
    {
        BindStatement(opener);
        var statements = lines.Skip(1).OfType<LineSyntax>().Select(line => line.Statement).ToList();
        foreach (var statement in statements)
            CollectUses(statement);
        var directive = opener is DataDeclarationSyntax data ? data.Directive : opener as DataDirectiveSyntax;
        if (directive?.Type is { } type)
            records.Add((type, [.. statements.OfType<MemberValueSyntax>()]));
    }

    /// <summary>
    /// Collects the records that a <c>.type T</c> directive gives on its line, in braces or as its
    /// values.
    /// </summary>
    private void CollectRecords(DataDirectiveSyntax? directive)
    {
        if (directive?.Type is not { } type)
            return;
        records.Add((type, directive.Tail switch
        {
            InlineDataSyntax inline => [.. inline.Values],
            BracedDataSyntax braced => [braced.Value],
            _ => [],
        }));
    }

    /// <summary>
    /// Records the member names that the records give as references to the members of their
    /// types. A name that is not a member of the type is reported where the records are laid out.
    /// </summary>
    private void ResolveRecords()
    {
        if (records.Count == 0)
            return;
        var named = references.Where(reference => !reference.IsDeclaration)
            .GroupBy(reference => reference.Span.Start)
            .ToDictionary(group => group.Key, group => group.Last().Symbol);
        foreach (var (type, values) in records)
        {
            if (type.LastPart is { } innermost && named.GetValueOrDefault(innermost.Name.Span.Start) is { IsLayout: true } layout)
                ReferMembers(layout, values);
        }
    }

    private void ReferMembers(Symbol type, IEnumerable<SyntaxNode> values)
    {
        foreach (var value in values)
        {
            if (value is RecordValuesSyntax record)
            {
                ReferMembers(type, record.Members);
            }
            else if (value is ValueListSyntax list)
            {
                ReferMembers(type, list.Values);
            }
            else if (value is MemberValueSyntax given
                && BodyOf(type)?.FindMember(given.Name.Text) is { Kind: SymbolKind.Member } member)
            {
                references.Add(new SymbolReference(member, given.Name.Span, false));

                // A member's type is resolved by the file that declares it, which may not have
                // been resolved yet. Only this file's own members are followed, so the result
                // does not depend on the order in which the files are read.
                if (member.Tree == tree && BodyOf(member) is not null)
                    ReferMembers(member, [given.Value]);
            }
        }
    }

    /// <summary>
    /// Returns the segment a block or a region puts its contents in, or null when its opener
    /// names none.
    /// </summary>
    private string? SegmentOf(StatementSyntax opener)
    {
        if (opener is not (SegmentBlockSyntax or SegmentRegionSyntax))
            return null;
        var token = ((SegmentStatementSyntax)opener).Name;
        if (SegmentNames.Of(token) is not { } name)
            return null;

        // A region or block that names a segment declared nowhere is an error, so a misspelled
        // name is caught before ld65 runs. Its contents still go there, which keeps the mistake
        // to one diagnostic.
        if (segments.Find(name) is null)
            Report(token.Span, Catalogue.SegmentUndeclared.Message(name));
        return name;
    }

    private void WalkLine(LineSyntax line)
    {
        var statement = line.Statement;
        if (NamedByBinding(statement, repeated) is null)
            CheckAllowedHere(statement);
        if (statement is not (BlankLineSyntax or ModuleDirectiveSyntax))
            pastFirstItem = true;
        CheckAnnotation(line, statement);
        if (statement is FallthroughDirectiveSyntax fallthrough && !Fallthrough.EndsABody(line))
            Report(fallthrough.Keyword.Span, Catalogue.FallthroughMisplaced);
        var label = bareLabel;
        if (statement is not BlankLineSyntax)
            bareLabel = null;
        inCodeRun = false;
        BindStatement(statement);

        // A run of misplaced instructions reads through the blank and comment lines among
        // them and ends at the first line that is anything else.
        if (!inCodeRun && statement is not BlankLineSyntax)
            EndCodeRun();

        // A `.state` after a bare label is recorded on the label rather than found by flow
        // analysis, so a jump from another file can be checked against it too.
        if (statement is StateDirectiveSyntax state && label is not null)
            label.StateDeclaration = state;
    }

    private void BindStatement(StatementSyntax statement)
    {
        CheckWidthsExist(statement);
        statements.Visit(statement);
    }

    /// <summary>
    /// Binds an import item, which is <c>name</c>, <c>name: size</c>, <c>name: proc(...)</c>,
    /// <c>name: .word[8]</c> or a checked <c>name = expr</c>. An element type is taken on trust,
    /// as a routine's signature is, so nt65 sizes the import and finds its members from what the
    /// import declares.
    /// </summary>
    private void BindImportItem(ImportItemSyntax item)
    {
        var name = item.Name;
        var checkedValue = item.Value;
        var kind = checkedValue is null ? SymbolKind.ImportedAddress : SymbolKind.ImportedConstant;
        if (Declare(name, kind, checkedValue, data: item.Element, type: item.Element?.Type) is { } symbol
            && kind == SymbolKind.ImportedAddress)
        {
            // An import states its own address size. An unqualified import is absolute, and so
            // is a routine, unless its signature declares it far.
            symbol.AddressSize = AddressSize.Absolute;
            if (item.AddressSize is { } sizeToken && SegmentNames.ParseSize(sizeToken.Text) is { } size)
                symbol.AddressSize = size;
            if (item.Signature is { } signature)
            {
                symbol.Signature = Signature.Read(signature);
                CollectUses(signature);
                if (symbol.Signature.IsFar)
                    symbol.AddressSize = AddressSize.Far;
            }

            // `in SEGMENT` gives where the imported name is. Its references are checked against
            // that segment's bank, direct page and address space.
            if (item.Segment is { IsMissing: false } segment)
            {
                if (segments.Find(segment.Text) is null)
                    Report(segment.Span, Catalogue.SegmentUndeclared.Message(segment.Text));
                else
                    symbol.Segment = segment.Text;
            }
        }
        CollectUses(checkedValue);
        CollectUses(item.Element);
    }

    /// <summary>
    /// Binds a label and whatever follows it. Inside a type body the label is a member, and the
    /// directive gives how much room it takes. Elsewhere it is a label, which is only a
    /// position, regardless of what follows it on the line.
    /// </summary>
    private void BindLabeledLine(LabeledLineSyntax statement)
    {
        var name = statement.Label.Name;
        var rest = statement.Statement;
        var member = scope.Kind == ScopeKind.Type;
        if (!member)
            CheckLabelPlacement(name);
        var declared = member
            ? Declare(name, SymbolKind.Member, data: rest, type: (rest as DataDirectiveSyntax)?.Type)
            : Declare(name, SymbolKind.Label);
        if (rest is null && !member)
            bareLabel = declared;
        if (rest is MacroCallSyntax call)
        {
            BindCall(call);
            return;
        }
        if (rest is InstructionStatementSyntax instruction)
            CheckCodePlacement(instruction);
        CollectUses(rest);
    }

    /// <summary>
    /// Binds <c>.data name: ...</c>, which declares an address with a size, and the fields of its
    /// type when it is a record. Mixed data, <c>.data name {</c>, opens a scope of its own where
    /// its block is walked.
    /// </summary>
    private void BindData(DataDeclarationSyntax statement)
    {
        var element = statement.Directive;
        if (NamedByBinding(statement, repeated) is { } each && element is not null)
        {
            AddFamily(each, statement, scope, SymbolKind.Data, null, element, element.Type);
        }
        else if (element is not null)
        {
            Declare(statement.Name, SymbolKind.Data, data: element, type: element.Type);
        }
        CollectUses(element);
        CollectRecords(element);
    }

    /// <summary>
    /// Gets the kind of construct the walk is inside, which determines what may appear there. It
    /// is a routine, which holds code, or a macro body or a block argument, which land wherever
    /// they are expanded and are checked there. It may also be mixed data or a type, or
    /// <see cref="ScopeKind.File"/> at item level.
    /// </summary>
    private ScopeKind Placement
    {
        get
        {
            for (var around = scope; around is not null; around = around.Parent)
            {
                if (around.Kind is ScopeKind.Proc or ScopeKind.Macro or ScopeKind.BlockArgument
                    or ScopeKind.Data or ScopeKind.Type)
                {
                    return around.Kind;
                }
            }
            return ScopeKind.File;
        }
    }

    /// <summary>
    /// Records an instruction outside a routine, where nothing calls it or runs into it. A run of
    /// such instructions is one mistake, so it is counted here and reported once, on its first
    /// line, when the run ends. A whole routine of pasted ca65 code therefore gets one
    /// diagnostic.
    /// </summary>
    private void CheckCodePlacement(InstructionStatementSyntax instruction)
    {
        var placement = Placement;
        if (placement is ScopeKind.Proc or ScopeKind.Macro or ScopeKind.BlockArgument)
            return;
        inCodeRun = true;
        if (codeRun is { } run && run.Placement == placement)
            codeRun = run with { Lines = run.Lines + 1 };
        else
            codeRun = new CodeRun(instruction.Mnemonic.Span, placement, 1);
    }

    /// <summary>Reports the run of misplaced instructions that has just ended, if there was one.</summary>
    private void EndCodeRun()
    {
        if (codeRun is not { } run)
            return;
        codeRun = null;
        var many = run.Lines > 1 ? $"these {run.Lines} instructions belong" : "an instruction belongs";
        Report(run.At, run.Placement == ScopeKind.Data
            ? Catalogue.InstructionInData.Message(many)
            : Catalogue.InstructionOutsideARoutine.Message(many));
    }

    /// <summary>
    /// Reports a label outside a routine. A label is a position in code, so it belongs in a
    /// routine. Data is named by a declaration with a size, and a position inside mixed data is a
    /// cheap local private to it.
    /// </summary>
    private void CheckLabelPlacement(SyntaxToken name)
    {
        // A cheap local at file level is already reported for having no owner, which covers this.
        var placement = Placement;
        if (placement is ScopeKind.Proc or ScopeKind.Macro or ScopeKind.BlockArgument
            || (name.Kind == SyntaxKind.CheapLocal && scope.Kind == ScopeKind.File))
        {
            return;
        }
        if (placement == ScopeKind.Data)
        {
            if (name.Kind != SyntaxKind.CheapLocal)
            {
                Report(name.Span, Catalogue.LabelInData.Message(name.Text, name.Text, name.Text));
                Fixed(new DiagnosticFix(FixKind.DataMember));
            }
            return;
        }
        Report(name.Span, Catalogue.LabelOutsideARoutine.Message(
            name.Text, $", and data is named by a declaration, `.data {name.Text.TrimStart('@')}: ...`"));
        if (name.Kind == SyntaxKind.Identifier)
            Fixed(new DiagnosticFix(FixKind.DataDeclaration));
    }

    /// <summary>
    /// Reports data at file level that is not in a <c>.data</c> declaration. Every byte outside a
    /// routine belongs to a <c>.data</c> declaration, except unnamed <c>.res</c> and
    /// <c>.align</c>, which pad between declarations.
    /// </summary>
    private void CheckDataPlacement(DataDirectiveSyntax statement)
    {
        if (Placement != ScopeKind.File)
            return;
        if (statement.Directive.DirectiveKind is not (DirectiveKind.Res or DirectiveKind.Align))
        {
            var directive = statement.Directive.Text;
            Report(statement.Directive.Span, Catalogue.PaddingOutsideARoutine.Message(
                directive, $": `.data name: {directive} ...`"));
        }
    }

    /// <summary>
    /// Binds one enum member. A member with no value of its own follows the one before it, so
    /// each keeps a link to its predecessor rather than a number that has not been computed yet.
    /// </summary>
    private void BindEnumMember(EnumMemberSyntax statement)
    {
        var value = statement.Value;
        var member = Declare(statement.Name, SymbolKind.Constant, value, follows: value is null);
        if (member is not null)
        {
            member.PreviousMember = previousEnumMember;
            member.IsEnumMember = true;
            previousEnumMember = member;
        }
        CollectUses(value);
    }

    /// <summary>
    /// Binds a function and the parameters its body names. The parameters live in a scope of
    /// their own, which nothing outside the body can reach, so the body reads as ordinary code. A
    /// call evaluates to the body with each parameter replaced by its argument.
    /// </summary>
    private void BindFunc(FuncDeclarationSyntax statement)
    {
        var body = statement.Body;
        if (Declare(statement.Name, SymbolKind.Func, value: null, items: [body]) is not { } symbol)
            return;

        var inside = new Scope(ScopeKind.Type, null, scope, symbol);
        symbol.Body = inside;

        var outer = scope;
        scope = inside;
        var parameters = new List<Symbol>();
        IReadOnlyList<ParameterSyntax> declaredParameters = statement.Parameters is { } list ? list.Parameters : [];
        foreach (var declared in declaredParameters)
        {
            if (Declare(declared.Name, SymbolKind.Constant) is { } parameter)
                parameters.Add(parameter);
        }
        symbol.ParameterSymbols = parameters;
        CollectUses(body);
        scope = outer;
    }

    /// <summary>
    /// Binds a name on its own line, which splices the block argument bound to it. Only a macro
    /// body can hold one. Anywhere else, a name alone on a line is a statement the parser could
    /// not read.
    /// </summary>
    private void BindSplice(BlockSpliceSyntax statement)
    {
        var token = statement.Name;
        if (!InMacroBody)
        {
            Report(token.Span, Catalogue.ExpectedStatement.Message("a label, a constant, an instruction or a directive"));
            return;
        }
        uses.Add(new Use(token, scope, Path: false, First: true, Last: true, Splice: true));
    }

    /// <summary>
    /// Records a call, kept whole until the file is read. Nothing in it can be resolved yet,
    /// because the macro it names may be declared further down or in another file, and the
    /// meaning of its arguments follows from the parameters they bind to.
    /// </summary>
    private void BindCall(MacroCallSyntax call) => calls.Add(new Invocation(call, scope, EnclosingMacro));

    /// <summary>Gets the macro whose body the walk is inside, or null when it is inside none.</summary>
    private Symbol? EnclosingMacro => scope.Enclosing(ScopeKind.Macro)?.Owner;

    /// <summary>
    /// Matches each call to the macro it names and resolves the arguments that are names.
    /// A <c>one</c> argument is a word compared against the parameter's list and never
    /// looked up, so collecting it as a use would report a register or a mnemonic that is
    /// doing exactly what it is there for.
    /// </summary>
    private void ResolveCalls()
    {
        foreach (var (call, at, inside) in calls)
        {
            var callee = call.Name;
            if (Resolve(new Use(callee, at, Path: false, First: true, Last: true), null) is not { Symbol: { } symbol } place)
                continue;
            references.Add(new SymbolReference(symbol, callee.Span, false, place.IsAlias, InMacro: inside is not null));
            if (symbol.Kind != SymbolKind.Macro)
            {
                Report(callee.Span, Catalogue.NotAMacro.Message(callee.Text, symbol.KindPhrase));
                continue;
            }
            if (inside is not null)
                inside.AddCall(symbol, tree.GetSpan(callee.Span));
            else
                called.Add(symbol);

            var invocation = MacroInvocation.Of(call, symbol, tree, diagnostics, at.Lookup);
            var argumentUses = new List<Use>();
            var outer = scope;
            scope = at;
            foreach (var argument in invocation.Arguments)
            {
                // A default was resolved where the macro is declared, so only the arguments the
                // call itself gives are collected here.
                if (!argument.IsGiven)
                    continue;

                // A `one` argument is a word, which is never looked up, except that a word may
                // be passed on from a `one` parameter of the macro whose body contains the call,
                // and that is a name. So it is collected either way, and is not reported when it
                // turns out not to be a name. A member of an enum may be given by its bare name,
                // which is resolved in the enum rather than in the caller's scope, so it is a
                // word here too.
                var words = argument.Parameter.Kind is ParameterKind.One or ParameterKind.Enum
                    || argument.Parameter.Accepts.Element is { Kind: ParameterKind.One or ParameterKind.Enum };
                CollectUses(argument.Value, argumentUses, words);
                foreach (var item in argument.Items)
                    CollectUses(item, argumentUses, words);
            }
            scope = outer;
            ResolveUses(argumentUses);
        }
    }

    /// <summary>
    /// Records every name inside <paramref name="node"/>, to be resolved once the file is read.
    /// </summary>
    private void CollectUses(SyntaxNode? node) => CollectUses(node, uses);

    private void CollectUses(SyntaxNode? node, List<Use> into, bool words = false, bool chosen = false)
    {
        if (node is null)
            return;
        // `.defined(NAME)` asks whether a name is a define. The name is not a use of
        // anything, because a name that is not declared is what the question is for.
        if (IsDefinedCall(node))
            return;

        // `.loadof(S)` and `.runof(S)` name a segment, which is in a table of its own.
        // `.spanof(S)` may also name one when S is a declared segment. S is then collected as a
        // word, so it is not reported when no symbol has that name, and it is read as the
        // segment's name later.
        if (node is CallExpressionSyntax call && SegmentFunctions.NameIn(call) is { } segmentName)
        {
            if (SegmentFunctions.TakesOnlyASegment(call))
            {
                if (segments.Find(segmentName.Text) is null)
                    Report(segmentName.Span, Catalogue.SegmentUndeclared.Message(segmentName.Text));
                return;
            }
            if (call.BuiltinKind == BuiltinKind.Spanof && segments.Find(segmentName.Text) is not null)
            {
                CollectUses(call.Arguments, into, words: true, chosen);
                return;
            }
        }

        // `.select` evaluates only the value its condition chooses, so only that value's names
        // have to resolve, and evaluation is what reports them.
        if (Evaluator.SelectArguments(node) is { Count: > 0 } selected)
        {
            CollectUses(selected[0], into, words, chosen);
            foreach (var value in selected.Skip(1))
                CollectUses(value, into, words, chosen: true);
            return;
        }
        if (node is NameExpressionSyntax name)
        {
            // A leading `::` starts the path at the root of the modules. The first name is marked
            // as already part of a path, so it is looked up there rather than in the scopes
            // around it.
            var path = name.GlobalToken is not null;
            var first = true;
            foreach (var token in name.Names)
            {
                into.Add(new Use(token, scope, Path: path, First: first, Last: false, Word: words, Chosen: chosen));
                path = true;
                first = false;
            }

            // The last part decides where an export is checked. Another file has to have
            // exported the `inner` of `outer::inner`, not the `outer` that leads to it.
            if (!first)
                into[^1] = into[^1] with { Last = true };

            // An `[i]` along the path is an expression of its own, whose names are looked up
            // where the path appears rather than inside whatever it leads to.
            foreach (var part in name.Parts)
            {
                if (part.Index is { } index)
                    CollectUses(index, into, words, chosen);
            }
            return;
        }
        foreach (var child in node.ChildNodes)
            CollectUses(child, into, words, chosen);
    }

    private Symbol? Declare(
        SyntaxToken name,
        SymbolKind kind,
        ExpressionSyntax? value = null,
        StatementSyntax? data = null,
        ExpressionSyntax? type = null,
        IReadOnlyList<SyntaxNode>? items = null,
        IReadOnlyList<CharmapEntrySyntax>? entries = null,
        bool follows = false)
    {
        // A name missing from the source declares nothing. The parser puts a missing token in
        // the name's child position so that the declaration keeps its shape, and that token has
        // no text. A symbol made from it would be named "", shadow the last such symbol, and
        // match no name in the source. Every declaration in the file comes through here, so
        // this is the one place that has to check for it.
        if (name.IsMissing)
            return null;

        // A member of a named type may be named after a register or a mnemonic, because it is
        // only ever named through its type, as `Reg::x`, so there is nothing for it to shadow.
        // A register elsewhere is reported but declared anyway, so that its uses and anything
        // that counts them do not produce further errors. A mnemonic is only warned about. A
        // macro is only ever called as `name!(...)`, which no reader takes for an instruction,
        // so a macro named like an instruction is not warned about either.
        if (kind != SymbolKind.Member && scope.Kind != ScopeKind.Type)
        {
            CheckReservedWord(name);
            if (kind != SymbolKind.Macro)
                WarnAboutMnemonic(name);
        }

        var cheap = name.Kind == SyntaxKind.CheapLocal;
        if (!cheap && kind != SymbolKind.MacroParameter && InABlockArgument)
        {
            Report(name.Span, Catalogue.DeclarationInABlockArgument.Message(name.Text));
            return null;
        }
        var owner = cheap ? CheapLocalOwner(name) : scope;
        var symbol = new Symbol(cheap ? name.Text[1..] : name.Text, kind, owner, tree, name.Span)
        {
            IsCheapLocal = cheap,
            ValueExpression = value,
            Segment = segment,
            Data = data,
            TypeExpression = type,
            Items = items ?? [],
            Entries = entries ?? [],
            FollowsPrevious = follows,
        };

        if (owner.Declare(symbol) is { } existing)
        {
            // A body that declares the name of an `ident` parameter would be declaring the
            // caller's name in the caller, so that case gets its own, plainer message.
            Report(name.Span,
                existing.Parameter is { Kind: ParameterKind.Ident }
                    ? Catalogue.IdentParameterDeclared.Message(symbol.DisplayName)
                    : Catalogue.NameAlreadyDeclared.Message(symbol.DisplayName),
                new RelatedSpan(existing.DeclarationSpan, "declared here"));
        }
        symbols.Add(symbol);
        references.Add(new SymbolReference(symbol, name.Span, true));

        // A declaration that follows `.export` exports what it declares. The parameters of an
        // exported macro or function appear inside it and are not exported with it.
        var declaring = name.Parent is ImportItemSyntax ? name.Parent.Parent : name.Parent;
        if (declaring is StatementSyntax { ExportToken: { } export })
            exportedDeclarations.Add((symbol, export.Span));
        return symbol;
    }

    /// <summary>
    /// Returns the <c>.proc</c> or <c>.scope</c> that a cheap local belongs to. A cheap local
    /// outside any of them is an error, and it is kept at file scope so that uses of it still
    /// resolve.
    /// </summary>
    private Scope CheapLocalOwner(SyntaxToken name)
    {
        for (var owner = scope; owner is not null; owner = owner.Parent)
        {
            if (owner.Kind != ScopeKind.File)
                return owner;
        }
        Report(name.Span, Catalogue.CheapLocalOutsideAScope.Message(name.Text));
        return fileScope;
    }

    /// <summary>
    /// Reports a symbol named after a register, since registers are the only reserved words. A
    /// mnemonic may name a symbol anywhere, and the cost to readability is only warned about, by
    /// <see cref="WarnAboutMnemonic"/>, not reported as an error. Members of a named struct,
    /// union or enum are exempt from both, because they are reached only through <c>::</c>.
    /// </summary>
    /// <returns>True if the name is not a register.</returns>
    private bool CheckReservedWord(SyntaxToken name)
    {
        if (name.Kind != SyntaxKind.Register)
            return true;
        Report(name.Span, Catalogue.RegisterName.Message(name.Text));
        return false;
    }

    /// <summary>
    /// Warns about a declared name that is also an instruction. Nothing is wrong with the program
    /// or with the output. The difficulty is in reading it, and that is the same in every project,
    /// so the warning does not depend on which CPU this program is built for. The warning names
    /// that CPU when the word is an instruction there, and otherwise the first CPU that has it.
    /// </summary>
    private void WarnAboutMnemonic(SyntaxToken name)
    {
        if (name.Kind != SyntaxKind.Mnemonic)
            return;
        var named = Instructions.Available(cpu, name.MnemonicKind)
            ? cpu
            : CpuNames.All.Cast<Cpu?>().FirstOrDefault(other => Instructions.Has(other!.Value, name.MnemonicKind));
        if (named is not { } having)
            return;
        Warn(name.Span, Catalogue.MnemonicName.Message(name.Text, CpuNames.Format(having)));

        // The only remedy is to rename it, which only the programmer can decide, so the editor
        // puts the caret on the name.
        Fixed(new DiagnosticFix(FixKind.Rename));
    }

    /// <summary>
    /// Binds <c>.module name</c>, which must appear once, before the file's other items. A file is
    /// one module, so everything it declares belongs to one path regardless of how the file is
    /// laid out.
    /// </summary>
    private void BindModule(ModuleDirectiveSyntax statement)
    {
        var parts = statement.Name.Names;
        if (parts.Length == 0)
            return;
        var span = new TextSpan(parts[0].Span.Start, parts[^1].Span.End - parts[0].Span.Start);
        if (moduleName is not null)
            Report(span, Catalogue.ModuleDeclaredTwice);
        else if (pastFirstItem || scope != fileScope)
            Report(statement.Keyword.Span, Catalogue.ModuleNotFirst);
        if (moduleName is not null)
            return;
        foreach (var part in parts)
            CheckReservedWord(part);
        moduleName = ModuleSyntax.PathOf(statement.Name);
        moduleNameSpan = span;
        fileScope.Module = moduleName;
    }

    /// <summary>
    /// Binds a <c>.use</c>, which is resolved once the program is known. What an exported
    /// <c>.use</c> re-exports is recorded as part of the module's exports straight away, using
    /// the path it gives. Where a <c>.use</c> may appear comes from its placement, which the editor
    /// reads too.
    /// </summary>
    private void BindUse(UseDirectiveSyntax statement)
    {
        if (SyntaxFacts.PlacementOf(DirectiveKind.Use).IsBarredBy(SyntaxFacts.NestingOf(statement)))
        {
            Report(statement.Keyword.Span, Catalogue.UseMisplaced);
            return;
        }
        useDirectives.Add(statement);
        if (statement.IsExported)
            reexports.AddRange(ModuleSyntax.Brought(statement));
    }

    /// <summary>
    /// Determines what the file exports once it has been read. The exports are each declaration
    /// that follows <c>.export</c> and each name an <c>.export</c> list gives, together with the
    /// members that are exported along with them. Only the file's own declarations are looked
    /// for, so this needs no other module.
    /// <para>
    /// It runs after the families are declared, because their instances are declarations of the
    /// file like any others, and an <c>.export</c> before a family exports every instance.
    /// </para>
    /// </summary>
    public void Export()
    {
        if (moduleName is null && !isDefines)
        {
            Report(new TextSpan(0, 0), Catalogue.ModuleMissing);
        }
        foreach (var (symbol, at) in exportedDeclarations)
            Export(symbol, at, linkerName: null, size: null);
        foreach (var (item, around) in exportItems)
        {
            if (Declared(item.Name, around) is not { } symbol)
                continue;
            var size = item.AddressSize is { } sizeToken ? SegmentNames.ParseSize(sizeToken.Text) : null;
            var linkerName = item.LinkerName?.Text.Trim('"');

            // An `as` name is the name in the object file, and the output has to define it with
            // exactly that spelling, because there is no module to prefix it with. ca65 reads a
            // word from its own instruction tables at the start of a line as an instruction, so
            // it could never define such a name. The CPU this program is built for does not
            // matter, because the name is what another module, built for another CPU, would
            // link against.
            if (linkerName is not null && Ca65Instructions.HasAnywhere(linkerName) && item.LinkerName is { } spelled)
            {
                Report(spelled.Span, Catalogue.LinkerNameIsAnInstruction.Message(linkerName));
            }
            Export(symbol, item.Span, linkerName, size);
        }
    }

    /// <summary>
    /// Exports one symbol. Exporting a named scope or mixed data exports what it declares,
    /// through the scopes and data inside it, and exporting a type exports its members. A
    /// routine's interior labels are exported one by one, and cheap locals never are.
    /// </summary>
    private void Export(Symbol symbol, TextSpan at, string? linkerName, AddressSize? size)
    {
        if (!symbol.IsExported)
        {
            symbol.IsExported = true;
            symbol.ExportSpan = at;
            exported.Add(symbol);
        }
        if (linkerName is not null)
            symbol.LinkerName = linkerName;
        symbol.LinkerName ??= symbol.Kind is SymbolKind.ImportedAddress or SymbolKind.ImportedConstant || moduleName is null
            ? symbol.FlatName
            : $"{moduleName.Replace("::", "__", StringComparison.Ordinal)}__{symbol.FlatName}";
        if (size is not null)
            symbol.ExportSize = size;
        if (symbol.Kind is not (SymbolKind.Scope or SymbolKind.Data or SymbolKind.Enum or SymbolKind.Struct or SymbolKind.Union))
            return;
        foreach (var member in symbol.Body?.Symbols ?? [])
        {
            if (!member.IsCheapLocal && !member.IsExported)
                Export(member, at, linkerName: null, size: null);
        }
    }

    /// <summary>
    /// Returns the file's own declaration that a name in an <c>.export</c> list refers to, or
    /// null.
    /// </summary>
    private static Symbol? Declared(NameExpressionSyntax name, Scope around)
    {
        Symbol? symbol = null;
        foreach (var token in name.Names)
        {
            symbol = symbol is null ? around.Lookup(token.Text) : symbol.Body?.FindMember(token.Text);
            if (symbol is null)
                return null;
        }
        return symbol;
    }

    private void Report(TextSpan span, DiagnosticMessage message, params RelatedSpan[] related) =>
        diagnostics.Add(new Diagnostic(tree.GetSpan(span), Severity.Error, message, related));

    private void Warn(TextSpan span, DiagnosticMessage message) =>
        diagnostics.Add(new Diagnostic(tree.GetSpan(span), Severity.Warning, message));

    /// <summary>Gives the diagnostic just reported the fix its message names.</summary>
    private void Fixed(DiagnosticFix fix) => diagnostics[^1] = diagnostics[^1] with { Fix = fix };

    /// <summary>Represents the result of binding one file.</summary>
    /// <param name="FileScope">The file's top-level scope.</param>
    /// <param name="Symbols">Every symbol declared in the file, in source order.</param>
    /// <param name="References">Declarations and uses, ordered by position.</param>
    /// <param name="Diagnostics">The problems binding found.</param>
    /// <param name="Regions">
    /// Each block that opens a scope, with the scope, outer blocks before the blocks inside them.
    /// </param>
    public sealed record Result(
        Scope FileScope,
        IReadOnlyList<Symbol> Symbols,
        IReadOnlyList<SymbolReference> References,
        List<Diagnostic> Diagnostics,
        IReadOnlyList<(TextSpan Span, Scope Scope)> Regions)
    {
        /// <summary>Gets the names the file's <c>.use</c> items bring in, each a symbol or a module path.</summary>
        public IReadOnlyDictionary<string, BroughtName> Brought { get; init; } =
            new Dictionary<string, BroughtName>();

        /// <summary>
        /// Gets the same names mapped to what they resolve to, which is what a lookup in this file
        /// returns.
        /// </summary>
        internal IReadOnlyDictionary<string, Resolution> Used { get; init; } =
            new Dictionary<string, Resolution>(StringComparer.Ordinal);

        /// <summary>Gets the modules whose exports a <c>.use module::*</c> brings in.</summary>
        public IReadOnlyList<ProgramSymbols.Module> Globs { get; init; } = [];

        /// <summary>Gets the families the file declares, each standing for one declaration per member.</summary>
        public IReadOnlyList<Family> Families { get; init; } = [];
    }

    /// <summary>
    /// Represents one name in the source, waiting for the whole file to be read before it is
    /// resolved.
    /// </summary>
    /// <param name="Token">The name.</param>
    /// <param name="Scope">The scope it appears in.</param>
    /// <param name="Path">Whether a <c>::</c> comes before it, so it names a member of a scope.</param>
    /// <param name="First">Whether it is the first part of the name it belongs to.</param>
    /// <param name="Last">Whether it is the last part, and so the symbol the whole name refers to.</param>
    /// <param name="Splice">Whether the name is alone on a line, and so splices a block.</param>
    /// <param name="Word">
    /// Whether it appears where a bare word is allowed, so it may be a word rather than a name.
    /// </param>
    /// <param name="Chosen">
    /// Whether it is in a value that a <c>.select</c> chooses between, so that problems with it
    /// matter only if the value is chosen.
    /// </param>
    private readonly record struct Use(
        SyntaxToken Token, Scope Scope, bool Path, bool First, bool Last,
        bool Splice = false, bool Word = false, bool Chosen = false);

    /// <summary>Represents consecutive instructions outside a routine, which count as one mistake.</summary>
    private sealed record CodeRun(TextSpan At, ScopeKind Placement, int Lines);

    /// <summary>
    /// Represents a repetition whose body is being read, with what a declaration named after its
    /// binding needs to know. This is what it walks, where the declarations would go, and what is
    /// wrong with that location when something is.
    /// </summary>
    /// <param name="Block">The repetition's block, which each iteration expands.</param>
    /// <param name="Walked">The enum or list the repetition walks.</param>
    /// <param name="Binding">The name it binds, which the declarations are named from.</param>
    /// <param name="Around">The scope the declarations go in, which is the one around the repetition.</param>
    /// <param name="Segment">The segment that scope is putting declarations in.</param>
    /// <param name="Why">Why a declaration named after the binding is not allowed here, or null when it is.</param>
    private sealed record Repeated(
        BlockSyntax Block, ExpressionSyntax? Walked, Symbol Binding, Scope Around, string? Segment,
        DiagnosticMessage? Why);

    /// <summary>
    /// Represents a declaration named by a repetition's binding, waiting for the enum it walks to
    /// be known.
    /// </summary>
    private sealed record PendingFamily(
        StatementSyntax Declaration, Repeated Each, Scope Body, SymbolKind Kind,
        ProcSignatureSyntax? Signature, DataDirectiveSyntax? Data, NameExpressionSyntax? Type);

    /// <summary>
    /// Represents one call, waiting for the whole program to be read before it is matched to its
    /// macro.
    /// </summary>
    /// <param name="Call">The call.</param>
    /// <param name="Scope">The scope it appears in, which its arguments resolve in.</param>
    /// <param name="Inside">The macro whose body holds it, or null when it is called directly.</param>
    private readonly record struct Invocation(MacroCallSyntax Call, Scope Scope, Symbol? Inside);

    /// <summary>
    /// Dispatches the binding of one statement, with a method per kind of statement. The work is
    /// the binder's own, and this class selects which part of it each kind needs. A kind with no
    /// method here either declares and names nothing, such as <c>.cpu</c>, a blank line or a
    /// closing line, or is read where its block is walked.
    /// </summary>
    /// <param name="binder">The binder whose file is being bound.</param>
    private sealed class Statements(Binder binder) : SyntaxVisitor
    {
        /// <inheritdoc/>
        public override void VisitLabeledLine(LabeledLineSyntax node) => binder.BindLabeledLine(node);

        /// <inheritdoc/>
        public override void VisitEnumMember(EnumMemberSyntax node) => binder.BindEnumMember(node);

        /// <inheritdoc/>
        public override void VisitFuncDeclaration(FuncDeclarationSyntax node) => binder.BindFunc(node);

        /// <summary>
        /// Declares a signature set's name. The values and sets in its items are read once the
        /// program's names have been resolved and its constants evaluated.
        /// </summary>
        /// <param name="node">The declaration.</param>
        public override void VisitSignatureDeclaration(SignatureDeclarationSyntax node)
        {
            var items = node.Items;
            var set = node.Name;
            if (SyntaxFacts.IsStateWord(set.Text))
                binder.Report(set.Span, Catalogue.SignatureSetNameIsAnItem.Message(set.Text));
            else if (binder.Declare(set, SymbolKind.SignatureSet) is { } declared)
                declared.Definition = items;
            binder.CollectUses(items);
        }

        /// <summary>
        /// Declares a setting, which is a constant whose value the build decided before anything
        /// was declared. A setting anywhere but at file level has already been reported, and
        /// declares nothing.
        /// </summary>
        /// <param name="node">The declaration.</param>
        public override void VisitConfigDeclaration(ConfigDeclarationSyntax node)
        {
            var setting = node.Value;
            if (Configuration.IsWellPlaced(node)
                && binder.Declare(node.Name, SymbolKind.Constant) is { } config)
            {
                config.IsConfig = true;
                config.Value = binder.configuration.SettingOf(binder.tree, node.Name.Text) is { } given
                    ? Value.Of(given)
                    : Value.Unknown;
            }
            binder.CollectUses(setting, binder.uses, words: true);
        }

        /// <inheritdoc/>
        public override void VisitConstantDeclaration(ConstantDeclarationSyntax node)
        {
            // The declaration is a constant or an address alias. Which one it is depends on the
            // expression, so the kind is decided once the names in it resolve.
            binder.Declare(node.Name, SymbolKind.Constant, node.Value);
            binder.CollectUses(node.Value);
        }

        /// <inheritdoc/>
        public override void VisitExternProcDeclaration(ExternProcDeclarationSyntax node)
        {
            if (binder.Declare(node.Name, SymbolKind.ExternProc, node.Address) is { } externProc)
                externProc.Signature = binder.ReadSignature(node);
            binder.CollectUses(node.Address);
        }

        /// <summary>
        /// Records the names an <c>.export</c> list exports. A cheap local can neither be reached
        /// with <c>::</c> nor exported, and the parser has already rejected one here.
        /// </summary>
        /// <param name="node">The directive.</param>
        public override void VisitExportDirective(ExportDirectiveSyntax node)
        {
            foreach (var item in node.Items)
            {
                binder.exportItems.Add((item, binder.scope));
                binder.CollectUses(item.Name);
            }
        }

        /// <inheritdoc/>
        public override void VisitModuleDirective(ModuleDirectiveSyntax node) => binder.BindModule(node);

        /// <inheritdoc/>
        public override void VisitUseDirective(UseDirectiveSyntax node) => binder.BindUse(node);

        /// <inheritdoc/>
        public override void VisitImportDirective(ImportDirectiveSyntax node)
        {
            foreach (var item in node.Items)
                binder.BindImportItem(item);
        }

        /// <inheritdoc/>
        public override void VisitBlockSplice(BlockSpliceSyntax node) => binder.BindSplice(node);

        /// <inheritdoc/>
        public override void VisitMacroCall(MacroCallSyntax node) => binder.BindCall(node);

        /// <inheritdoc/>
        public override void VisitDataDeclaration(DataDeclarationSyntax node) => binder.BindData(node);

        /// <inheritdoc/>
        public override void VisitInstructionStatement(InstructionStatementSyntax node)
        {
            binder.CheckCodePlacement(node);
            binder.CollectUses(node);
        }

        /// <inheritdoc/>
        public override void VisitDataDirective(DataDirectiveSyntax node)
        {
            binder.CheckDataPlacement(node);
            binder.CollectUses(node);
            binder.CollectRecords(node);
        }

        /// <summary>Collects the records of a <c>.type T</c> body, one or more to a line.</summary>
        /// <param name="node">The values.</param>
        public override void VisitDataValues(DataValuesSyntax node)
        {
            binder.CollectUses(node);
            if (DataSyntax.DirectiveOfValues(node)?.Type is { } recordType)
                binder.records.Add((recordType, node.Values));
        }

        /// <summary>
        /// Reports a region line reached here as an ordinary line, which means it is inside a
        /// block, where it is not allowed. A region line at file level opens the region it names,
        /// and is walked as that block's opener instead.
        /// </summary>
        /// <param name="node">The region line.</param>
        public override void VisitSegmentRegion(SegmentRegionSyntax node) =>
            binder.Report(node.Keyword.Span, Catalogue.SegmentRegionMisplaced);

        /// <inheritdoc/>
        public override void VisitAssertDirective(AssertDirectiveSyntax node) => binder.CollectUses(node);

        /// <summary>
        /// Collects the names an annotation uses. An annotation names labels and nothing else, so
        /// its names resolve as any other use does. The flow analysis checks that they name labels
        /// rather than constants.
        /// </summary>
        /// <param name="node">The annotation.</param>
        public override void VisitNextDirective(NextDirectiveSyntax node) => binder.CollectUses(node);

        /// <inheritdoc cref="VisitNextDirective"/>
        public override void VisitPatchDirective(PatchDirectiveSyntax node) => binder.CollectUses(node);

        /// <inheritdoc cref="VisitNextDirective"/>
        public override void VisitFallthroughDirective(FallthroughDirectiveSyntax node) => binder.CollectUses(node);

        /// <inheritdoc/>
        public override void VisitSegmentDeclaration(SegmentDeclarationSyntax node) => Valued(node);

        /// <inheritdoc/>
        public override void VisitStateDirective(StateDirectiveSyntax node) => Valued(node);

        /// <summary>
        /// Declares a frame, which is named like data of a type, so that its members are reached
        /// through it.
        /// </summary>
        /// <param name="node">The directive.</param>
        public override void VisitFrameDirective(FrameDirectiveSyntax node)
        {
            binder.Declare(node.Name, SymbolKind.Frame, type: node.Type);
            binder.CollectUses(node.Type);
        }

        /// <summary>
        /// Collects the names in the values of a segment declaration or a <c>.state</c>. The
        /// <c>dp = e</c> and <c>bank = e</c> of a segment declaration, and the <c>dp = e</c> and
        /// <c>dbr = e</c> of a <c>.state</c>, may name constants. So may every bank of a
        /// <c>mirrors</c> or a <c>dbr = [...]</c>, which is a list of ranges rather than one value.
        /// </summary>
        private void Valued(StatementSyntax statement)
        {
            foreach (var item in statement.DescendantNodes())
            {
                if (item is SegmentAttributeSyntax attribute)
                {
                    // A space is named in a table of its own, as a segment is.
                    if (!attribute.Name.Text.Equals("space", StringComparison.OrdinalIgnoreCase))
                        binder.CollectUses(attribute.Value);
                }
                else if (item is BankRangeSyntax range)
                {
                    binder.CollectUses(range.First);
                    binder.CollectUses(range.Last);
                }
                else if (item is StateValueItemSyntax valued)
                {
                    binder.CollectUses(valued.Value);
                }
                else if (item is StateSetItemSyntax named)
                {
                    binder.CollectUses(named.Name);
                }
            }
        }
    }
}
