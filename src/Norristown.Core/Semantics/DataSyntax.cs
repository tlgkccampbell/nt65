using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What a data directive's syntax says: its element type, its count, its values and the body
/// it opens. Binding, evaluation, layout and emission all read the same pieces, so they are
/// read in one place.
/// </summary>
public static class DataSyntax
{
    /// <summary>The directive's name in lower case, such as <c>.byte</c>.</summary>
    public static string NameOf(DataDirectiveSyntax directive) => directive.Directive.Text.ToLowerInvariant();

    /// <summary>
    /// Whether the directive is an element type: <c>.byte</c>, <c>.word</c>, <c>.addr</c>,
    /// <c>.faraddr</c>, <c>.dword</c> or <c>.type T</c>, whose size is its elements'.
    /// </summary>
    public static bool IsElementType(DataDirectiveSyntax directive) =>
        directive.IsRecord || SyntaxFacts.ElementSize(NameOf(directive)) is not null;

    /// <summary>The <c>T</c> of a <c>.type T</c>, or null for any other directive.</summary>
    public static NameExpressionSyntax? TypeOf(DataDirectiveSyntax? directive) =>
        directive is { IsRecord: true } ? directive.ChildNodes.OfType<NameExpressionSyntax>().FirstOrDefault() : null;

    /// <summary>The braced values written on the directive's line, <c>{ 1, 2 }</c> or <c>{ x = 1 }</c>, or null.</summary>
    public static SyntaxNode? BracedOf(DataDirectiveSyntax directive) =>
        directive.ChildNodes.FirstOrDefault(c => c is ValueListSyntax or RecordValuesSyntax);

    /// <summary>
    /// The values written after the directive on its line: its operands for any directive that
    /// is not an element type, and the unbraced values of one that is.
    /// </summary>
    public static IReadOnlyList<SyntaxNode> ValuesOf(DataDirectiveSyntax directive)
    {
        var type = TypeOf(directive);
        return [.. directive.ChildNodes.Where(c =>
            c != type && c is not (ElementCountSyntax or ValueListSyntax or RecordValuesSyntax))];
    }

    /// <summary>
    /// The block of values or <c>member = value</c> lines the directive's line opens, or null
    /// when it opens none.
    /// </summary>
    public static BlockSyntax? BodyOf(DataDirectiveSyntax directive)
    {
        if (directive.ChildTokens is not [.., { Kind: SyntaxKind.OpenBrace }])
            return null;
        var line = directive.FirstAncestorOrSelf<LineSyntax>();
        return line?.Parent is BlockSyntax { BlockKind: BlockKind.DataBody or BlockKind.RecordInitializer } block
            && block.Opener == line
            ? block
            : null;
    }

    /// <summary>
    /// The directive whose body a line of values is in: the element type every value on the
    /// line is one of. A body may hold conditionals and repetitions, so the walk goes up
    /// through those to the block the directive opens.
    /// </summary>
    public static DataDirectiveSyntax? DirectiveOfValues(DataValuesSyntax values)
    {
        for (var at = values.FirstAncestorOrSelf<LineSyntax>()?.Parent; at is not null; at = at.Parent)
        {
            if (at is not BlockSyntax block)
                return null;
            if (block.BlockKind == BlockKind.DataBody)
            {
                return block.Opener.Statement switch
                {
                    DataDirectiveSyntax directive => directive,
                    DataDeclarationSyntax declaration => declaration.Directive,
                    LabeledLineSyntax labeled => labeled.Statement as DataDirectiveSyntax,
                    _ => null,
                };
            }
            if (block.BlockKind is not (BlockKind.If or BlockKind.Repeat or BlockKind.Each))
                return null;
        }
        return null;
    }
}
