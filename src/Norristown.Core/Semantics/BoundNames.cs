using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Holds the names the binder resolved and what each name bound at one place currently stands
/// for, and looks names up through them.
/// <para>
/// A name is bound by a repetition to its current iteration, by a macro to what its parameter
/// was given, and by a function call to its argument's value. A lookup needs only these and the
/// resolved names, so a caller that asks what a name refers to reads it here without evaluating
/// anything. An <see cref="Evaluator"/> keeps one of these and updates the bindings as it walks
/// into repetitions and function bodies.
/// </para>
/// </summary>
internal sealed class BoundNames
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BoundNames"/> class, with each name in
    /// <paramref name="bound"/> taking what it is bound to there.
    /// </summary>
    public BoundNames(
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null)
    {
        Resolved = resolved;

        // A repetition's binding takes its value for the current iteration, just as a
        // function's parameter takes its argument's.
        foreach (var (symbol, value) in bound ?? new Dictionary<Symbol, Expansion.Bound>())
        {
            if (value.Argument is { } argument)
                Given[symbol] = argument;
            if (value.Member is { } member)
                Members[symbol] = member;
            if (value.Item is { } item)
                Items[symbol] = item;
            else
                Values[symbol] = value.Value;
        }
    }

    /// <summary>
    /// Gets the symbol each resolved name refers to, keyed by its file and position. The file is
    /// part of the key, because a position in one file means something else in another.
    /// </summary>
    public IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> Resolved { get; }

    /// <summary>
    /// Gets the value that each bound name has, such as a function's parameter in the call being
    /// evaluated or a repetition's index.
    /// </summary>
    public Dictionary<Symbol, Value> Values { get; } = [];

    /// <summary>
    /// Gets the list item that each name an <c>.each</c> bound stands for. Such a name is replaced
    /// by the item itself wherever it appears, not just by the item's value.
    /// </summary>
    public Dictionary<Symbol, SyntaxNode> Items { get; } = [];

    /// <summary>
    /// Gets the enum member each repetition binding is bound to in the current iteration. A path
    /// that ends in the binding reaches the container's member of that enum member's name.
    /// </summary>
    public Dictionary<Symbol, Symbol> Members { get; } = [];

    /// <summary>
    /// Gets what each macro parameter was given, for the built-ins that ask about the argument
    /// rather than about its value.
    /// </summary>
    public Dictionary<Symbol, MacroArgument> Given { get; } = [];

    /// <summary>
    /// Returns the symbol that a node refers to when the node is a name, or null otherwise,
    /// because only a name can refer to a symbol.
    /// </summary>
    public Symbol? SymbolOf(SyntaxNode node) => node is NameExpressionSyntax name ? SymbolOf(name, out _) : null;

    /// <summary>
    /// Returns the symbol a name resolved to, taken from the last resolved part of the path,
    /// which is what the name refers to.
    /// </summary>
    /// <param name="name">The name to look up.</param>
    /// <param name="problem">
    /// The problem to report when a path through a repetition binding names nothing, with the
    /// name to report it at, or null when there is none.
    /// </param>
    public Symbol? SymbolOf(NameExpressionSyntax name, out (NameExpressionSyntax At, DiagnosticMessage Message)? problem)
    {
        problem = null;

        // A name bound to a list item is that item. `.each handlers, h` makes `h` the label it
        // stands for, with that label's address size and everything else about it.
        if (BoundItem(name) is NameExpressionSyntax item)
            return SymbolOf(item, out problem);

        // A name with a leading `::` is already a path, so a binding in its first part
        // is treated as one in a later part would be.
        var reached = name.GlobalToken is not null;
        var names = name.Names;
        for (var i = names.Length - 1; i >= 0; i--)
        {
            if (Resolved.TryGetValue((name.Tree, names[i].Span.Start), out var symbol))
            {
                return (i > 0 || reached) && symbol.Kind == SymbolKind.Binding
                    ? Namesake(name, i, symbol, out problem)
                    : symbol;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the item a name is bound to in the current iteration, or null when it is bound to
    /// none.
    /// </summary>
    public SyntaxNode? BoundItem(NameExpressionSyntax name)
    {
        if (Items.Count == 0 || name.SimpleName is not { } only)
            return null;
        return Resolved.TryGetValue((name.Tree, only.Span.Start), out var symbol)
            && Items.TryGetValue(symbol, out var item)
            ? item
            : null;
    }

    /// <summary>
    /// Returns the symbol that a one-part name was resolved to at its position, without
    /// substituting what a binding gave it.
    /// </summary>
    public Symbol? Parameter(SyntaxNode name) =>
        name is NameExpressionSyntax { Names.Length: 1, SimpleName: { } only } nameExpression
        && Resolved.TryGetValue((nameExpression.Tree, only.Span.Start), out var symbol)
            ? symbol
            : null;

    /// <summary>
    /// Returns the argument that a name's macro parameter was given, or null when the name refers
    /// to no parameter. When the argument is another macro's parameter, it is followed to the
    /// argument that parameter was given. Any other argument, even a plain name, is returned as
    /// it is and not followed further.
    /// </summary>
    public MacroArgument? Argument(SyntaxNode name)
    {
        var argument = Parameter(name) is { } parameter ? Given.GetValueOrDefault(parameter) : null;
        for (var steps = 0; steps < 64 && argument?.Value is NameExpressionSyntax passed; steps++)
        {
            if (Parameter(passed) is not { Kind: SymbolKind.MacroParameter } outer
                || !Given.TryGetValue(outer, out var next))
            {
                break;
            }
            argument = next;
        }
        return argument;
    }

    /// <summary>
    /// Returns the expression inside the operand that the call passed as <c>p</c> in
    /// <c>.exprof(p)</c>, such as <c>5</c> for <c>{#5}</c> or <c>ptr</c> for <c>{(ptr),y}</c>.
    /// When the call passed another macro's <c>operand</c> parameter, this is the expression
    /// inside the operand that parameter was passed. Returns null when <c>p</c> is not an
    /// <c>operand</c> parameter.
    /// </summary>
    public SyntaxNode? ExprOf(CallExpressionSyntax call) =>
        OperandOf(call) is { } operand ? Operands.ExpressionOf(operand) : null;

    /// <summary>
    /// Returns the operand that the call passed as <c>p</c> in <c>.exprof(p)</c>, following
    /// another macro's <c>operand</c> parameter to the operand that parameter was passed. Returns
    /// null when <c>p</c> is not an <c>operand</c> parameter.
    /// </summary>
    public SyntaxNode? OperandOf(CallExpressionSyntax call)
    {
        var arguments = call.Arguments.Arguments;
        if (arguments.Count != 1)
            return null;
        SyntaxNode? operand = null;
        SyntaxNode? inner = arguments[0];
        for (var steps = 0; steps < 64 && inner is NameExpressionSyntax; steps++)
        {
            if (Argument(inner) is not { Parameter.Kind: ParameterKind.Operand, Operand: { } passed })
                break;
            operand = passed;
            inner = Operands.ExpressionOf(passed);
        }
        return operand;
    }

    /// <summary>
    /// Returns the symbol that <c>actions::c</c> names when <c>c</c> walks an enum. This is the
    /// member of <c>actions</c> named after the enum member that <c>c</c> is bound to in the
    /// current iteration. Outside any iteration, as when an editor asks, it names nothing.
    /// </summary>
    private Symbol? Namesake(
        NameExpressionSyntax name, int last, Symbol binding, out (NameExpressionSyntax At, DiagnosticMessage Message)? problem)
    {
        problem = null;
        Symbol? container = null;
        var names = name.Names;
        for (var i = last - 1; i >= 0 && container is null; i--)
            Resolved.TryGetValue((name.Tree, names[i].Span.Start), out container);
        if (container?.Body is not { } body)
            return null;

        if (!Members.TryGetValue(binding, out var member))
        {
            if (Items.ContainsKey(binding) || Values.ContainsKey(binding))
                problem = (name, Catalogue.BindingNotOverAnEnum.Message(binding.Name));
            return null;
        }
        if (body.FindMember(member.Name) is { } namesake)
            return namesake;
        problem = (name, Catalogue.FamilyMemberMissing.Message(container.Name, member.Name, binding.Name));
        return null;
    }
}
