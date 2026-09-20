using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What a data directive's syntax says beyond its slots: whether it is an element type, and
/// the body it opens, which is a block rather than a piece of the line. Binding, evaluation,
/// layout and emission all ask the same questions, so they are answered in one place.
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

    /// <summary>
    /// The block of values or <c>member = value</c> lines the directive's line opens, or null
    /// when it opens none.
    /// </summary>
    public static BlockSyntax? BodyOf(DataDirectiveSyntax directive)
    {
        if (directive.Tail is not DataBodySyntax)
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
