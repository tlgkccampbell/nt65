using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What one file means: its scopes and declarations, what every name in it refers to,
/// and what its expressions are worth.
/// <para>
/// A model is built once and is then read-only, so an editor may ask it anything from any
/// thread. A file is part of a program: a name it does not declare may be one another
/// file exports, so models are built together by <see cref="ProgramModel"/>.
/// </para>
/// </summary>
public sealed class SemanticModel
{
    private readonly IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved;
    private readonly IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> declared;
    private readonly ILookup<Symbol, SymbolReference> bySymbol;
    private readonly Func<string, long?>? binaryLength;
    private readonly IReadOnlyList<(TextSpan Span, Scope Scope)> regions;
    private readonly Dictionary<StatementSyntax, Family> byFamily;

    // What the file may name in the other modules, and the places its `.use` items reach,
    // which together are what a lookup at a position is answered from.
    private readonly ProgramSymbols program;
    private readonly IReadOnlyDictionary<string, Place> used;

    internal SemanticModel(
        SyntaxTree tree,
        SegmentTable segments,
        Configuration configuration,
        ProgramSymbols program,
        Binder.Result bound,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> declared,
        IReadOnlyList<Symbol> expanded,
        IEnumerable<Diagnostic> fromTheProgram,
        IReadOnlySet<string> namedUnexported,
        Func<string, long?>? binaryLength = null)
    {
        NamedUnexported = namedUnexported;
        this.declared = declared;
        this.binaryLength = binaryLength;
        this.program = program;
        used = bound.Used;
        Tree = tree;
        Segments = segments;
        Configuration = configuration;
        FileScope = bound.FileScope;
        Symbols = bound.Symbols;
        References = bound.References;
        this.resolved = resolved;
        regions = bound.Regions;
        Brought = bound.Brought;
        Globs = bound.Globs;
        Families = bound.Families;
        byFamily = bound.Families.ToDictionary(family => family.Declaration);

        Diagnostics = Norristown.Diagnostics.Ordered(bound.Diagnostics.Concat(fromTheProgram));
        bySymbol = References.ToLookup(reference => reference.Symbol);
        // A struct member is written out as the number it is, so it is no symbol to the
        // linker either, any more than a define is.
        // A macro this file calls is expanded into it, so what its body uses is named in this
        // file's output and has to be brought in here, exactly as if the file had written it.
        Used = [.. References
            .Where(reference => reference is { IsDeclaration: false, InUse: false, IsStep: false, InMacro: false })
            .Select(reference => reference.Symbol)
            .Concat(expanded)
            .Concat(Namesakes(References))
            .Distinct()];
        ExternalSymbols = [.. Used
            .Where(symbol => symbol.Tree != tree && !symbol.IsDefine && !symbol.IsConfig
                && symbol.Kind is not (SymbolKind.Member or SymbolKind.Macro or SymbolKind.MacroParameter))];
    }

    /// <summary>The file this model is of.</summary>
    public SyntaxTree Tree { get; }

    /// <summary>The program's segments, which is what address sizes come from.</summary>
    public SegmentTable Segments { get; }

    /// <summary>Which <c>.if</c> branches this build takes.</summary>
    public Configuration Configuration { get; }

    /// <summary>The file's top-level scope.</summary>
    public Scope FileScope { get; }

    /// <summary>Every symbol the file declares, in source order.</summary>
    public IReadOnlyList<Symbol> Symbols { get; }

    /// <summary>Every place a name is written, declarations included, ordered by position.</summary>
    public IReadOnlyList<SymbolReference> References { get; }

    /// <summary>What is wrong with the file's names and constants, ordered by line and column.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>
    /// The symbols this file names but another file declares, in the order it first names
    /// them. These are what its output imports; a define is not among them, because a
    /// define is written as its value and is no symbol to the linker.
    /// </summary>
    public IReadOnlyList<Symbol> ExternalSymbols { get; }

    /// <summary>
    /// What this file declares and does not export, and another file writes all the same, by
    /// qualified name. That file is told the name is not exported; this one is not also told
    /// that nothing uses it, which would be the same mistake reported twice.
    /// </summary>
    internal IReadOnlySet<string> NamedUnexported { get; }

    /// <summary>
    /// Every symbol this file's output uses, in the order it first names them: what its code and
    /// data name, and what the bodies of the macros it calls name. A path uses what it leads to
    /// and not the steps on the way, and a macro body is used where it is called.
    /// </summary>
    public IReadOnlyList<Symbol> Used { get; }

