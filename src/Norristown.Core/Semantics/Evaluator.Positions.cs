using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Computes where an address falls inside the data declaration it is part of. nt65 never knows
/// where a declaration lands, but it lays out every byte of a declaration it can size. Two
/// locations in the same declaration are therefore a known distance apart wherever it lands,
/// and that distance is a constant like any other. Such a location is the declaration itself,
/// its end, a member declared in it, an <c>@</c> position, or an element <c>name[i]</c>.
/// <para>
/// A distance is known only when every length between the two locations is known. An
/// <c>.align</c> between them depends on where the declaration lands, and a macro call emits its
/// bytes only once it is expanded, which is after every constant has its value. Code is sized
/// only by layout, which also comes after constants, so no location in code counts here. Two
/// declarations of one segment in one file are also a known distance apart, when everything the
/// file emits to that segment between them is data or padding whose length nt65 knows,
/// regardless of the regions and blocks it is in.
/// </para>
/// </summary>
internal sealed partial class Evaluator
{
    /// <summary>
    /// Returns the distance from the location that the right side of
    /// <paramref name="difference"/> names to the location its left side names. Returns null
    /// unless both are in one data declaration, or in two declarations a known distance apart,
    /// and every length between them is known.
    /// </summary>
    private long? Distance(BinaryExpressionSyntax difference)
    {
        if (PositionOf(difference.Left) is not { } to || PositionOf(difference.Right) is not { } from)
            return null;
        if (to.Data == from.Data)
            return to.Offset - from.Offset;
        return Apart(from.Data, to.Data) is { } between ? between + to.Offset - from.Offset : null;
    }

    /// <summary>
    /// Returns how far the start of <paramref name="second"/> is from the start of
    /// <paramref name="first"/>, where both are data declarations of one file in one segment, or
    /// null when nt65 does not know. ca65 emits a segment's bytes in the order the file emits
    /// them, regardless of the region or block they are in. Two declarations of a segment are
    /// therefore a known distance apart wherever that segment lands, when every byte the file
    /// emits to it between them has a length nt65 knows. Such bytes are data, and padding other
    /// than an <c>.align</c>. Code between them is sized only at layout, which comes after
    /// constants, and a macro call is also expanded after constants, so either leaves the
    /// distance unknown. So does a <c>.place</c>, because the output of the placed module lands
    /// between them.
    /// </summary>
    private long? Apart(Symbol first, Symbol second)
    {
        // A length computed along the way may be that of a declaration whose count is this very
        // distance, which would recurse. The walk context's `Apart` flag prevents that.
        if (first.Tree != second.Tree || context.Apart)
            return null;
        var writes = new List<Write>();
        using (Enter(context with { Apart = true }))
            Writes(first.Tree.Root.Members, 0, null, writes);
        var at = writes.FindIndex(write => write.Declares(first));
        var to = writes.FindIndex(write => write.Declares(second));
        if (at < 0 || to < 0 || writes[at].Segment is not { } segment || writes[to].Segment != segment)
            return null;
        var (low, high) = at < to ? (at, to) : (to, at);
        long distance = 0;
        for (var i = low; i < high; i++)
        {
            if (writes[i].Everywhere)
                return null;
            if (writes[i].Segment != segment)
                continue;
            if (writes[i].Length is not { } length)
                return null;
            distance += length;
        }
        return at < to ? distance : -distance;
    }

    /// <summary>
    /// Collects into <paramref name="writes"/> what <paramref name="children"/> emit to each
    /// segment, in the order they emit it. Each data declaration and each piece of padding is
    /// recorded with its length, and each routine and macro call with no length. Anything that
    /// could emit to any segment is recorded as a write to every segment. The conditionals are
    /// decided as the build decides them.
    /// </summary>
    private void Writes(IReadOnlyList<SyntaxNode> children, int from, string? segment, List<Write> writes)
    {
        var chaining = false;
        var taken = false;
        for (var i = from; i < children.Count; i++)
        {
            var child = children[i];
            if (child is LineSyntax line)
            {
                chaining = false;
                switch (line.Statement)
                {
                    case DataDeclarationSyntax or DataDirectiveSyntax:
                        writes.Add(new Write(segment, line.Span, BytesOnLine(line.Statement), false));
                        break;
                    case MacroCallSyntax or BlockSpliceSyntax:
                        writes.Add(new Write(segment, line.Span, null, false));
                        break;
                    case PlaceDirectiveSyntax:
                        writes.Add(new Write(null, line.Span, null, true));
                        break;
                    default:
                        break;
                }
                continue;
            }
            if (child is not BlockSyntax block)
                continue;
            var opener = block.Opener.Statement;
            if (block.BlockKind != BlockKind.If)
                chaining = false;
            switch (block.BlockKind)
            {
                case BlockKind.If:
                    var continues = opener is ElseIfDirectiveSyntax or ElseDirectiveSyntax;
                    var take = (!continues || chaining) && Holds(block, opener, continues && taken);
                    chaining = true;
                    taken = (continues && taken) || take;
                    if (take)
                        Writes(block.Members, 1, segment, writes);
                    break;
                case BlockKind.Region or BlockKind.Segment:
                    Writes(block.Members, 1, Constructs.SegmentOf(opener) ?? segment, writes);
                    break;
                case BlockKind.Scope:
                    Writes(block.Members, 1, segment, writes);
                    break;
                case BlockKind.Data or BlockKind.DataBody or BlockKind.RecordInitializer:
                    writes.Add(new Write(segment, block.Span, NestedBytes(block), false));
                    break;

                // A routine's own length is known only at layout. What it emits to other
                // segments is in the segment blocks inside it, which emit at their position.
                case BlockKind.Proc or BlockKind.MultiProc:
                    writes.Add(new Write(segment, block.Span, null, false));
                    Detours(block.Members, segment, writes);
                    break;
                case BlockKind.Macro or BlockKind.Struct or BlockKind.Union or BlockKind.Enum
                    or BlockKind.Charmap or BlockKind.List:
                    break;
                default:
                    writes.Add(new Write(null, block.Span, null, true));
                    break;
            }
        }
    }

