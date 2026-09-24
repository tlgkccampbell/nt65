using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Represents what one file means: its scopes and declarations, what every name in it refers
/// to, and the values of its expressions.
/// <para>
/// A model is built once and is then read-only, so an editor may query it from any thread. A
/// file is part of a program, and a name it does not declare may be one that another file
/// exports, so models are built together by <see cref="ProgramModel"/>.
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

    // What the file may name in the other modules, and the places its `.use` items reach.
    // Together they answer a lookup at a position.
    private readonly ProgramSymbols program;
    private readonly IReadOnlyDictionary<string, Resolution> used;

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
        this.resolved = resolved;
        References = WithMembersNamedBare(bound.References);
        regions = bound.Regions;
        Brought = bound.Brought;
        Globs = bound.Globs;
        Families = bound.Families;
        byFamily = bound.Families.ToDictionary(family => family.Declaration);

        Diagnostics = Norristown.Diagnostics.Ordered(bound.Diagnostics.Concat(fromTheProgram));
        bySymbol = References.ToLookup(reference => reference.Symbol);
        // A struct member is emitted as its number, so, like a setting, it is not a symbol to the
        // linker and is left out of the external symbols below.
        // A macro this file calls is expanded into it, so what its body uses is named in this
        // file's output and must be brought in here, exactly as if the file had named it itself.
        Used = [.. References
            .Where(reference => reference is { IsDeclaration: false, InUse: false, IsStep: false, InMacro: false })
            .Select(reference => reference.Symbol)
            .Concat(expanded)
            .Concat(Namesakes(References))
            .Distinct()];
        ExternalSymbols = [.. Used
            .Where(symbol => symbol.Tree != tree && !symbol.IsSetting
                && symbol.Kind is not (SymbolKind.Member or SymbolKind.Macro or SymbolKind.MacroParameter))];
    }

    /// <summary>Gets the file this model describes.</summary>
    public SyntaxTree Tree { get; }

    /// <summary>Gets the program's segments, from which address sizes come.</summary>
    public SegmentTable Segments { get; }

    /// <summary>Gets the configuration that decides which <c>.if</c> branches this build takes.</summary>
    public Configuration Configuration { get; }

    /// <summary>Gets the file's top-level scope.</summary>
    public Scope FileScope { get; }

    /// <summary>Gets every symbol the file declares, in source order.</summary>
    public IReadOnlyList<Symbol> Symbols { get; }

    /// <summary>Gets every reference to a name in the file, including declarations, ordered by position.</summary>
    public IReadOnlyList<SymbolReference> References { get; }

    /// <summary>
    /// Gets the diagnostics for the file's names and constants, ordered by line and column.
    /// </summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>
    /// Gets the symbols this file names but another file declares, in the order it first names
    /// them. These are what its output imports. Settings are not included, because a setting is
    /// emitted as its value and is not a symbol to the linker.
    /// </summary>
    public IReadOnlyList<Symbol> ExternalSymbols { get; }

    /// <summary>
    /// Gets every symbol this file's output uses, in the order the file first names them. These
    /// are what its code and data name, and what the bodies of the macros it calls name. A path
    /// uses only what it leads to, not the steps on the way, and what a macro body names is used
    /// by the file that calls the macro.
    /// </summary>
    public IReadOnlyList<Symbol> Used { get; }

    /// <summary>
    /// Gets the names the file's <c>.use</c> items bring in, keyed as they appear in the file.
    /// Each refers to a symbol, or to a module path that the file may follow with <c>::</c>.
    /// </summary>
    public IReadOnlyDictionary<string, BroughtName> Brought { get; }

    /// <summary>Gets every module whose exports a <c>.use module::*</c> brings in.</summary>
    public IReadOnlyList<ProgramSymbols.Module> Globs { get; }

    /// <summary>
    /// Gets the families the file declares. Each <see cref="Family"/> is declared once in the
    /// source and covers one declaration per member of the enum it iterates over.
    /// </summary>
    public IReadOnlyList<Family> Families { get; }

    /// <summary>
    /// Gets the qualified names this file declares and does not export but that another file
    /// uses anyway. That file reports that the name is not exported. This file does not also
    /// report that nothing uses the name, which would report the same mistake twice.
    /// </summary>
    internal IReadOnlySet<string> NamedUnexported { get; }

    /// <summary>Builds the model for <paramref name="tree"/> alone, seeing no other file.</summary>
    public static SemanticModel Create(SyntaxTree tree, SegmentTable segments) =>
        ProgramModel.Create([tree], segments).Files[0];

    /// <summary>
    /// Returns the family <paramref name="declaration"/> declares, or null when it declares a
    /// single name.
    /// </summary>
    public Family? FamilyAt(StatementSyntax declaration) => byFamily.GetValueOrDefault(declaration);

    /// <summary>
    /// Returns the symbol <paramref name="header"/> declares in the expansion
    /// <paramref name="on"/>. For a family, this is the instance the current iteration emits;
    /// otherwise it is the one name the header declares.
    /// </summary>
    public Symbol? DeclaredBy(SyntaxNode header, Expansion? on)
    {
        if (header is StatementSyntax declaration && byFamily.GetValueOrDefault(declaration) is { } family)
            return family.InstanceAt(on);
        foreach (var token in header.ChildTokens)
        {
            // A missing token declares nothing, and it starts where the token after it does, so
            // using a missing token would return whatever is declared at that position.
            if (!token.IsMissing && token.Kind is SyntaxKind.Identifier or SyntaxKind.CheapLocal
                or SyntaxKind.Register or SyntaxKind.Mnemonic)
            {
                return SymbolAt(token);
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the symbol the name at <paramref name="token"/> refers to, in whichever file of the
    /// program the token appears. A macro body is expanded in every file that calls it, so the
    /// lines being emitted may belong to a file other than the one being emitted. What their
    /// names mean is therefore answered for the whole program, not for any one file.
    /// </summary>
    public Symbol? SymbolAt(SyntaxToken token)
    {
        var at = (token.Parent.Tree, token.Span.Start);
        return declared.GetValueOrDefault(at) ?? resolved.GetValueOrDefault(at);
    }

    /// <summary>Returns the reference at <paramref name="position"/>, or null if there is none.</summary>
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
    /// Returns the innermost scope containing <paramref name="position"/>. This is the file's
    /// scope, or that of the routine, scope, type, macro or repetition whose block contains the
    /// position. A block's first line is its opener, which belongs to the scope around the
    /// block, so a position on that line is outside the block.
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

        int LineAfter(int start) => Tree.GetLineEnd(Tree.GetLineIndex(start));
    }

    /// <summary>
    /// Returns every name that may appear alone at <paramref name="position"/>, each with what it
    /// means there, in the order a lookup tries them. The order is what the scopes out to the
    /// file declare, nearest first, then what <c>.use</c> brought in, and then what a
    /// <c>.use module::*</c> brings in. Where two entries share a name, the first is what
    /// the name means, which is the rule the binder resolves the file by.
    /// <para>
    /// Modules are not included, because a module is the start of a path, not a name that refers
    /// to something. <see cref="GetSymbolInfo(int, IReadOnlyList{string}, bool)"/> resolves a
    /// path, whether or not it leads to a module.
    /// </para>
    /// </summary>
    public IEnumerable<(string Name, SymbolInfo Means)> LookupNames(int position) =>
        Lookup.InScope(ScopeAt(position), program, used, Globs)
            .Select(found => (found.Name, found.Means.Means));

    /// <summary>
    /// Returns the symbols a name appearing alone at <paramref name="position"/> could refer to,
    /// with the one the binder would choose first. With no <paramref name="name"/>, returns every
    /// symbol in scope there, in the same order. A cheap local matches its <c>@</c> name, as it
    /// appears in the source.
    /// </summary>
    public IReadOnlyList<Symbol> LookupSymbols(int position, string? name = null) =>
        [.. LookupNames(position)
            .Where(found => (name is null || found.Name == name) && found.Means.Symbol is not null)
            .Select(found => found.Means.Symbol!)
            .Distinct()];

    /// <summary>
    /// Returns what a path at <paramref name="position"/> means, given each part as it is
    /// spelled. The result is the symbol the path reaches, the module it stops at, or nothing.
    /// <paramref name="fromRoot"/> indicates that the path starts at the root of the modules, as
    /// the path of a <c>.use</c> does.
    /// <para>
    /// The path is given as text rather than as a node, because the question is asked about a
    /// line being typed as often as about a line the file parsed. The result is what binding the
    /// same name would produce, from the same lookup.
    /// </para>
    /// </summary>
    public SymbolInfo GetSymbolInfo(int position, IReadOnlyList<string> path, bool fromRoot = false)
    {
        if (path.Count == 0)
            return SymbolInfo.None;
        var start = Lookup.First(path[0], path.Count == 1, fromRoot, ScopeAt(position), program, used, Globs);
        return Lookup.Walk(start, path, program) is { IsReported: false } found ? found.Means : SymbolInfo.None;
    }

    /// <summary>
    /// Returns what a name in the file means. <paramref name="on"/> is the iteration the name is
    /// in, for a path that ends in a repetition's name.
    /// </summary>
    public SymbolInfo GetSymbolInfo(SyntaxNode name, Expansion? on = null) => new(SymbolOf(name, on));

    /// <summary>
    /// Returns every reference to <paramref name="symbol"/> in the file, including its declaration.
    /// </summary>
    public IReadOnlyList<SymbolReference> ReferencesTo(Symbol symbol) => [.. bySymbol[symbol]];

    /// <summary>
    /// Returns the value of an expression, for an editor to show. <paramref name="on"/> is the
    /// iteration of the repetition that contains the expression, whose bindings it may name.
    /// <para>
    /// <paramref name="spans"/> gives how many bytes a routine or a data declaration takes, for a
    /// caller that has laid the file out. Without it, a span is simply unknown, as an address is.
    /// The model stays read-only either way, because the caller supplies what only layout knows
    /// instead of the model keeping it. <paramref name="cycles"/> likewise gives what one pass
    /// over a span of code costs.
    /// </para>
    /// </summary>
    public Value ValueOf(
        SyntaxNode expression, Expansion? on = null, Func<Symbol, long?>? spans = null,
        Func<Symbol, Symbol, bool, CycleSpan>? cycles = null) =>
        Evaluator.ValueOf(expression, Segments, resolved, BindingsOf(on), spans, cycles, Configuration);

    /// <summary>
    /// Returns the value a <c>.select</c> or a <c>.switch</c> chooses, or null when
    /// <paramref name="node"/> is neither or what decides the choice is not a constant.
    /// <paramref name="on"/> is the expansion the node is read in.
    /// </summary>
    public SyntaxNode? ChosenBy(SyntaxNode node, Expansion? on = null) =>
        Evaluator.ChosenOf(node, Segments, resolved, BindingsOf(on), Configuration);

    /// <summary>
    /// Returns the symbol a name refers to, or null when it names none. <paramref name="on"/> is
    /// the iteration the name is in, for a path that ends in a repetition's name.
    /// </summary>
    public Symbol? SymbolOf(SyntaxNode name, Expansion? on = null) =>
        new BoundNames(resolved, BindingsOf(on)).SymbolOf(name);

    /// <summary>
    /// Returns how much room a data directive takes, which is the bytes it generates and the
    /// number of elements they form. Returns null where nt65 cannot tell, such as for an
    /// <c>.align</c>.
    /// </summary>
    public DataSize? RoomFor(StatementSyntax directive, Expansion? on = null) =>
        Evaluator.DataSizeOf(directive, Segments, resolved, binaryLength, BindingsOf(on), Configuration);

    /// <summary>
    /// Returns how many elements an element type's count declares, and how many its values come
    /// to. Either may be unknown, and where both are known they must agree.
    /// </summary>
    public (long? Declared, long? Given) ElementsOf(DataDirectiveSyntax directive, Expansion? on = null) =>
        Evaluator.ElementsOf(directive, Segments, resolved, BindingsOf(on), Configuration);

    /// <summary>
    /// Evaluates an expression and reports its problems into <paramref name="diagnostics"/>. It
    /// is used for the operands of a data directive, which are not any symbol's value and so
    /// would otherwise never be evaluated with their problems reported.
    /// </summary>
    public void Check(
        SyntaxNode expression, List<Diagnostic> diagnostics, Expansion? on = null,
        Func<Symbol, long?>? spans = null, Func<Symbol, Symbol, bool, CycleSpan>? cycles = null) =>
        Evaluator.Check(
            expression, Segments, resolved, diagnostics, binaryLength, BindingsOf(on), spans, cycles, Configuration);

    /// <summary>
    /// Returns the bytes an operand becomes, for a literal or for text that a charmap maps.
    /// </summary>
    public IReadOnlyList<long>? BytesOf(SyntaxNode operand, Expansion? on = null) =>
        Evaluator.BytesOf(operand, Segments, resolved, BindingsOf(on), Configuration);

    /// <summary>
    /// Returns the items of the list <paramref name="operand"/> names, or null when it does not
    /// name a list.
    /// </summary>
    public IReadOnlyList<SyntaxNode>? ItemsOf(SyntaxNode operand) =>
        new BoundNames(resolved).SymbolOf(operand) is { Kind: SymbolKind.List } list ? list.Items : null;

    /// <summary>
    /// Returns the address size of an expression. <c>*</c> takes the size of
    /// <paramref name="segment"/>, and has none outside every segment.
    /// </summary>
    public AddressSize? AddressSizeOf(SyntaxNode expression, string? segment = null, Expansion? on = null) =>
        Evaluator.AddressSizeOf(expression, segment, Segments, resolved, BindingsOf(on), Configuration);

    /// <summary>
    /// Returns what every name bound at <paramref name="on"/> and at the levels around it is
    /// bound to. These are a repetition's name in this iteration and a macro's parameters in this
    /// expansion. Where two levels bind the same name the innermost wins, though that never
    /// happens.
    /// <para>
    /// This is computed each time rather than kept, because an expansion is identified by the
    /// call it expands and by nothing derived from it. That way, two walkers that reach the same
    /// line agree about which <see cref="Expansion"/> of it they are on.
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
                // A `one` is bound to the word it was given, which only a comparison reads. A
                // `list` and a `block` have no value at all, and the built-ins that query them read
                // the argument itself.
                bound.TryAdd(argument.Parameter.Symbol, argument.Parameter.Kind switch
                {
                    ParameterKind.One => new Expansion.Bound(WordFor(argument, level.Outer), null, argument),
                    ParameterKind.List or ParameterKind.Block =>
                        new Expansion.Bound(Value.Unknown, null, argument),

                    // An enum parameter is bound to its member's value.
                    ParameterKind.Enum when MemberFor(argument, level.Outer) is { } member =>
                        new Expansion.Bound(member.Value, null, argument, member),
                    _ => new Expansion.Bound(Value.Unknown, argument.Value, argument),
                });
            }
        }
        return bound;
    }

    /// <summary>
    /// Returns, for <c>.exprof(p)</c> at <paramref name="on"/>, the expression inside the operand
    /// the call passed as <c>p</c>, such as <c>5</c> for <c>{#5}</c> or <c>ptr</c> for
    /// <c>{(ptr),y}</c>. Returns null when <c>p</c> is not an <c>operand</c> parameter.
    /// </summary>
    public SyntaxNode? ExprOf(CallExpressionSyntax call, Expansion? on) =>
        new BoundNames(resolved, BindingsOf(on)).ExprOf(call);

    /// <summary>
    /// Returns the enum an enum kind names, resolved where the macro that declares the parameter
    /// is declared, or null when it names none.
    /// </summary>
    public Symbol? EnumOf(ArgumentKind kind) =>
        kind.Enum is { } name && SymbolOf(name) is { Kind: SymbolKind.Enum } named ? named : null;

    /// <summary>
    /// Returns the member of its enum that an argument of an enum kind names, resolved in the
    /// caller's expansion <paramref name="caller"/>. A bare name among the enum's members is tried
    /// first, because that is what the parameter takes. Then a path to a member is tried. When
    /// the argument is itself an enum parameter, what that parameter was given is used. Returns
    /// null when the argument names no member of the enum.
    /// </summary>
    public Symbol? MemberFor(MacroArgument argument, Expansion? caller) =>
        MemberOf(argument.Parameter.Accepts, argument.Value, caller);

    /// <summary>
    /// Returns the member of the enum of <paramref name="kind"/> that <paramref name="argument"/>
    /// names, resolved in the caller's expansion <paramref name="caller"/>. The expression is an
    /// argument of an enum kind, or an item of a <c>list</c> of that kind. Returns null when it
    /// names no member of the enum.
    /// </summary>
    public Symbol? MemberOf(ArgumentKind kind, SyntaxNode? argument, Expansion? caller)
    {
        if (EnumOf(kind) is not { Body: { } members } || argument is not NameExpressionSyntax name)
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

    /// <summary>
    /// Returns the macro <paramref name="call"/> names, in whichever file of the program the call
    /// appears.
    /// </summary>
    public Symbol? MacroAt(MacroCallSyntax call) =>
        Macros.CalleeOf(call) is { } callee
            ? resolved.GetValueOrDefault((callee.Parent.Tree, callee.Span.Start)) is { Kind: SymbolKind.Macro } macro
                ? macro
                : null
            : null;

    /// <summary>
    /// Returns the argument <paramref name="call"/> gives each parameter. Nothing is reported
    /// from here, because binding has already reported every problem with the call's arguments.
    /// </summary>
    public MacroInvocation? InvocationAt(MacroCallSyntax call) =>
        MacroAt(call) is { } macro ? MacroInvocation.Of(call, macro, call.Tree, null) : null;

    /// <summary>
    /// Returns the argument <paramref name="parameter"/> was given at <paramref name="on"/>. The
    /// expansions are searched outward, because a body may name a parameter of a macro that
    /// called it, not by seeing that parameter but only by being given it as an argument.
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
    /// Returns the argument <paramref name="parameter"/> was given at <paramref name="on"/>,
    /// together with the expansion that contains the call that gave it. An argument is the caller's
    /// own expression, so anything read from it, such as the label it names or its address size,
    /// is read at the caller's level rather than inside the body.
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

    /// <summary>
    /// Returns the symbols a path ending in a repetition's name can reach. For example,
    /// <c>reset::b</c>, where <c>b</c> iterates over an enum, names a different member of
    /// <c>reset</c> in every iteration. The output must be able to reach each of them, and a
    /// member another module declares must be imported.
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
    /// Returns the word a <c>one</c> parameter was given. An argument that names another
    /// <c>one</c> parameter passes on that parameter's word, so a macro can hand a word it was
    /// given to the macro it calls.
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

    /// <summary>
    /// Returns the references the binder recorded, adjusted so that each enum member a call names
    /// by its bare name, as an argument of an enum kind, refers to that member. The binder reads
    /// such a name as a word, because it is resolved in the parameter's enum rather than in the
    /// caller's scope. The binder therefore records nothing for it, or records whatever the
    /// caller's scope has by that name. The member is what the argument means, and what an editor
    /// shows and renames.
    /// </summary>
    private IReadOnlyList<SymbolReference> WithMembersNamedBare(IReadOnlyList<SymbolReference> references)
    {
        var members = new List<SymbolReference>();
        foreach (var call in Tree.Root.DescendantNodes().OfType<MacroCallSyntax>())
        {
            if (MacroAt(call) is not { } macro
                || !macro.Parameters.Any(parameter => (parameter.Accepts.Element ?? parameter.Accepts).Kind == ParameterKind.Enum)
                || InvocationAt(call) is not { } invocation)
            {
                continue;
            }
            var inMacro = call.Ancestors().Any(node => node is BlockSyntax { Opener.Statement: MacroDeclarationSyntax });
            foreach (var argument in invocation.Arguments.Where(argument => argument.IsGiven))
            {
                var accepts = argument.Parameter.Accepts;
                var kind = accepts.Kind == ParameterKind.List ? accepts.Element : accepts;
                if (kind is not { Kind: ParameterKind.Enum } || EnumOf(kind) is not { Body: { } body })
                    continue;
                var givenNodes = accepts.Kind == ParameterKind.List ? argument.Items : argument.Value is { } value ? [value] : [];
                foreach (var name in givenNodes)
                {
                    if (name is NameExpressionSyntax { Names.Length: 1, GlobalToken: null, SimpleName: { Kind: SyntaxKind.Identifier } word }
                        && body.FindMember(word.Text) is { Kind: SymbolKind.Constant } member)
                    {
                        members.Add(new SymbolReference(member, word.Span, false, InMacro: inMacro));
                    }
                }
            }
        }
        if (members.Count == 0)
            return references;
        var at = members.Select(reference => reference.Span.Start).ToHashSet();
        return [.. references.Where(reference => !at.Contains(reference.Span.Start))
            .Concat(members)
            .OrderBy(reference => reference.Span.Start)];
    }
}
