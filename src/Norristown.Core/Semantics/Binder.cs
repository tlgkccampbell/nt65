using System.Collections.Immutable;
using Norristown.Layout;
using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Builds one file's scopes and declarations and resolves the names it uses.
/// <para>
/// Declarations are collected first and resolved afterwards, so a name may be used before
/// the line that declares it.
/// </para>
/// <para>
/// A macro body is read once, wherever many times it is expanded. It sees the scope the
/// macro is declared in, and what it declares belongs to a scope of its own that nothing
/// outside can reach, so an expansion never declares a name in its caller. The block
/// argument of a call is the other way round: it is the caller's own code, and its names
/// resolve there.
/// </para>
/// <para>
/// A file is a module, and a name another module declares is reached only by its path,
/// <c>hw::init</c>, or by bringing it in with <c>.use</c>. A name is looked for in the scopes
/// around it, then among what the file's <c>.use</c> items name, then among the defines, then
/// as the start of a module's path, and last among what a <c>.use module::*</c> brings in: a
/// module adding an export can never change what a name in another module already means.
/// </para>
/// <para>
/// An <c>.if</c> is neither: its conditions were answered before any of this ran, so a
/// branch the build takes is read as if the <c>.if</c> were not written and one it leaves
/// out is not read at all. That is what lets the same name be declared under two of them.
/// </para>
/// <para>
/// A <c>.repeat</c> or an <c>.each</c> body is read once, however many times it is written
/// out, with the name the repetition binds in a scope of its own. What it declares is local
/// to each turn, as what a macro body declares is local to each expansion.
/// </para>
/// <para>
/// This file is the walk: what each line declares, and where. What the names it collected
/// refer to is <c>Binder.Resolution.cs</c>, which runs after it.
/// </para>
/// </summary>
internal sealed partial class Binder
{
    // What binding a statement dispatches through: one method per kind of statement.
    private readonly Statements statements;
    private readonly SyntaxTree tree;
    private readonly SegmentTable segments;
    private readonly Configuration configuration;
    private readonly Cpu cpu;
    private readonly bool isDefines;
    private readonly List<Diagnostic> diagnostics = [];

    // What a lookup that may report reports through; one asked quietly is given none.
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

    // What the file exports: the declarations written after `.export`, the items of its
    // `.export` lists with the scope each was written in, and everything exporting those spreads to.
    private readonly List<(Symbol Symbol, TextSpan At)> exportedDeclarations = [];

    // The blocks that open a scope, with the scope each opens, in the order they are opened.
    private readonly List<(TextSpan Span, Scope Scope)> regions = [];
    private readonly List<(ExportItemSyntax Item, Scope Scope)> exportItems = [];
    private readonly List<Symbol> exported = [];

    // The records `.type T` data gives values in, with the `T` each is of: the member names they
    // write are references to `T`'s members once `T` is resolved.
    private readonly List<(NameExpressionSyntax Type, IReadOnlyList<SyntaxNode> Values)> records = [];

    // The file's `.use` items, what they bring in once resolved, and what it re-exports.
    private readonly List<UseDirectiveSyntax> useDirectives = [];
    private readonly Dictionary<string, Place> used = new(StringComparer.Ordinal);

    // Where each `.use` writes the name it brings in, so that an item nothing names can be
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
    // The segment the walk is placing things in, or null before any region or block names one.
    private string? segment;
    private string? moduleName;
    private TextSpan moduleNameSpan;

    // Whether the walk has passed an item, which `.module` has to come before.
    private bool pastFirstItem;
    private Symbol? previousEnumMember;

    // A label written on a line of its own, while nothing but blank lines has followed it: a
    // `.state` here is that label's declaration.
    private Symbol? bareLabel;

    // The run of misplaced instructions the walk is in the middle of, reported when it ends,
    // and whether the line being walked is one of them.
    private CodeRun? codeRun;
    private bool inCodeRun;

    // The declarations named by a repetition's binding, waiting for the enum walked to be
    // known before each becomes one declaration per member, and what they became.
    private readonly List<PendingFamily> pendingFamilies = [];
    private readonly List<Family> families = [];

    // The repetition whose body the walk is directly in, where a declaration named by the name
    // it binds is a family; null everywhere else, including inside a block of the body.
    private Repeated? repeated;

    private Binder(SyntaxTree tree, SegmentTable segments, Configuration configuration, Cpu cpu, bool isDefines)
    {
        statements = new Statements(this);
        this.tree = tree;
        this.segments = segments;
        this.configuration = configuration;
        this.cpu = cpu;
        this.isDefines = isDefines;
        fileScope = new Scope(ScopeKind.File, null, null, null);
        scope = fileScope;
        report = (span, message) => Report(span, message);
    }

    /// <summary>The file being bound.</summary>
    public SyntaxTree Tree => tree;

    /// <summary>The file's top-level scope, which is what another file can reach into.</summary>
    public Scope FileScope => fileScope;

    /// <summary>
    /// What resolving this file looked for in other modules, whether it found it or not: each
    /// name in a module, with that module's name, and each name no one declared, with none,
    /// which another module exporting would change what is said about.
    /// </summary>
    public IReadOnlySet<LookedUpName> LookedUp => lookedUp;

    /// <summary>The file as the program sees it: its module's name, its top level and what it exports.</summary>
    public ProgramSymbols.Module Module => new(tree, moduleName, moduleNameSpan, fileScope, exported, reexports);

    /// <summary>
    /// Reads the declarations of <paramref name="tree"/>, leaving the names it uses to be
    /// resolved once every file of the program has been read. What the file exports is known
    /// from here on, because a file exports only what it declares. <paramref name="isDefines"/>
    /// says the file is the build configuration's defines, which is no module.
    /// </summary>
    public static Binder Collect(
        SyntaxTree tree, SegmentTable segments, Configuration configuration, Cpu cpu, bool isDefines = false)
    {
        var binder = new Binder(tree, segments, configuration, cpu, isDefines);
        binder.WalkContainer(tree.Root);
        binder.EndCodeRun();
        return binder;
    }

    /// <summary>Whether the file holds any declaration named by a repetition's binding.</summary>
    public bool HasFamilies => pendingFamilies.Count > 0;

