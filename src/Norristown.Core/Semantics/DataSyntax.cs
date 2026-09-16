using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What a data directive's syntax says: its element type, its count, its values and the body
/// it opens. Binding, evaluation, layout and emission all read the same pieces, so they are
/// read in one place.
/// </summary>
public static class DataSyntax
{
    /// <summary>The directive's name in lower case, such as <c>.byte</c>, or empty when it has none.</summary>
    public static string NameOf(SyntaxNode directive) =>
        directive.ChildTokens.Length > 0 ? directive.ChildTokens[0].Text.ToLowerInvariant() : "";

    /// <summary>
    /// Whether the directive is an element type: <c>.byte</c>, <c>.word</c>, <c>.addr</c>,
    /// <c>.faraddr</c>, <c>.dword</c> or <c>.type T</c>, whose size is its elements'.
    /// </summary>
    public static bool IsElementType(SyntaxNode directive) =>
        IsRecord(directive) || SyntaxFacts.ElementSize(NameOf(directive)) is not null;

    /// <summary>Whether the directive is <c>.type T</c>, a record or an array of them.</summary>
    public static bool IsRecord(SyntaxNode? directive) =>
        directive is { Kind: SyntaxKind.DataDirective } && NameOf(directive) == ".type";

    /// <summary>The <c>T</c> of a <c>.type T</c>, or null for any other directive.</summary>
    public static SyntaxNode? TypeOf(SyntaxNode? directive) =>
        IsRecord(directive) ? directive!.ChildNodes.FirstOrDefault(c => c.Kind is SyntaxKind.NameExpression) : null;

    /// <summary>The <c>[n]</c> or <c>[]</c> written after the element type, or null.</summary>
    public static SyntaxNode? CountOf(SyntaxNode directive) =>
        directive.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.ElementCount);

    /// <summary>The <c>n</c> of <c>[n]</c>, or null for <c>[]</c> and for no count at all.</summary>
    public static SyntaxNode? CountExpressionOf(SyntaxNode directive) => CountOf(directive)?.ChildNodes.FirstOrDefault();

    /// <summary>The braced values written on the directive's line, <c>{ 1, 2 }</c> or <c>{ x = 1 }</c>, or null.</summary>
    public static SyntaxNode? BracedOf(SyntaxNode directive) =>
        directive.ChildNodes.FirstOrDefault(c => c.Kind is SyntaxKind.ValueList or SyntaxKind.RecordValues);

    /// <summary>
    /// The values written after the directive on its line: its operands for any directive that
    /// is not an element type, and the unbraced values of one that is.
    /// </summary>
    public static IReadOnlyList<SyntaxNode> ValuesOf(SyntaxNode directive)
    {
        var type = TypeOf(directive);
        return [.. directive.ChildNodes.Where(c =>
            c != type && c.Kind is not (SyntaxKind.ElementCount or SyntaxKind.ValueList or SyntaxKind.RecordValues))];
    }

    /// <summary>
    /// The block of values or <c>member = value</c> lines the directive's line opens, or null
    /// when it opens none.
    /// </summary>
    public static SyntaxNode? BodyOf(SyntaxNode directive)
    {
        if (directive.ChildTokens.Length == 0 || directive.ChildTokens[^1].Kind != SyntaxKind.OpenBrace)
            return null;
        var line = LineOf(directive);
        return line?.Parent is { Green: GreenBlock { BlockKind: BlockKind.DataBody or BlockKind.RecordInitializer } } block
            && block.ChildNodes.Length > 0 && block.ChildNodes[0] == line
            ? block
            : null;
    }

    /// <summary>The element directive a <c>.data</c> declaration writes after its <c>:</c>, or null.</summary>
    public static SyntaxNode? ElementOf(SyntaxNode? declaration) =>
        declaration is { Kind: SyntaxKind.DataDeclaration }
            ? declaration.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.DataDirective)
            : null;

    /// <summary>The name a <c>.data</c> declaration declares, or null when it writes none.</summary>
    public static SyntaxToken? DeclaredName(SyntaxNode declaration)
    {
        foreach (var token in declaration.ChildTokens)
        {
            if (token.Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic)
                return token;
        }
        return null;
    }

    /// <summary>
    /// The directive whose body a line of values is in: the element type every value on the
    /// line is one of. A body may hold conditionals and repetitions, so the walk goes up
    /// through those to the block the directive opens.
    /// </summary>
    public static SyntaxNode? DirectiveOfValues(SyntaxNode values)
    {
        for (var at = LineOf(values)?.Parent; at is not null; at = at.Parent)
        {
            if (at.Green is not GreenBlock block)
                return null;
            if (block.BlockKind == BlockKind.DataBody)
            {
                var opener = at.ChildNodes[0].Statement;
                return opener?.Kind switch
                {
                    SyntaxKind.DataDirective => opener,
                    SyntaxKind.DataDeclaration => ElementOf(opener),
                    SyntaxKind.LabeledLine => opener.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.DataDirective),
                    _ => null,
                };
            }
            if (block.BlockKind is not (BlockKind.If or BlockKind.Repeat or BlockKind.Each))
                return null;
        }
        return null;
    }

    /// <summary>The line a node is written on.</summary>
    public static SyntaxNode? LineOf(SyntaxNode node)
    {
        var line = node;
        while (line is not null && line.Green is not GreenLine)
            line = line.Parent;
        return line;
    }
}
