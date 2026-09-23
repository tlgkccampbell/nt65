using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// One statement as the walk reached it, in the order its bytes are written: which writing
/// of it, which routine it is inside, and which stream of bytes it lands in.
/// <para>
/// Layout is the one pass that walks every writing of every line, macros expanded and
/// repetitions unrolled, so the order a routine's statements run in is read from here
/// rather than worked out a second time from the same blocks.
/// </para>
/// </summary>
/// <param name="Statement">The statement, or the label, the walk reached.</param>
/// <param name="On">Which writing of it, or null outside every expansion.</param>
/// <param name="Routine">The routine it is inside, or null at file level.</param>
/// <param name="Stream">Which stream of bytes it lands in.</param>
/// <param name="Segment">The segment its bytes land in, or null outside every segment.</param>
/// <param name="Label">The label the step declares, for a label's step; null for every other.</param>
/// <param name="Closes">
/// Whether the step is where an expansion or a splice written as <paramref name="Statement"/>
/// ends, rather than where it starts. The ends of a macro call with a state signature, and of
/// a block spliced into one, are steps of their own, because the analysis checks both.
/// </param>
public readonly record struct Step(
    SyntaxNode Statement, Expansion? On, Symbol? Routine, int Stream, string? Segment, Symbol? Label, bool Closes = false)
{
    /// <summary>Whether the step marks where an expansion or a splice starts or ends, rather than a statement.</summary>
    public bool IsMarker => Statement is MacroCallSyntax or BlockSpliceSyntax;
}

