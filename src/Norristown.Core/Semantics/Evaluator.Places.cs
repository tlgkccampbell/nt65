using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Where an address falls inside the data declaration it is part of. nt65 never knows where a
/// declaration lands, but it lays out every byte of one it can size, so two places in the same
/// declaration — the declaration itself, its end, a member declared in it, an <c>@</c> position
/// and an element <c>name[i]</c> — are a known distance apart wherever it lands, and that
/// distance is a constant like any other.
/// <para>
/// A distance is known only where every length between the two places is: an <c>.align</c>
/// between them depends on where the declaration lands, and a macro call writes its bytes only
/// once it is expanded, which is after every constant has its value. Code is sized only by
/// layout, which also comes after constants, so nothing in code is a place here. Two
/// declarations of one segment in one file are a known distance apart too, where everything
/// the file writes to that segment between them is data or padding nt65 knows the length of,
/// whichever regions and blocks it is in.
/// </para>
/// </summary>
internal sealed partial class Evaluator
{
    /// <summary>
    /// The distance from the place <paramref name="difference"/>'s right side names to the one
    /// its left side names, where both are in one data declaration and every length between
    /// them is known; null for anything else.
    /// </summary>
    private long? Distance(BinaryExpressionSyntax difference)
    {
        if (PlaceOf(difference.Left) is not { } to || PlaceOf(difference.Right) is not { } from)
            return null;
        if (to.Data == from.Data)
            return to.Offset - from.Offset;
        return Apart(from.Data, to.Data) is { } between ? between + to.Offset - from.Offset : null;
    }

    /// <summary>
    /// How far the start of <paramref name="second"/> is from the start of <paramref name="first"/>,
    /// two data declarations of one file in one segment, or null where nt65 does not know. ca65
    /// writes a segment's bytes in the order the file writes them, whichever region or block they
    /// are in, so two declarations of a segment are a known distance apart wherever that segment
    /// lands when every byte the file writes to it between them has a length nt65 knows: data, and
    /// padding other than an <c>.align</c>. Code between them is sized only at layout, which
    /// comes after constants, and a macro call is expanded after constants too, so either leaves
    /// the distance unknown; so does a <c>.place</c>, because what the placed module writes lands
    /// between them.
    /// </summary>
    private long? Apart(Symbol first, Symbol second)
    {
        // A length computed on the way may be that of a declaration whose count is this very
        // distance, which would recurse; the `apart` flag cuts that off.
        if (first.Tree != second.Tree || apart)
            return null;
        apart = true;
        var writes = new List<Write>();
        Writes(first.Tree.Root.Members, 0, null, writes);
        apart = false;
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
    /// What <paramref name="children"/> write to each segment, in the order they write it, into
    /// <paramref name="writes"/>: each data declaration and each piece of padding with its
    /// length, each routine and macro call with none, and what could write to any segment as a
    /// write everywhere. The conditionals are decided as the build decides them.
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

                // A routine's own length is known only at layout. What it writes to other
                // segments is in the segment blocks inside it, which write at their position.
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
    /// The segment blocks inside a routine's body, which write at their position, with the
    /// conditionals around them decided. A repetition in a body could write any number of them.
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
    /// The place a name refers to: a declaration or a position, plus the elements its indexes
    /// step over, or a member reached through a record, with the member offsets along the path
    /// added to the record's own place.
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
        // in it; the distance is then unknown, rather than the question recursing.
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

    /// <summary>
    /// What one item writes to a segment: where it is written, and how many bytes, or none
    /// nt65 knows. One that could write to any segment is a write everywhere.
    /// </summary>
    /// <param name="Segment">The segment written to, or null for a write everywhere.</param>
    /// <param name="Span">Where the item is written.</param>
    /// <param name="Length">How many bytes it writes, or null when nt65 does not know.</param>
    /// <param name="Everywhere">Whether it could write to any segment.</param>
    private readonly record struct Write(string? Segment, TextSpan Span, long? Length, bool Everywhere)
    {
        /// <summary>Whether this is the item that declares <paramref name="data"/>.</summary>
        public bool Declares(Symbol data) => !Everywhere && Span.Contains(data.NameSpan.Start);
    }
}
