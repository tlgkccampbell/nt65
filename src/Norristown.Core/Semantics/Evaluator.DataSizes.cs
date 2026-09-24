using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Computes how much room a declaration takes and how many elements that is. These are the
/// values <c>.sizeof</c> and <c>.countof</c> return, and the offsets and sizes a structure gives
/// its members.
/// <para>
/// This is nt65's own arithmetic rather than anything the output contains, so it is computed
/// with the conditionals decided and the repetitions unrolled. It is null wherever nt65 cannot
/// determine it, such as for an <c>.align</c>, whose size depends on where it lands.
/// </para>
/// </summary>
internal sealed partial class Evaluator
{
    /// <summary>
    /// Returns how much room a data directive takes, without reporting anything, for a caller
    /// that has already reported its problems.
    /// </summary>
    public static DataSize? DataSizeOf(
        StatementSyntax directive,
        SegmentTable segments,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        Func<string, long?>? binaryLength,
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null,
        Configuration? configuration = null) =>
        new Evaluator(segments, resolved, null, binaryLength, bound, configuration: configuration).RoomFor(directive);

    /// <summary>
    /// Returns the number of elements an element type declares with its count and the number its
    /// values add up to, either of which may be unknown. The two have to agree.
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

    /// <summary>Returns the bytes that a literal or a mapped string becomes, or null for anything else.</summary>
    public static IReadOnlyList<long>? BytesOf(
        SyntaxNode argument,
        SegmentTable segments,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null) =>
        new Evaluator(segments, resolved, null, null, bound).BytesIn(argument);

