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
    public static bool Is(SyntaxNode statement) =>
        statement.Kind is SyntaxKind.NextDirective or SyntaxKind.PatchDirective;

    /// <summary>Whether a statement is something an annotation can be about.</summary>
    public static bool IsAnnotatable(SyntaxNode statement) => statement.Kind switch
    {
        SyntaxKind.InstructionStatement or SyntaxKind.DataDirective
            or SyntaxKind.MacroCall or SyntaxKind.BlockSplice => true,
        SyntaxKind.LabeledLine => statement.ChildNodes.Any(IsAnnotatable),
        _ => false,
    };

    /// <summary>Whether a <c>.next</c> is the <c>.next ?</c> that ends a path unchecked.</summary>
    public static bool IsUnchecked(SyntaxNode directive) =>
        directive.Kind == SyntaxKind.NextDirective
        && directive.ChildTokens.Any(token => token.Kind == SyntaxKind.Question);

    /// <summary>The labels an annotation names, each as it was written.</summary>
    public static IReadOnlyList<SyntaxNode> TargetsOf(SyntaxNode directive) =>
        [.. directive.ChildNodes.Where(child => child.Kind == SyntaxKind.NameExpression)];

    /// <summary>
    /// The statement the annotation on <paramref name="line"/> is about: the nearest one
    /// above it among its siblings, with blank lines and the other annotations of the same
    /// statement passed over. A macro call that opens a block argument is a block among the
    /// siblings rather than a line, and the call is its first line.
    /// </summary>
    public static SyntaxNode? Annotated(SyntaxNode line)
    {
        if (line.Parent is not { } container)
            return null;
        var siblings = container.ChildNodes;
        for (var i = siblings.IndexOf(line) - 1; i >= 0; i--)
        {
            var above = siblings[i];
            if (above.Green is GreenBlock { BlockKind: BlockKind.MacroBlock })
                return above.ChildNodes.Length > 0 ? above.ChildNodes[0] : null;
            if (above.Green is GreenBlock)
                return null;
            if (above.Statement is not { } statement)
                return null;
            if (statement.Kind == SyntaxKind.BlankLine || Is(statement))
                continue;
            return IsAnnotatable(statement) ? above : null;
        }
        return null;
    }

    /// <summary>
    /// Why an annotation cannot stand where it does, or null when it can. What it is about
    /// has to be above it: a label below takes the address the annotation's statement ends
    /// at, and an annotation about nothing is a claim about nothing.
    /// </summary>
    public static string? Misplaced(SyntaxNode line, SyntaxNode directive) =>
        Annotated(line) is not null ? null
            : $"`{Spell(directive)}` is about the statement above it, and there is none here";

    /// <summary>The directive as it is written, for a message that names it.</summary>
    public static string Spell(SyntaxNode directive) =>
        directive.Kind == SyntaxKind.PatchDirective ? ".patch" : ".next";
}