    /// <summary>
    /// The names the file's <c>.use</c> items bring in, as it writes them: each stands for a
    /// symbol, or for a module path it may write <c>::</c> after.
    /// </summary>
    public IReadOnlyDictionary<string, BroughtName> Brought { get; }

    /// <summary>The modules everything of whose exports a <c>.use module::*</c> brings in.</summary>
    public IReadOnlyList<ProgramSymbols.Module> Globs { get; }

    /// <summary>
    /// The families the file declares: each is one declaration per member of the enum it
    /// walks, written once and standing for all of them.
    /// </summary>
    public IReadOnlyList<Family> Families { get; }

    /// <summary>The family <paramref name="declaration"/> stands for, or null when it stands for one name.</summary>
    public Family? FamilyAt(StatementSyntax declaration) => byFamily.GetValueOrDefault(declaration);

    /// <summary>
    /// The declaration <paramref name="header"/> makes at <paramref name="on"/>: the one
    /// instance of a family the turn writes out, or the one name it declares anywhere else.
    /// </summary>
    public Symbol? DeclaredBy(SyntaxNode header, Expansion? on)
    {
        if (header is StatementSyntax declaration && byFamily.GetValueOrDefault(declaration) is { } family)
            return family.InstanceAt(on);
        foreach (var token in header.ChildTokens)
        {
            // A missing token declares nothing, and it starts where the token after it does, so
            // taking one would answer with whatever is declared there.
            if (!token.IsMissing && token.Kind is SyntaxKind.Identifier or SyntaxKind.CheapLocal
                or SyntaxKind.Register or SyntaxKind.Mnemonic)
            {
                return SymbolAt(token);
            }
        }
        return null;
    }

    /// <summary>Builds the model for <paramref name="tree"/> alone, seeing no other file.</summary>
    public static SemanticModel Create(SyntaxTree tree, SegmentTable segments) =>
        ProgramModel.Create([tree], segments).Files[0];

    /// <summary>
    /// What the name written at <paramref name="token"/> means, wherever in the program it
    /// was written. A macro body is expanded in every file that calls it, so the lines being
    /// written out may belong to a file other than the one being emitted, and what its names
    /// mean is the program's answer rather than any one file's.
    /// </summary>
    public Symbol? SymbolAt(SyntaxToken token)
    {
        var at = (token.Parent.Tree, token.Span.Start);
        return declared.GetValueOrDefault(at) ?? resolved.GetValueOrDefault(at);
    }

