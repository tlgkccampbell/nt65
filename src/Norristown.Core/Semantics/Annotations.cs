using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Provides queries about the two annotation directives, which tell the analysis what it cannot
/// see for itself. The <c>.next</c> directive replaces where flow goes after a statement, and
/// <c>.patch</c> acknowledges a store into the instruction stream.
/// <para>
/// Each annotation applies to the statement above it and comes before any following label.
/// Finding the statement an annotation applies to therefore means looking at its sibling lines,
/// so only code that walks the whole list of siblings can find it.
/// </para>
/// </summary>
public static class Annotations
{
    /// <summary>Determines whether a statement is one of the two annotations.</summary>
    public static bool Is(StatementSyntax statement) =>
        statement is NextDirectiveSyntax or PatchDirectiveSyntax;

    /// <summary>Determines whether a statement is one that an annotation can apply to.</summary>
    public static bool IsAnnotatable(StatementSyntax statement) => statement switch
    {
        InstructionStatementSyntax or DataDirectiveSyntax
            or MacroCallSyntax or BlockSpliceSyntax => true,
        LabeledLineSyntax { Statement: { } labelled } => IsAnnotatable(labelled),
        _ => false,
    };

    /// <summary>Returns the labels an annotation names, as they appear in the source.</summary>
    public static IReadOnlyList<NameExpressionSyntax> TargetsOf(StatementSyntax directive) => directive switch
    {
        NextDirectiveSyntax next => [.. next.Targets],
        PatchDirectiveSyntax { Target: { } target } => [target],
        _ => [],
    };

    /// <summary>
    /// Returns the statement that the annotation on <paramref name="line"/> applies to. This is
    /// the nearest statement above the annotation among its siblings, skipping blank lines and
    /// the other annotations of the same statement. A macro call that opens a block argument
    /// appears among the siblings as a block rather than a line, and the call is the block's
    /// first line.
    /// </summary>
    public static LineSyntax? Annotated(LineSyntax line)
    {
        if (line.Parent is not { } container)
            return null;
        var siblings = container.ChildNodes;
        for (var i = siblings.IndexOf(line) - 1; i >= 0; i--)
        {
            var above = siblings[i];
            if (above is BlockSyntax { BlockKind: BlockKind.MacroBlock } block)
                return block.Opener;
            if (above is not LineSyntax { Statement: var statement } candidate)
                return null;
            if (statement is BlankLineSyntax || Is(statement))
                continue;
            return IsAnnotatable(statement) ? candidate : null;
        }
        return null;
    }

    /// <summary>
    /// Returns the reason an annotation is misplaced, or null when it is not. The statement it
    /// applies to must be above it, because a label below it takes the address where that
    /// statement ends. An annotation with no statement above it has nothing to apply to.
    /// </summary>
    public static DiagnosticMessage? Misplaced(LineSyntax line, StatementSyntax directive) =>
        Annotated(line) is not null ? null : (DiagnosticMessage?)Catalogue.AnnotationAboutNothing.Message(Format(directive));

    /// <summary>Returns the directive's name as it appears in the source, for a message that names it.</summary>
    public static string Format(StatementSyntax directive) =>
        directive is PatchDirectiveSyntax ? ".patch" : ".next";
}