    /// <summary>
    /// Collects the segment blocks inside a routine's body, which emit at their position,
    /// deciding the conditionals around them. A repetition in a body could emit any number of
    /// them, so it is recorded as a write to every segment.
    /// </summary>
    private void Detours(IReadOnlyList<SyntaxNode> members, string? segment, List<Write> writes)
    {
        var chaining = false;
        var taken = false;
        foreach (var member in members.Skip(1))
        {
            if (member is not BlockSyntax block)
            {
                chaining = false;
                continue;
            }
            var opener = block.Opener.Statement;
            switch (block.BlockKind)
            {
                case BlockKind.If:
                    var continues = opener is ElseIfDirectiveSyntax or ElseDirectiveSyntax;
                    var take = (!continues || chaining) && Holds(block, opener, continues && taken);
                    chaining = true;
                    taken = (continues && taken) || take;
                    if (take)
                        Detours(block.Members, segment, writes);
                    break;
                case BlockKind.Segment:
                    chaining = false;
                    Writes(block.Members, 1, Constructs.SegmentOf(opener) ?? segment, writes);
                    break;
                case BlockKind.Repeat or BlockKind.Each:
                    chaining = false;
                    writes.Add(new Write(null, block.Span, null, true));
                    break;
                default:
                    chaining = false;
                    break;
            }
        }
    }

    /// <summary>
    /// Returns the outermost data declaration in which an expression names a location, and how
    /// far into it that location is. Returns null when the expression names no such location, or
    /// one whose offset nt65 cannot determine.
    /// </summary>
    private (Symbol Data, long Offset)? PositionOf(SyntaxNode node)
    {
        switch (node)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return PositionOf(parenthesized.Expression);

            // A location a constant distance from another location is also a location.
            case BinaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.Plus or SyntaxKind.Minus } moved:
                var minus = moved.OperatorToken.Kind == SyntaxKind.Minus;
                if (PositionOf(moved.Left) is { } left && Evaluate(moved.Right).AsNumber() is { } by)
                    return (left.Data, minus ? left.Offset - by : left.Offset + by);
                if (!minus && Evaluate(moved.Left).AsNumber() is { } ahead && PositionOf(moved.Right) is { } right)
                    return (right.Data, right.Offset + ahead);
                return null;

            // The end of a declaration is as far into it as it is long.
            case CallExpressionSyntax { BuiltinKind: BuiltinKind.Endof } call
                when call.Arguments.Arguments is [NameExpressionSyntax measured]
                    && SymbolOf(measured) is { Kind: SymbolKind.Data } ended:
                EnsureEvaluated(ended);
                return ended.Size is { } size && PositionOf(ended) is { } start ? (start.Data, start.Offset + size) : null;

            case NameExpressionSyntax name:
                return PositionOfName(name);

