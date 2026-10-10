using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// Works out the labels a <c>.next</c> names. A target that is a list stands for every label in
/// it, and so does a table: data declared as addresses whose items are all code labels or
/// routines, each optionally minus one as in an RTS dispatch table. Both the flow analysis and
/// the cycle spans that layout counts spread a target this way, so that the two agree on where
/// control goes.
/// </summary>
/// <param name="model">The file the <c>.next</c> annotations are in.</param>
/// <param name="steps">The file's statements in the order layout emitted them, which holds the values a body emits.</param>
internal sealed class NextTargets(SemanticModel model, IReadOnlyList<Step> steps)
{
    /// <summary>
    /// Returns whether a symbol is data that holds addresses, which is what a table of targets is.
    /// That is data declared with <c>.addr</c> or <c>.faraddr</c>, records whose type has a member
    /// declared so, and mixed data with a member that is either.
    /// </summary>
    public static bool IsAddressData(Symbol symbol) => symbol switch
    {
        { Kind: not SymbolKind.Data } => false,
        { Data: DataDirectiveSyntax element } => IsAddressElement(element)
            || (element.IsRecord && symbol.Type is { } type && HoldsAddresses(type, [])),
        { Data: null, Body: { } body } => body.Symbols.Any(IsAddressData),
        _ => false,
    };

    /// <summary>
    /// Returns the labels one <c>.next</c> names, with a list or a table spread into the labels it
    /// holds. A target nt65 cannot resolve, and data that is not a table of code labels, name
    /// nowhere code goes, so they are left out.
    /// </summary>
    public IEnumerable<(Symbol Symbol, Expansion? At)> Named(NextDirectiveSyntax next, Expansion? on) =>
        next.Targets.SelectMany(name => NamedBy(name, on));

    /// <summary>
    /// Returns the labels one target of a <c>.next</c> names. A <c>list</c> parameter names every
    /// label the call gave it, a list or a table names every label it holds, and anything else
    /// names itself. Data that is not a table of code labels names nothing, and so does a name nt65
    /// cannot resolve.
    /// </summary>
    public IEnumerable<(Symbol Symbol, Expansion? At)> NamedBy(NameExpressionSyntax name, Expansion? on)
    {
        if (model.SymbolOf(name, on) is { Kind: SymbolKind.MacroParameter, Parameter.Kind: ParameterKind.List } list
            && model.GivenAt(list, on) is { Argument: var given, Caller: var caller })
        {
            foreach (var item in given.Items)
            {
                if (Targets.Of(model, item, caller) is { } each)
                    yield return each;
            }
            yield break;
        }

        if (Targets.Of(model, name, on) is not { } target)
            yield break;
        var spread = Spread(target.Symbol, on).ToList();
        if (spread.Count > 0)
        {
            foreach (var item in spread)
                yield return item;
            yield break;
        }

        // Data that is not a table of code labels names nowhere code goes. It is reported where
        // the targets are checked rather than followed into the bytes.
        if (!IsDataWithoutCodeLabels(target.Symbol, on))
            yield return target;
    }

    /// <summary>
    /// Returns whether a symbol is data that does not spread to code labels, and so names nowhere
    /// code goes.
    /// </summary>
    public bool IsDataWithoutCodeLabels(Symbol symbol, Expansion? on) =>
        symbol.Kind == SymbolKind.Data && !Spread(symbol, on).Any();

    /// <summary>
    /// Returns the labels a table or a list expands to. The result is empty when the target does
    /// not expand to labels and so names only itself.
    /// </summary>
    public IEnumerable<(Symbol Symbol, Expansion? At)> Spread(Symbol target, Expansion? on)
    {
        foreach (var (item, at) in ItemsOf(target, on))
        {
            if (Targets.Of(model, Stripped(item), at) is { } named
                && (named.Symbol.Kind is SymbolKind.Label || named.Symbol.Signature is not null))
            {
                yield return named;
            }
            else
                yield break;
        }
    }

    /// <summary>
    /// Returns the items of a list, or the addresses a table holds, each paired with the
    /// <see cref="Expansion"/> it is in. Any other symbol yields none. The items are what
    /// <see cref="Spread"/> reads labels from.
    /// </summary>
    public IEnumerable<(SyntaxNode Item, Expansion? On)> ItemsOf(Symbol target, Expansion? on) =>
        target.Kind == SymbolKind.List
            ? target.Items.Select(item => (Item: (SyntaxNode)item, On: on))
            : ItemsOfTable(target);

    /// <summary>Returns whether a directive's element type is <c>.addr</c> or <c>.faraddr</c>.</summary>
    private static bool IsAddressElement(DataDirectiveSyntax element) =>
        element.Directive.DirectiveKind is DirectiveKind.Addr or DirectiveKind.FarAddr;

    /// <summary>
    /// Returns whether a struct or union has a member declared as an address, directly or in a
    /// record it holds. <paramref name="seen"/> holds the types already being asked about, so a
    /// type that contains itself ends the walk.
    /// </summary>
    private static bool HoldsAddresses(Symbol type, HashSet<Symbol> seen) =>
        type.IsLayout && seen.Add(type) && (type.Body?.Symbols ?? []).Any(member =>
            member.Kind == SymbolKind.Member
            && (member.Data is DataDirectiveSyntax element && IsAddressElement(element)
                || member.Type is { } inner && HoldsAddresses(inner, seen)));

