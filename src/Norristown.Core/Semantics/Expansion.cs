using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Represents an expansion, which is one emitted copy of a macro body or of a repetition's body.
/// Each iteration of a <c>.repeat</c> or an <c>.each</c> produces one expansion, as does each
/// call of a macro and each splice of a block argument. Expansions nest, and each one is a level
/// inside the expansion that encloses it.
/// <para>
/// The lines of a body are read once and emitted many times, so an expansion is what tells one
/// emitted copy from another. It records what each name it binds is bound to, and it identifies
/// the expansion that a line belongs to.
/// </para>
/// <para>
/// Layout and emission walk the same blocks in the same order and create expansions
/// independently, so expansions are compared by what they hold rather than by identity. They
/// hold only what identifies the expansion, such as an iteration's index or the call being
/// expanded, and never anything derived from it. Anything derived is recomputed each time it is
/// requested.
/// </para>
/// </summary>
public sealed class Expansion : IEquatable<Expansion>
{
    private readonly bool splice;

    private Expansion(
        Expansion? outer, Symbol? binding, Value value, SyntaxNode? item, int index,
        MacroCallSyntax? call, BlockSyntax? body, bool splice = false, Symbol? member = null, Symbol? indexBinding = null)
    {
        this.splice = splice;
        Member = member;
        IndexBinding = indexBinding;
        Outer = outer;
        Binding = binding;
        Value = value;
        Item = item;
        Index = index;
        Call = call;
        Body = body;
    }

    /// <summary>Gets the expansion that encloses this one, or null at the top level.</summary>
    public Expansion? Outer { get; }

    /// <summary>
    /// Gets the name a repetition binds, or null for a macro expansion and for a repetition that
    /// binds no name.
    /// </summary>
    public Symbol? Binding { get; }

    /// <summary>
    /// Gets the name an <c>.each</c> binds to the iteration's <see cref="Index"/>, or null when it
    /// binds none.
    /// </summary>
    public Symbol? IndexBinding { get; }

    /// <summary>Gets the value of the bound name in this iteration.</summary>
    public Value Value { get; }

    /// <summary>Gets the list item the name is bound to, for an <c>.each</c> over a list.</summary>
    public SyntaxNode? Item { get; }

    /// <summary>Gets the enum member the name is bound to, for an <c>.each</c> over an enum.</summary>
    public Symbol? Member { get; }

    /// <summary>Gets the zero-based index of the iteration, or zero for a macro expansion.</summary>
    public int Index { get; }

    /// <summary>Gets the call being expanded, or null for an iteration of a repetition.</summary>
    public MacroCallSyntax? Call { get; }

    /// <summary>
    /// Gets the block this expansion emits, which is a repetition's body, a macro's body, or the
    /// block argument a splice names. Declarations inside the block belong to this expansion, so
    /// a name declared there is distinguished from the same name in another expansion.
    /// </summary>
    public BlockSyntax? Body { get; }

    /// <summary>Gets the nesting depth of this expansion, which is used to stop a runaway expansion.</summary>
    public int Depth
    {
        get
        {
            var depth = 0;
            for (var level = this; level is not null; level = level.Outer)
                depth++;
            return depth;
        }
    }

    /// <summary>Gets the nearest macro call this expansion is inside, or null when it is inside none.</summary>
    public MacroCallSyntax? NearestCall
    {
        get
        {
            for (var level = this; level is not null; level = level.Outer)
            {
                if (level.Call is { } call)
                    return call;
            }
            return null;
        }
    }

    /// <summary>
    /// Creates the expansion for one iteration of a repetition, with the names it binds and the
    /// first name's value. The name an <c>.each</c> binds to the index, when it binds one, is
    /// worth <paramref name="index"/>.
    /// </summary>
    public static Expansion Iteration(
        Expansion? outer, BlockSyntax block, Symbol? binding, Value value, SyntaxNode? item, int index,
        Symbol? member = null, Symbol? indexBinding = null) =>
        new(outer, binding, value, item, index, null, block, member: member, indexBinding: indexBinding);

    /// <summary>Creates one expansion of the macro that <paramref name="call"/> names.</summary>
    public static Expansion Of(Expansion? outer, MacroCallSyntax call, BlockSyntax definition) =>
        new(outer, null, Value.Unknown, null, 0, call, definition);

