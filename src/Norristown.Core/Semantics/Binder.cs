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
    private readonly List<(SyntaxNode Item, Scope Scope)> exportItems = [];
    private readonly List<Symbol> exported = [];

    // The file's `.use` items, what they bring in once resolved, and what it re-exports.
    private readonly List<SyntaxNode> useDirectives = [];
    private readonly Dictionary<string, Place> used = new(StringComparer.Ordinal);
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
        binder.Export();
        return binder;
    }

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
        references.Sort((a, b) => a.Span.Start.CompareTo(b.Span.Start));
        return new Result(fileScope, symbols, references, diagnostics);
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
        if (opener is not null)
            CheckAllowedHere(opener);
        var outerScope = scope;
        var outerSegment = segment;

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
                scope = OpenScope(ScopeKind.Proc, opener, SymbolKind.Proc);
                break;
            case BlockKind.Scope:
                scope = OpenScope(ScopeKind.Scope, opener, SymbolKind.Scope);
                break;
            case BlockKind.Segment:
            case BlockKind.Region:
                segment = SegmentOf(opener) ?? segment;
                break;
            case BlockKind.Data:
                scope = OpenData(opener, block);
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
                break;
            default:
                if (opener is not null)
                    BindStatement(opener);
                break;
        }

        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Green is GreenBlock inner)
                WalkBlock(lines[i], inner.BlockKind);
            else
                WalkLine(lines[i]);
        }

        scope = outerScope;
        segment = outerSegment;
        if (kind == BlockKind.Enum)
            previousEnumMember = null;
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
            symbol.Signature = ReadSignature(opener);
        var body = new Scope(kind, symbol?.Name ?? name.Text, scope, symbol);
        if (symbol is not null)
            symbol.Body = body;
        return body;
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
        CheckAllowedHere(statement);
        if (statement.Kind is not (SyntaxKind.BlankLine or SyntaxKind.ModuleDirective))
            pastFirstItem = true;
        CheckAnnotation(line, statement);
        var label = bareLabel;
        if (statement.Kind != SyntaxKind.BlankLine)
            bareLabel = null;
        BindStatement(statement);

        // Recorded on the label rather than found in the flow, so a jump from another file
        // can be checked against it too.
        if (statement.Kind == SyntaxKind.StateDirective && label is not null)
            label.StateDeclaration = statement;
    }

    private void BindStatement(SyntaxNode statement)
    {
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
                break;

            // A region line reached as a line is inside a block: one at file level opens the
            // region it names, and is walked as that block's opener.
            case SyntaxKind.SegmentRegion when statement.ChildTokens.Length > 0:
                Report(statement.ChildTokens[0].Span, "a `.segment NAME` region belongs at file level, outside "
                    + "every block: inside one, `.segment NAME { }` places what it holds");
                break;

            case SyntaxKind.DataValues:
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
        if (NameToken(statement) is { } name && element is not null)
        {
            Declare(name, SymbolKind.Data, data: element, type: DataSyntax.TypeOf(element));
        }
        CollectUses(element);
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

    /// <summary>An instruction belongs in a routine: outside one, nothing calls it or runs into it.</summary>
    private void CheckCodePlacement(SyntaxNode instruction)
    {
        if (Placement is ScopeKind.Proc or ScopeKind.Macro or ScopeKind.BlockArgument || instruction.ChildTokens.Length == 0)
            return;
        Report(instruction.ChildTokens[0].Span, Placement == ScopeKind.Data
            ? "an instruction belongs in a `.proc`, and `.data` holds only data"
            : "an instruction belongs in a `.proc`: code outside one is reached by nothing nt65 can follow");
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
            }
            return;
        }
        Report(name.Span, $"`{name.Text}` is a label outside a `.proc`: a label is only a position in code, "
            + $"and data is named by a declaration, `.data {name.Text.TrimStart('@')}: ...`");
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
            references.Add(new SymbolReference(symbol, callee.Span, false, place.IsAlias));
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

    private void CollectUses(SyntaxNode? node, List<Use> into, bool words = false)
    {
        if (node is null)
            return;
        // `.defined(NAME)` asks whether a name is a define. The name is not a use of
        // anything: one that is not declared is what the question is for.
        if (IsDefinedCall(node))
            return;
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
                    into.Add(new Use(token, scope, path, first, Last: false, Word: words));
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
            CollectUses(child, into, words);
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
            return true;
        Report(name.Span, $"`{name.Text}` is {what} and cannot be used as a name");
        return false;
    }

    private void ResolveUses(IReadOnlyList<Use> list)
    {
        Place? previous = null;
        var broken = false;
        foreach (var use in list)
        {
            var (token, at, _, first, last, splice, _) = use;
            if (first)
            {
                previous = null;
                broken = false;
            }
            else if (broken)
            {
                // The part before this one did not resolve, and has been reported. What the
                // rest of the path would mean is unanswerable, not wrong.
                continue;
            }

            previous = Resolve(use, previous);
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
            references.Add(new SymbolReference(symbol, token.Span, false, place.IsAlias));
            RecordBodyUse(at, symbol, token);
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
    private static void RecordBodyUse(Scope at, Symbol used, SyntaxToken token)
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
            return;

        // What the body declares, and the parameters it was given, travel with it.
        for (var owner = used.Scope; owner is not null; owner = owner.Parent)
        {
            if (owner == body)
                return;
        }
        if (!macro.Uses.Any(seen => seen.Used == used))
            macro.Uses.Add((used, token.Parent.Tree.GetSpan(token.Span)));
    }

    /// <summary>
    /// What one part of a written name means. <paramref name="previous"/> is what the part
    /// before it resolved to, so a path walks into a scope or a module instead of looking
    /// outward again.
    /// </summary>
    private Place? Resolve(Use use, Place? previous)
    {
        var (token, at, path, _, last, _, word) = use;
        if (token.Kind == SyntaxKind.CheapLocal)
        {
            if (path)
            {
                Report(token.Span, $"`{token.Text}` is a cheap local and cannot be reached with `::`");
                return null;
            }
            var local = at.LookupCheapLocal(token.Text[1..]);
            if (local is null)
                Report(token.Span, $"`{token.Text}` is not declared");
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
                ReportUndeclared(token);
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

    /// <summary>A name no scope, <c>.use</c> or define gives any meaning, and the modules that export one like it.</summary>
    private void ReportUndeclared(SyntaxToken token)
    {
        lookedUp.Add("name:" + token.Text);
        var exporting = program.ModulesExporting(token.Text).ToList();
        Report(token.Span, exporting.Count == 0
            ? $"`{token.Text}` is not declared"
            : $"`{token.Text}` is not declared here, and module `{exporting[0]}` exports it: "
                + $"write `{exporting[0]}::{token.Text}`, or bring it in with `.use {exporting[0]}::{token.Text}`");
    }

    /// <summary>The first part of a path written from the root of the modules.</summary>
    private Place? ModuleRoot(SyntaxToken token, Action<TextSpan, string>? report)
    {
        if (IsModulePath(token.Text))
            return new Place(null, token.Text);
        report?.Invoke(token.Span, $"there is no module `{token.Text}`");
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
            report?.Invoke(token.Span, $"there is no module `{path}`");
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
            Report(name.Span, $"a `.use` already brings in `{name.Text}`");
    }

    /// <summary>
    /// Works out what the file exports, once it has been read: each declaration written after
    /// <c>.export</c> and each name an <c>.export</c> list gives, and what exporting those
    /// spreads to. Only what the file declares is looked for, so this needs no other module.
    /// </summary>
    private void Export()
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

    /// <summary>What binding one file produced.</summary>
    /// <param name="FileScope">The file's top-level scope.</param>
    /// <param name="Symbols">Every symbol declared in the file, in source order.</param>
    /// <param name="References">Declarations and uses, ordered by position.</param>
    /// <param name="Diagnostics">What binding found wrong.</param>
    public sealed record Result(
        Scope FileScope,
        IReadOnlyList<Symbol> Symbols,
        IReadOnlyList<SymbolReference> References,
        List<Diagnostic> Diagnostics);

    /// <summary>One written name, waiting for the whole file to be read before it is resolved.</summary>
    /// <param name="Token">The name.</param>
    /// <param name="Scope">The scope it was written in.</param>
    /// <param name="Path">Whether a <c>::</c> comes before it, so it names a member of a scope.</param>
    /// <param name="First">Whether it is the first part of the name it belongs to.</param>
    /// <param name="Last">Whether it is the last part, and so the symbol the whole name stands for.</param>
    /// <param name="Splice">Whether the name stands alone on a line, and so splices a block.</param>
    /// <param name="Word">Whether it is written where a bare word may stand, and so may be one.</param>
    private readonly record struct Use(
        SyntaxToken Token, Scope Scope, bool Path, bool First, bool Last,
        bool Splice = false, bool Word = false);

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
