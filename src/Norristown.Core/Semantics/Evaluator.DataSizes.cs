using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// How much room a declaration takes, and how many elements that is: what <c>.sizeof</c> and
/// <c>.countof</c> answer, and the offsets and sizes a structure gives its members.
/// <para>
/// This is nt65's own arithmetic rather than anything the output carries, so it is worked out
/// with the conditionals decided and the repetitions unrolled, and it is null wherever nt65
/// cannot say, such as for an <c>.align</c>, whose size depends on where it lands.
/// </para>
/// </summary>
internal sealed partial class Evaluator
{
    /// <summary>How much room a data directive takes, reporting nothing, for a caller that has already reported its problems.</summary>
    public static DataSize? DataSizeOf(
        StatementSyntax directive,
        SegmentTable segments,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        Func<string, long?>? binaryLength,
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null,
        Configuration? configuration = null) =>
        new Evaluator(segments, resolved, null, binaryLength, bound, configuration: configuration).RoomFor(directive);

    /// <summary>
    /// How many elements an element type declares with its count, and how many its values
    /// come to, either of which may be unknown. The two have to agree.
    /// </summary>
    public static (long? Declared, long? Given) ElementsOf(
        DataDirectiveSyntax directive,
        SegmentTable segments,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null,
        Configuration? configuration = null)
    {
        var evaluator = new Evaluator(segments, resolved, null, null, bound, configuration: configuration);
        return (evaluator.DeclaredCount(directive), evaluator.GivenCount(directive));
    }

    /// <summary>The bytes a literal or a mapped string becomes, or null for anything else.</summary>
    public static IReadOnlyList<long>? BytesOf(
        SyntaxNode argument,
        SegmentTable segments,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null) =>
        new Evaluator(segments, resolved, null, null, bound).BytesIn(argument);

    /// <summary>
    /// How much room a data directive takes: the bytes it generates, and how many elements
    /// they are. Null where nt65 cannot say — an `.align`, whose size depends on where it
    /// lands, or a directive whose operands do not add up.
    /// </summary>
    private DataSize? RoomFor(StatementSyntax written)
    {
        // A line in a data body holds values, each one element of the body's element type.
        if (written is DataValuesSyntax values)
        {
            return DataSyntax.DirectiveOfValues(values) is { } of && ElementWidth(of) is { } each
                ? Spread(values.Values, each)
                : null;
        }
        if (written is not DataDirectiveSyntax directive)
            return null;
        if (DataSyntax.IsElementType(directive))
            return RoomForElements(directive);
        SeparatedSyntaxList<SyntaxNode> operands = directive.Tail is InlineDataSyntax inline ? inline.Values : default;

        switch (DataSyntax.NameOf(directive))
        {
            case ".lobytes":
            case ".hibytes":
            case ".bankbytes":
                return Spread(operands, width: 1);

            // `.strz` is the text and the zero byte that ends it.
            case ".strz":
                return Spread(operands, width: 1) is { } text
                    ? new DataSize(text.Bytes + 1, text.Elements + 1)
                    : null;

            case ".res":
                return operands.Count > 0 && Evaluate(operands[0]).AsNumber() is { } reserved and >= 0
                    ? new DataSize(reserved, reserved)
                    : null;

            case ".incbin":
                return RoomForBinary(directive, operands);

            // An `.align` generates however many bytes it takes to reach the next boundary,
            // which depends on where it lands, so it has no size of its own.
            default:
                return null;
        }
    }

    /// <summary>
    /// An element type: as many elements as its count says, or as its values come to, or one
    /// when it has neither, each as big as the element type — a record's being its type's size,
    /// which is also the stride of an array of them.
    /// </summary>
    private DataSize? RoomForElements(DataDirectiveSyntax directive)
    {
        if (ElementWidth(directive) is not { } width)
            return null;
        var count = directive.Count is { } counted
            ? counted.Count is null ? GivenCount(directive) ?? 0 : DeclaredCount(directive)
            : GivenCount(directive) ?? 1;
        return count is { } many and >= 0 ? new DataSize(width * many, many) : null;
    }

    /// <summary>
    /// How many bytes one element of an element type takes: a record's is its type's size. The
    /// type is laid out when the program's symbols are evaluated, whether or not anything
    /// names it, so asking how much room a declaration takes never lays one out.
    /// </summary>
    private long? ElementWidth(DataDirectiveSyntax directive)
    {
        if (directive.Type is not { } named)
            return SyntaxFacts.ElementSize(DataSyntax.NameOf(directive));
        if (SymbolOf(named) is not { } type)
            return null;
        Settle(type);
        return type.IsLayout ? type.Size : null;
    }

    /// <summary>The <c>n</c> of <c>[n]</c>, or null when there is none or it is no constant.</summary>
    private long? DeclaredCount(DataDirectiveSyntax directive) =>
        directive.Count?.Count is { } count ? Evaluate(count).AsNumber() : null;

