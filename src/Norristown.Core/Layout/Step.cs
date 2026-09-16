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
/// <param name="Label">The label it declares, for a step that is one; null for every other.</param>
public readonly record struct Step(
    SyntaxNode Statement, Expansion? On, Symbol? Routine, int Stream, Symbol? Label);
