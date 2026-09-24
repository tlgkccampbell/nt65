using Norristown.Semantics;
using Norristown.Syntax;
using static Norristown.Emit.Ca65Directives;
using static Norristown.Emit.Ca65Numbers;

namespace Norristown.Emit;

/// <summary>
/// Writes records, the data declared with a struct or union type, as one directive per member.
/// It reads values in <paramref name="expansion"/>, and it writes through
/// <paramref name="output"/>, which is all it can reach of the emitter.
/// </summary>
/// <param name="model">The model of the file being written.</param>
/// <param name="expansion">The expansion being written, in which every value is read.</param>
/// <param name="output">The emitter the lines are written to.</param>
internal sealed class RecordWriter(SemanticModel model, Expansion? expansion, IRecordOutput output)
{
    /// <summary>
    /// Writes records. A record with values may be on its line or in the block its line opens, or
    /// there may be an array of records in braces. Each record is written as one directive per
    /// member in the type's order, regardless of the order of the values in the source. Room with
    /// no values is zeros, which one <c>.res</c> expresses, unless a member pads with something
    /// else.
    /// </summary>
    public void Records(LineSyntax line, DataDirectiveSyntax directive, Symbol? symbol, Symbol type)
    {
        if (model.RoomFor(directive, expansion) is not { } room)
        {
            output.NotTranspiled(directive);
            return;
        }
        IReadOnlyList<IReadOnlyDictionary<string, MemberValueSyntax>> records;
        if (directive.Tail is BracedDataSyntax { Value: { } braced })
        {
            records = braced is ValueListSyntax list ? [.. list.Values.Select(ValuesIn)] : [ValuesIn(braced)];
        }
        else if (DataSyntax.BodyOf(directive) is { } block)
        {
            records = [ValuesIn(block.Members.Skip(1).OfType<LineSyntax>().Select(member => member.Statement))];
        }
        else if (!Pads(type))
        {
            var reserved = Reservations(room.Bytes).ToList();
            output.Named(line, symbol, $".res {reserved[0]}", (int)reserved[0], type.QualifiedName);
            foreach (var rest in reserved.Skip(1))
                output.Code(line, $"{Emitter.Body}.res {rest}", (int)rest, type.QualifiedName);
            return;
        }
        else
        {
            records = [.. Enumerable.Repeat(ValuesIn([]), (int)room.Elements)];
        }

        if (symbol is not null)
            output.Label(line, symbol);
        long bytes = 0;
        foreach (var record in records)
            bytes += Fields(line, type, record, path: "");
        if (bytes != room.Bytes)
            output.NotTranspiled(directive);
    }

    /// <summary>Writes the records of one line of a body whose type is <paramref name="type"/>.</summary>
    public void Values(LineSyntax line, Symbol type, DataValuesSyntax values)
    {
        foreach (var record in values.Values)
            Fields(line, type, ValuesIn(record), path: "");
    }

    /// <summary>
    /// Returns the <c>member = value</c> pairs of a record by member name, from a braced record or
    /// from the lines of one.
    /// </summary>
    private static IReadOnlyDictionary<string, MemberValueSyntax> ValuesIn(SyntaxNode? record) =>
        ValuesIn(record is RecordValuesSyntax values ? values.Members : []);