    /// <summary>The name written at <paramref name="position"/>, or null if there is none.</summary>
    public SymbolReference? ReferenceAt(int position)
    {
        // References do not overlap, so the last one starting at or before the position is
        // the only one that can hold it.
        var low = 0;
        var high = References.Count - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            var span = References[middle].Span;
            if (position < span.Start)
                high = middle - 1;
            else if (position > span.End)
                low = middle + 1;
            else
                return References[middle];
        }
        return null;
    }

    /// <summary>
    /// The innermost scope <paramref name="position"/> is in: the file's, or that of the routine,
    /// scope, type, macro or repetition whose block holds it. A block's first line is its
    /// opener, which is written in the scope around it, so a position there is outside it.
    /// </summary>
    public Scope ScopeAt(int position)
    {
        var found = FileScope;
        foreach (var (span, scope) in regions)
        {
            if (position >= LineAfter(span.Start) && position <= span.End)
                found = scope;
        }
        return found;

        int LineAfter(int start)
        {
            var line = Tree.GetLineIndex(start) + 1;
            return line < Tree.LineStarts.Length ? Tree.LineStarts[line] : Tree.Text.Length;
        }
    }

    /// <summary>
    /// Every name that may be written alone at <paramref name="position"/>, each with what it
    /// means there, in the order a lookup tries them: what the scopes out to the file declare,
    /// the nearest first, what <c>.use</c> brought in, the defines, and what a
    /// <c>.use module::*</c> brings in. Where two share a name the first is what the name
    /// means, which is the rule the binder resolves the file by.
    /// <para>
    /// A module is not one of them: it is the start of a path rather than a name that stands
    /// for something. <see cref="GetSymbolInfo(int, IReadOnlyList{string}, bool)"/> answers a
    /// path, module or not.
    /// </para>
    /// </summary>
    public IEnumerable<(string Name, SymbolInfo Means)> LookupNames(int position) =>
        Lookup.InScope(ScopeAt(position), program, used, Globs)
            .Select(found => (found.Name, found.Means.Means));

    /// <summary>
    /// The symbols a name written alone at <paramref name="position"/> could stand for, the
    /// one the binder would choose first. With no <paramref name="name"/>, every symbol in
    /// scope there, in the same order; a cheap local answers to its <c>@</c> name, as it is
    /// written.
    /// </summary>
    public IReadOnlyList<Symbol> LookupSymbols(int position, string? name = null) =>
        [.. LookupNames(position)
            .Where(found => (name is null || found.Name == name) && found.Means.Symbol is not null)
            .Select(found => found.Means.Symbol!)
            .Distinct()];

    /// <summary>
    /// What a path written at <paramref name="position"/> means, each part as it is spelled:
    /// the symbol it reaches, the module it stops at, or nothing. <paramref name="fromRoot"/>
    /// says it starts at the root of the modules, as the path of a <c>.use</c> does.
    /// <para>
    /// The path is given as text rather than as a node, because the question is asked of a
    /// line being typed as much as of one the file parsed. What it answers is what binding
    /// the same name would answer, from the same lookup.
    /// </para>
    /// </summary>
    public SymbolInfo GetSymbolInfo(int position, IReadOnlyList<string> path, bool fromRoot = false)
    {
        var at = ScopeAt(position);
        Place? found = null;
        for (var i = 0; i < path.Count; i++)
        {
            var last = i == path.Count - 1;
            found = i == 0
                ? fromRoot
                    ? Lookup.ModuleRoot(path[0], program)
                    : at.Lookup(path[0]) is { } local
                        ? new Place(local)
                        : Lookup.Outside(path[0], last, program, used, Globs)
                : found!.Value.Module is { } prefix
                    ? Lookup.InModule(path[i], prefix, program)
                    : Lookup.BodyOf(found.Value.Symbol!)?.FindMember(path[i]) is { } member
                        ? new Place(member)
                        : null;
            if (found is null or { IsReported: true })
                return SymbolInfo.None;
        }
        return found?.Means ?? SymbolInfo.None;
    }

    /// <summary>
    /// What a name written in the file means. <paramref name="on"/> is the turn it is written
    /// on, for a path that ends in a repetition's name.
    /// </summary>
    public SymbolInfo GetSymbolInfo(SyntaxNode name, Expansion? on = null) => new(SymbolOf(name, on));

    /// <summary>Every place <paramref name="symbol"/> is written, its declaration included.</summary>
    public IReadOnlyList<SymbolReference> ReferencesTo(Symbol symbol) => [.. bySymbol[symbol]];

    /// <summary>
    /// What an expression is worth, for an editor to show. <paramref name="on"/> is the turn
    /// of the repetition it was written in, whose bindings it may name.
    /// <para>
    /// <paramref name="spans"/> answers how many bytes a routine or a data declaration takes,
    /// for a caller that has laid the file out; without it a span is simply unknown, as an
    /// address is. The model stays read-only either way: what only layout knows is supplied by
    /// whoever asks rather than kept here, and <paramref name="cycles"/> answers what one pass
    /// over a span of code costs the same way.
    /// </para>
    /// </summary>
    public Value ValueOf(
        SyntaxNode expression, Expansion? on = null, Func<Symbol, long?>? spans = null,
        Func<Symbol, Symbol, bool, CycleSpan>? cycles = null) =>
        Evaluator.ValueOf(expression, Segments, resolved, BindingsOf(on), spans, cycles);

    /// <summary>
    /// The symbol a written name stands for, or null when it names none. <paramref name="on"/>
    /// is the turn it is written on, for a path that ends in a repetition's name.
    /// </summary>
    public Symbol? SymbolOf(SyntaxNode name, Expansion? on = null) =>
        Evaluator.SymbolNamed(name, resolved, BindingsOf(on));

    /// <summary>
    /// How much room a data directive takes: the bytes it generates and how many elements
    /// they are. Null where nt65 cannot say, such as for an <c>.align</c>.
    /// </summary>
    public DataSize? RoomFor(StatementSyntax directive, Expansion? on = null) =>
        Evaluator.DataSizeOf(directive, Segments, resolved, binaryLength, BindingsOf(on), Configuration);

    /// <summary>
    /// How many elements an element type's count declares, and how many its values come to.
    /// Either may be unknown, and where both are known they have to agree.
    /// </summary>
    public (long? Declared, long? Given) ElementsOf(DataDirectiveSyntax directive, Expansion? on = null) =>
        Evaluator.ElementsOf(directive, Segments, resolved, BindingsOf(on), Configuration);

    /// <summary>
    /// Evaluates an expression and reports what is wrong with it into
    /// <paramref name="diagnostics"/>. Used for the operands of a data directive, which no
    /// symbol holds and which nothing else would ever evaluate with anything to say.
    /// </summary>
    public void Check(
        SyntaxNode expression, List<Diagnostic> diagnostics, Expansion? on = null,
        Func<Symbol, long?>? spans = null, Func<Symbol, Symbol, bool, CycleSpan>? cycles = null) =>
        Evaluator.Check(
            expression, Segments, resolved, diagnostics, binaryLength, BindingsOf(on), spans, cycles);

    /// <summary>The bytes an operand becomes: a literal, or text a charmap maps.</summary>
    public IReadOnlyList<long>? BytesOf(SyntaxNode operand, Expansion? on = null) =>
        Evaluator.BytesOf(operand, Segments, resolved, BindingsOf(on));

    /// <summary>The items an operand stands for when it names a list, or null when it does not.</summary>
    public IReadOnlyList<SyntaxNode>? ItemsOf(SyntaxNode operand) => Evaluator.ItemsOf(operand, resolved);

    /// <summary>
    /// The address size of an expression. <c>*</c> takes the size of
    /// <paramref name="segment"/>, and has none outside every segment.
    /// </summary>
    public AddressSize? AddressSizeOf(SyntaxNode expression, string? segment = null, Expansion? on = null) =>
        Evaluator.AddressSizeOf(expression, segment, Segments, resolved, BindingsOf(on));

    /// <summary>
    /// What every name bound at <paramref name="on"/> and at the levels around it stands
    /// for: a repetition's name on this turn, and a macro's parameters at this expansion.
    /// The innermost level wins where two share a name, which two never do.
    /// <para>
    /// This is worked out each time rather than kept, because an expansion is identified by
    /// the call it expands and nothing derived from it, so that two walkers that reach the
    /// same line agree about which writing of it they are on.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<Symbol, Expansion.Bound>? BindingsOf(Expansion? on)
    {
        if (on is null)
            return null;
        var bound = new Dictionary<Symbol, Expansion.Bound>();
        for (var level = on; level is not null; level = level.Outer)
        {
            if (level.Binding is { } name)
            {
                bound.TryAdd(name, new Expansion.Bound(level.Value, level.Item, Member: level.Member));
                continue;
            }
            if (level.Call is not { } call || InvocationAt(call) is not { } invocation)
                continue;
            foreach (var argument in invocation.Arguments)
            {
                // A `one` stands for the word it was given, which only a comparison reads. A
                // `list` and a `block` stand for no value at all, and the built-ins that ask
                // about them read the argument itself.
                bound.TryAdd(argument.Parameter.Symbol, argument.Parameter.Kind switch
                {
                    ParameterKind.One => new Expansion.Bound(WordFor(argument, level.Outer), null, argument),
                    ParameterKind.List or ParameterKind.Block =>
                        new Expansion.Bound(Value.Unknown, null, argument),

                    // A member stands for its value.
                    ParameterKind.Enum when MemberFor(argument, level.Outer) is { } member =>
                        new Expansion.Bound(member.Value, null, argument, member),
                    _ => new Expansion.Bound(Value.Unknown, argument.Value, argument),
                });
            }
        }
        return bound;
    }

    /// <summary>
    /// What a path ending in a repetition's name reaches: <c>reset::b</c>, where <c>b</c> walks
    /// an enum, names a member of <c>reset</c> on every turn, so the output has to be able to
    /// reach each of them and a member another module declares has to be imported.
    /// </summary>
    private static IEnumerable<Symbol> Namesakes(IReadOnlyList<SymbolReference> references)
    {
        for (var i = 1; i < references.Count; i++)
        {
            if (references[i] is { IsDeclaration: false, Symbol.Kind: SymbolKind.Binding }
                && references[i - 1].Symbol.Body is { } container)
            {
                foreach (var member in container.Symbols)
                    yield return member;
            }
        }
    }

    /// <summary>
    /// The word a <c>one</c> parameter stands for. An argument that names another
    /// <c>one</c> parameter passes that one's word on, which is how a macro hands a word it
    /// was given to the macro it calls.
    /// </summary>
    private Value WordFor(MacroArgument argument, Expansion? outer)
    {
        if (argument.Value is NameExpressionSyntax name
            && SymbolOf(name) is { Kind: SymbolKind.MacroParameter } passed
            && ArgumentFor(passed, outer) is { } given)
        {
            return WordFor(given, outer);
        }
        return argument.Word is { } word ? Value.Word(word) : Value.Unknown;
    }

    /// <summary>The enum an enum kind names, where the macro that declares the parameter is written; null when it names none.</summary>
    public Symbol? EnumOf(ArgumentKind kind) =>
        kind.Enum is { } name && SymbolOf(name) is { Kind: SymbolKind.Enum } named ? named : null;

    /// <summary>
    /// The member of its enum an argument of an enum kind names, read where the caller stands
    /// at <paramref name="caller"/>: a bare name among the enum's members first, since that is
    /// what the parameter takes, then a path to one, then what a parameter of the same kind
    /// that it passes on was given. Null when it names no member of the enum.
    /// </summary>
    public Symbol? MemberFor(MacroArgument argument, Expansion? caller) =>
        MemberOf(argument.Parameter.Accepts, argument.Value, caller);

    /// <summary>The same, for one expression written for an enum kind: an argument, or an item of a <c>list</c> of one.</summary>
    public Symbol? MemberOf(ArgumentKind kind, SyntaxNode? written, Expansion? caller)
    {
        if (EnumOf(kind) is not { Body: { } members } || written is not NameExpressionSyntax name)
            return null;
        if (name is { Names.Length: 1, GlobalToken: null, SimpleName: { Kind: SyntaxKind.Identifier } word }
            && members.FindMember(word.Text) is { Kind: SymbolKind.Constant } bare)
        {
            return bare;
        }
        var symbol = SymbolOf(name);
        if (symbol is { Kind: SymbolKind.MacroParameter, Parameter.Kind: ParameterKind.Enum } passed
            && GivenAt(passed, caller) is { } given)
        {
            return MemberOf(passed.Parameter.Accepts, given.Argument.Value, given.Caller) is { } member
                && member.Scope == members
                    ? member
                    : null;
        }
        return symbol is { Kind: SymbolKind.Constant } found && found.Scope == members ? found : null;
    }

    /// <summary>The macro a call names, wherever in the program the call was written.</summary>
    public Symbol? MacroAt(MacroCallSyntax call) =>
        Macros.CalleeOf(call) is { } callee
            ? resolved.GetValueOrDefault((callee.Parent.Tree, callee.Span.Start)) is { Kind: SymbolKind.Macro } macro
                ? macro
                : null
            : null;

    /// <summary>
    /// What one call gives each parameter. Nothing is reported from here: binding has
    /// already said everything there is to say about this call's arguments.
    /// </summary>
    public MacroInvocation? InvocationAt(MacroCallSyntax call) =>
        MacroAt(call) is { } macro ? MacroInvocation.Of(call, macro, call.Tree, null) : null;

    /// <summary>
    /// What <paramref name="parameter"/> was given at <paramref name="on"/>. The expansions
    /// are searched outwards, because a body may name a parameter of a macro that called it
    /// only by having been given it as an argument, never by seeing it.
    /// </summary>
    public MacroArgument? ArgumentFor(Symbol parameter, Expansion? on)
    {
        for (var level = on; level is not null; level = level.Outer)
        {
            if (level.Call is { } call && InvocationAt(call)?.For(parameter) is { } argument)
                return argument;
        }
        return null;
    }

    /// <summary>
    /// The same, with the level the argument was written at. What a call gave is the
    /// caller's own expression, so anything read from it — the label it names, how wide an
    /// address it is — is read where the caller stands rather than inside the body.
    /// </summary>
    public (MacroArgument Argument, Expansion? Caller)? GivenAt(Symbol parameter, Expansion? on)
    {
        for (var level = on; level is not null; level = level.Outer)
        {
            if (level.Call is { } call && InvocationAt(call)?.For(parameter) is { } argument)
                return (argument, level.Outer);
        }
        return null;
    }
}