    /// <summary>Resolves the names the file uses, with <paramref name="program"/> for the ones it does not declare.</summary>
    public Result Resolve(ProgramSymbols program)
    {
        this.program = program;
        foreach (var directive in useDirectives)
            ResolveUse(directive);
        ResolveUses(uses);

        // Conditions are answered before the program is read, so `.defined` of a name the
        // program declares would be false whatever the program says.
        foreach (var (name, at) in definedAsked)
        {
            if (at.Lookup(name.Text) is { IsDefine: false, Kind: not (SymbolKind.MacroParameter or SymbolKind.Binding) })
            {
                Report(name.Span, Catalogue.DefinedAsksAboutDefines.Says(name.Text));
            }
        }

        // A module exports what it declares. What it brought in from another module is that
        // module's, and making it part of this one is a re-export, which says where it came from.
        foreach (var (item, _) in exportItems)
        {
            if (item.Name is not { LastPart: { } innermost } name)
                continue;
            var last = innermost.Name;
            var reference = references.LastOrDefault(found => found.Span.Start == last.Span.Start && !found.IsDeclaration);
            if (reference?.Symbol is { } foreign && foreign.Tree != tree)
            {
                Report(name.Span, Catalogue.ReexportNeeded.Says(foreign.Name, foreign.Module, foreign.PathName));
            }
        }

        // A call's arguments are resolved after everything else, because whether a name in
        // one is a name at all depends on the parameter it binds to: a `one` argument is a
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

    /// <summary>The macros this file declares, which is what the recursion check reads.</summary>
    public IEnumerable<Symbol> DeclaredMacros() => symbols.Where(symbol => symbol.Kind == SymbolKind.Macro);

    /// <summary>
    /// The macros this file calls outright, rather than from inside another macro's body.
    /// What their bodies use is what the file's own output has to bring in.
    /// </summary>
    public IReadOnlyList<Symbol> CalledMacros() => called;

    /// <summary>Whether <paramref name="node"/> is a call of <c>.defined</c>.</summary>
    private static bool IsDefinedCall(SyntaxNode node) =>
        node is CallExpressionSyntax { Function: { } function }
        && function.Text.Equals(".defined", StringComparison.OrdinalIgnoreCase);

    /// <summary>The first token of a statement that could be a declared name.</summary>
    private static SyntaxToken? NameToken(StatementSyntax statement)
    {
        foreach (var token in statement.ChildTokens)
        {
            // A missing token names nothing, and it starts where the token after it does, so
            // taking one would name whatever is written there.
            if (!token.IsMissing && token.Kind is SyntaxKind.Identifier or SyntaxKind.CheapLocal
                or SyntaxKind.Register or SyntaxKind.Mnemonic)
            {
                return token;
            }
        }
        return null;
    }

    /// <summary>The signature a statement writes after its name, or null when it writes none or takes none.</summary>
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
    /// A block: what its opener declares, and its contents in whatever scope and segment the
    /// opener puts them. A segment block changes the segment of its contents, not their
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

        // A family stands directly in the body of a repetition; a block inside the body is
        // another place, and what it declares is private to the turn as it always was.
        repeated = null;

        switch (kind)
        {
            case BlockKind.Macro:
                scope = OpenMacro(opener);
                if (scope.Owner is { Kind: SymbolKind.Macro } macro)
                    macro.Definition = block;
                break;

            // A block argument is written at the call and belongs to it: its names resolve
            // in the caller, and the cheap locals it declares are private to it, because the
            // same block may be spliced in more than one place.
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
                // Whatever an included branch declares belongs to the scope around it. The
                // condition is read for its names so that an editor can follow a define to
                // the configuration that gives it a value.
                // A `.defined` is asked whichever way it was answered, and a false answer
                // leaves the branch out.
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
                // bare word, which is never looked up, so a name here that turns
                // out to be no name is a word rather than a mistake.
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
    /// What a repetition's body may declare by the name it binds: the block, the enum or list
    /// it walks and the scope the instances would go in, or null for a repetition that binds
    /// no name. The reason it carries is what is wrong with the place, for a declaration
    /// written there anyway.
    /// </summary>
    private Repeated? RepeatedIn(BlockSyntax block, StatementSyntax opener, Scope around, BlockKind kind)
    {
        if (scope.Symbols is not [{ Kind: SymbolKind.Binding } binding])
            return null;
        DiagnosticMessage? why = kind == BlockKind.Repeat
            ? Catalogue.FamilyMisplaced.Says("a `.repeat` counts its turns, and a count is no name: a family is an "
                + "`.each` over a named enum, whose members are the names it declares")
            : around.Kind == ScopeKind.Repetition
                ? Catalogue.FamilyMisplaced.Says("a family declares into the scope around its `.each`, and this one "
                    + "is inside another repetition, where every name is a different one on every turn")
                : Placement is ScopeKind.File
                    ? (DiagnosticMessage?)null
                    : Catalogue.FamilyMisplaced.Says(
                        "a family declares one routine per member into the scope around its `.each`, and this one is "
                        + $"inside {Article(Placement)}: " + (Placement is ScopeKind.Macro or ScopeKind.BlockArgument
                            ? "a body declares nothing in its caller"
                            : "a routine belongs at file level or in a `.scope`"));
        return new Repeated(block, (opener as RepetitionDirectiveSyntax)?.Expression, binding, around, segment, why);
    }

    /// <summary>What a place is called where a message says a declaration may not stand in it.</summary>
    private static string Article(ScopeKind kind) => kind switch
    {
        ScopeKind.Proc => "a routine",
        ScopeKind.Macro => "a macro body",
        ScopeKind.BlockArgument => "a block argument",
        ScopeKind.Data => "a `.data` block",
        _ => "a type",
    };

    /// <summary>
    /// The repetition around <paramref name="opener"/> when the declaration is named after the
    /// name it binds, and so declares one per member rather than one private to each turn.
    /// </summary>
    private static Repeated? NamedByBinding(StatementSyntax opener, Repeated? repeated) =>
        repeated is { } found && NameToken(opener) is { } name && name.Text == found.Binding.Name
            ? found
            : null;

    /// <summary>
    /// The body of a <c>.proc</c> named after a repetition's binding: one routine per member,
    /// declared once the enum is known. The body is read once, as every repetition body is.
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
    /// <c>.multiproc E, b: signature { }</c>: an <c>.each</c> over <c>E</c> whose body is one
    /// routine's, folded into one line. It opens the repetition's scope and the routine's
    /// inside it, exactly as the two blocks it stands for would.
    /// </summary>
    private Scope OpenMultiProc(BlockSyntax block, StatementSyntax opener)
    {
        if (opener is not MultiProcDeclarationSyntax multiProc)
        {
            BindStatement(opener);
            return new Scope(ScopeKind.Proc, null, scope, null);
        }

        // A macro body, a block argument and a repetition have already been told that a
        // routine may not stand in them, by the rules that say so for `.proc` as well.
        var around = scope;
        var placement = Placement;
        var why = placement is ScopeKind.Proc or ScopeKind.Data or ScopeKind.Type
            ? Catalogue.MultiprocMisplaced.Says(Article(placement))
            : (DiagnosticMessage?)null;
        if (why is { } misplaced)
            Report(multiProc.Keyword.Span, misplaced);

        // One inside a macro body, a block argument or a repetition has been told so by the
        // rule that holds `.proc` there; either way it declares nothing.
        var declares = why is null && placement is ScopeKind.File && around.Kind != ScopeKind.Repetition;

        CheckWidthsExist(multiProc);
        var walked = multiProc.Expression;
        var signature = multiProc.Signature;

        // The enum is named outside the repetition and the signature inside it, because a
        // signature may name the binding: `dbr = Bank::b` is that bank on each instance.
        CollectUses(walked);
        var turns = new Scope(ScopeKind.Repetition, null, around, null);
        var outer = scope;
        scope = turns;
        var binding = Declare(multiProc.Name, SymbolKind.Binding);
        CollectUses(signature);
        var body = new Scope(ScopeKind.Proc, binding?.Name, turns, null);
        if (binding is not null && declares)
            AddFamily(new Repeated(block, walked, binding, around, segment, null), multiProc, body, SymbolKind.Proc, signature, null, null);
        scope = outer;
        return body;
    }

    /// <summary>
    /// A <c>.scope</c> or a <c>.data</c> block named after a repetition's binding. What such a
    /// block holds is reached through it, which is one declaration per member of everything
    /// inside; a family declares routines and data, so this says what to write instead.
    /// </summary>
    private Scope RefuseScopeFamily(Repeated each, StatementSyntax opener, ScopeKind kind)
    {
        var what = kind == ScopeKind.Scope ? "scope" : "`.data` block";
        Report(NameToken(opener)?.Span ?? opener.Span,
            Catalogue.FamilyDeclaresTooMuch.Says(each.Binding.Name, what, each.Binding.Name, each.Binding.Name));

        // The block still opens a scope of its own, so what it holds has somewhere to go and
        // one refusal stays one.
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
    /// Declares what each family in the file stands for: one declaration per member of the
    /// enum it walks, named after the member, in the scope around the repetition. It runs
    /// once every file has been collected, because the enum may be another module's, and
    /// before the modules' exports are read, because the instances are among them.
    /// </summary>
    public void DeclareFamilies(ProgramSymbols provisional)
    {
        if (pendingFamilies.Count == 0)
            return;

        // The `.use` items are read here only to find an enum one of them brought in: the
        // program they are read against is not complete yet, so what they found is thrown
        // away and `Resolve` reads them again against the one that is.
        var referenced = references.Count;
        var reported = diagnostics.Count;
        program = provisional;
        foreach (var directive in useDirectives)
            ResolveUse(directive);
        references.RemoveRange(referenced, references.Count - referenced);
        diagnostics.RemoveRange(reported, diagnostics.Count - reported);
        unexported.Clear();

        foreach (var pending in pendingFamilies)
            DeclareFamily(pending);
        pendingFamilies.Clear();

        used.Clear();
        broughtAt.Clear();
        globs.Clear();
        reexports.Clear();
        program = ProgramSymbols.Empty;
    }

    /// <summary>One family: the enum it walks, and the declaration it makes for each member.</summary>
    private void DeclareFamily(PendingFamily pending)
    {
        var at = NameToken(pending.Declaration)?.Span ?? pending.Declaration.Span;
        var walked = pending.Each.Walked;
        var written = walked?.GetText().Trim();
        var found = walked is null ? null : NamedByPath(walked, pending.Each.Around);
        if (found is not { Kind: SymbolKind.Enum, Body: { } members })
        {
            Report(walked?.Span ?? at, Catalogue.FamilyNotOverAnEnum.Says(
                written, found is null ? "names none" : $"is {Named(found)}"));
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

        // The family's line writes the name the repetition binds, and that is the name
        // declared there; the instances are found by their own names, and their declaration is
        // that line, which go to definition lands on. References do not overlap, so nothing of
        // theirs is written at it.
        if (instances.Count > 0)
            pending.Body.Owner = instances[0].Instance;
        families.Add(new Family(pending.Declaration, pending.Each.Block, pending.Each.Binding, found, instances));
    }

    /// <summary>One instance of a family, declared under its member's name.</summary>
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

        // The signature may name the binding, `dbr = Bank::b`, and each instance's is that
        // expression with its own member's value.
        instance.Bound = (pending.Each.Binding, new Expansion.Bound(member.Value, null, Member: member));
        if (scope.Declare(instance) is { } existing)
        {
            Report(at, Catalogue.FamilyMemberCollides.Says(member.Name, pending.Each.Walked?.GetText().Trim()),
                new RelatedSpan(existing.DeclarationSpan, "declared here"));
        }
        symbols.Add(instance);
        if (pending.Declaration.ExportToken is { } export)
            exportedDeclarations.Add((instance, export.Span));
        return instance;
    }

    /// <summary>What a symbol is called where a message says it is not what was wanted.</summary>
    private static string Named(Symbol symbol) =>
        symbol.Kind == SymbolKind.Enum ? "an anonymous enum, whose members are ordinary names" : $"a {symbol.KindText}";

    /// <summary>
    /// The symbol a path names, resolved from <paramref name="at"/> and reporting nothing. It
    /// is how a family finds the enum it walks, which has to be known before the file's names
    /// are resolved, because the declarations it makes are among them.
    /// </summary>
    private Symbol? NamedByPath(ExpressionSyntax expression, Scope at)
    {
        if (expression is not NameExpressionSyntax name)
            return null;
        Place? part = null;
        var path = name.GlobalToken is not null;
        var parts = name.Parts;
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i].Name is not { IsMissing: false } token)
                break;
            var last = i == parts.Count - 1;
            part = !path
                ? at.Lookup(token.Text) is { } local ? new Place(local) : Outside(token, last, null)
                : part is null ? ModuleRoot(token, null)
                : part.Value.Module is { } prefix ? InModule(token, prefix, last, null)
                : BodyOf(part.Value.Symbol!)?.FindMember(token.Text) is { } member ? new Place(member)
                : null;
            path = true;
            if (part is null or { IsReported: true })
                return null;
        }
        return part?.Symbol;
    }