    /// <summary>
    /// Returns a table item without the <c>- 1</c> of an RTS dispatch table, which names the same
    /// label either way.
    /// </summary>
    private static SyntaxNode Stripped(SyntaxNode item) =>
        item is BinaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.Minus } difference ? difference.Left : item;

    /// <summary>
    /// Returns the values a braced record gives the address members of <paramref name="type"/>.
    /// </summary>
    private static IEnumerable<SyntaxNode> AddressesIn(Symbol type, SyntaxNode record) =>
        AddressesIn(type, record is RecordValuesSyntax values ? values.Members : []);

    /// <summary>
    /// Returns the values that <c>member = value</c> pairs give the address members of
    /// <paramref name="type"/>, in the order the type declares its members. A member that is an
    /// array of addresses gives each item of its list, and a member that is a record, or an array
    /// of them, gives the addresses its own values hold. A member no value names holds zero, which
    /// is no address of code, so it gives nothing.
    /// </summary>
    private static IEnumerable<SyntaxNode> AddressesIn(Symbol type, IEnumerable<StatementSyntax> pairs)
    {
        if (type.IsCyclic)
            yield break;
        var given = new Dictionary<string, SyntaxNode>(StringComparer.Ordinal);
        foreach (var pair in pairs.OfType<MemberValueSyntax>())
            given[pair.Name.Text] = pair.Value;
        foreach (var member in type.Body?.Symbols ?? [])
        {
            if (member.Kind != SymbolKind.Member || given.GetValueOrDefault(member.Name) is not { } value)
                continue;
            var items = value is ValueListSyntax list ? [.. list.Values] : new[] { value };
            foreach (var item in items)
            {
                if (member.Type is { IsLayout: true } inner)
                {
                    foreach (var address in AddressesIn(inner, item))
                        yield return address;
                }
                else if (member.Data is DataDirectiveSyntax element && IsAddressElement(element))
                    yield return item;
            }
        }
    }

    /// <summary>
    /// Returns the addresses a table holds, each paired with the <see cref="Expansion"/> it is in.
    /// A table is data that <see cref="IsAddressData"/> accepts. Its addresses are the values of
    /// an <c>.addr</c> or <c>.faraddr</c> declaration, the values of the address members of
    /// records, and those of each member of mixed data in turn. Values in a body are read from the
    /// expansions layout made of them, since each iteration of a repetition there may emit a value
    /// differently. A label on an <c>.addr</c> or <c>.faraddr</c> line is a table too, of the
    /// values on that line. Any other symbol yields no values.
    /// </summary>
    private IEnumerable<(SyntaxNode Item, Expansion? On)> ItemsOfTable(Symbol target)
    {
        if (target.Kind == SymbolKind.Label)
        {
            foreach (var item in ItemsAfterLabel(target))
                yield return item;
            yield break;
        }
        if (!IsAddressData(target))
            yield break;
        if (target.Data is not DataDirectiveSyntax element)
        {
            foreach (var member in target.Body?.Symbols ?? [])
            {
                foreach (var item in ItemsOfTable(member))
                    yield return item;
            }
            yield break;
        }

        var type = element.IsRecord ? target.Type : null;
        foreach (var value in DataLengths.ElementsOf(element))
        {
            foreach (var item in type is null ? [value] : AddressesIn(type, value))
                yield return (item, null);
        }
        if (type is not null && element.Tail is BracedDataSyntax { Value: RecordValuesSyntax record })
        {
            foreach (var item in AddressesIn(type, record))
                yield return (item, null);
        }
        if (DataSyntax.BodyOf(element) is not { } body)
            yield break;

        // A single record's body holds its `member = value` lines; an array's holds records.
        if (type is not null && body.BlockKind == BlockKind.RecordInitializer)
        {
            foreach (var item in AddressesIn(type, body.Members.Skip(1).OfType<LineSyntax>().Select(line => line.Statement)))
                yield return (item, null);
            yield break;
        }
        foreach (var step in steps)
        {
            if (step.Statement is DataValuesSyntax values && DataSyntax.DirectiveOfValues(values) == element)
            {
                foreach (var value in DataLengths.ElementsOf(values))
                {
                    foreach (var item in type is null ? [value] : AddressesIn(type, value))
                        yield return (item, step.On);
                }
            }
        }
    }

    /// <summary>
    /// Returns the addresses on the <c>.addr</c> or <c>.faraddr</c> line that directly follows a
    /// label, each paired with the <see cref="Expansion"/> it is in. A label on any other line
    /// yields no values.
    /// </summary>
    private IEnumerable<(SyntaxNode Item, Expansion? On)> ItemsAfterLabel(Symbol label)
    {
        for (var i = 0; i < steps.Count - 1; i++)
        {
            if (steps[i].Label != label)
                continue;
            var next = steps[i + 1];
            if (next.Statement is DataDirectiveSyntax { IsRecord: false } element && IsAddressElement(element))
            {
                foreach (var value in DataLengths.ElementsOf(element))
                    yield return (value, next.On);
            }
            yield break;
        }
    }
}
