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
/// </summary>
internal sealed class Binder
{
    private readonly SyntaxTree tree;
    private readonly SegmentTable segments;
    private readonly Configuration configuration;
    private readonly Cpu cpu;
    private readonly bool isDefines;
    private readonly List<Diagnostic> diagnostics = [];

    // What a lookup that may report reports through; one asked quietly is given none.
    private readonly Action<TextSpan, string> report;
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
    private readonly List<(SyntaxNode Item, Scope Scope)> exportItems = [];
    private readonly List<Symbol> exported = [];

    // The records `.type T` data gives values in, with the `T` each is of: the member names they
    // write are references to `T`'s members once `T` is resolved.
    private readonly List<(SyntaxNode Type, IReadOnlyList<SyntaxNode> Values)> records = [];

    // The file's `.use` items, what they bring in once resolved, and what it re-exports.
    private readonly List<SyntaxNode> useDirectives = [];
    private readonly Dictionary<string, Place> used = new(StringComparer.Ordinal);

    // Where each `.use` writes the name it brings in, so that an item nothing names can be
    // reported on the item rather than on the whole line.
    private readonly Dictionary<string, (TextSpan At, bool Exported)> broughtAt = new(StringComparer.Ordinal);
    private readonly List<ProgramSymbols.Module> globs = [];
    private readonly List<ProgramSymbols.Reexport> reexports = [];

