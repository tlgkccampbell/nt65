using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// The two directives that tell the analysis what it cannot see for itself: <c>.next</c>,
/// which replaces where flow goes after a statement, and <c>.patch</c>, which acknowledges
/// a store into the instruction stream.
/// <para>
/// Each applies to the statement above it and comes before any following label, so which
/// statement an annotation is about is a question about siblings: only the walk that has
/// the whole run of them can answer it.
/// </para>
/// </summary>
public static class Annotations
{
    /// <summary>Whether a statement is one of the two annotations.</summary>
    public static bool Is(StatementSyntax statement) =>
        statement is NextDirectiveSyntax or PatchDirectiveSyntax;

    /// <summary>Whether a statement is something an annotation can be about.</summary>
    public static bool IsAnnotatable(StatementSyntax statement) => statement switch
    {
        InstructionStatementSyntax or DataDirectiveSyntax
            or MacroCallSyntax or BlockSpliceSyntax => true,
        LabeledLineSyntax { Statement: { } labelled } => IsAnnotatable(labelled),
        _ => false,
    };

    /// <summary>The labels an annotation names, each as it was written.</summary>
    public static IReadOnlyList<NameExpressionSyntax> TargetsOf(StatementSyntax directive) => directive switch
    {
        NextDirectiveSyntax next => [.. next.Targets],
        PatchDirectiveSyntax { Target: { } target } => [target],
        _ => [],
    };

    /// <summary>
    /// The statement the annotation on <paramref name="line"/> is about: the nearest one
    /// above it among its siblings, with blank lines and the other annotations of the same
    /// statement passed over. A macro call that opens a block argument is a block among the
    /// siblings rather than a line, and the call is its first line.
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
            if (above is not LineSyntax { Statement: var statement } written)
                return null;
            if (statement is BlankLineSyntax || Is(statement))
                continue;
            return IsAnnotatable(statement) ? written : null;
        }
        return null;
    }

    /// <summary>
    /// Why an annotation cannot stand where it does, or null when it can. What it is about
    /// has to be above it: a label below takes the address the annotation's statement ends
    /// at, and an annotation about nothing is a claim about nothing.
    /// </summary>
    public static DiagnosticMessage? Misplaced(LineSyntax line, StatementSyntax directive) =>
        Annotated(line) is not null ? null : (DiagnosticMessage?)Catalogue.AnnotationAboutNothing.Says(Spell(directive));

    /// <summary>The directive as it is written, for a message that names it.</summary>
    public static string Spell(StatementSyntax directive) =>
        directive is PatchDirectiveSyntax ? ".patch" : ".next";
}
