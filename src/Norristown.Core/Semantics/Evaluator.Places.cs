using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Where an address stands inside the data declaration it is part of. nt65 never knows where a
/// declaration lands, but it lays out every byte of one it can size, so two places in the same
/// declaration — the declaration itself, its end, a member declared in it, an <c>@</c> position
/// and an element <c>name[i]</c> — are a known distance apart wherever it lands, and that
/// distance is a constant like any other.
/// <para>
/// A distance is known only where every length between the two places is: an <c>.align</c>
/// between them depends on where the declaration lands, and a macro call writes its bytes only
/// once it is expanded, which is after every constant has its value (§3.1). Code is layout and
/// never a place here, whatever it holds.
/// </para>
/// </summary>
internal sealed partial class Evaluator
{
    /// <summary>
    /// The distance from the place <paramref name="difference"/>'s right side names to the one
    /// its left side names, where both are in one data declaration and every length between
    /// them is known; null for anything else.
    /// </summary>
    private long? Distance(BinaryExpressionSyntax difference) =>
        PlaceOf(difference.Left) is { } to && PlaceOf(difference.Right) is { } from && to.Data == from.Data
            ? to.Offset - from.Offset
            : null;

    /// <summary>
    /// The data declaration an expression names a place in, outermost first, and how far into
    /// it that place is; null where it names no such place, or one whose offset nt65 cannot say.
    /// </summary>
    private (Symbol Data, long Offset)? PlaceOf(SyntaxNode node)
    {
        switch (node)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return PlaceOf(parenthesized.Expression);

            // A place a constant away from another is a place too.
            case BinaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.Plus or SyntaxKind.Minus } moved:
                var minus = moved.OperatorToken.Kind == SyntaxKind.Minus;
                if (PlaceOf(moved.Left) is { } left && Evaluate(moved.Right).AsNumber() is { } by)
                    return (left.Data, minus ? left.Offset - by : left.Offset + by);
                if (!minus && Evaluate(moved.Left).AsNumber() is { } ahead && PlaceOf(moved.Right) is { } right)
                    return (right.Data, right.Offset + ahead);
                return null;

            // The end of a declaration is as far into it as it is long.
            case CallExpressionSyntax { Function: { } function } call
                when function.Text.Equals(".endof", StringComparison.OrdinalIgnoreCase)
                    && call.Arguments.Arguments is [NameExpressionSyntax measured]
                    && SymbolOf(measured) is { Kind: SymbolKind.Data } ended:
                Settle(ended);
                return ended.Size is { } size && PlaceOf(ended) is { } start ? (start.Data, start.Offset + size) : null;

            case NameExpressionSyntax name:
                return PlaceOfName(name);

            default:
                return null;
        }
    }

    /// <summary>
    /// The place a name stands for: a declaration or a position, with the elements its indexes
    /// step over, or a member reached through a record, the offsets along the path added to the
    /// place the record stands at.
    /// </summary>
    private (Symbol Data, long Offset)? PlaceOfName(NameExpressionSyntax name)
    {
        if (BoundItem(name) is not null)
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
            Settle(part);
            if (part.Value.AsNumber() is not { } offset)
                return null;
            along += offset;
        }
        if (address is null || PlaceOf(address) is not { } place)
            return null;
        if (name.IsIndexed)
        {
            if (IndexOffset(name) is not { } stepped)
                return null;
            along += stepped;
        }
        return (place.Data, place.Offset + along);
    }

    /// <summary>
    /// Where a symbol stands in the outermost data declaration around it: a declaration at the
    /// top is its own start, and a member or a position is as far in as the bytes above it in
    /// every body around it. A label outside every declaration is in none.
    /// </summary>
    private (Symbol Data, long Offset)? PlaceOf(Symbol symbol)
    {
        var outermost = symbol;
        while (outermost.Scope is { Kind: ScopeKind.Data, Owner: { Kind: SymbolKind.Data } around } && around.Tree == symbol.Tree)
            outermost = around;
        if (outermost == symbol)
            return symbol.Kind == SymbolKind.Data ? (symbol, 0) : null;

        // A declaration whose size is being worked out may be asked about by what is written
        // in it, and the distance is then unknown rather than a question that asks itself.
        if (outermost.Definition is not BlockSyntax body || !placing.Add(outermost))
            return null;
        long offset = 0;
        var found = Seek(body.Members, 1, symbol.NameSpan.Start, ref offset);
        placing.Remove(outermost);
        return found == true ? (outermost, offset) : null;
    }

    /// <summary>
    /// Adds up the bytes of <paramref name="children"/> from <paramref name="from"/> until the
    /// line or declaration written at <paramref name="position"/>, with the conditionals decided
    /// as the build decides them and the repetitions unrolled, as a body's size is. True when it
    /// is reached, with <paramref name="offset"/> the bytes before it; false when it is not among
    /// them, with the bytes they all take; null as soon as a length on the way is unknown.
    /// </summary>
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
                part = child is LineSyntax written ? BytesOnLine(written.Statement) : 0;
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
                        part = Turns(nested, repetition, () => Total(nested.Members, 1, BytesOnLine, NestedBytes));
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
}
