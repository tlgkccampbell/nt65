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

    internal SemanticModel(
        SyntaxTree tree,
        SegmentTable segments,
        Configuration configuration,
        Binder.Result bound,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> declared,
        IReadOnlyList<Symbol> expanded,
        IEnumerable<Diagnostic> fromTheProgram,
        Func<string, long?>? binaryLength = null)
    {
        this.declared = declared;
        this.binaryLength = binaryLength;
        Tree = tree;
        Segments = segments;
        Configuration = configuration;
        FileScope = bound.FileScope;
        Symbols = bound.Symbols;
        References = bound.References;
        this.resolved = resolved;

        Diagnostics = Norristown.Diagnostics.Ordered(bound.Diagnostics.Concat(fromTheProgram));
        bySymbol = References.ToLookup(reference => reference.Symbol);
        // A struct member is written out as the number it is, so it is no symbol to the
        // linker either, any more than a define is.
        // A macro this file calls is expanded into it, so what its body uses is named in this
        // file's output and has to be brought in here, exactly as if the file had written it.
        ExternalSymbols = [.. References
            .Where(reference => !reference.IsDeclaration)
            .Select(reference => reference.Symbol)
            .Concat(expanded)
            .Where(symbol => symbol.Tree != tree && !symbol.IsDefine
                && symbol.Kind is not (SymbolKind.Member or SymbolKind.Macro or SymbolKind.MacroParameter))
            .Distinct()];
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

    /// <summary>Every place <paramref name="symbol"/> is written, its declaration included.</summary>
    public IReadOnlyList<SymbolReference> ReferencesTo(Symbol symbol) => [.. bySymbol[symbol]];

    /// <summary>
    /// What an expression is worth, for an editor to show. <paramref name="on"/> is the turn
    /// of the repetition it was written in, whose bindings it may name.
    /// </summary>
    public Value ValueOf(SyntaxNode expression, Expansion? on = null) =>
        Evaluator.ValueOf(expression, Segments, resolved, BindingsOf(on));

    /// <summary>The symbol a written name stands for, or null when it names none.</summary>
    public Symbol? SymbolOf(SyntaxNode name) => Evaluator.SymbolNamed(name, resolved);

    /// <summary>
    /// How much room a data directive takes: the bytes it generates and how many elements
    /// they are. Null where nt65 cannot say, such as for an <c>.align</c>.
    /// </summary>
    public DataSize? RoomFor(SyntaxNode directive, Expansion? on = null) =>
        Evaluator.DataSizeOf(directive, Segments, resolved, binaryLength, BindingsOf(on));

    /// <summary>
    /// Evaluates an expression and reports what is wrong with it into
    /// <paramref name="diagnostics"/>. Used for the operands of a data directive, which no
    /// symbol holds and which nothing else would ever evaluate with anything to say.
    /// </summary>
    public void Check(SyntaxNode expression, List<Diagnostic> diagnostics, Expansion? on = null) =>
        Evaluator.Check(expression, Segments, resolved, diagnostics, binaryLength, BindingsOf(on));

    /// <summary>The bytes an operand becomes: a literal, or text a charmap maps.</summary>
    public IReadOnlyList<long>? BytesOf(SyntaxNode operand, Expansion? on = null) =>
        Evaluator.BytesOf(operand, Segments, resolved, BindingsOf(on));

    /// <summary>The items an operand stands for when it names a list, or null when it does not.</summary>
    public IReadOnlyList<SyntaxNode>? ItemsOf(SyntaxNode operand) => Evaluator.ItemsOf(operand, resolved);

    /// <summary>
    /// The address size of an expression. <c>*</c> takes the size of
    /// <paramref name="segment"/>, or of the default segment when none is named.
    /// </summary>
    public AddressSize? AddressSizeOf(SyntaxNode expression, string? segment = null, Expansion? on = null) =>
        Evaluator.AddressSizeOf(
            expression, segment ?? SegmentTable.DefaultSegment, Segments, resolved, BindingsOf(on));

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
                bound.TryAdd(name, new Expansion.Bound(level.Value, level.Item));
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
                    _ => new Expansion.Bound(Value.Unknown, argument.Value, argument),
                });
            }
        }
        return bound;
    }

    /// <summary>
    /// The word a <c>one</c> parameter stands for. An argument that names another
    /// <c>one</c> parameter passes that one's word on, which is how a macro hands a word it
    /// was given to the macro it calls.
    /// </summary>
    private Value WordFor(MacroArgument argument, Expansion? outer)
    {
        if (argument.Value is { Kind: SyntaxKind.NameExpression } name
            && SymbolOf(name) is { Kind: SymbolKind.MacroParameter } passed
            && ArgumentFor(passed, outer) is { } given)
        {
            return WordFor(given, outer);
        }
        return argument.Word is { } word ? Value.Word(word) : Value.Unknown;
    }

    /// <summary>The macro a call names, wherever in the program the call was written.</summary>
    public Symbol? MacroAt(SyntaxNode call) =>
        Macros.CalleeOf(call) is { } callee
            ? resolved.GetValueOrDefault((callee.Parent.Tree, callee.Span.Start)) is { Kind: SymbolKind.Macro } macro
                ? macro
                : null
            : null;

    /// <summary>
    /// What one call gives each parameter. Nothing is reported from here: binding has
    /// already said everything there is to say about this call's arguments.
    /// </summary>
    public MacroInvocation? InvocationAt(SyntaxNode call) =>
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
}