            default:
                return null;
        }
    }

    /// <summary>
    /// Returns the location a name refers to. This is a declaration or a position, plus the
    /// elements its indexes step over, or a member reached through a record, with the member
    /// offsets along the path added to the record's own location.
    /// </summary>
    private (Symbol Data, long Offset)? PositionOfName(NameExpressionSyntax name)
    {
        if (names.BoundItem(name) is not null)
            return null;
        Symbol? address = null;
        long along = 0;
        foreach (var token in name.Names)
        {
            if (!resolved.TryGetValue((name.Tree, token.Span.Start), out var part))
                return null;
            if (part.IsAddress)
            {
                address = part;
                along = 0;
                continue;
            }
            if (address is null || part.Kind != SymbolKind.Member)
                continue;
            EnsureEvaluated(part);
            if (part.Value.AsNumber() is not { } offset)
                return null;
            along += offset;
        }
        if (address is null || PositionOf(address) is not { } position)
            return null;
        if (name.IsIndexed)
        {
            if (IndexOffset(name) is not { } stepped)
                return null;
            along += stepped;
        }
        return (position.Data, position.Offset + along);
    }

    /// <summary>
    /// Returns where a symbol stands in the outermost data declaration around it. A declaration
    /// at the top level stands at its own start, and a member or a position is as far in as the
    /// bytes above it in every body around it. A label outside every declaration is in none.
    /// </summary>
    private (Symbol Data, long Offset)? PositionOf(Symbol symbol)
    {
        var outermost = symbol;
        while (outermost.Scope is { Kind: ScopeKind.Data, Owner: { Kind: SymbolKind.Data } around } && around.Tree == symbol.Tree)
            outermost = around;
        if (outermost == symbol)
            return symbol.Kind == SymbolKind.Data ? (symbol, 0) : null;

        // A declaration whose size is being computed may be asked about by code inside it. The
        // distance is then unknown, rather than the question recursing.
        if (outermost.Definition is not BlockSyntax body || !placing.Add(outermost))
            return null;
        long offset = 0;
        bool? found;
        try
        {
            found = Seek(body.Members, 1, symbol.NameSpan.Start, ref offset);
        }
        finally
        {
            placing.Remove(outermost);
        }
        return found == true ? (outermost, offset) : null;
    }

    /// <summary>
    /// Adds up the bytes of <paramref name="children"/> from <paramref name="from"/> until the
    /// line or declaration at <paramref name="position"/>. The conditionals are decided as the
    /// build decides them and the repetitions are unrolled, as they are for a body's size.
    /// </summary>
    /// <returns>
    /// True when the position is reached, with <paramref name="offset"/> holding the bytes before
    /// it. False when it is not among the children, with <paramref name="offset"/> holding the
    /// bytes they all take. Null as soon as a length along the way is unknown.
    /// </returns>
    private bool? Seek(IReadOnlyList<SyntaxNode> children, int from, int position, ref long offset)
    {
        var chaining = false;
        var taken = false;
        for (var i = from; i < children.Count; i++)
        {
            var child = children[i];
            var holds = child.FullSpan.Contains(position);
            long? part;
            if (child is not BlockSyntax nested)
            {
                chaining = false;
                if (holds)
                    return true;
                part = child is LineSyntax childLine ? BytesOnLine(childLine.Statement) : 0;
            }
            else
            {
                var opener = nested.Opener.Statement;
                switch (nested.BlockKind)
                {
                    case BlockKind.If:
                        var continues = opener is ElseIfDirectiveSyntax or ElseDirectiveSyntax;
                        var take = (!continues || chaining) && Holds(nested, opener, continues && taken);
                        chaining = true;
                        taken = (continues && taken) || take;
                        if (holds)
                            return take ? Seek(nested.Members, 1, position, ref offset) : null;
                        part = take ? Total(nested.Members, 1, BytesOnLine, NestedBytes) : 0;
                        break;
                    case BlockKind.Repeat or BlockKind.Each when opener is RepetitionDirectiveSyntax repetition:
                        chaining = false;
                        if (holds)
                            return null;
                        part = Iterations(nested, repetition, () => Total(nested.Members, 1, BytesOnLine, NestedBytes));
                        break;
                    case BlockKind.Data when holds:
                        return nested.Opener.FullSpan.Contains(position) ? true : Seek(nested.Members, 1, position, ref offset);
                    default:
                        chaining = false;
                        if (holds)
                            return nested.Opener.FullSpan.Contains(position) ? true : null;
                        part = NestedBytes(nested);
                        break;
                }
            }
            if (part is not { } known)
                return null;
            offset += known;
        }
        return false;
    }

    /// <summary>
    /// Represents what one item emits to a segment, with where the item is and how many bytes it
    /// emits, when nt65 knows. An item that could emit to any segment is a write to every
    /// segment.
    /// </summary>
    /// <param name="Segment">The segment emitted to, or null for a write to every segment.</param>
    /// <param name="Span">The span of the item.</param>
    /// <param name="Length">The number of bytes the item emits, or null when nt65 does not know.</param>
    /// <param name="Everywhere">Whether the item could emit to any segment.</param>
    private readonly record struct Write(string? Segment, TextSpan Span, long? Length, bool Everywhere)
    {
        /// <summary>Determines whether this is the item that declares <paramref name="data"/>.</summary>
        public bool Declares(Symbol data) => !Everywhere && Span.Contains(data.NameSpan.Start);
    }
}
