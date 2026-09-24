using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Answers questions about a data directive that its own child elements do not answer. These
/// are whether it is an element type, and which body it opens, since the body is a block rather
/// than part of the line. Binding, evaluation, layout and emission all ask the same questions,
/// so they are answered in one place.
/// </summary>
public static class DataSyntax
{
    /// <summary>Returns the directive's name in lower case, such as <c>.byte</c>.</summary>
    public static string NameOf(DataDirectiveSyntax directive) => directive.Directive.Text.ToLowerInvariant();

    /// <summary>
    /// Determines whether the directive is an element type, whose size is the size of its
    /// elements. The element types are <c>.byte</c>, <c>.word</c>, <c>.addr</c>,
    /// <c>.faraddr</c>, <c>.dword</c> and <c>.type T</c>.
    /// </summary>
    public static bool IsElementType(DataDirectiveSyntax directive) =>
        directive.IsRecord || SyntaxFacts.ElementSize(directive.Directive.DirectiveKind) is not null;

    /// <summary>
    /// Returns the block of values or <c>member = value</c> lines that the directive's line
    /// opens, or null when it opens none.
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
    /// Returns the directive whose body contains a line of values, which gives the element type
    /// of every value on the line. A body may hold conditionals and repetitions, so the search
    /// goes up through those to the block the directive opens.
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