    /// <summary>
    /// How many elements an element type's values come to, wherever they are written: after it
    /// on the line, in braces, or in the body its line opens. Null when it has none, or when
    /// nt65 cannot count them.
    /// </summary>
    private long? GivenCount(DataDirectiveSyntax directive)
    {
        if (directive.Tail is BracedDataSyntax { Value: { } braced })
            return braced is ValueListSyntax list ? Spread(list.Values, 1).Elements : 1;
        if (DataSyntax.BodyOf(directive) is { } body)
        {
            return body.BlockKind == BlockKind.RecordInitializer
                ? 1
                : Total(body.Members, 1, statement =>
                    statement is DataValuesSyntax row ? Spread(row.Values, 1).Elements : 0, _ => null);
        }
        return directive.Tail is InlineDataSyntax written ? Spread(written.Values, 1).Elements : null;
    }

    /// <summary>
    /// How many bytes mixed data takes: its lines, and the data declared in it, with its
    /// conditionals decided and its repetitions unrolled. Null when an `.align` in it makes
    /// that depend on where it lands.
    /// </summary>
    private long? RoomForMixed(BlockSyntax block) => Total(block.Members, 1, BytesOnLine, NestedBytes);

    /// <summary>Whether mixed data holds a macro call, whose bytes nothing knows before it is expanded.</summary>
    private static bool Expands(Symbol data) =>
        data.Definition is BlockSyntax block
        && block.DescendantNodes().Any(node => node is MacroCallSyntax or BlockSpliceSyntax);

    /// <summary>The bytes a block inside mixed data takes, or null when nt65 cannot say.</summary>
    private long? NestedBytes(BlockSyntax nested) => nested.BlockKind switch
    {
        BlockKind.Data => RoomForMixed(nested),
        BlockKind.DataBody or BlockKind.RecordInitializer => BytesOnLine(nested.Opener.Statement),
        _ => null,
    };

    /// <summary>
    /// The bytes one line of mixed data takes, or null when nt65 cannot say. A macro call writes
    /// its bytes only once it is expanded, which is after every constant and every shape has its
    /// value, so what one takes is not known here.
    /// </summary>
    private long? BytesOnLine(StatementSyntax statement)
    {
        if (statement is MacroCallSyntax or BlockSpliceSyntax)
            return null;
        var directive = statement switch
        {
            DataDirectiveSyntax data => data,
            DataDeclarationSyntax declaration => declaration.Directive,
            LabeledLineSyntax labeled => labeled.Statement as DataDirectiveSyntax,
            _ => null,
        };
        return directive is null ? 0 : RoomFor(directive)?.Bytes;
    }