    /// <summary>
    /// Creates the expansion for one splice of a block argument, identified by the line that
    /// spliced it. A block binds no name, so this expansion binds none. It exists because the same
    /// block may be spliced in more than one place, and each splice emits the block's lines again.
    /// </summary>
    public static Expansion Spliced(Expansion? outer, BlockSpliceSyntax splice, BlockSyntax block) =>
        new(outer, null, Value.Unknown, splice, 0, null, block, splice: true);

    /// <summary>
    /// Returns the expansion whose block contains the declaration of <paramref name="declared"/>,
    /// or null when none does and the name belongs to the file. This distinguishes one expansion's
    /// locals from another's, because the same body emitted twice declares two sets of locals.
    /// </summary>
    public static Expansion? Owning(Expansion? at, Symbol declared)
    {
        for (var level = at; level is not null; level = level.Outer)
        {
            if (level.Body is { } body && body.Tree == declared.Tree
                && declared.NameSpan.Start >= body.Position
                && declared.NameSpan.Start < body.FullSpan.End)
            {
                return level;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the expansions whose lines a line emitted in <paramref name="at"/> is part of, from
    /// the innermost out. A block argument is the caller's code, not the macro's, so from a splice
    /// the walk leaves out the macro whose body did the splicing and goes on from where that macro
    /// was called.
    /// </summary>
    public static IEnumerable<Expansion> Enclosing(Expansion? at)
    {
        for (var level = at; level is not null; level = level.Outer)
        {
            yield return level;
            if (level.splice && level.Item is { } line)
                level = SplicedBy(level, line) ?? level;
        }
    }

    /// <summary>
    /// Determines whether <paramref name="definition"/> is already being expanded in
    /// <paramref name="at"/> or in an expansion that encloses it. That happens when a macro
    /// expands itself, directly or indirectly. Such a macro is reported where it is declared and
    /// is never emitted again inside itself.
    /// <para>
    /// A block argument is the caller's code, not the macro's, so a call inside a block argument
    /// is not inside the macro the block was given to. For example,
    /// <c>if!(eq) { if!(ne) { ... } }</c> nests two calls and is not recursion. From a splice, the
    /// walk jumps straight out to the expansion of the macro whose body did the splicing.
    /// </para>
    /// </summary>
    public static bool Expanding(Expansion? at, BlockSyntax definition)
    {
        for (var level = at; level is not null; level = level.Outer)
        {
            if (level.Call is not null && level.Body == definition)
                return true;
            if (level.splice && level.Item is { } line)
                level = SplicedBy(level, line) ?? level;
        }
        return false;
    }

    /// <summary>
    /// Creates a diagnostic for a problem with the text at <paramref name="span"/> of
    /// <paramref name="tree"/>, found while laying out or emitting <paramref name="file"/> within
    /// the expansion <paramref name="at"/>. Text in the file itself is reported at its own
    /// position. Text in another file's macro body is reported at the nearest call in this file
    /// that expanded it, which this file controls, with the body's text as a related location.
    /// The body's position belongs to the other file, and this file's diagnostics should not move
    /// when that file is edited.
    /// </summary>
    public static Diagnostic Problem(
        SyntaxTree file, SyntaxTree tree, TextSpan span, Expansion? at, Severity? severity, DiagnosticMessage message)
    {
        if (tree != file)
        {
            for (var level = at; level is not null; level = level.Outer)
            {
                if (level.Call is { } call && call.Tree == file)
                {
                    return new Diagnostic(
                        file.GetSpan(call.Span), severity ?? message.Descriptor.Severity, message,
                        [new RelatedSpan(tree.GetSpan(span), "in the macro body")]);
                }
            }
        }
        return new Diagnostic(tree.GetSpan(span), severity ?? message.Descriptor.Severity, message);
    }

    /// <summary>
    /// Returns where a problem with a line of a macro body is reported, or null where
    /// <paramref name="node"/> is the text of <paramref name="file"/> itself. A block argument a
    /// call gives is the caller's own text, so a line in one is not a body line.
    /// </summary>
    /// <param name="node">The line, or a part of it, that has the problem.</param>
    /// <param name="on">The expansion the line was found in.</param>
    /// <param name="file">The file whose analysis found the problem.</param>
    /// <returns>
    /// The nearest call written in the file's own text, outside every macro body, which is the
    /// side that can change and so the place the problem is reported. <c>Holder</c> is the call
    /// of the macro whose body holds the line, which is the same call where it is not nested.
    /// <c>Level</c> is the expansion of the reported call, so that a caller can tell which lines
    /// that call expanded.
    /// </returns>
    public static (MacroCallSyntax Call, MacroCallSyntax Holder, Expansion Level)? BodyLine(
        SyntaxNode node, Expansion? on, SyntaxTree file)
    {
        MacroCallSyntax? holder = null;
        for (var level = on; level is not null && holder is null; level = level.Outer)
        {
            if (level.Call is { } call && level.Holds(node))
                holder = call;
        }
        if (holder is null && node.Tree == file)
            return null;
        for (var level = on; level is not null; level = level.Outer)
        {
            if (level.Call is { } call && call.Tree == file && !InAnyBody(call, level.Outer))
                return (call, holder ?? call, level);
        }
        return null;

        static bool InAnyBody(SyntaxNode node, Expansion? from)
        {
            for (var level = from; level is not null; level = level.Outer)
            {
                if (level.Call is not null && level.Holds(node))
                    return true;
            }
            return false;
        }
    }

    /// <summary>Determines whether two expansions are the same expansion of the same lines.</summary>
    public static bool operator ==(Expansion? a, Expansion? b) => Equals(a, b);

    /// <summary>Determines whether two expansions are different.</summary>
    public static bool operator !=(Expansion? a, Expansion? b) => !Equals(a, b);

    /// <inheritdoc/>
    public bool Equals(Expansion? other) =>
        other is not null
        && Index == other.Index
        && Call == other.Call
        && Binding == other.Binding
        && Value == other.Value
        && Item == other.Item
        && Member == other.Member
        && Body == other.Body
        && Equals(Outer, other.Outer);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as Expansion);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Outer, Binding, Value, Item, Index, Call, Body);

    /// <summary>Returns a description of what this expansion expands, for debugging.</summary>
    public override string ToString() =>
        Call is not null ? $"expansion of {Call.GetText()}" : $"iteration {Index}";

    /// <summary>
    /// Returns whether this expansion is inside <paramref name="level"/>, or is that expansion, so
    /// that what it emits is part of what <paramref name="level"/> emits.
    /// </summary>
    public bool IsWithin(Expansion level)
    {
        for (var at = this; at is not null; at = at.Outer)
        {
            if (at == level)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns the expansion enclosing <paramref name="splice"/> that belongs to the macro whose
    /// body contains <paramref name="line"/>.
    /// </summary>
    private static Expansion? SplicedBy(Expansion splice, SyntaxNode line)
    {
        for (var level = splice.Outer; level is not null; level = level.Outer)
        {
            if (level.Call is not null && level.Holds(line))
                return level;
        }
        return null;
    }

    /// <summary>Returns whether the block this expansion emits contains <paramref name="node"/>.</summary>
    private bool Holds(SyntaxNode node) =>
        Body is { } body && body.Tree == node.Tree && node.Position >= body.Position && node.Position < body.FullSpan.End;

    /// <summary>
    /// Represents what one name is bound to in one expansion. An item is kept as the expression
    /// from the source, not only as a number, because a list of labels has no numbers and a macro
    /// argument may name an address. A name bound to such an item has to behave as that expression
    /// everywhere, including its value, the width of the address, and what is emitted.
    /// </summary>
    /// <param name="Value">The number or word, when there is one.</param>
    /// <param name="Item">The expression the name is bound to, or null when it is bound to a value.</param>
    /// <param name="Argument">The argument a macro parameter was given, for the built-ins that ask about it.</param>
    /// <param name="Member">
    /// The enum member the name is bound to. A path ending in the name reaches the container's
    /// member of the same name.
    /// </param>
    public readonly record struct Bound(Value Value, SyntaxNode? Item, MacroArgument? Argument = null, Symbol? Member = null);
}
