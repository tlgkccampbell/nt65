using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// One level of "these same lines, written out again": a turn of a <c>.repeat</c> or an
/// <c>.each</c>, or one expansion of a macro. Both are read once and written out many
/// times, so this is what tells one writing from another: what every name the level binds
/// stands for, and which writing a line belongs to.
/// <para>
/// Layout and emission walk the same blocks in the same order and work these out
/// independently, so they are compared by what they hold rather than by identity. What they
/// hold is only what identifies the level — a turn's index, or the call being expanded —
/// and never anything derived from it, which is worked out afresh each time it is asked for.
/// </para>
/// </summary>
public sealed class Expansion : IEquatable<Expansion>
{
    private readonly bool splice;

    private Expansion(
        Expansion? outer, Symbol? binding, Value value, SyntaxNode? item, int index,
        SyntaxNode? call, SyntaxNode? body, bool splice = false, Symbol? member = null)
    {
        this.splice = splice;
        Member = member;
        Outer = outer;
        Binding = binding;
        Value = value;
        Item = item;
        Index = index;
        Call = call;
        Body = body;
    }

    /// <summary>The turn or expansion around this one, or null at the top.</summary>
    public Expansion? Outer { get; }

    /// <summary>The name a repetition binds, or null for a macro expansion and for one that names none.</summary>
    public Symbol? Binding { get; }

    /// <summary>What that name is worth on this turn.</summary>
    public Value Value { get; }

    /// <summary>The item it stands for, for an <c>.each</c> over a list.</summary>
    public SyntaxNode? Item { get; }

    /// <summary>The member it stands for, for an <c>.each</c> over an enum.</summary>
    public Symbol? Member { get; }

    /// <summary>Which turn it is, from zero; zero for an expansion.</summary>
    public int Index { get; }

    /// <summary>The call being expanded, or null for a repetition turn.</summary>
    public SyntaxNode? Call { get; }

    /// <summary>
    /// The block this level writes out: a repetition's body, a macro's body, or the block
    /// argument a splice names. What it holds is what this level is a writing of, which is
    /// how a name declared there is told from the same name at another level.
    /// </summary>
    public SyntaxNode? Body { get; }

    /// <summary>One turn of a repetition, with the name it binds and what that is worth.</summary>
    public static Expansion Turn(
        Expansion? outer, SyntaxNode block, Symbol? binding, Value value, SyntaxNode? item, int index,
        Symbol? member = null) =>
        new(outer, binding, value, item, index, null, block, member: member);

    /// <summary>One expansion of the macro <paramref name="call"/> names.</summary>
    public static Expansion Of(Expansion? outer, SyntaxNode call, SyntaxNode definition) =>
        new(outer, null, Value.Unknown, null, 0, call, definition);

    /// <summary>
    /// One splice of a block argument, named by the line that spliced it. A block binds no
    /// name, so this level carries none; it is here because the same block may be spliced in
    /// more than one place, and each splice writes its lines out again.
    /// </summary>
    public static Expansion Spliced(Expansion? outer, SyntaxNode splice, SyntaxNode block) =>
        new(outer, null, Value.Unknown, splice, 0, null, block, splice: true);

    /// <summary>
    /// The level that writes out the place <paramref name="declared"/> was written, or null
    /// when none of them does and the name is simply the file's. This is what tells one
    /// expansion's locals from another's: the same body, written out twice, declares two.
    /// </summary>
    public static Expansion? Owning(Expansion? at, Symbol declared)
    {
        for (var level = at; level is not null; level = level.Outer)
        {
            if (level.Body is { } body && body.Tree == declared.Tree
                && declared.NameSpan.Start >= body.Position
                && declared.NameSpan.Start < body.Position + body.Green.FullWidth)
            {
                return level;
            }
        }
        return null;
    }

    /// <summary>
    /// Whether <paramref name="definition"/> is already being expanded at <paramref name="at"/>
    /// or around it: a macro that reaches itself, which is reported where it is declared and is
    /// never written out again inside itself.
    /// <para>
    /// A block argument is the caller's code, not the macro's, so a call written in one is not
    /// inside the macro it was given to: <c>if!(eq) { if!(ne) { ... } }</c> nests two calls
    /// and reaches nothing. The walk steps from a splice straight out to the level that wrote
    /// the macro whose body spliced it.
    /// </para>
    /// </summary>
    public static bool Expanding(Expansion? at, SyntaxNode definition)
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

    /// <summary>The expansion, around <paramref name="splice"/>, of the macro whose body holds <paramref name="line"/>.</summary>
    private static Expansion? SplicedBy(Expansion splice, SyntaxNode line)
    {
        for (var level = splice.Outer; level is not null; level = level.Outer)
        {
            if (level.Call is not null && level.Body is { } body && body.Tree == line.Tree
                && line.Position >= body.Position && line.Position < body.Position + body.Green.FullWidth)
            {
                return level;
            }
        }
        return null;
    }

    /// <summary>
    /// A problem with the text at <paramref name="span"/> of <paramref name="tree"/>, found
    /// laying out or writing <paramref name="file"/> on the writing <paramref name="at"/>. The
    /// file's own text is reported where it stands. The text of another file's macro body is
    /// reported at the nearest call in this file that expanded it, the side that can change,
    /// with the body's text named beside it: where it stands is the other file's, and a line of
    /// this file's diagnostics does not move when that file is edited.
    /// </summary>
    public static Diagnostic Problem(
        SyntaxTree file, SyntaxTree tree, TextSpan span, Expansion? at, Severity severity, string message)
    {
        if (tree != file)
        {
            for (var level = at; level is not null; level = level.Outer)
            {
                if (level.Call is { } call && call.Tree == file)
                {
                    return new Diagnostic(file.GetSpan(call.Span), severity, message,
                        [new RelatedSpan(tree.GetSpan(span), "in the macro body")]);
                }
            }
        }
        return new Diagnostic(tree.GetSpan(span), severity, message);
    }

    /// <summary>How deep the expansions go, which is what bounds a runaway one.</summary>
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

    /// <summary>The macro call this line is inside, nearest first, or null when it is in none.</summary>
    public SyntaxNode? NearestCall
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

    /// <summary>Whether two levels are the same writing of the same lines.</summary>
    public static bool operator ==(Expansion? a, Expansion? b) => Equals(a, b);

    /// <summary>Whether two levels are different writings.</summary>
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

    /// <summary>What it expands, for debugging.</summary>
    public override string ToString() =>
        Call is not null ? $"expansion of {Call.GetText()}" : $"turn {Index}";

    /// <summary>
    /// What one name stands for at one level. An item is kept as the expression it was
    /// written as, not only as a number: a list of labels has no numbers, a macro argument
    /// may name an address, and a name standing for one has to be that expression
    /// everywhere — in what it is worth, how wide an address it is, and what is written out.
    /// </summary>
    /// <param name="Value">The number or word, where there is one.</param>
    /// <param name="Item">The expression it stands for, or null when it stands for a value.</param>
    /// <param name="Argument">What a macro parameter was given, for the built-ins that ask about it.</param>
    /// <param name="Member">The enum member it stands for, which a path ending in the name reaches the namesake of.</param>
    public readonly record struct Bound(Value Value, SyntaxNode? Item, MacroArgument? Argument = null, Symbol? Member = null);
}