    /// <summary>
    /// Returns how much room a data directive takes, as the bytes it generates and the number of
    /// elements they form. Returns null when nt65 cannot determine it, as for an <c>.align</c>,
    /// whose size depends on where it lands, or for a directive whose operands do not add up.
    /// </summary>
    private DataSize? RoomFor(StatementSyntax statement)
    {
        // A line in a data body holds values, each one element of the body's element type.
        if (statement is DataValuesSyntax values)
        {
            return DataSyntax.DirectiveOfValues(values) is { } of && ElementWidth(of) is { } each
                ? Spread(values.Values, each)
                : null;
        }
        if (statement is not DataDirectiveSyntax directive)
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

            // An `.align` generates as many bytes as it takes to reach the next boundary, which
            // depends on where it lands, so it has no size of its own.
            default:
                return null;
        }
    }

    /// <summary>
    /// Returns the room an element type takes. It has as many elements as its count gives, or as
    /// its values add up to, or one when it has neither. Each element is as big as the element
    /// type. For a record, that is its type's size, which is also the stride of an array of
    /// records.
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
    /// Returns the number of bytes one element of an element type takes, which for a record is
    /// its type's size. The type is laid out when the program's symbols are evaluated, whether or
    /// not anything names it, so computing how much room a declaration takes never lays one out.
    /// </summary>
    private long? ElementWidth(DataDirectiveSyntax directive)
    {
        if (directive.Type is not { } named)
            return SyntaxFacts.ElementSize(DataSyntax.NameOf(directive));
        if (SymbolOf(named) is not { } type)
            return null;
        EnsureEvaluated(type);
        return type.IsLayout ? type.Size : null;
    }

    /// <summary>Returns the <c>n</c> of <c>[n]</c>, or null when there is none or it is not a constant.</summary>
    private long? DeclaredCount(DataDirectiveSyntax directive) =>
        directive.Count?.Count is { } count ? Evaluate(count).AsNumber() : null;

    /// <summary>
    /// Returns the number of elements an element type's values add up to, whether the values
    /// follow it on the line, appear in braces, or appear in the body its line opens. Returns null
    /// when it has no values, or when nt65 cannot count them.
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
        return directive.Tail is InlineDataSyntax inline ? Spread(inline.Values, 1).Elements : null;
    }

    /// <summary>
    /// Returns the number of bytes mixed data takes, counting its lines and the data declared in
    /// it, with its conditionals decided and its repetitions unrolled. Returns null when an
    /// <c>.align</c> in it makes the size depend on where it lands.
    /// </summary>
    private long? RoomForMixed(BlockSyntax block) => Total(block.Members, 1, BytesOnLine, NestedBytes);

    /// <summary>
    /// Determines whether mixed data contains a macro call, whose bytes are unknown until it is
    /// expanded.
    /// </summary>
    private static bool Expands(Symbol data) =>
        data.Definition is BlockSyntax block
        && block.DescendantNodes().Any(node => node is MacroCallSyntax or BlockSpliceSyntax);

    /// <summary>
    /// Returns the bytes a block inside mixed data takes, or null when nt65 cannot determine
    /// them.
    /// </summary>
    private long? NestedBytes(BlockSyntax nested) => nested.BlockKind switch
    {
        BlockKind.Data => RoomForMixed(nested),
        BlockKind.DataBody or BlockKind.RecordInitializer => BytesOnLine(nested.Opener.Statement),
        _ => null,
    };

    /// <summary>
    /// Returns the bytes one line of mixed data takes, or null when nt65 cannot determine them. A
    /// macro call emits its bytes only once it is expanded, which is after every constant and
    /// every shape has its value, so the room a call takes is not known here.
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
    /// Adds up the lines of a body, taking one number per line from <paramref name="line"/> and
    /// one per block from <paramref name="block"/>. The conditionals are decided as the build
    /// decides them, and the repetitions are unrolled one iteration at a time. Returns null as
    /// soon as any part is unknown.
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
                part = child is LineSyntax childLine ? line(childLine.Statement) : 0;
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
                        part = Iterations(nested, repetition, () => Total(nested.Members, 1, line, block));
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
    /// Determines whether a branch of a conditional is taken. The build's decision is used where
    /// the build evaluated the condition. Otherwise the condition is evaluated as one inside an
    /// expansion would be.
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
    /// Returns the sum of <paramref name="body"/> over every iteration of a repetition, with its
    /// binding set to that iteration's index, item or member. Returns null when the iterations
    /// are unknown.
    /// </summary>
    private long? Iterations(BlockSyntax block, RepetitionDirectiveSyntax opener, Func<long?> body)
    {
        var counted = opener.Expression;
        var binding = BindingIn(block, opener);
        List<Action> iterations = [];
        if (opener is RepeatDirectiveSyntax)
        {
            if (Evaluate(counted).AsNumber() is not { } count || count < 0)
                return null;
            if (count > Repetitions.MaximumIterations)
            {
                Report(counted, Repetitions.Beyond(count));
                return null;
            }
            if (binding is null)
                return body() * count;
            for (long i = 0; i < count; i++)
            {
                var index = i;
                iterations.Add(() => arguments[binding] = Value.Of(index));
            }
        }
        else if (SymbolOf(counted) is { Kind: SymbolKind.List } list)
        {
            iterations.AddRange(list.Items.Select(item => (Action)(() => { if (binding is not null) items[binding] = item; })));
        }
        else if (SymbolOf(counted) is { Kind: SymbolKind.Enum, Body: { } walked })
        {
            iterations.AddRange(walked.Symbols.Select(member => (Action)(() =>
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

        try
        {
            long total = 0;
            foreach (var iteration in iterations)
            {
                iteration();
                if (body() is not { } part)
                    return null;
                total += part;
            }
            return total;
        }
        finally
        {
            // The binding is removed however the iterations end, so that no value from them
            // outlives the repetition.
            if (binding is not null)
            {
                arguments.Remove(binding);
                items.Remove(binding);
                members.Remove(binding);
            }
        }
    }

    /// <summary>
    /// Returns the name a repetition binds, found where its body refers to it, or null when
    /// nothing does.
    /// </summary>
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
    /// Returns the room that <paramref name="operands"/> take at <paramref name="width"/> bytes
    /// per element. Each operand is one element, except that text is one element per byte and a
    /// named list is one element per item.
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
    /// Returns the room a binary file takes, reading the file's length directly. The path is
    /// relative to the file that names it, and an offset and a length may narrow it.
    /// </summary>
    private DataSize? RoomForBinary(DataDirectiveSyntax directive, SeparatedSyntaxList<SyntaxNode> operands)
    {
        if (operands.Count == 0 || Evaluate(operands[0]) is not { Kind: ValueKind.String, Text: { } path })
            return null;

        var from = Paths.Beside(directive.Tree.Path, path);
        if (binaryLength?.Invoke(from) is not { } length)
        {
            Report(directive, Catalogue.IncbinUnreadable.Message(path));
            return null;
        }

        var offset = operands.Count > 1 ? Evaluate(operands[1]).AsNumber() ?? 0 : 0;
        var taken = operands.Count > 2 ? Evaluate(operands[2]).AsNumber() : null;
        var available = Math.Max(0, length - offset);
        var bytes = taken is { } wanted ? Math.Min(wanted, available) : available;
        return new DataSize(bytes, bytes);
    }

    /// <summary>
    /// Returns the bytes an operand becomes, or null for anything other than text. A string or
    /// character literal becomes its characters, a charmap applied to one becomes what the
    /// mapping gives them, and text built by a call becomes its bytes.
    /// </summary>
    private IReadOnlyList<long>? BytesIn(SyntaxNode operand)
    {
        // A macro parameter given text is that text, as many bytes as it has.
        if (operand is NameExpressionSyntax bound && BoundItem(bound) is { } item)
            return BytesIn(item);

        // A string constant is its text wherever it is named, as a literal would be. A constant
        // still being evaluated is skipped, because it can only be a number whose value depends
        // on the size of this very line, and it is one element regardless of that value.
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
                Report(operand, Catalogue.CharmapHasNoEntry.Message(charmap.Name, (char)character));
                return null;
            }
            bytes.Add(b);
        }
        return bytes;
    }

    /// <summary>
    /// Returns the bytes of text that an expression builds rather than contains as a literal.
    /// Such an expression is a call to a function whose body is text, a <c>.strcat</c> or
    /// <c>.strsub</c>, a <c>.select</c> that chooses text, or a parameter or binding given any of
    /// these. Returns null for anything that is not text, and for text holding a character above
    /// <c>$ff</c>, which only a charmap turns into a byte and which is reported where it appears.
    /// Nothing else is evaluated here, because this runs while a declaration is being sized, and
    /// evaluating a value that measures that same declaration would recurse.
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
    /// Lays out a structure or a union. Each member gets its offset and size, and the type takes
    /// its own size from them. A union puts every member at offset zero and is as big as its
    /// largest member.
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
            // The type a member names is worth keeping on it. Emission walks into it, and
            // nothing else would have resolved it unless a path happened to reach through.
            member.Type ??= (member.Data as DataDirectiveSyntax)?.Type is { } named ? SymbolOf(named) : null;

            // A member reserves room and holds no value, so an operand is reported rather than
            // silently ignored. For example, `colors: .word 16`, meant as sixteen words, would
            // reserve two bytes.
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
                    Report(valued, Catalogue.MemberHasNoValue.Message(member.Name, spelled));
                }
                else if (element.Count is { Count: null } count)
                {
                    Report(count, Catalogue.MemberCountNotANumber.Message(member.Name, spelled));
                }
            }
            if (room is null)
            {
                Report(member.DeclarationSpan, Catalogue.MemberReservesNothing.Message(member.Name), []);
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