    /// <summary>
    /// What the lines of a body come to, one number per line from <paramref name="line"/> and
    /// per block from <paramref name="block"/>, with the conditionals decided as the build
    /// decides them and the repetitions unrolled a turn at a time. Null as soon as any part of
    /// it is unknown.
    /// </summary>
    private long? Total(
        IReadOnlyList<SyntaxNode> children, int from, Func<StatementSyntax, long?> line, Func<BlockSyntax, long?> block)
    {
        long total = 0;
        var chaining = false;
        var taken = false;
        for (var i = from; i < children.Count; i++)
        {
            var child = children[i];
            long? part;
            if (child is not BlockSyntax nested)
            {
                chaining = false;
                part = child is LineSyntax written ? line(written.Statement) : 0;
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
                        part = take ? Total(nested.Members, 1, line, block) : 0;
                        break;
                    case BlockKind.Repeat or BlockKind.Each when opener is RepetitionDirectiveSyntax repetition:
                        chaining = false;
                        part = Turns(nested, repetition, () => Total(nested.Members, 1, line, block));
                        break;
                    default:
                        chaining = false;
                        part = block(nested);
                        break;
                }
            }
            if (part is not { } known)
                return null;
            total += known;
        }
        return total;
    }

    /// <summary>
    /// Whether a branch of a conditional is taken: as the build answered it where it could, and
    /// otherwise by its condition, as one inside an expansion is.
    /// </summary>
    private bool Holds(BlockSyntax block, StatementSyntax opener, bool already)
    {
        if (configuration?.Answered(block) == true)
            return configuration.Includes(block);
        if (already)
            return false;
        if (opener is ElseDirectiveSyntax)
            return true;
        return opener is ConditionalDirectiveSyntax conditional && Evaluate(conditional.Condition).AsNumber() is { } value and not 0;
    }

    /// <summary>
    /// The sum of <paramref name="body"/> over every turn of a repetition, with its binding set
    /// to that turn's index, item or member. Null when the turns are unknown.
    /// </summary>
    private long? Turns(BlockSyntax block, RepetitionDirectiveSyntax opener, Func<long?> body)
    {
        var counted = opener.Expression;
        var binding = BindingIn(block, opener);
        List<Action> turns = [];
        if (opener is RepeatDirectiveSyntax)
        {
            if (Evaluate(counted).AsNumber() is not { } count || count < 0)
                return null;
            if (count > Repetitions.MaximumTurns)
            {
                Report(counted, Repetitions.Beyond(count));
                return null;
            }
            if (binding is null)
                return body() * count;
            for (long i = 0; i < count; i++)
            {
                var turn = i;
                turns.Add(() => arguments[binding] = Value.Of(turn));
            }
        }
        else if (SymbolOf(counted) is { Kind: SymbolKind.List } list)
        {
            turns.AddRange(list.Items.Select(item => (Action)(() => { if (binding is not null) items[binding] = item; })));
        }
        else if (SymbolOf(counted) is { Kind: SymbolKind.Enum, Body: { } walked })
        {
            turns.AddRange(walked.Symbols.Select(member => (Action)(() =>
            {
                if (binding is null)
                    return;
                arguments[binding] = member.Value;
                members[binding] = member;
            })));
        }
        else
        {
            return null;
        }

        long total = 0;
        foreach (var turn in turns)
        {
            turn();
            if (body() is not { } part)
                return null;
            total += part;
        }
        if (binding is not null)
        {
            arguments.Remove(binding);
            items.Remove(binding);
            members.Remove(binding);
        }
        return total;
    }

    /// <summary>The name a repetition binds, found where its body names it; null when nothing does.</summary>
    private Symbol? BindingIn(BlockSyntax block, RepetitionDirectiveSyntax opener)
    {
        if (opener.Name is not { } declared)
            return null;
        foreach (var name in block.DescendantNodes().OfType<NameExpressionSyntax>())
        {
            foreach (var token in name.Names)
            {
                if (resolved.TryGetValue((name.Tree, token.Span.Start), out var symbol)
                    && symbol.Kind == SymbolKind.Binding && symbol.Tree == block.Tree
                    && symbol.NameSpan.Start == declared.Span.Start)
                {
                    return symbol;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// One element per operand, except that text is one element per byte and a named list is
    /// one element per item.
    /// </summary>
    private DataSize Spread(SeparatedSyntaxList<SyntaxNode> operands, long width)
    {
        long elements = 0;
        foreach (var operand in operands)
        {
            if (BytesIn(operand) is { } bytes)
                elements += bytes.Count;
            else if (operand is NameExpressionSyntax name && SymbolOf(name) is { Kind: SymbolKind.List } list)
                elements += list.Items.Count;
            else
                elements++;
        }
        return new DataSize(elements * width, elements);
    }

    /// <summary>
    /// A binary file, whose length nt65 reads for itself. The path is relative to the file
    /// that names it, and an offset and a length may narrow it.
    /// </summary>
    private DataSize? RoomForBinary(DataDirectiveSyntax directive, SeparatedSyntaxList<SyntaxNode> operands)
    {
        if (operands.Count == 0 || Evaluate(operands[0]) is not { Kind: ValueKind.String, Text: { } path })
            return null;

        var from = Paths.Beside(directive.Tree.Path, path);
        if (binaryLength?.Invoke(from) is not { } length)
        {
            Report(directive, Catalogue.IncbinUnreadable.Says(path));
            return null;
        }

        var offset = operands.Count > 1 ? Evaluate(operands[1]).AsNumber() ?? 0 : 0;
        var taken = operands.Count > 2 ? Evaluate(operands[2]).AsNumber() : null;
        var available = Math.Max(0, length - offset);
        var bytes = taken is { } wanted ? Math.Min(wanted, available) : available;
        return new DataSize(bytes, bytes);
    }

    /// <summary>
    /// The bytes an operand becomes: a string or character literal is its characters, a
    /// charmap applied to one is what the mapping gives them, and text built by a call is its
    /// bytes. Null for anything else.
    /// </summary>
    private IReadOnlyList<long>? BytesIn(SyntaxNode operand)
    {
        // A macro parameter given text is that text, as many bytes as it has.
        if (operand is NameExpressionSyntax bound && BoundItem(bound) is { } item)
            return BytesIn(item);

        // A string constant is its text wherever it is named, as a literal would be. A constant
        // still being evaluated is skipped: it can only be a number whose value depends on the
        // size of this very line, and it is one element whatever that value turns out to be.
        if (operand is NameExpressionSyntax constant && SymbolOf(constant) is { Kind: SymbolKind.Constant } declared
            && !evaluating.Contains(declared)
            && Evaluate(constant) is { Kind: ValueKind.String, Text: { } named })
            return [.. named.Select(c => (long)c)];

        if (operand is StringExpressionSyntax or CharacterExpressionSyntax)
        {
            var value = Evaluate(operand);
            return value.Kind switch
            {
                ValueKind.String => [.. value.Text!.Select(c => (long)c)],
                ValueKind.Number => [value.Number],
                _ => null,
            };
        }

        if (operand is not CallExpressionSyntax { Callee: { } callee } call
            || SymbolOf(callee) is not { Kind: SymbolKind.Charmap } charmap)
        {
            return BuiltText(operand);
        }

        var given = call.Arguments.Arguments;
        if (given.Count != 1)
            return null;
        var text = Evaluate(given[0]);
        var characters = text.Kind switch
        {
            ValueKind.String => text.Text!.Select(c => (int)c),
            ValueKind.Number => [(int)text.Number],
            _ => (IEnumerable<int>)[],
        };

        var mapped = Map(charmap);
        var bytes = new List<long>();
        foreach (var character in characters)
        {
            if (!mapped.TryGetValue(character, out var b))
            {
                Report(operand, Catalogue.CharmapHasNoEntry.Says(charmap.Name, (char)character));
                return null;
            }
            bytes.Add(b);
        }
        return bytes;
    }

    /// <summary>
    /// The bytes of text an expression builds rather than writes: a function whose body is text,
    /// <c>.strcat</c> and <c>.strsub</c>, a <c>.select</c> that chooses text, and a parameter or a
    /// binding given any of them. Null for anything that is not text, and for text holding a
    /// character above <c>$ff</c>, which only a charmap turns into a byte and which is reported
    /// where it is written. Nothing else is evaluated here: this runs while a declaration is
    /// being sized, and evaluating a value that measures that same declaration would recurse.
    /// </summary>
    private IReadOnlyList<long>? BuiltText(SyntaxNode operand)
    {
        switch (operand)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return BytesIn(parenthesized.Expression);
            case CallExpressionSyntax when SelectArguments(operand) is not null:
                return ChosenBy(operand) is { } chosen ? BytesIn(chosen) : null;
            case CallExpressionSyntax { Function: { } function }
                when function.Text.ToLowerInvariant() is ".strsub" or ".strcat":
            case CallExpressionSyntax { Callee: { } callee } when SymbolOf(callee) is { Kind: SymbolKind.Func }:
            case NameExpressionSyntax name when SymbolOf(name) is { Kind: SymbolKind.MacroParameter or SymbolKind.Binding }:
                return Evaluate(operand) is { Kind: ValueKind.String, Text: { } text } && text.All(c => c <= 0xff)
                    ? [.. text.Select(c => (long)c)]
                    : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// A structure or a union, and the members it holds: each gets its offset and its size,
    /// and the type takes its own size from them. A union puts every member at zero and is
    /// as big as its largest.
    /// </summary>
    private void LayOut(Symbol type)
    {
        long offset = 0;
        long largest = 0;
        long members = 0;
        foreach (var member in type.Body?.Symbols ?? [])
        {
            if (member.Kind != SymbolKind.Member)
                continue;
            // The type a member names is worth keeping on it: emission walks into it, and
            // nothing else would have resolved it unless a path happened to reach through.
            member.Type ??= (member.Data as DataDirectiveSyntax)?.Type is { } named ? SymbolOf(named) : null;

            // A member reserves room and holds no value, so an operand is reported rather than
            // silently ignored: `colors: .word 16`, meant as sixteen words, would reserve two bytes.
            var room = member.Data is DataDirectiveSyntax data
                && (DataSyntax.IsElementType(data) || DataSyntax.NameOf(data) == ".res")
                ? RoomFor(data)
                : null;
            if (member.Data is DataDirectiveSyntax element && DataSyntax.IsElementType(element))
            {
                var spelled = element.Directive.Text;
                SyntaxNode? valued = element.Tail switch
                {
                    InlineDataSyntax { Values: [var first, ..] } => first,
                    BracedDataSyntax braced => braced.Value,
                    _ => null,
                };
                if (valued is not null)
                {
                    Report(valued, Catalogue.MemberHasNoValue.Says(member.Name, spelled));
                }
                else if (element.Count is { Count: null } count)
                {
                    Report(count, Catalogue.MemberCountNotANumber.Says(member.Name, spelled));
                }
            }
            if (room is null)
            {
                Report(member.DeclarationSpan, Catalogue.MemberReservesNothing.Says(member.Name), []);
                continue;
            }

            member.Value = Value.Of(type.Kind == SymbolKind.Union ? 0 : offset);
            member.Size = room.Value.Bytes;
            member.Count = room.Value.Elements;
            evaluated.Add(member);
            if (type.Kind == SymbolKind.Union)
                largest = Math.Max(largest, room.Value.Bytes);
            else
                offset += room.Value.Bytes;
            members++;
        }
        type.Size = type.Kind == SymbolKind.Union ? largest : offset;
        type.Count = members;
    }
}