    /// <summary>
    /// Returns the <c>member = value</c> pairs among <paramref name="values"/> by member name.
    /// </summary>
    private static IReadOnlyDictionary<string, MemberValueSyntax> ValuesIn(IEnumerable<StatementSyntax> values)
    {
        var named = new Dictionary<string, MemberValueSyntax>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is MemberValueSyntax member)
                named[member.Name.Text] = member;
        }
        return named;
    }

    /// <summary>
    /// Returns whether a type, or a record inside it, has a member that pads with something other
    /// than zero. A type that contains itself has no layout at all, which the analysis has
    /// already reported, so there is nothing here to walk into.
    /// </summary>
    private bool Pads(Symbol type) => !type.IsCyclic && (type.Body?.Symbols ?? []).Any(member =>
        member.Kind == SymbolKind.Member
        && (member.Type is { IsLayout: true } inner ? Pads(inner) : Fill(member) != 0));

    /// <summary>Returns the byte a <c>.res n, fill</c> member pads with, which is zero when it names none.</summary>
    private long Fill(Symbol member) =>
        member.Data is DataDirectiveSyntax { Directive.DirectiveKind: DirectiveKind.Res } data
        && data.Tail is InlineDataSyntax { Values: [_, var padding, ..] }
        && model.ValueOf(padding, expansion).AsNumber() is { } fill
            ? fill & 0xff
            : 0;

    /// <summary>
    /// Writes the members of a type, one directive each, in the order the type declares them, and
    /// returns the number of bytes written. A member that is itself a record, or an array of
    /// them, is written out the same way, so a nested value reaches the fields inside it.
    /// </summary>
    private long Fields(
        LineSyntax line, Symbol type, IReadOnlyDictionary<string, MemberValueSyntax> givenValues, string path)
    {
        // A type that contains itself has no layout to write out, and the analysis has reported it.
        if (type.IsCyclic)
            return 0;
        long bytes = 0;
        var members = (type.Body?.Symbols ?? []).Where(member => member.Kind == SymbolKind.Member).ToList();

        // A union is written as the one member it is given, or its first, and zeros to its size.
        if (type.Kind == SymbolKind.Union && members.Count > 0)
            members = [members.FirstOrDefault(member => givenValues.ContainsKey(member.Name)) ?? members[0]];

        foreach (var member in members)
        {
            if (member.Size is not { } size)
                continue;
            var given = givenValues.GetValueOrDefault(member.Name)?.Value;
            var named = path.Length == 0 ? member.Name : $"{path}::{member.Name}";
            var element = member.Data as DataDirectiveSyntax;

            // An array member takes a braced list, and an array member no value names is zeros.
            if (element is { Count: not null })
            {
                SeparatedSyntaxList<SyntaxNode> items = given is ValueListSyntax list ? list.Values : default;
                if (member.Type is { IsLayout: true } records)
                {
                    for (var i = 0; i < member.Count; i++)
                        bytes += Fields(line, records, ValuesIn(i < items.Count ? items[i] : null), $"{named}[{i}]");
                    continue;
                }
                if (items.Count == 0)
                {
                    foreach (var reserved in Reservations(size))
                        Field(line, $".res {reserved}", named, reserved);
                    bytes += size;
                    continue;
                }
                var (width, bigEndian) = ElementFormat(element);
                Field(line,
                    $"{ForCa65(element.Directive.DirectiveKind, DataSyntax.NameOf(element))} {string.Join(", ", items.Select(item => output.ValueText(item, width, bigEndian)))}",
                    named, size);
                bytes += size;
                continue;
            }

            // A nested record takes a braced list of its own members; anything else is a value.
            if (member.Type is { IsLayout: true } inner)
            {
                bytes += Fields(line, inner, ValuesIn(given), named);
                continue;
            }
            foreach (var (text, part) in Member(member, element, given))
                Field(line, text, named, part);
            bytes += size;
        }

        if (type.Kind == SymbolKind.Union && type.Size is { } whole && whole > bytes)
        {
            foreach (var reserved in Reservations(whole - bytes))
                Field(line, $".res {reserved}, $00", path.Length == 0 ? type.Name : path, reserved);
            bytes = whole;
        }
        return bytes;
    }

    /// <summary>Writes one member's directive, with the path it fills in a comment.</summary>
    private void Field(LineSyntax line, string directive, string path, long size) =>
        output.Code(line, Emitter.Body + directive, (int)size, path);

    /// <summary>
    /// Returns the directives for one member of a record, written with the directive its type
    /// gave it. A member that no value names is zero, and a member reserved by <c>.res</c> takes
    /// text, padded to the room it has with the byte it pads with.
    /// </summary>
    private IEnumerable<(string Text, long Size)> Member(Symbol member, DataDirectiveSyntax? element, SyntaxNode? given)
    {
        var size = member.Size ?? 0;
        var directive = element?.Directive.DirectiveKind ?? DirectiveKind.Res;

        // Room with a fill is what `.res n, fill` says in both languages, so the room a value
        // does not reach is written as the one directive rather than as a row of equal bytes.
        if (directive == DirectiveKind.Res)
        {
            var values = given is null ? [] : model.BytesOf(given, expansion)?.ToList() ?? [];
            var used = Math.Min(values.Count, size);
            if (used > 0)
                yield return ($".byte {string.Join(", ", values.Take((int)used).Select(b => Hex(b & 0xff, 2)))}", used);
            if (size > used)
            {
                foreach (var reserved in Reservations(size - used))
                    yield return ($".res {reserved}, {Hex(Fill(member), 2)}", reserved);
            }
            yield break;
        }
        var (width, bigEndian) = ElementFormat(element!);
        var value = given is null ? Constant(0) : output.ValueText(given, width, bigEndian);
        yield return ($"{ForCa65(directive, SyntaxFacts.TextOf(directive))} {(given is null && bigEndian && width > 2 ? string.Join(", ", Enumerable.Repeat(Hex(0, 2), width)) : value)}", size);
    }
}