    /// <summary>
    /// The scope a <c>.proc</c> or <c>.scope</c> opens. A block whose opener is broken — a
    /// missing name, or a <c>.proc</c> written after a label — still opens a scope, so the
    /// cheap locals inside it have an owner and one bad line stays one bad line.
    /// </summary>
    private Scope OpenScope(ScopeKind kind, StatementSyntax opener, SymbolKind symbolKind)
    {
        var (expected, written) = opener switch
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

        // `.scope { }` is anonymous, and a `.proc` whose name the source does not have declares
        // nothing either: the scope it opens is nameless, as the routine is.
        if (written is not { IsMissing: false } name)
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
    /// A 16-bit width cannot hold on a CPU whose registers are eight bits, so <c>a16</c> and
    /// <c>i16</c> are refused there wherever they are written: a signature, a set, a macro's
    /// signature, a <c>.state</c> or an <c>.ensure</c>. The other items are about registers the
    /// CPU does not have rather than false of the ones it does, so they are simply inert (§7.3),
    /// which is what lets one module serve a 6502 program and a 65816 one.
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
                    Report(item.Node.Span, Catalogue.SignatureItemNeeds65816.Says(item.Text, CpuNames.Spell(cpu)));
                }
            }
            foreach (var child in node.ChildNodes)
            {
                if (child is not StateListSyntax)
                    Walk(child);
            }
        }
    }

    /// <summary>The signature a proc or an extern proc writes after its name, or the default.</summary>
    private Signature ReadSignature(StatementSyntax declaration)
    {
        var written = SignatureOf(declaration);
        CollectUses(written);
        return Signature.Read(written);
    }

    /// <summary>
    /// The scope mixed data opens: its named members, reached as <c>name::member</c>, and the
    /// <c>@</c> positions private to it. A block whose opener is broken still opens one, so
    /// what is inside it has an owner.
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
    /// The scope an <c>.enum</c>, <c>.struct</c> or <c>.union</c> opens. An anonymous one
    /// opens nothing: its members are declared where it is written, which is how an
    /// anonymous enum names constants and an anonymous struct groups fields.
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
    /// The scope a <c>.repeat</c> or an <c>.each</c> opens, which holds the one name it binds
    /// and nothing else. The body is read in it once: what the name is worth differs from
    /// turn to turn, but what it refers to does not, so one reading answers for every turn.
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
    /// The scope a <c>.macro</c> opens: its parameters, and everything its body declares. The
    /// scope carries the macro's name, so a label in the body is named after it in the
    /// output, but nothing outside can reach into it, which is what makes each expansion's
    /// locals its own.
    /// </summary>
    private Scope OpenMacro(StatementSyntax opener)
    {
        if (opener is not MacroDeclarationSyntax declaration)
        {
            BindStatement(opener);
            return new Scope(ScopeKind.Macro, null, scope, null);
        }

        CheckMacroPlacement(declaration);
        var written = declaration.Name;
        var symbol = Declare(written, SymbolKind.Macro);

        // What a body declares is named after the macro, so a macro the source did not name
        // opens a nameless scope, as a routine with no name does.
        var body = new Scope(
            ScopeKind.Macro, symbol?.Name ?? (written.IsMissing ? null : written.Text), scope, symbol);
        if (symbol is not null)
            symbol.Body = body;
        if (symbol is not null && declaration.Signature is { } signature)
        {
            CheckWidthsExist(signature);
            symbol.MacroSignature = Signature.ReadMacro(signature);
            CollectUses(signature);
        }

        // A default is written in the header, so it resolves where the macro is declared
        // rather than in the body it is used in.
        IReadOnlyList<MacroParameterSyntax> declarations =
            declaration.Parameters is { } list ? list.Parameters : [];
        foreach (var parameter in declarations)
            CollectUses(Macros.DefaultOf(parameter));

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
    /// <c>.macro</c> at file level or in a <c>.scope</c> outside any routine. One declared in
    /// a proc would see that proc's cheap locals, and an expansion in another proc would
    /// branch into them, out of sight of the first proc's flow analysis.
    /// </summary>
    private void CheckMacroPlacement(MacroDeclarationSyntax opener)
    {
        for (var around = scope; around is { Kind: not ScopeKind.File }; around = around.Parent)
        {
            if (around.Kind is not (ScopeKind.Proc or ScopeKind.Macro))
                continue;
            Report(opener.Keyword.Span, Catalogue.MacroMisplaced.Says(
                around.Kind == ScopeKind.Proc ? "a routine" : "another macro"));
            return;
        }
    }

    /// <summary>
    /// The order the parameters have to be written in: at most one <c>list</c>, which takes
    /// every remaining positional argument, and the blocks after it, which are written after
    /// the parentheses and so cannot be positional at all.
    /// </summary>
    private void CheckParameterOrder(
        IReadOnlyList<MacroParameterSyntax> written, IReadOnlyList<MacroParameter> parameters)
    {
        MacroParameter? list = null;
        MacroParameter? block = null;
        for (var i = 0; i < parameters.Count && i < written.Count; i++)
        {
            var parameter = parameters[i];
            var at = written[i].Span;
            if (parameter.IsBlock)
            {
                block = parameter;
                continue;
            }
            if (block is not null)
            {
                Report(at, Catalogue.ParameterAfterBlock.Says(parameter.Name, block.Name));
            }
            if (list is not null)
            {
                Report(at, Catalogue.ParameterAfterList.Says(parameter.Name, list.Name));
            }
            if (parameter.Kind == ParameterKind.List)
                list = list is null ? parameter : list;
        }
    }

    /// <summary>
    /// <c>} name {</c>, which continues the block argument above it. Which parameter it
    /// names is the call's business; all that is left here is a continuation with no call
    /// above it at all, which the call never sees.
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
    /// What a macro body and a block argument may not hold. A body would declare in its
    /// caller or make something program-wide depend on how often it is called; a block
    /// argument is spliced wherever the body names it, so anything it declared would be
    /// declared once per splice.
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
    /// An annotation stands between the statement it is about and whatever follows, so one
    /// with nothing above it is about nothing and is reported where it is written.
    /// </summary>
    private void CheckAnnotation(LineSyntax line, StatementSyntax statement)
    {
        SyntaxToken? keyword = statement switch
        {
            NextDirectiveSyntax next => next.Keyword,
            PatchDirectiveSyntax patch => patch.Keyword,
            _ => null,
        };
        if (keyword is { } written && Annotations.Misplaced(line, statement) is { } why)
            Report(written.Span, why);
    }

    /// <summary>Whether the walk is inside a <c>.repeat</c> or <c>.each</c> body, however many scopes deep.</summary>
    private bool InRepetition => scope.Enclosing(ScopeKind.Repetition) is not null;

    /// <summary>Whether the walk is inside a macro body, however many scopes deep.</summary>
    private bool InMacroBody => scope.Enclosing(ScopeKind.Macro) is not null;

    /// <summary>
    /// Whether a declaration here would land in a block argument. A routine or a macro
    /// written inside one owns what it declares, so the walk stops at the first of those.
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
    /// A <c>.charmap</c> or a <c>.list</c>, whose lines are entries rather than declarations:
    /// the block is one symbol holding them. A list's items name symbols, and those names
    /// belong to the scope the list is written in.
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
    /// A record written over several lines. What the opener declares is data of the type; the
    /// member names its values give are checked against that type once it is known, so only the
    /// values themselves are names to resolve here.
    /// </summary>
    private void BindInitializer(StatementSyntax opener, ImmutableArray<SyntaxNode> lines)
    {
        BindStatement(opener);
        var written = lines.Skip(1).OfType<LineSyntax>().Select(line => line.Statement).ToList();
        foreach (var statement in written)
            CollectUses(statement);
        var directive = opener is DataDeclarationSyntax data ? data.Directive : opener as DataDirectiveSyntax;
        if (directive?.Type is { } type)
            records.Add((type, [.. written.OfType<MemberValueSyntax>()]));
    }

    /// <summary>The records a <c>.type T</c> directive writes on its line, braced or as its values.</summary>
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
    /// The member names the records give, as references to the members of their types. A name the
    /// type has no member of is reported where the records are laid out.
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
                // been resolved yet; only this file's own members are followed into, so what is
                // found does not depend on the order the files are read in.
                if (member.Tree == tree && BodyOf(member) is not null)
                    ReferMembers(member, [given.Value]);
            }
        }
    }

    /// <summary>The segment a block or a region puts its contents in, or null when its opener does not say.</summary>
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
            Report(token.Span, Catalogue.SegmentUndeclared.Says(name));
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
        var label = bareLabel;
        if (statement is not BlankLineSyntax)
            bareLabel = null;
        inCodeRun = false;
        BindStatement(statement);

        // A run of misplaced instructions reads through the blank and comment lines among
        // them and ends at the first line that is anything else.
        if (!inCodeRun && statement is not BlankLineSyntax)
            EndCodeRun();

        // Recorded on the label rather than found in the flow, so a jump from another file
        // can be checked against it too.
        if (statement is StateDirectiveSyntax state && label is not null)
            label.StateDeclaration = state;
    }

    private void BindStatement(StatementSyntax statement)
    {
        CheckWidthsExist(statement);
        statements.Visit(statement);
    }

    /// <summary>
    /// <c>name</c>, <c>name: size</c>, <c>name: proc(...)</c>, <c>name: .word[8]</c> or a
    /// checked <c>name = expr</c>. An element type is declared and trusted as a routine's
    /// signature is: nt65 sizes the import and reaches its members from what the import says.
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
            // is a routine, unless its signature says it is called far.
            symbol.AddressSize = AddressSize.Absolute;
            if (item.AddressSize is { } written && SegmentNames.ParseSize(written.Text) is { } size)
                symbol.AddressSize = size;
            if (item.Signature is { } signature)
            {
                symbol.Signature = Signature.Read(signature);
                CollectUses(signature);
                if (symbol.Signature.IsFar)
                    symbol.AddressSize = AddressSize.Far;
            }
        }
        CollectUses(checkedValue);
        CollectUses(item.Element);
    }

    /// <summary>
    /// A label and whatever follows it. Inside a type body the label is a member and the
    /// directive says how much room it takes; elsewhere it is a label, which is only a
    /// position, whatever follows it on the line.
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
    /// <c>.data name: ...</c>: an address with a size, and the fields of its type when it is a
    /// record. Mixed data, <c>.data name {</c>, opens a scope of its own where its block is walked.
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
    /// What the walk is inside, for what may be written there: a routine, which holds code;
    /// a macro body or a block argument, which land wherever they are expanded and are checked
    /// there; mixed data; a type; or none of those, at item level.
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
    /// An instruction belongs in a routine: outside one, nothing calls it or runs into it.
    /// A run of them is one mistake, so it is counted here and reported once, on its first
    /// line, when the run ends: a routine's worth of ca65 pasted in says so once.
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
            ? Catalogue.InstructionInData.Says(many)
            : Catalogue.InstructionOutsideARoutine.Says(many));
    }

    /// <summary>
    /// A label is a position in code, so it belongs in a routine. Data is named by a declaration
    /// with a size, and a position inside mixed data is a cheap local private to it.
    /// </summary>
    private void CheckLabelPlacement(SyntaxToken name)
    {
        // A cheap local at file level is reported for having no owner, which says it already.
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
                Report(name.Span, Catalogue.LabelInData.Says(name.Text, name.Text, name.Text));
                Fixed(new DiagnosticFix(FixKind.DataMember));
            }
            return;
        }
        Report(name.Span, Catalogue.LabelOutsideARoutine.Says(
            name.Text, $", and data is named by a declaration, `.data {name.Text.TrimStart('@')}: ...`"));
        if (name.Kind == SyntaxKind.Identifier)
            Fixed(new DiagnosticFix(FixKind.DataDeclaration));
    }

    /// <summary>
    /// Every byte outside a routine belongs to a <c>.data</c> declaration, except unnamed
    /// <c>.res</c> and <c>.align</c>, which pad between declarations.
    /// </summary>
    private void CheckDataPlacement(DataDirectiveSyntax statement)
    {
        if (Placement != ScopeKind.File)
            return;
        if (statement.Directive.Text.ToLowerInvariant() is not (".res" or ".align"))
        {
            var directive = statement.Directive.Text;
            Report(statement.Directive.Span, Catalogue.PaddingOutsideARoutine.Says(
                directive, $": `.data name: {directive} ...`"));
        }
    }

    /// <summary>
    /// One enum member. A member with no value of its own follows the one before it, so each
    /// keeps a link to its predecessor rather than a number nothing has worked out yet.
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
    /// A function and the parameters its body names. The parameters live in a scope of their
    /// own, which nothing outside the body can reach, so the body reads as ordinary code and
    /// a call is the body with each parameter standing for its argument.
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
        IReadOnlyList<ParameterSyntax> written = statement.Parameters is { } list ? list.Parameters : [];
        foreach (var declared in written)
        {
            if (Declare(declared.Name, SymbolKind.Constant) is { } parameter)
                parameters.Add(parameter);
        }
        symbol.ParameterSymbols = parameters;
        CollectUses(body);
        scope = outer;
    }

    /// <summary>
    /// A name on its own, which splices the block argument bound to it. Only a macro body
    /// can hold one: everywhere else a name alone is the line the parser could not read.
    /// </summary>
    private void BindSplice(BlockSpliceSyntax statement)
    {
        var token = statement.Name;
        if (!InMacroBody)
        {
            Report(token.Span, Catalogue.ExpectedStatement.Says("a label, a constant, an instruction or a directive"));
            return;
        }
        uses.Add(new Use(token, scope, Path: false, First: true, Last: true, Splice: true));
    }

    /// <summary>
    /// A call, kept whole until the file is read. Nothing in it can be resolved yet: the
    /// macro it names may be declared further down or in another file, and what its
    /// arguments mean follows from the parameters they bind to.
    /// </summary>
    private void BindCall(MacroCallSyntax call) => calls.Add(new Invocation(call, scope, EnclosingMacro));

    /// <summary>The macro whose body the walk is inside, or null when it is in none.</summary>
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
                Report(callee.Span, Catalogue.NotAMacro.Says(callee.Text, symbol.KindPhrase));
                continue;
            }
            if (inside is not null)
                inside.AddCall(symbol, tree.GetSpan(callee.Span));
            else
                called.Add(symbol);

            var invocation = MacroInvocation.Of(call, symbol, tree, diagnostics, at.Lookup);
            var written = new List<Use>();
            var outer = scope;
            scope = at;
            foreach (var argument in invocation.Arguments)
            {
                // A default was resolved where the macro is declared, so only what the call
                // itself wrote is collected here.
                if (!argument.Written)
                    continue;

                // A `one` argument is a word, which is never looked up — except that a word
                // may be passed on from a `one` parameter of the macro whose body writes the
                // call, and that is a name. So it is collected either way and stays silent
                // when it turns out to be no name at all.
                var words = argument.Parameter.Kind == ParameterKind.One
                    || argument.Parameter.Accepts.Element is { Kind: ParameterKind.One };
                CollectUses(argument.Value, written, words);
                foreach (var item in argument.Items)
                    CollectUses(item, written, words);
            }
            scope = outer;
            ResolveUses(written);
        }
    }

    /// <summary>Records every name written inside <paramref name="node"/>, to resolve once the file is read.</summary>
    private void CollectUses(SyntaxNode? node) => CollectUses(node, uses);

    private void CollectUses(SyntaxNode? node, List<Use> into, bool words = false, bool chosen = false)
    {
        if (node is null)
            return;
        // `.defined(NAME)` asks whether a name is a define. The name is not a use of
        // anything: one that is not declared is what the question is for.
        if (IsDefinedCall(node))
            return;

        // `.select` evaluates only the value its condition chooses, and only that one's names
        // have to mean something, which evaluation says.
        if (Evaluator.SelectArguments(node) is { Count: > 0 } selected)
        {
            CollectUses(selected[0], into, words, chosen);
            foreach (var value in selected.Skip(1))
                CollectUses(value, into, words, chosen: true);
            return;
        }
        if (node is NameExpressionSyntax name)
        {
            // A leading `::` starts the path at file scope, which the first name sees by
            // already being part of a path.
            var path = name.GlobalToken is not null;
            var first = true;
            foreach (var token in name.Names)
            {
                into.Add(new Use(token, scope, path, first, Last: false, Word: words, Chosen: chosen));
                path = true;
                first = false;
            }

            // Which part is the last decides where an export is checked: another file has to
            // have exported the `inner` of `outer::inner`, not the `outer` that leads to it.
            if (!first)
                into[^1] = into[^1] with { Last = true };

            // An `[i]` along the path is an expression of its own, whose names are looked up
            // where the path is written rather than inside whatever it leads to.
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
        // A name the source does not have declares nothing. The parser stands a missing token
        // in the slot so that the declaration keeps its shape, and that token has no text: a
        // symbol made from one would be called "", shadow the last such symbol and answer to
        // nothing anyone wrote. Every declaration in the file comes through here, so this is
        // the one place that has to say so.
        if (name.IsMissing)
            return null;

        // A member of a named type may be called after a register or a mnemonic: it is only
        // ever named through its type, as `Reg::x`, so there is nothing for it to shadow. A
        // register elsewhere is reported, and declared all the same, so that what uses it and
        // what counts it are not wrong a second time; a mnemonic is only warned about.
        if (kind != SymbolKind.Member && scope.Kind != ScopeKind.Type)
        {
            CheckReservedWord(name);
            WarnAboutMnemonic(name);
        }

        var cheap = name.Kind == SyntaxKind.CheapLocal;
        if (!cheap && kind != SymbolKind.MacroParameter && InABlockArgument)
        {
            Report(name.Span, Catalogue.DeclarationInABlockArgument.Says(name.Text));
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
            // A body that declared the name an `ident` parameter stands for would be
            // declaring a name in its caller, which is what says it plainly here.
            Report(name.Span,
                existing.Parameter is { Kind: ParameterKind.Ident }
                    ? Catalogue.IdentParameterDeclared.Says(symbol.DisplayName)
                    : Catalogue.NameAlreadyDeclared.Says(symbol.DisplayName),
                new RelatedSpan(existing.DeclarationSpan, "declared here"));
        }
        symbols.Add(symbol);
        references.Add(new SymbolReference(symbol, name.Span, true));

        // A declaration written after `.export` exports what it declares; the parameters of an
        // exported macro or function are written inside it and are not declarations of it.
        var declaring = name.Parent is ImportItemSyntax ? name.Parent.Parent : name.Parent;
        if (declaring is StatementSyntax { ExportToken: { } export })
            exportedDeclarations.Add((symbol, export.Span));
        return symbol;
    }

    /// <summary>
    /// The <c>.proc</c> or <c>.scope</c> a cheap local belongs to. One written outside
    /// any of them is an error, and is kept at file scope so that uses of it still resolve.
    /// </summary>
    private Scope CheapLocalOwner(SyntaxToken name)
    {
        for (var owner = scope; owner is not null; owner = owner.Parent)
        {
            if (owner.Kind != ScopeKind.File)
                return owner;
        }
        Report(name.Span, Catalogue.CheapLocalOutsideAScope.Says(name.Text));
        return fileScope;
    }

    /// <summary>
    /// The reserved words: a symbol may not be named after a register. Nothing else is
    /// reserved — a mnemonic names a symbol wherever one may stand, and what a reader loses
    /// by it is <see cref="WarnAboutMnemonic"/>'s business rather than an error's. Members of
    /// a named struct, union or enum are exempt from both, being reached only through <c>::</c>.
    /// </summary>
    private bool CheckReservedWord(SyntaxToken name)
    {
        if (name.Kind != SyntaxKind.Register)
            return true;
        Report(name.Span, Catalogue.RegisterName.Says(name.Text));
        return false;
    }

    /// <summary>
    /// A declared name that is also an instruction. Nothing is wrong with the program and
    /// nothing is wrong with the output; what is hard is reading it, and that is the same in
    /// every project, so the warning does not depend on which CPU this program is built for.
    /// It names that CPU where the word is an instruction there, and otherwise the first CPU
    /// that has it.
    /// </summary>
    private void WarnAboutMnemonic(SyntaxToken name)
    {
        if (name.Kind != SyntaxKind.Mnemonic)
            return;
        var named = Instructions.Writable(cpu, name.Text)
            ? cpu
            : CpuNames.All.Cast<Cpu?>().FirstOrDefault(other => Instructions.Has(other!.Value, name.Text));
        if (named is not { } having)
            return;
        Warn(name.Span, Catalogue.MnemonicName.Says(name.Text, CpuNames.Spell(having)));

        // What is left to do about it is to call it something else, which only the programmer
        // can decide; the editor puts the caret on the name.
        Fixed(new DiagnosticFix(FixKind.Rename));
    }

    /// <summary>
    /// <c>.module name</c>: once, before the file's other items. A file is one module, so what
    /// it declares belongs to one path however the file is laid out.
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
        moduleName = string.Join("::", parts.Select(part => part.Text));
        moduleNameSpan = span;
        fileScope.Module = moduleName;
    }

    /// <summary>
    /// A <c>.use</c>, which is resolved once the program is known. What an exported one
    /// re-exports is part of the module from here on, as the path it was written with.
    /// </summary>
    private void BindUse(UseDirectiveSyntax statement)
    {
        if (scope != fileScope)
        {
            Report(statement.Keyword.Span, Catalogue.UseMisplaced);
            return;
        }
        useDirectives.Add(statement);
        if (!statement.IsExported)
            return;
        var path = statement.Path.Names;
        if (path.Length == 0 || statement.StarToken is not null)
            return;
        if (statement.Items.Count == 0)
        {
            reexports.Add(new ProgramSymbols.Reexport((statement.Alias ?? path[^1]).Text, [.. path.Select(part => part.Text)]));
            return;
        }
        foreach (var item in statement.Items)
            reexports.Add(new ProgramSymbols.Reexport((item.Alias ?? item.Name).Text, [.. path.Select(part => part.Text), item.Name.Text]));
    }

    /// <summary>
    /// Works out what the file exports, once it has been read: each declaration written after
    /// <c>.export</c> and each name an <c>.export</c> list gives, and what exporting those
    /// spreads to. Only what the file declares is looked for, so this needs no other module.
    /// <para>
    /// It runs after the families are declared, because their instances are declarations of the
    /// file like any others and an <c>.export</c> before one exports every instance.
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
            var size = item.AddressSize is { } written ? SegmentNames.ParseSize(written.Text) : null;
            var linkerName = item.LinkerName?.Text.Trim('"');

            // An `as` name is the name in the object file, and the output has to define it
            // under exactly that spelling: there is no module to put in front of one. ca65
            // reads a word of its own instruction tables at the start of a line as an
            // instruction, so such a name is one it could never define. Which CPU this
            // program is built for does not come into it — the name is what another module,
            // built for another CPU, would link against.
            if (linkerName is not null && Ca65Instructions.HasAnywhere(linkerName) && item.LinkerName is { } spelled)
            {
                Report(spelled.Span, Catalogue.LinkerNameIsAnInstruction.Says(linkerName));
            }
            Export(symbol, item.Span, linkerName, size);
        }
    }

    /// <summary>
    /// Exports one symbol. Exporting a named scope or mixed data exports what it declares,
    /// through the scopes and data inside it, and exporting a type exports its members; a
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

    /// <summary>What a name in an <c>.export</c> list is among the file's own declarations, or null.</summary>
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

    /// <summary>What binding one file produced.</summary>
    /// <param name="FileScope">The file's top-level scope.</param>
    /// <param name="Symbols">Every symbol declared in the file, in source order.</param>
    /// <param name="References">Declarations and uses, ordered by position.</param>
    /// <param name="Diagnostics">What binding found wrong.</param>
    /// <param name="Regions">Each block that opens a scope, with the scope, outer blocks before the blocks inside them.</param>
    public sealed record Result(
        Scope FileScope,
        IReadOnlyList<Symbol> Symbols,
        IReadOnlyList<SymbolReference> References,
        List<Diagnostic> Diagnostics,
        IReadOnlyList<(TextSpan Span, Scope Scope)> Regions)
    {
        /// <summary>The names the file's <c>.use</c> items bring in, each a symbol or a module path.</summary>
        public IReadOnlyDictionary<string, BroughtName> Brought { get; init; } =
            new Dictionary<string, BroughtName>();

        /// <summary>The same as the places those names reach, which is what a lookup here answers with.</summary>
        internal IReadOnlyDictionary<string, Place> Used { get; init; } =
            new Dictionary<string, Place>(StringComparer.Ordinal);

        /// <summary>The modules whose exports a <c>.use module::*</c> brings in.</summary>
        public IReadOnlyList<ProgramSymbols.Module> Globs { get; init; } = [];

        /// <summary>The families the file declares, each standing for one declaration per member.</summary>
        public IReadOnlyList<Family> Families { get; init; } = [];
    }

    /// <summary>One written name, waiting for the whole file to be read before it is resolved.</summary>
    /// <param name="Token">The name.</param>
    /// <param name="Scope">The scope it was written in.</param>
    /// <param name="Path">Whether a <c>::</c> comes before it, so it names a member of a scope.</param>
    /// <param name="First">Whether it is the first part of the name it belongs to.</param>
    /// <param name="Last">Whether it is the last part, and so the symbol the whole name stands for.</param>
    /// <param name="Splice">Whether the name stands alone on a line, and so splices a block.</param>
    /// <param name="Word">Whether it is written where a bare word may stand, and so may be one.</param>
    /// <param name="Chosen">
    /// Whether it is in a value a <c>.select</c> chooses between, so that what is wrong with it
    /// matters only if the value is chosen.
    /// </param>
    private readonly record struct Use(
        SyntaxToken Token, Scope Scope, bool Path, bool First, bool Last,
        bool Splice = false, bool Word = false, bool Chosen = false);

    /// <summary>Instructions written one after another outside a routine, which are one mistake.</summary>
    private sealed record CodeRun(TextSpan At, ScopeKind Placement, int Lines);

    /// <summary>
    /// A repetition whose body is being read, as far as a declaration named after the name it
    /// binds needs to know: what it walks, where the declarations would go, and what is wrong
    /// with the place when something is.
    /// </summary>
    /// <param name="Block">The block the turns are written out from.</param>
    /// <param name="Walked">The enum or list the repetition walks.</param>
    /// <param name="Binding">The name it binds, which the declarations are named from.</param>
    /// <param name="Around">The scope the declarations go in, which is the one around the repetition.</param>
    /// <param name="Segment">The segment that scope is placing things in.</param>
    /// <param name="Why">Why a declaration named after the binding may not stand here, or null when it may.</param>
    private sealed record Repeated(
        BlockSyntax Block, ExpressionSyntax? Walked, Symbol Binding, Scope Around, string? Segment,
        DiagnosticMessage? Why);

    /// <summary>A declaration named by a repetition's binding, waiting for the enum it walks to be known.</summary>
    private sealed record PendingFamily(
        StatementSyntax Declaration, Repeated Each, Scope Body, SymbolKind Kind,
        ProcSignatureSyntax? Signature, DataDirectiveSyntax? Data, NameExpressionSyntax? Type);

    /// <summary>One call, waiting for the whole program to be read before it is matched up.</summary>
    /// <param name="Call">The call.</param>
    /// <param name="Scope">The scope it was written in, which its arguments resolve in.</param>
    /// <param name="Inside">The macro whose body holds it, or null when it is called outright.</param>
    private readonly record struct Invocation(MacroCallSyntax Call, Scope Scope, Symbol? Inside);

    /// <summary>
    /// What binding one statement does, a method per kind. The work is the binder's own; this
    /// says which of it each kind asks for. A kind with no method here either declares nothing
    /// and names nothing — <c>.cpu</c>, a blank or a closing line — or is read where its block
    /// is walked.
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
        /// A signature set names its items, whose values and sets are read once the program's
        /// names and constants are.
        /// </summary>
        /// <param name="node">The declaration.</param>
        public override void VisitSignatureDeclaration(SignatureDeclarationSyntax node)
        {
            var items = node.Items;
            var set = node.Name;
            if (SyntaxFacts.IsStateWord(set.Text))
                binder.Report(set.Span, Catalogue.SignatureSetNameIsAnItem.Says(set.Text));
            else if (binder.Declare(set, SymbolKind.SignatureSet) is { } declared)
                declared.Definition = items;
            binder.CollectUses(items);
        }

        /// <summary>
        /// A setting is a constant whose value the build decided before anything was declared.
        /// One written anywhere but at file level has been reported, and declares nothing.
        /// </summary>
        /// <param name="node">The declaration.</param>
        public override void VisitConfigDeclaration(ConfigDeclarationSyntax node)
        {
            var setting = node.Value;
            if (Configuration.AtFileLevel(node)
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
            // A constant or an address alias: which one depends on the expression, so the
            // kind is settled once the names in it resolve.
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
        /// The names an <c>.export</c> list exports. A cheap local can neither be reached with
        /// <c>::</c> nor exported, and the parser has already refused one here.
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

        /// <summary>The records of a <c>.type T</c> body, one or more to a line.</summary>
        /// <param name="node">The values.</param>
        public override void VisitDataValues(DataValuesSyntax node)
        {
            binder.CollectUses(node);
            if (DataSyntax.DirectiveOfValues(node)?.Type is { } recordType)
                binder.records.Add((recordType, node.Values));
        }

        /// <summary>
        /// A region line reached as a line is inside a block: one at file level opens the region
        /// it names, and is walked as that block's opener.
        /// </summary>
        /// <param name="node">The region line.</param>
        public override void VisitSegmentRegion(SegmentRegionSyntax node) =>
            binder.Report(node.Keyword.Span, Catalogue.SegmentRegionMisplaced);

        /// <inheritdoc/>
        public override void VisitAssertDirective(AssertDirectiveSyntax node) => binder.CollectUses(node);

        /// <summary>
        /// An annotation names labels and nothing else, so its names resolve as any other use
        /// does; that they name labels rather than constants is the flow analysis's business.
        /// </summary>
        /// <param name="node">The annotation.</param>
        public override void VisitNextDirective(NextDirectiveSyntax node) => binder.CollectUses(node);

        /// <inheritdoc cref="VisitNextDirective"/>
        public override void VisitPatchDirective(PatchDirectiveSyntax node) => binder.CollectUses(node);

        /// <inheritdoc/>
        public override void VisitSegmentDeclaration(SegmentDeclarationSyntax node) => Valued(node);

        /// <inheritdoc/>
        public override void VisitStateDirective(StateDirectiveSyntax node) => Valued(node);

        /// <summary>A frame is named like data of a type, so its members are reached through it.</summary>
        /// <param name="node">The directive.</param>
        public override void VisitFrameDirective(FrameDirectiveSyntax node)
        {
            binder.Declare(node.Name, SymbolKind.Frame, type: node.Type);
            binder.CollectUses(node.Type);
        }

        /// <summary>
        /// The <c>dp = e</c> and <c>bank = e</c> of a segment declaration, and the <c>dp = e</c>
        /// and <c>dbr = e</c> of a <c>.state</c>, may name constants; so may every bank of a
        /// <c>mirrors</c>, which is a list of ranges rather than one value.
        /// </summary>
        private void Valued(StatementSyntax statement)
        {
            foreach (var item in statement.DescendantNodes())
            {
                if (item is SegmentAttributeSyntax attribute)
                {
                    binder.CollectUses(attribute.Value);
                    foreach (var range in attribute.Ranges)
                    {
                        binder.CollectUses(range.First);
                        binder.CollectUses(range.Last);
                    }
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