    // Every name looked for in the other files, found or not: a file that declares or stops
    // declaring one of them, or changes what it means, changes what this file means.
    private readonly HashSet<string> lookedUp = new(StringComparer.Ordinal);
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
    /// name in a module, as <c>member:</c> and <c>module::name</c>, and each name no one
    /// declared, as <c>name:</c> and the name, which another module exporting would change
    /// what is said.
    /// </summary>
    public IReadOnlySet<string> LookedUp => lookedUp;

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
                Report(name.Span, $"`{name.Text}` is declared by the program, and `.defined` asks only about "
                    + "defines: a condition tests the build configuration, and a check on the program is an `.assert`");
            }
        }

        // A module exports what it declares. What it brought in from another module is that
        // module's, and making it part of this one is a re-export, which says where it came from.
        foreach (var (item, _) in exportItems)
        {
            if (item.ChildNodes.FirstOrDefault() is not { } name || name.ChildTokens.Length == 0)
                continue;
            var last = name.ChildTokens[^1];
            var reference = references.LastOrDefault(found => found.Span.Start == last.Span.Start && !found.IsDeclaration);
            if (reference?.Symbol is { } foreign && foreign.Tree != tree)
            {
                Report(name.Span, $"`{foreign.Name}` is declared in module `{foreign.Module}`, and a module exports what it "
                    + $"declares: `.export .use {foreign.PathName}` makes it part of this one");
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
        node.Kind == SyntaxKind.CallExpression && node.ChildTokens.Length > 0
        && node.ChildTokens[0].Text.Equals(".defined", StringComparison.OrdinalIgnoreCase);

    /// <summary>The first token of a statement that could be a declared name.</summary>
    private static SyntaxToken? NameToken(SyntaxNode statement)
    {
        foreach (var token in statement.ChildTokens)
        {
            if (token.Kind is SyntaxKind.Identifier or SyntaxKind.CheapLocal
                or SyntaxKind.Register or SyntaxKind.Mnemonic)
            {
                return token;
            }
        }
        return null;
    }

    private void WalkContainer(SyntaxNode container)
    {
        foreach (var child in container.ChildNodes)
        {
            if (child.Green is GreenBlock block)
            {
                bareLabel = null;
                EndCodeRun();
                WalkBlock(child, block.BlockKind);
                pastFirstItem = true;
            }
            else
            {
                WalkLine(child);
            }
        }
    }

    /// <summary>
    /// A block: what its opener declares, and its contents in whatever scope and segment the
    /// opener puts them. A segment block changes the segment of its contents, not their
    /// scope.
    /// </summary>
    private void WalkBlock(SyntaxNode block, BlockKind kind)
    {
        var lines = block.ChildNodes;
        var opener = lines.Length > 0 ? lines[0].Statement : null;
        var outerRepeated = repeated;
        var declaresInstances = NamedByBinding(opener, outerRepeated) is not null;
        if (opener is not null && !declaresInstances)
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
                if (opener is { Kind: SyntaxKind.MacroCall or SyntaxKind.LabeledLine })
                    BindStatement(opener);
                else if (opener is { Kind: SyntaxKind.BlockContinuation })
                    CheckContinuation(block, opener);
                scope = new Scope(ScopeKind.BlockArgument, null, scope, null);
                break;

            case BlockKind.Proc:
                scope = NamedByBinding(opener, outerRepeated) is { } familyProc
                    ? OpenFamilyRoutine(familyProc, opener!)
                    : OpenScope(ScopeKind.Proc, opener, SymbolKind.Proc);
                break;
            case BlockKind.MultiProc:
                scope = OpenMultiProc(block, opener);
                break;
            case BlockKind.Scope:
                scope = NamedByBinding(opener, outerRepeated) is { } familyScope
                    ? RefuseScopeFamily(familyScope, opener!, ScopeKind.Scope)
                    : OpenScope(ScopeKind.Scope, opener, SymbolKind.Scope);
                break;
            case BlockKind.Segment:
            case BlockKind.Region:
                segment = SegmentOf(opener) ?? segment;
                break;
            case BlockKind.Data:
                scope = NamedByBinding(opener, outerRepeated) is { } familyData
                    ? RefuseScopeFamily(familyData, opener!, ScopeKind.Data)
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
                foreach (var call in opener?.DescendantNodes().Where(IsDefinedCall) ?? [])
                {
                    foreach (var argument in call.DescendantNodes().Where(node => node.Kind == SyntaxKind.NameExpression))
                    {
                        if (argument.ChildTokens is [{ Kind: SyntaxKind.Identifier } name])
                            definedAsked.Add((name, scope));
                    }
                }
                if (!configuration.Includes(block))
                    return;
                // A condition may compare a `one` parameter or a repetition binding with a
                // bare word, which is never looked up, so a name here that turns
                // out to be no name is a word rather than a mistake.
                if (opener is not null)
                    CollectUses(opener, uses, words: true);
                break;
            case BlockKind.Repeat:
            case BlockKind.Each:
                scope = OpenRepetition(opener);
                repeated = RepeatedIn(block, opener, outerScope, kind);
                break;
            default:
                if (opener is not null)
                    BindStatement(opener);
                break;
        }

        if (scope != outerScope)
            regions.Add((block.Span, scope));
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Green is GreenBlock inner)
            {
                EndCodeRun();
                WalkBlock(lines[i], inner.BlockKind);
            }
            else
            {
                WalkLine(lines[i]);
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
    private Repeated? RepeatedIn(SyntaxNode block, SyntaxNode? opener, Scope around, BlockKind kind)
    {
        if (opener is null || scope.Symbols is not [{ Kind: SymbolKind.Binding } binding])
            return null;
        var why = kind == BlockKind.Repeat
            ? "a `.repeat` counts its turns, and a count is no name: a family is an `.each` over a named enum, "
                + "whose members are the names it declares"
            : around.Kind == ScopeKind.Repetition
                ? "a family declares into the scope around its `.each`, and this one is inside another repetition, "
                    + "where every name is a different one on every turn"
                : Placement is ScopeKind.File
                    ? null
                    : "a family declares one routine per member into the scope around its `.each`, and this one is "
                        + $"inside {Article(Placement)}: " + (Placement is ScopeKind.Macro or ScopeKind.BlockArgument
                            ? "a body declares nothing in its caller"
                            : "a routine belongs at file level or in a `.scope`");
        return new Repeated(block, opener.ChildNodes.FirstOrDefault(), binding, around, segment, why);
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
    private static Repeated? NamedByBinding(SyntaxNode? opener, Repeated? repeated) =>
        repeated is { } found && opener is not null && NameToken(opener) is { } name && name.Text == found.Binding.Name
            ? found
            : null;

    /// <summary>
    /// The body of a <c>.proc</c> named after a repetition's binding: one routine per member,
    /// declared once the enum is known. The body is read once, as every repetition body is.
    /// </summary>
    private Scope OpenFamilyRoutine(Repeated each, SyntaxNode opener)
    {
        CheckWidthsExist(opener);
        var signature = opener.ChildNodes.FirstOrDefault(child => child.Kind == SyntaxKind.ProcSignature);
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
    private Scope OpenMultiProc(SyntaxNode block, SyntaxNode? opener)
    {
        if (opener is not { Kind: SyntaxKind.MultiProcDeclaration })
        {
            if (opener is not null)
                BindStatement(opener);
            return new Scope(ScopeKind.Proc, null, scope, null);
        }

        // A macro body, a block argument and a repetition have already been told that a
        // routine may not stand in them, by the rules that say so for `.proc` as well.
        var around = scope;
        var placement = Placement;
        var why = placement is ScopeKind.Proc or ScopeKind.Data or ScopeKind.Type
            ? $"`.multiproc` declares routines, and this one is inside {Article(placement)}: "
                + "a routine belongs at file level or in a `.scope`"
            : null;
        if (why is not null)
            Report(opener.ChildTokens[0].Span, why);

        // One inside a macro body, a block argument or a repetition has been told so by the
        // rule that holds `.proc` there; either way it declares nothing.
        var declares = why is null && placement is ScopeKind.File && around.Kind != ScopeKind.Repetition;

        CheckWidthsExist(opener);
        var walked = opener.ChildNodes.FirstOrDefault(child => child.Kind != SyntaxKind.ProcSignature);
        var signature = opener.ChildNodes.FirstOrDefault(child => child.Kind == SyntaxKind.ProcSignature);

        // The enum is named outside the repetition and the signature inside it, because a
        // signature may name the binding: `dbr = Bank::b` is that bank on each instance.
        CollectUses(walked);
        var turns = new Scope(ScopeKind.Repetition, null, around, null);
        var outer = scope;
        scope = turns;
        var binding = NameToken(opener) is { } name ? Declare(name, SymbolKind.Binding) : null;
        CollectUses(signature);
        var body = new Scope(ScopeKind.Proc, binding?.Name, turns, null);
        if (binding is not null && declares)
            AddFamily(new Repeated(block, walked, binding, around, segment, null), opener, body, SymbolKind.Proc, signature, null, null);
        scope = outer;
        return body;
    }

    /// <summary>
    /// A <c>.scope</c> or a <c>.data</c> block named after a repetition's binding. What such a
    /// block holds is reached through it, which is one declaration per member of everything
    /// inside; a family declares routines and data, so this says what to write instead.
    /// </summary>
    private Scope RefuseScopeFamily(Repeated each, SyntaxNode opener, ScopeKind kind)
    {
        var what = kind == ScopeKind.Scope ? "scope" : "`.data` block";
        Report(NameToken(opener)?.Span ?? opener.Span,
            $"`{each.Binding.Name}` here would declare one {what} per member, and everything inside it once for "
            + "each: a family declares routines and data, so write one family per role, "
            + $"`note::{each.Binding.Name}` and `stop::{each.Binding.Name}`");

        // The block still opens a scope of its own, so what it holds has somewhere to go and
        // one refusal stays one.
        return new Scope(kind, null, scope, null);
    }

    /// <summary>Records a declaration named by a repetition's binding, to be declared once the enum is known.</summary>
    private void AddFamily(
        Repeated each, SyntaxNode declaration, Scope body, SymbolKind kind,
        SyntaxNode? signature, SyntaxNode? data, SyntaxNode? type)
    {
        if (each.Why is not null)
        {
            if (declaration.Kind != SyntaxKind.MultiProcDeclaration)
                Report(NameToken(declaration)?.Span ?? declaration.Span, each.Why);
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
            Report(walked?.Span ?? at, found is null
                ? $"a family declares one routine per member of a named enum, and `{written}` names none"
                : $"a family declares one routine per member of a named enum, and `{written}` is {Named(found)}");
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
            Report(at, $"`{member.Name}` is a member of `{pending.Each.Walked?.GetText().Trim()}` and is already "
                + "declared in this scope: a family declares one name per member",
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
    private Symbol? NamedByPath(SyntaxNode expression, Scope at)
    {
        if (expression.Kind != SyntaxKind.NameExpression)
            return null;
        Place? part = null;
        var path = false;
        var tokens = expression.ChildTokens;
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Kind == SyntaxKind.ColonColon)
            {
                path = true;
                continue;
            }
            var last = i == tokens.Length - 1;
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
    private Scope OpenScope(ScopeKind kind, SyntaxNode? opener, SymbolKind symbolKind)
    {
        var expected = kind == ScopeKind.Proc ? SyntaxKind.ProcDeclaration : SyntaxKind.ScopeDeclaration;
        if (opener is null || opener.Kind != expected)
        {
            if (opener is not null)
                BindStatement(opener);
            return new Scope(kind, null, scope, null);
        }

        // `.scope { }` is anonymous, and declares nothing.
        if (NameToken(opener) is not { } name)
            return new Scope(kind, null, scope, null);

        var symbol = Declare(name, symbolKind);
        if (symbol is not null && kind == ScopeKind.Proc)
        {
            CheckWidthsExist(opener);
            symbol.Signature = ReadSignature(opener);
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
                    Report(item.Node.Span, $"`{item.Text}` cannot hold on the {CpuNames.Spell(cpu)}, "
                        + "whose registers are eight bits");
                }
            }
            foreach (var child in node.ChildNodes)
            {
                if (child.Kind != SyntaxKind.StateList)
                    Walk(child);
            }
        }
    }

    /// <summary>The signature a proc or an extern proc writes after its name, or the default.</summary>
    private Signature ReadSignature(SyntaxNode declaration)
    {
        var written = declaration.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.ProcSignature);
        CollectUses(written);
        return Signature.Read(written);
    }

    /// <summary>
    /// The scope mixed data opens: its named members, reached as <c>name::member</c>, and the
    /// <c>@</c> positions private to it. A block whose opener is broken still opens one, so
    /// what is inside it has an owner.
    /// </summary>
    private Scope OpenData(SyntaxNode? opener, SyntaxNode block)
    {
        if (opener is not { Kind: SyntaxKind.DataDeclaration } || NameToken(opener) is not { } name)
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
    private Scope OpenType(SyntaxNode? opener, SymbolKind kind)
    {
        if (opener is null || NameToken(opener) is not { } name)
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
    private Scope OpenRepetition(SyntaxNode? opener)
    {
        var body = new Scope(ScopeKind.Repetition, null, scope, null);
        if (opener is null)
            return body;

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
    private Scope OpenMacro(SyntaxNode? opener)
    {
        if (opener is not { Kind: SyntaxKind.MacroDeclaration })
        {
            if (opener is not null)
                BindStatement(opener);
            return new Scope(ScopeKind.Macro, null, scope, null);
        }

        CheckMacroPlacement(opener);
        var written = NameToken(opener);
        var symbol = written is { } name ? Declare(name, SymbolKind.Macro) : null;
        var body = new Scope(ScopeKind.Macro, symbol?.Name ?? written?.Text, scope, symbol);
        if (symbol is not null)
            symbol.Body = body;
        if (symbol is not null && opener.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.ProcSignature) is { } signature)
        {
            CheckWidthsExist(signature);
            symbol.MacroSignature = Signature.ReadMacro(signature);
            CollectUses(signature);
        }

        // A default is written in the header, so it resolves where the macro is declared
        // rather than in the body it is used in.
        var declarations = Macros.ParametersOf(opener);
        foreach (var parameter in declarations)
            CollectUses(Macros.DefaultOf(parameter));

        var outer = scope;
        scope = body;
        var parameters = new List<MacroParameter>();
        foreach (var parameter in declarations)
        {
            if (Macros.NameOf(parameter) is not { } spelled
                || Declare(spelled, SymbolKind.MacroParameter) is not { } declared)
            {
                continue;
            }
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
    private void CheckMacroPlacement(SyntaxNode opener)
    {
        for (var around = scope; around is { Kind: not ScopeKind.File }; around = around.Parent)
        {
            if (around.Kind is not (ScopeKind.Proc or ScopeKind.Macro))
                continue;
            Report(opener.ChildTokens[0].Span,
                around.Kind == ScopeKind.Proc
                    ? "a `.macro` belongs at file level or in a `.scope`, not inside a routine"
                    : "a `.macro` belongs at file level or in a `.scope`, not inside another macro");
            return;
        }
    }

    /// <summary>
    /// The order the parameters have to be written in: at most one <c>list</c>, which takes
    /// every remaining positional argument, and the blocks after it, which are written after
    /// the parentheses and so cannot be positional at all.
    /// </summary>
    private void CheckParameterOrder(IReadOnlyList<SyntaxNode> written, IReadOnlyList<MacroParameter> parameters)
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
                Report(at, $"`{parameter.Name}` comes after the `block` parameter `{block.Name}`, "
                    + "and a block is written after the parentheses");
            }
            if (list is not null)
            {
                Report(at, $"`{parameter.Name}` comes after the `list` parameter `{list.Name}`, "
                    + "which takes every remaining argument");
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
    private void CheckContinuation(SyntaxNode block, SyntaxNode opener)
    {
        if (block.Parent is { } container
            && container.ChildNodes.IndexOf(block) is > 0 and var at
            && container.ChildNodes[at - 1].Green is GreenBlock { BlockKind: BlockKind.MacroBlock })
        {
            return;
        }
        if (opener.ChildTokens.Length > 1)
            Report(opener.ChildTokens[1].Span, "this block continues no macro call");
    }

    /// <summary>
    /// What a macro body and a block argument may not hold. A body would declare in its
    /// caller or make something program-wide depend on how often it is called; a block
    /// argument is spliced wherever the body names it, so anything it declared would be
    /// declared once per splice.
    /// </summary>
    private void CheckAllowedHere(SyntaxNode statement)
    {
        var why = InMacroBody ? Macros.Forbidden(statement) : null;
        if (why is null && InRepetition)
            why = Repetitions.Forbidden(statement);
        if (why is null)
            return;
        Report(statement.ChildTokens.Length > 0 ? statement.ChildTokens[0].Span : statement.Span, why);
    }

    /// <summary>
    /// An annotation stands between the statement it is about and whatever follows, so one
    /// with nothing above it is about nothing and is reported where it is written.
    /// </summary>
    private void CheckAnnotation(SyntaxNode line, SyntaxNode statement)
    {
        if (!Annotations.Is(statement) || statement.ChildTokens.Length == 0)
            return;
        if (Annotations.Misplaced(line, statement) is { } why)
            Report(statement.ChildTokens[0].Span, why);
    }

    /// <summary>Whether the walk is inside a <c>.repeat</c> or <c>.each</c> body, however many scopes deep.</summary>
    private bool InRepetition
    {
        get
        {
            for (var around = scope; around is not null; around = around.Parent)
            {
                if (around.Kind == ScopeKind.Repetition)
                    return true;
            }
            return false;
        }
    }

    /// <summary>Whether the walk is inside a macro body, however many scopes deep.</summary>
    private bool InMacroBody
    {
        get
        {
            for (var around = scope; around is not null; around = around.Parent)
            {
                if (around.Kind == ScopeKind.Macro)
                    return true;
            }
            return false;
        }
    }

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
    private void DeclareCollected(SyntaxNode? opener, SymbolKind kind, ImmutableArray<SyntaxNode> lines)
    {
        var bodies = new List<SyntaxNode>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Statement is { Kind: SyntaxKind.CharmapEntry or SyntaxKind.ListItems } line)
                bodies.Add(line);
        }

        if (opener is null || NameToken(opener) is not { } name)
            return;
        if (kind == SymbolKind.List)
        {
            Declare(name, kind, value: null, items: [.. bodies.SelectMany(line => line.ChildNodes)]);
            foreach (var line in bodies)
                CollectUses(line);
            return;
        }
        Declare(name, kind, value: null, entries: bodies);
        foreach (var line in bodies)
            CollectUses(line);
    }

    /// <summary>
    /// A record written over several lines. What the opener declares is data of the type; the
    /// member names its values give are checked against that type once it is known, so only the
    /// values themselves are names to resolve here.
    /// </summary>
    private void BindInitializer(SyntaxNode? opener, ImmutableArray<SyntaxNode> lines)
    {
        if (opener is not null)
            BindStatement(opener);
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Statement is { } line)
                CollectUses(line);
        }
        var directive = opener?.Kind == SyntaxKind.DataDeclaration ? DataSyntax.ElementOf(opener) : opener;
        if (DataSyntax.TypeOf(directive) is { } type)
        {
            records.Add((type, [.. lines.Skip(1).Select(line => line.Statement)
                .OfType<SyntaxNode>().Where(statement => statement.Kind == SyntaxKind.MemberValue)]));
        }
    }

    /// <summary>The records a <c>.type T</c> directive writes on its line, braced or as its values.</summary>
    private void CollectRecords(SyntaxNode? directive)
    {
        if (DataSyntax.TypeOf(directive) is not { } type)
            return;
        records.Add((type, [.. DataSyntax.ValuesOf(directive!).Append(DataSyntax.BracedOf(directive!)).OfType<SyntaxNode>()]));
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
            if (type.ChildTokens.Length > 0 && named.GetValueOrDefault(type.ChildTokens[^1].Span.Start) is { IsLayout: true } layout)
                ReferMembers(layout, values);
        }
    }

    private void ReferMembers(Symbol type, IEnumerable<SyntaxNode> values)
    {
        foreach (var value in values)
        {
            if (value.Kind is SyntaxKind.RecordValues or SyntaxKind.ValueList)
            {
                ReferMembers(type, value.ChildNodes);
            }
            else if (value.Kind == SyntaxKind.MemberValue && value.ChildTokens.Length > 0
                && BodyOf(type)?.FindMember(value.ChildTokens[0].Text) is { Kind: SymbolKind.Member } member)
            {
                references.Add(new SymbolReference(member, value.ChildTokens[0].Span, false));

                // A member's type is resolved by the file that declares it, which may not have
                // been resolved yet; only this file's own members are followed into, so what is
                // found does not depend on the order the files are read in.
                if (member.Tree == tree && BodyOf(member) is not null)
                    ReferMembers(member, value.ChildNodes);
            }
        }
    }

    /// <summary>The segment a block or a region puts its contents in, or null when its opener does not say.</summary>
    private string? SegmentOf(SyntaxNode? opener)
    {
        if (opener is null || opener.Kind is not (SyntaxKind.SegmentBlock or SyntaxKind.SegmentRegion))
            return null;

        if (Constructs.SegmentOf(opener) is not { } name)
            return null;

        // A region or block that names a segment declared nowhere is an error, so a misspelled
        // name is caught before ld65 runs. Its contents still go there, which keeps the mistake
        // to one diagnostic.
        if (segments.Find(name) is null)
            Report(opener.ChildTokens[1].Span, $"segment \"{name}\" is not declared");
        return name;
    }

    private void WalkLine(SyntaxNode line)
    {
        if (line.Statement is not { } statement)
            return;
        if (NamedByBinding(statement, repeated) is null)
            CheckAllowedHere(statement);
        if (statement.Kind is not (SyntaxKind.BlankLine or SyntaxKind.ModuleDirective))
            pastFirstItem = true;
        CheckAnnotation(line, statement);
        var label = bareLabel;
        if (statement.Kind != SyntaxKind.BlankLine)
            bareLabel = null;
        inCodeRun = false;
        BindStatement(statement);

        // A run of misplaced instructions reads through the blank and comment lines among
        // them and ends at the first line that is anything else.
        if (!inCodeRun && statement.Kind != SyntaxKind.BlankLine)
            EndCodeRun();

        // Recorded on the label rather than found in the flow, so a jump from another file
        // can be checked against it too.
        if (statement.Kind == SyntaxKind.StateDirective && label is not null)
            label.StateDeclaration = statement;
    }

    private void BindStatement(SyntaxNode statement)
    {
        CheckWidthsExist(statement);
        switch (statement.Kind)
        {
            case SyntaxKind.LabeledLine:
                BindLabeledLine(statement);
                break;

            case SyntaxKind.EnumMember:
                BindEnumMember(statement);
                break;

            case SyntaxKind.FuncDeclaration:
                BindFunc(statement);
                break;

            // A signature set names its items, whose values and sets are read once the
            // program's names and constants are.
            case SyntaxKind.SignatureDeclaration:
                var items = statement.ChildNodes.FirstOrDefault(child => child.Kind == SyntaxKind.StateList);
                if (NameToken(statement) is { } set)
                {
                    if (SyntaxFacts.IsStateWord(set.Text))
                        Report(set.Span, $"`{set.Text}` is a signature item, and cannot name a signature set");
                    else if (Declare(set, SymbolKind.SignatureSet) is { } declared)
                        declared.Definition = items;
                }
                CollectUses(items);
                break;

            // A setting is a constant whose value the build decided before anything was declared.
            // One written anywhere but at file level has been reported, and declares nothing.
            case SyntaxKind.ConfigDeclaration:
                var setting = statement.ChildNodes.FirstOrDefault();
                if (NameToken(statement) is { } configured && Configuration.AtFileLevel(statement)
                    && Declare(configured, SymbolKind.Constant) is { } config)
                {
                    config.IsConfig = true;
                    config.Value = configuration.SettingOf(tree, configured.Text) is { } given ? Value.Of(given) : Value.Unknown;
                }
                CollectUses(setting, uses, words: true);
                break;

            case SyntaxKind.ConstantDeclaration:
                // A constant or an address alias: which one depends on the expression, so the
                // kind is settled once the names in it resolve.
                var value = statement.ChildNodes.FirstOrDefault();
                if (NameToken(statement) is { } constant)
                    Declare(constant, SymbolKind.Constant, value);
                CollectUses(value);
                break;

            case SyntaxKind.ExternProcDeclaration:
                var address = statement.ChildNodes.FirstOrDefault(c => c.Kind != SyntaxKind.ProcSignature);
                if (NameToken(statement) is { } routine
                    && Declare(routine, SymbolKind.ExternProc, address) is { } externProc)
                {
                    externProc.Signature = ReadSignature(statement);
                }
                CollectUses(address);
                break;

            // A cheap local can neither be reached with `::` nor exported, and the parser has
            // already refused one here.
            case SyntaxKind.ExportDirective:
                foreach (var item in statement.ChildNodes.Where(child => child.Kind == SyntaxKind.ExportItem))
                {
                    exportItems.Add((item, scope));
                    CollectUses(item.ChildNodes.FirstOrDefault());
                }
                break;

            case SyntaxKind.ModuleDirective:
                BindModule(statement);
                break;

            case SyntaxKind.UseDirective:
                BindUse(statement);
                break;

            case SyntaxKind.ImportDirective:
                foreach (var item in statement.ChildNodes)
                    BindImportItem(item);
                break;

            case SyntaxKind.BlockSplice:
                BindSplice(statement);
                break;

            case SyntaxKind.MacroCall:
                BindCall(statement);
                break;

            case SyntaxKind.DataDeclaration:
                BindData(statement);
                break;

            case SyntaxKind.InstructionStatement:
                CheckCodePlacement(statement);
                CollectUses(statement);
                break;

            case SyntaxKind.DataDirective:
                CheckDataPlacement(statement);
                CollectUses(statement);
                CollectRecords(statement);
                break;

            // The records of a `.type T` body, one or more to a line.
            case SyntaxKind.DataValues:
                CollectUses(statement);
                if (DataSyntax.TypeOf(DataSyntax.DirectiveOfValues(statement)) is { } recordType)
                    records.Add((recordType, statement.ChildNodes));
                break;

            // A region line reached as a line is inside a block: one at file level opens the
            // region it names, and is walked as that block's opener.
            case SyntaxKind.SegmentRegion when statement.ChildTokens.Length > 0:
                Report(statement.ChildTokens[0].Span, "a `.segment NAME` region belongs at file level, outside "
                    + "every block: inside one, `.segment NAME { }` places what it holds");
                break;

            case SyntaxKind.AssertDirective:

            // An annotation names labels and nothing else, so its names resolve as any
            // other use does; that they name labels rather than constants is the flow
            // analysis's business.
            case SyntaxKind.NextDirective:
            case SyntaxKind.PatchDirective:
                CollectUses(statement);
                break;

            // The `dp = e` and `bank = e` of a segment declaration, and the `dp = e` and
            // `dbr = e` of a `.state`, may name constants.
            case SyntaxKind.SegmentDeclaration:
            case SyntaxKind.StateDirective:
                foreach (var item in statement.DescendantNodes())
                {
                    if (item.Kind is SyntaxKind.SegmentAttribute or SyntaxKind.StateItem)
                        CollectUses(item.ChildNodes.FirstOrDefault());
                }
                break;

            // A frame is named like data of a type, so its members are reached through it.
            case SyntaxKind.FrameDirective:
                var type = statement.ChildNodes.FirstOrDefault();
                if (NameToken(statement) is { } frame)
                    Declare(frame, SymbolKind.Frame, type: type);
                CollectUses(type);
                break;

            // Everything else either declares nothing and names nothing — `.cpu`, a blank or
            // closing line — or is read where its block is walked.
            default:
                break;
        }
    }

    /// <summary><c>name</c>, <c>name: size</c>, <c>name: proc(...)</c> or a checked <c>name = expr</c>.</summary>
    private void BindImportItem(SyntaxNode item)
    {
        if (item.Kind != SyntaxKind.ImportItem || NameToken(item) is not { } name)
            return;

        var checkedValue = item.ChildNodes.FirstOrDefault(c => c.Kind != SyntaxKind.ImportSignature);
        var kind = checkedValue is null ? SymbolKind.ImportedAddress : SymbolKind.ImportedConstant;
        if (Declare(name, kind, checkedValue) is { } symbol && kind == SymbolKind.ImportedAddress)
        {
            // An import states its own address size. An unqualified import is absolute, and so
            // is a routine, unless its signature says it is called far.
            symbol.AddressSize = AddressSize.Absolute;
            foreach (var token in item.ChildTokens)
            {
                if (token.Kind == SyntaxKind.Identifier && SegmentNames.ParseSize(token.Text) is { } size)
                    symbol.AddressSize = size;
            }
            if (item.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.ImportSignature) is { } signature)
            {
                symbol.Signature = Signature.Read(signature);
                CollectUses(signature);
                if (symbol.Signature.IsFar)
                    symbol.AddressSize = AddressSize.Far;
            }
        }
        CollectUses(checkedValue);
    }

    /// <summary>
    /// A label and whatever follows it. Inside a type body the label is a member and the
    /// directive says how much room it takes; elsewhere it is a label, which is only a
    /// position, whatever follows it on the line.
    /// </summary>
    private void BindLabeledLine(SyntaxNode statement)
    {
        var label = statement.ChildNodes.FirstOrDefault(child => child.Kind == SyntaxKind.Label);
        var rest = statement.ChildNodes.FirstOrDefault(child => child.Kind != SyntaxKind.Label);
        var member = scope.Kind == ScopeKind.Type;
        if (label is { ChildTokens.Length: > 0 })
        {
            if (!member)
                CheckLabelPlacement(label.ChildTokens[0]);
            var declared = member
                ? Declare(label.ChildTokens[0], SymbolKind.Member, data: rest, type: DataSyntax.TypeOf(rest))
                : Declare(label.ChildTokens[0], SymbolKind.Label);
            if (rest is null && !member)
                bareLabel = declared;
        }
        if (rest is { Kind: SyntaxKind.MacroCall })
        {
            BindCall(rest);
            return;
        }
        if (rest is { Kind: SyntaxKind.InstructionStatement })
            CheckCodePlacement(rest);
        CollectUses(rest);
    }

    /// <summary>
    /// <c>.data name: ...</c>: an address with a size, and the fields of its type when it is a
    /// record. Mixed data, <c>.data name {</c>, opens a scope of its own where its block is walked.
    /// </summary>
    private void BindData(SyntaxNode statement)
    {
        var element = DataSyntax.ElementOf(statement);
        if (NamedByBinding(statement, repeated) is { } each && element is not null)
        {
            AddFamily(each, statement, scope, SymbolKind.Data, null, element, DataSyntax.TypeOf(element));
        }
        else if (NameToken(statement) is { } name && element is not null)
        {
            Declare(name, SymbolKind.Data, data: element, type: DataSyntax.TypeOf(element));
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
    private void CheckCodePlacement(SyntaxNode instruction)
    {
        var placement = Placement;
        if (placement is ScopeKind.Proc or ScopeKind.Macro or ScopeKind.BlockArgument || instruction.ChildTokens.Length == 0)
            return;
        inCodeRun = true;
        if (codeRun is { } run && run.Placement == placement)
            codeRun = run with { Lines = run.Lines + 1 };
        else
            codeRun = new CodeRun(instruction.ChildTokens[0].Span, placement, 1);
    }

    /// <summary>Reports the run of misplaced instructions that has just ended, if there was one.</summary>
    private void EndCodeRun()
    {
        if (codeRun is not { } run)
            return;
        codeRun = null;
        var many = run.Lines > 1 ? $"these {run.Lines} instructions belong" : "an instruction belongs";
        Report(run.At, run.Placement == ScopeKind.Data
            ? $"{many} in a `.proc`, and `.data` holds only data"
            : $"{many} in a `.proc`: code outside one is reached by nothing nt65 can follow");
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
                Report(name.Span, $"`{name.Text}` is a label in `.data`: a named member is `.data {name.Text}: ...`, "
                    + $"and a position is `@{name.Text}:`");
                Fixed(new DiagnosticFix(FixKind.DataMember));
            }
            return;
        }
        Report(name.Span, $"`{name.Text}` is a label outside a `.proc`: a label is only a position in code, "
            + $"and data is named by a declaration, `.data {name.Text.TrimStart('@')}: ...`");
        if (name.Kind == SyntaxKind.Identifier)
            Fixed(new DiagnosticFix(FixKind.DataDeclaration));
    }

    /// <summary>
    /// Every byte outside a routine belongs to a <c>.data</c> declaration, except unnamed
    /// <c>.res</c> and <c>.align</c>, which pad between declarations.
    /// </summary>
    private void CheckDataPlacement(SyntaxNode statement)
    {
        if (Placement != ScopeKind.File || statement.ChildTokens.Length == 0)
            return;
        if (statement.Kind == SyntaxKind.DataDirective && DataSyntax.NameOf(statement) is not (".res" or ".align"))
        {
            var directive = statement.ChildTokens[0].Text;
            Report(statement.ChildTokens[0].Span, $"`{directive}` outside a `.proc` belongs to a `.data` declaration: "
                + $"`.data name: {directive} ...`");
        }
    }

    /// <summary>
    /// One enum member. A member with no value of its own follows the one before it, so each
    /// keeps a link to its predecessor rather than a number nothing has worked out yet.
    /// </summary>
    private void BindEnumMember(SyntaxNode statement)
    {
        if (NameToken(statement) is not { } name)
            return;
        var value = statement.ChildNodes.FirstOrDefault();
        var member = Declare(name, SymbolKind.Constant, value, follows: value is null);
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
    private void BindFunc(SyntaxNode statement)
    {
        var body = statement.ChildNodes.LastOrDefault(child => child.Kind != SyntaxKind.ParameterList);
        var written = statement.ChildNodes.FirstOrDefault(child => child.Kind == SyntaxKind.ParameterList);
        if (NameToken(statement) is not { } name)
            return;

        var symbol = Declare(name, SymbolKind.Func, value: null, items: body is null ? [] : [body]);
        if (symbol is null)
            return;

        var inside = new Scope(ScopeKind.Type, null, scope, symbol);
        symbol.Body = inside;

        var outer = scope;
        scope = inside;
        var parameters = new List<Symbol>();
        foreach (var token in written?.ChildTokens ?? [])
        {
            if (token.Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic
                && Declare(token, SymbolKind.Constant) is { } parameter)
            {
                parameters.Add(parameter);
            }
        }
        symbol.ParameterSymbols = parameters;
        CollectUses(body);
        scope = outer;
    }

    /// <summary>
    /// A name on its own, which splices the block argument bound to it. Only a macro body
    /// can hold one: everywhere else a name alone is the line the parser could not read.
    /// </summary>
    private void BindSplice(SyntaxNode statement)
    {
        if (statement.ChildTokens.Length == 0)
            return;
        var token = statement.ChildTokens[0];
        if (!InMacroBody)
        {
            Report(token.Span, "expected a label, a constant, an instruction or a directive");
            return;
        }
        uses.Add(new Use(token, scope, Path: false, First: true, Last: true, Splice: true));
    }

    /// <summary>
    /// A call, kept whole until the file is read. Nothing in it can be resolved yet: the
    /// macro it names may be declared further down or in another file, and what its
    /// arguments mean follows from the parameters they bind to.
    /// </summary>
    private void BindCall(SyntaxNode call) => calls.Add(new Invocation(call, scope, EnclosingMacro));

    /// <summary>The macro whose body the walk is inside, or null when it is in none.</summary>
    private Symbol? EnclosingMacro
    {
        get
        {
            for (var around = scope; around is not null; around = around.Parent)
            {
                if (around.Kind == ScopeKind.Macro)
                    return around.Owner;
            }
            return null;
        }
    }

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
            if (Macros.CalleeOf(call) is not { } callee)
                continue;
            if (Resolve(new Use(callee, at, Path: false, First: true, Last: true), null) is not { Symbol: { } symbol } place)
                continue;
            references.Add(new SymbolReference(symbol, callee.Span, false, place.IsAlias, InMacro: inside is not null));
            if (symbol.Kind != SymbolKind.Macro)
            {
                Report(callee.Span, $"`{callee.Text}` is a {symbol.KindText}, and `!` calls a macro");
                continue;
            }
            if (inside is not null)
                inside.Calls.Add((symbol, tree.GetSpan(callee.Span)));
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
        if (node.Kind == SyntaxKind.NameExpression)
        {
            // A leading `::` starts the path at file scope, which the first name sees by
            // already being part of a path.
            var path = false;
            var first = true;
            foreach (var token in node.ChildTokens)
            {
                if (token.Kind == SyntaxKind.ColonColon)
                {
                    path = true;
                }
                else if (token.Kind is SyntaxKind.Identifier or SyntaxKind.CheapLocal
                    or SyntaxKind.Register or SyntaxKind.Mnemonic)
                {
                    into.Add(new Use(token, scope, path, first, Last: false, Word: words, Chosen: chosen));
                    path = true;
                    first = false;
                }
            }

            // Which part is the last decides where an export is checked: another file has to
            // have exported the `inner` of `outer::inner`, not the `outer` that leads to it.
            if (!first)
                into[^1] = into[^1] with { Last = true };
            return;
        }
        foreach (var child in node.ChildNodes)
            CollectUses(child, into, words, chosen);
    }

    private Symbol? Declare(
        SyntaxToken name,
        SymbolKind kind,
        SyntaxNode? value = null,
        SyntaxNode? data = null,
        SyntaxNode? type = null,
        IReadOnlyList<SyntaxNode>? items = null,
        IReadOnlyList<SyntaxNode>? entries = null,
        bool follows = false)
    {
        // A member of a named type may be called after a register or a mnemonic: it is only
        // ever named through its type, as `Reg::x`, so there is nothing for it to shadow. Any
        // other reserved name is reported, and declared all the same, so that what uses it and
        // what counts it are not wrong a second time.
        if (kind != SymbolKind.Member && scope.Kind != ScopeKind.Type)
            CheckReservedWord(name);

        var cheap = name.Kind == SyntaxKind.CheapLocal;
        if (!cheap && kind != SymbolKind.MacroParameter && InABlockArgument)
        {
            Report(name.Span, $"`{name.Text}` is declared in a block argument, which may declare only "
                + "cheap locals: the macro it is given to may splice it in more than one place");
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
                    ? $"`{symbol.DisplayName}` is an `ident` parameter, and a body may not declare "
                        + "the name it stands for"
                    : $"`{symbol.DisplayName}` is already declared in this scope",
                new RelatedSpan(existing.DeclarationSpan, "declared here"));
        }
        symbols.Add(symbol);
        references.Add(new SymbolReference(symbol, name.Span, true));

        // A declaration written after `.export` exports what it declares; the parameters of an
        // exported macro or function are written inside it and are not declarations of it.
        var declaring = name.Parent.Kind == SyntaxKind.ImportItem ? name.Parent.Parent : name.Parent;
        if (declaring?.ExportToken is { } export)
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
        Report(name.Span, $"`{name.Text}` is a cheap local, which needs an enclosing `.proc` or `.scope`");
        return fileScope;
    }

    /// <summary>
    /// The reserved words: a symbol may not be named after a register, or after a mnemonic of
    /// the CPU the program is built for. Members of a named struct, union or enum are exempt.
    /// </summary>
    private bool CheckReservedWord(SyntaxToken name)
    {
        var what = name.Kind switch
        {
            SyntaxKind.Mnemonic when SyntaxFacts.LongBranches.Contains(name.Text) || Instructions.Has(cpu, name.Text) =>
                $"a mnemonic of the {CpuNames.Spell(cpu)}",
            SyntaxKind.Register => "a register name",
            _ => null,
        };
        if (what is null)
        {
            WarnAboutOtherCpusMnemonic(name);
            return true;
        }
        Report(name.Span, $"`{name.Text}` is {what} and cannot be used as a name");
        return false;
    }

    /// <summary>
    /// A mnemonic of a CPU this program is not built for may name a symbol here, and the same
    /// name in a program built for that CPU cannot (§4). A module reached by a glob above the
    /// project root belongs to every project that takes it, so the name that builds here fails
    /// there. Saying so where the name is declared costs nothing; finding out when the module is
    /// shared costs a rename across a library.
    /// </summary>
    private void WarnAboutOtherCpusMnemonic(SyntaxToken name)
    {
        if (name.Kind != SyntaxKind.Mnemonic)
            return;
        var having = CpuNames.All.Where(other => Instructions.Has(other, name.Text)).Select(CpuNames.Spell).ToList();
        if (having.Count == 0)
            return;
        Warn(name.Span, $"`{name.Text}` is a mnemonic on the "
            + (having.Count == 1 ? having[0] : string.Join(", ", having.SkipLast(1)) + " and " + having[^1])
            + ", and cannot name a symbol in a program built for one of those: "
            + "a module shared with one will not build");
    }

    private void ResolveUses(IReadOnlyList<Use> list)
    {
        Place? previous = null;
        var broken = false;
        var steps = new List<(int Reference, Scope At, Symbol Symbol, SyntaxToken Token)>();
        foreach (var use in list)
        {
            var (token, at, _, first, last, splice, _, _) = use;
            if (first)
            {
                previous = null;
                broken = false;
                steps.Clear();
            }
            else if (broken)
            {
                // The part before this one did not resolve, and has been reported. What the
                // rest of the path would mean is unanswerable, not wrong.
                continue;
            }

            // A name in a value `.select` may leave out means something only if it is chosen.
            var reported = diagnostics.Count;
            previous = Resolve(use, previous);
            if (use.Chosen)
                diagnostics.RemoveRange(reported, diagnostics.Count - reported);
            if (previous is not { IsReported: false } place)
            {
                broken = true;
                continue;
            }
            if (place.Symbol is not { } symbol)
            {
                if (last)
                {
                    Report(token.Span, $"`{place.Module}` is a module: a name in it is written `{place.Module}::name`");
                    broken = true;
                }
                continue;
            }
            var inMacro = RecordBodyUse(at, symbol, token, last);
            if (!last)
                steps.Add((references.Count, at, symbol, token));
            references.Add(new SymbolReference(symbol, token.Span, false, place.IsAlias, IsStep: !last, InMacro: inMacro));

            // A path to a member is an offset into what it walks through, which it therefore uses:
            // `oam::x` is the address of `oam` plus the offset of `x`.
            if (last && symbol.Kind == SymbolKind.Member)
            {
                foreach (var step in steps)
                {
                    references[step.Reference] = references[step.Reference] with { IsStep = false };
                    RecordBodyUse(step.At, step.Symbol, step.Token, last: true);
                }
            }
            if (splice && symbol.Parameter is not { Kind: ParameterKind.Block })
            {
                Report(token.Span, $"`{token.Text}` is a {symbol.KindText}; a name written on its "
                    + "own splices a `block` parameter, and nothing else belongs on a line alone");
            }
        }
    }

    /// <summary>
    /// A name a macro body uses that it neither declared nor was given. An expansion needs it
    /// wherever it lands, so the macro remembers it: the file that calls the macro brings it
    /// in, and an exported macro may only use what is exported too.
    /// </summary>
    /// <returns>Whether the name is written in a macro body.</returns>
    private static bool RecordBodyUse(Scope at, Symbol used, SyntaxToken token, bool last)
    {
        Scope? body = null;
        for (var around = at; around is not null; around = around.Parent)
        {
            if (around.Kind == ScopeKind.Macro)
            {
                body = around;
                break;
            }
        }
        if (body?.Owner is not { } macro)
            return false;

        // What the body declares, and the parameters it was given, travel with it. A step on a
        // path is walked through, and only what the path leads to is used.
        for (var owner = used.Scope; owner is not null; owner = owner.Parent)
        {
            if (owner == body)
                return true;
        }
        if (last && !macro.Uses.Any(seen => seen.Used == used))
            macro.Uses.Add((used, token.Parent.Tree.GetSpan(token.Span)));
        return true;
    }

    /// <summary>
    /// What one part of a written name means. <paramref name="previous"/> is what the part
    /// before it resolved to, so a path walks into a scope or a module instead of looking
    /// outward again.
    /// </summary>
    private Place? Resolve(Use use, Place? previous)
    {
        var (token, at, path, _, last, _, word, _) = use;
        if (token.Kind == SyntaxKind.CheapLocal)
        {
            if (path)
            {
                Report(token.Span, $"`{token.Text}` is a cheap local and cannot be reached with `::`");
                return null;
            }
            var local = at.LookupCheapLocal(token.Text[1..]);
            if (local is null)
            {
                var near = NearestName(at, token.Text[1..], cheap: true);
                Report(token.Span, $"`{token.Text}` is not declared" + (near is null ? "" : $"; `@{near}` is"));
                if (near is not null)
                    Fixed(new DiagnosticFix(FixKind.NearestName, "@" + near));
            }
            return local is null ? null : new Place(local);
        }

        if (!path)
        {
            // A register or a mnemonic parses as a name so that a macro body may pass it as a
            // word. Outside one it can only be a mistake, and saying which reserved word it
            // is beats saying the name is not declared.
            // A reserved name that was declared anyway has been reported where it was declared.
            if (at.Lookup(token.Text) is { } symbol)
                return new Place(symbol);
            if (!word && !CheckReservedWord(token))
                return null;
            if (Outside(token, last, report) is { } found)
                return found;

            // In a condition a bare name may be a word rather than a name at all, and a word
            // is compared, never looked up.
            if (!word)
                ReportUndeclared(token, last, at);
            return null;
        }

        // A leading `::` starts at the root of the modules.
        if (previous is not { } before)
            return ModuleRoot(token, report);
        if (before.Module is { } prefix)
            return InModule(token, prefix, last, report);

        // A part after `::`: the scope to look in is the one the part before it opened.
        var container = BodyOf(before.Symbol!);
        if (container is null)
        {
            Report(token.Span, $"`{before.Symbol!.DisplayName}` is a {before.Symbol.KindText}, not a scope");
            return null;
        }

        var member = container.FindMember(token.Text);
        if (member is null)
        {
            // A repetition's name at the end of a path means the member of that scope with
            // the same spelling, which is a different member on every turn: each turn works
            // out which.
            if (last && at.Lookup(token.Text) is { Kind: SymbolKind.Binding } binding)
                return new Place(binding);
            Report(token.Span, $"`{token.Text}` is not declared in `{container.Name}`");
            return null;
        }
        return new Place(CheckExported(token, member, last));
    }

    /// <summary>
    /// What a name the scopes around it do not declare means: what a <c>.use</c> brought in, a
    /// define, the first part of a module's path, or what a <c>.use module::*</c> brought in.
    /// </summary>
    private Place? Outside(SyntaxToken token, bool last, Action<TextSpan, string>? report)
    {
        if (used.TryGetValue(token.Text, out var brought))
            return brought with { IsAlias = brought.Symbol is { } target && target.Name != token.Text };
        if (program.Define(token.Text) is { } define)
            return new Place(define);
        if (!last && IsModulePath(token.Text))
            return new Place(null, token.Text);

        // A module on its own is no value, so a name a `*` brought in is what one standing
        // alone means; with nothing else, it is the module, which is reported as one.
        Place? chosen = null;
        foreach (var module in globs)
        {
            if (program.Member(module, token.Text, Touch) is not { } exported
                || exported.Tree == module.Tree && !exported.IsExported || chosen?.Symbol == exported)
            {
                continue;
            }
            if (chosen is { } other)
            {
                report?.Invoke(token.Span, $"`{token.Text}` is exported by both `{other.From}` and `{module.Name}`, "
                    + $"and a `.use` brings in everything each exports: `{module.Name}::{token.Text}` says which");
                return Place.Reported;
            }
            chosen = new Place(exported, From: module.Name);
        }
        return chosen ?? (last && IsModulePath(token.Text) ? new Place(null, token.Text) : null);
    }

    /// <summary>
    /// A name no scope, <c>.use</c> or define gives any meaning, and the modules that export one
    /// like it. One that starts a path, <paramref name="last"/> being false, is most likely a
    /// module the build does not have, such as one left off the command line.
    /// </summary>
    private void ReportUndeclared(SyntaxToken token, bool last, Scope at)
    {
        lookedUp.Add("name:" + token.Text);
        var exporting = program.ModulesExporting(token.Text).ToList();
        var nearest = exporting.Count == 0 && last ? NearestName(at, token.Text, cheap: false) : null;
        Report(token.Span, exporting.Count > 0
            ? $"`{token.Text}` is not declared here, and module `{exporting[0]}` exports it: "
                + $"write `{exporting[0]}::{token.Text}`, or bring it in with `.use {exporting[0]}::{token.Text}`"
            : last
                ? $"`{token.Text}` is not declared" + (nearest is null ? "" : $"; `{nearest}` is")
                : $"`{token.Text}` is not declared, and no module `{token.Text}` is in this build");
        if (exporting.Count > 0)
            Fixed(new DiagnosticFix(FixKind.Use, $"{exporting[0]}::{token.Text}"));
        else if (nearest is not null)
            Fixed(new DiagnosticFix(FixKind.NearestName, nearest));
    }

    /// <summary>
    /// The declared name a written one is nearly: one in scope, or one a <c>.use</c> brought in,
    /// that differs from it by a letter or two.
    /// </summary>
    private string? NearestName(Scope at, string written, bool cheap) =>
        Spelling.Nearest(written, Candidates(at, cheap));

    /// <summary>The names a misspelling could have meant: what the scopes around it hold, and what a <c>.use</c> named.</summary>
    private IEnumerable<string> Candidates(Scope at, bool cheap)
    {
        for (var scope = at; scope is not null; scope = scope.Parent)
        {
            foreach (var symbol in scope.Symbols)
            {
                if (symbol.IsCheapLocal == cheap)
                    yield return symbol.Name;
            }
        }
        if (!cheap)
        {
            foreach (var name in used.Keys)
                yield return name;
        }
    }

    /// <summary>The first part of a path written from the root of the modules.</summary>
    private Place? ModuleRoot(SyntaxToken token, Action<TextSpan, string>? report)
    {
        if (IsModulePath(token.Text))
            return new Place(null, token.Text);
        report?.Invoke(token.Span, $"no module `{token.Text}` is in this build");
        return null;
    }

    /// <summary>The part after <paramref name="prefix"/>, which is a module or the start of one's name.</summary>
    private Place? InModule(SyntaxToken token, string prefix, bool last, Action<TextSpan, string>? report)
    {
        var path = $"{prefix}::{token.Text}";
        if (IsModulePath(path))
            return new Place(null, path);
        if (program.ModuleNamed(prefix) is not { } module)
        {
            report?.Invoke(token.Span, $"no module `{path}` is in this build");
            return null;
        }
        if (program.Member(module, token.Text, Touch) is not { } member)
        {
            report?.Invoke(token.Span, $"`{token.Text}` is not declared in module `{prefix}`");
            return null;
        }
        return new Place(report is null ? member : CheckExported(token, member, last));
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a module or the start of one's name. Which modules
    /// there are changes only when a file names a different one, which is read again whole.
    /// </summary>
    private bool IsModulePath(string path) => program.IsModulePath(path);

    /// <summary>Remembers that resolving this file looked for <paramref name="member"/>, written <c>module::name</c>.</summary>
    private void Touch(string member) => lookedUp.Add("member:" + member);

    /// <summary>
    /// A symbol another module declares may only be named if that module exports it. The
    /// check is on the last part of a name: <c>hw::outer::inner</c> needs <c>inner</c>
    /// exported, and <c>outer</c> is only the way in. The symbol is returned either way, so an
    /// editor can still go to a declaration that is private rather than missing.
    /// </summary>
    private Symbol CheckExported(SyntaxToken token, Symbol symbol, bool last)
    {
        // Said once, where the file first names it: every other use is the same mistake.
        if (!last || symbol.Tree == tree || symbol.IsExported || symbol.IsDefine || !unexported.Add(symbol))
            return symbol;
        Report(token.Span, $"`{symbol.PathName}` is not exported by module `{symbol.Module}`",
            new RelatedSpan(symbol.DeclarationSpan, "declared here"));
        Fixed(new DiagnosticFix(FixKind.Export, symbol.QualifiedName, symbol.DeclarationSpan));
        return symbol;
    }

    /// <summary>
    /// What a name may reach into. A routine or a scope opens its own; a member or an
    /// data declaration opens the one belonging to the type it names, which is what makes the
    /// fields of `.type T` data reachable through it.
    /// </summary>
    private Scope? BodyOf(Symbol symbol)
    {
        // A macro has a body scope, but it is not one a path may reach into: what a body
        // declares is local to each expansion, so there is no one symbol to name from
        // outside.
        if (symbol.Kind == SymbolKind.Macro)
            return null;
        if (symbol.Body is { } own)
            return own;
        if (symbol.TypeExpression is null || !resolving.Add(symbol))
            return null;
        var type = TypeOf(symbol);
        resolving.Remove(symbol);
        return type?.Body;
    }

    /// <summary>
    /// The type a <c>.type</c> names, resolved from where it was written. This runs on demand
    /// rather than in order, because a name may reach into a type the file declares later.
    /// Nothing is reported from here: the names in the type are uses like any others, and are
    /// reported where they are resolved.
    /// </summary>
    private Symbol? TypeOf(Symbol symbol)
    {
        if (symbol.Type is { } known)
            return known;
        if (symbol.TypeExpression is not { } named)
            return null;

        Place? part = null;
        var path = false;
        var tokens = named.ChildTokens;
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Kind == SyntaxKind.ColonColon)
            {
                path = true;
                continue;
            }
            var last = i == tokens.Length - 1;
            part = !path ? (symbol.Scope.Lookup(token.Text) is { } local ? new Place(local) : Outside(token, last, null))
                : part is null ? ModuleRoot(token, null)
                : part.Value.Module is { } prefix ? InModule(token, prefix, last, null)
                : BodyOf(part.Value.Symbol!)?.FindMember(token.Text) is { } member ? new Place(member)
                : null;
            path = true;
            if (part is null or { IsReported: true })
                return null;
        }
        symbol.Type = part?.Symbol;
        return symbol.Type;
    }

    /// <summary>
    /// <c>.module name</c>: once, before the file's other items. A file is one module, so what
    /// it declares belongs to one path however the file is laid out.
    /// </summary>
    private void BindModule(SyntaxNode statement)
    {
        var parts = statement.ChildTokens.Skip(1)
            .Where(token => token.Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic).ToList();
        if (parts.Count == 0)
            return;
        var span = new TextSpan(parts[0].Span.Start, parts[^1].Span.End - parts[0].Span.Start);
        if (moduleName is not null)
            Report(span, "a file is one module, and names it once");
        else if (pastFirstItem || scope != fileScope)
            Report(statement.ChildTokens[0].Span, "`.module` comes first: the file's other items belong to the module it names");
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
    private void BindUse(SyntaxNode statement)
    {
        if (scope != fileScope)
        {
            Report(statement.ChildTokens[0].Span, "`.use` belongs at the top level of a module");
            return;
        }
        useDirectives.Add(statement);
        if (!statement.IsExported)
            return;
        var (path, glob, items, alias) = UseParts(statement);
        if (path.Count == 0 || glob)
            return;
        if (items.Count == 0)
        {
            reexports.Add(new ProgramSymbols.Reexport((alias ?? path[^1]).Text, [.. path.Select(part => part.Text)]));
            return;
        }
        foreach (var (name, itemAlias) in items)
            reexports.Add(new ProgramSymbols.Reexport((itemAlias ?? name).Text, [.. path.Select(part => part.Text), name.Text]));
    }

    /// <summary>
    /// What a <c>.use</c> is written as: the path, whether it ends <c>::*</c>, the names in its
    /// braces with the names they are brought in as, and the name after <c>as</c>.
    /// </summary>
    private static (List<SyntaxToken> Path, bool Glob, List<(SyntaxToken Name, SyntaxToken? Alias)> Items, SyntaxToken? Alias)
        UseParts(SyntaxNode statement)
    {
        var path = new List<SyntaxToken>();
        var glob = false;
        SyntaxToken? alias = null;
        var tokens = statement.ChildTokens;
        for (var i = 1; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Kind == SyntaxKind.Star)
                glob = true;
            else if (token.Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic
                && (i == 1 || tokens[i - 1].Kind == SyntaxKind.ColonColon))
                path.Add(token);
            else if (token.Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic
                && tokens[i - 1].Text.Equals("as", StringComparison.OrdinalIgnoreCase))
                alias = token;
        }
        var items = new List<(SyntaxToken Name, SyntaxToken? Alias)>();
        foreach (var item in statement.ChildNodes.Where(child => child.Kind == SyntaxKind.UseItem))
        {
            if (item.ChildTokens.Length == 0)
                continue;
            items.Add((item.ChildTokens[0], item.ChildTokens.Length > 2 ? item.ChildTokens[2] : null));
        }
        return (path, glob, items, alias);
    }

    /// <summary>
    /// Resolves a <c>.use</c>: its path from the root of the modules, and each name it brings
    /// in. A name it brings in may not also be declared in the module, because then which one a
    /// use of it meant would depend on a rule rather than on what is written.
    /// </summary>
    private void ResolveUse(SyntaxNode statement)
    {
        var (path, glob, items, alias) = UseParts(statement);
        if (path.Count == 0)
            return;
        Place? place = null;
        for (var i = 0; i < path.Count && (i == 0 || place is not null); i++)
        {
            var last = i == path.Count - 1 && !glob && items.Count == 0;
            place = i == 0 ? ModuleRoot(path[i], report)
                : place!.Value.Module is { } prefix ? InModule(path[i], prefix, last, report)
                : BodyOf(place.Value.Symbol!)?.FindMember(path[i].Text) is { } member ? new Place(CheckExported(path[i], member, last))
                : NotIn(path[i], place.Value.Symbol!);
            if (place?.Symbol is { } symbol)
                references.Add(new SymbolReference(symbol, path[i].Span, false, InUse: true));
        }
        if (place is not { } target)
            return;

        if (glob)
        {
            if (statement.IsExported)
            {
                Report(statement.Span, "`.export .use` names what it re-exports: a `*` would make everything the other "
                    + "module exports, now and later, part of this one");
            }
            else if (target.Module is { } name && program.ModuleNamed(name) is { } module)
            {
                globs.Add(module);
            }
            else
            {
                Report(path[^1].Span, $"`.use {target.Module ?? target.Symbol!.PathName}::*` brings in what a module exports, "
                    + $"and `{path[^1].Text}` is {(target.Module is null ? "not a module" : "only the start of a module's name")}");
            }
            return;
        }
        if (items.Count == 0)
        {
            BringIn(alias ?? path[^1], target, alias is not null, statement.IsExported);
            return;
        }
        foreach (var (name, itemAlias) in items)
        {
            var found = target.Module is { } prefix ? InModule(name, prefix, last: true, report)
                : BodyOf(target.Symbol!)?.FindMember(name.Text) is { } member ? new Place(CheckExported(name, member, last: true))
                : NotIn(name, target.Symbol!);
            if (found is not { } item)
                continue;
            if (item.Symbol is { } symbol)
                references.Add(new SymbolReference(symbol, name.Span, false, InUse: true));
            BringIn(itemAlias ?? name, item, itemAlias is not null, statement.IsExported);
        }
    }

    /// <summary>A part of a <c>.use</c> path that names nothing in the symbol before it.</summary>
    private Place? NotIn(SyntaxToken token, Symbol container)
    {
        Report(token.Span, container.Body is null && container.TypeExpression is null
            ? $"`{container.DisplayName}` is a {container.KindText}, not a scope"
            : $"`{token.Text}` is not declared in `{container.DisplayName}`");
        return null;
    }

    /// <summary>One name a <c>.use</c> brings in, under the name <paramref name="name"/> writes.</summary>
    private void BringIn(SyntaxToken name, Place target, bool renamed, bool exported)
    {
        if (exported && target.Symbol is null)
        {
            Report(name.Span, $"`{target.Module}` is a module, and `.export .use` re-exports a name in one");
            return;
        }
        if (renamed && target.Symbol is { } symbol)
            references.Add(new SymbolReference(symbol, name.Span, true, IsAlias: true, InUse: true));
        if (fileScope.FindMember(name.Text) is { } local)
        {
            Report(name.Span, $"`{name.Text}` is declared in this module, and a `.use` may not bring in another: "
                + $"`as` brings it in under a name of its own", new RelatedSpan(local.DeclarationSpan, "declared here"));
            return;
        }
        if (!used.TryAdd(name.Text, target))
        {
            Report(name.Span, $"a `.use` already brings in `{name.Text}`");
            return;
        }
        broughtAt[name.Text] = (name.Span, exported);
    }

    /// <summary>
    /// Works out what the file exports, once it has been read: each declaration written after
    /// <c>.export</c> and each name an <c>.export</c> list gives, and what exporting those
    /// spreads to. Only what the file declares is looked for, so this needs no other module.
    /// </summary>
    /// <summary>
    /// Reads what the file exports. It runs after the families are declared, because their
    /// instances are declarations of the file like any others and an <c>.export</c> before one
    /// exports every instance.
    /// </summary>
    public void Export()
    {
        if (moduleName is null && !isDefines)
        {
            Report(new TextSpan(0, 0), "a file is a module, and says which first: `.module name`");
        }
        foreach (var (symbol, at) in exportedDeclarations)
            Export(symbol, at, linkerName: null, size: null);
        foreach (var (item, around) in exportItems)
        {
            if (item.ChildNodes.FirstOrDefault() is not { } name || Declared(name, around) is not { } symbol)
                continue;
            AddressSize? size = null;
            string? linkerName = null;
            var tokens = item.ChildTokens;
            for (var i = 0; i < tokens.Length; i++)
            {
                if (tokens[i].Kind == SyntaxKind.Identifier && i > 0 && tokens[i - 1].Kind == SyntaxKind.Colon)
                    size = SegmentNames.ParseSize(tokens[i].Text);
                if (tokens[i].Kind == SyntaxKind.StringLiteral)
                    linkerName = tokens[i].Text.Trim('"');
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
    private static Symbol? Declared(SyntaxNode name, Scope around)
    {
        Symbol? symbol = null;
        foreach (var token in name.ChildTokens)
        {
            if (token.Kind == SyntaxKind.ColonColon)
                continue;
            symbol = symbol is null ? around.Lookup(token.Text) : symbol.Body?.FindMember(token.Text);
            if (symbol is null)
                return null;
        }
        return symbol;
    }

    private void Report(TextSpan span, string message, params RelatedSpan[] related) =>
        diagnostics.Add(new Diagnostic(tree.GetSpan(span), Severity.Error, message, related));

    private void Warn(TextSpan span, string message) =>
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
        SyntaxNode Block, SyntaxNode? Walked, Symbol Binding, Scope Around, string? Segment, string? Why);

    /// <summary>A declaration named by a repetition's binding, waiting for the enum it walks to be known.</summary>
    private sealed record PendingFamily(
        SyntaxNode Declaration, Repeated Each, Scope Body, SymbolKind Kind,
        SyntaxNode? Signature, SyntaxNode? Data, SyntaxNode? Type);

    /// <summary>One call, waiting for the whole program to be read before it is matched up.</summary>
    /// <param name="Call">The call.</param>
    /// <param name="Scope">The scope it was written in, which its arguments resolve in.</param>
    /// <param name="Inside">The macro whose body holds it, or null when it is called outright.</param>
    private readonly record struct Invocation(SyntaxNode Call, Scope Scope, Symbol? Inside);

    /// <summary>What a part of a name resolved to: a symbol, or a module or the start of one's name.</summary>
    /// <param name="Symbol">The symbol, or null for a module path.</param>
    /// <param name="Module">The module path, when it is one.</param>
    /// <param name="IsAlias">Whether the name was written as the name a <c>.use ... as</c> gave the symbol.</param>
    /// <param name="From">The module whose <c>.use module::*</c> brought the symbol in, when one did.</param>
    private readonly record struct Place(Symbol? Symbol, string? Module = null, bool IsAlias = false, string? From = null)
    {
        /// <summary>A name that means nothing, which has been reported as such.</summary>
        public static Place Reported => default;

        /// <summary>Whether this is <see cref="Reported"/>.</summary>
        public bool IsReported => Symbol is null && Module is null;
    }
}
