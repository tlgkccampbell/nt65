using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents where each routine of a program runs from: under an interrupt, in the rest of the
/// program, or both. It is found by walking the routines from every interrupt handler, and from
/// every other routine that nothing hands control to, along calls, tail calls, branches out of a
/// routine, <c>.next</c> targets and <c>.fallthrough</c>.
/// <para>
/// A routine marked <c>interrupt</c> is a handler. So is one that nothing in the program calls,
/// jumps or runs into and that returns with <c>rti</c> on some path, as a routine whose address
/// only a vector table holds does. The warning <c>rti-outside-handler</c> still asks for the mark
/// on such a routine, but the walk treats it as the handler it is.
/// </para>
/// <para>
/// A handler's walk does not go into another handler, since a handler runs by being interrupted
/// into, not by being called. A transfer whose target nt65 cannot identify, such as a call through
/// a pointer or one with <c>.next ?</c> under it, stops the walk. Each such transfer is listed
/// with <see cref="Unfollowed"/>, because the routines it reaches are not marked.
/// </para>
/// </summary>
public sealed class RoutineContexts
{
    private readonly Dictionary<Symbol, RoutineContext> contexts;
    private readonly Dictionary<Symbol, List<Symbol>> handlers;
    private readonly Dictionary<Symbol, List<TextSpan>> unfollowed;
    private readonly HashSet<Symbol> unmarked;

    private RoutineContexts(
        Dictionary<Symbol, RoutineContext> contexts, Dictionary<Symbol, List<Symbol>> handlers,
        Dictionary<Symbol, List<TextSpan>> unfollowed, HashSet<Symbol> unmarked)
    {
        this.contexts = contexts;
        this.handlers = handlers;
        this.unfollowed = unfollowed;
        this.unmarked = unmarked;
    }

    /// <summary>Returns the contexts of <paramref name="analysis"/>'s program.</summary>
    /// <param name="analysis">The analysis of the program.</param>
    public static RoutineContexts Of(ProgramAnalysis analysis)
    {
        var program = analysis.Program;
        var routines = new List<Symbol>();
        var targets = new Dictionary<Symbol, List<Symbol>>();
        var hasCaller = new HashSet<Symbol>();
        var returnsByRti = new HashSet<Symbol>();
        var unfollowed = new Dictionary<Symbol, List<TextSpan>>();
        foreach (var file in analysis.Files)
        {
            foreach (var region in file.Flow.Regions)
            {
                var routine = program.Current(region.Routine);
                if (!targets.TryGetValue(routine, out var list))
                {
                    targets[routine] = list = [];
                    routines.Add(routine);
                }
                foreach (var block in region.Blocks)
                {
                    // An `rti` with a `.next` is a computed jump, not a handler's return.
                    if (block is
                        {
                            End: BlockEnd.Return,
                            Next: null,
                            Steps: [.., { Statement: InstructionStatementSyntax { MnemonicKind: MnemonicKind.Rti } }],
                        })
                    {
                        returnsByRti.Add(routine);
                    }
                    if (block.CallsUnknown && block.Steps.Count > 0 && StepLines.Of(file.Model.Tree, block.Steps[^1]) is { } shown)
                    {
                        if (!unfollowed.TryGetValue(routine, out var spans))
                            unfollowed[routine] = spans = [];
                        if (!spans.Contains(shown.Span))
                            spans.Add(shown.Span);
                    }
                    foreach (var target in block.Calls.Concat(block.Leaves).Append(block.RunsInto))
                    {
                        if (target is null || RegisterWalk.Owner(target) is not { } owner)
                            continue;
                        var callee = program.Current(owner);
                        if (!list.Contains(callee))
                            list.Add(callee);
                        if (callee != routine)
                            hasCaller.Add(callee);
                    }
                }
            }
        }

        var unmarked = returnsByRti.Where(routine => !IsHandler(routine) && !hasCaller.Contains(routine)).ToHashSet();
        var contexts = new Dictionary<Symbol, RoutineContext>();
        var reachedBy = new Dictionary<Symbol, List<Symbol>>();
        foreach (var handler in routines.Where(Handled))
        {
            foreach (var routine in Reach([handler]))
            {
                contexts[routine] = contexts.GetValueOrDefault(routine) | RoutineContext.Interrupt;
                if (!reachedBy.TryGetValue(routine, out var by))
                    reachedBy[routine] = by = [];
                by.Add(handler);
            }
        }
        foreach (var routine in Reach(routines.Where(routine => !Handled(routine) && !hasCaller.Contains(routine))))
            contexts[routine] = contexts.GetValueOrDefault(routine) | RoutineContext.Main;
        return new RoutineContexts(contexts, reachedBy, unfollowed, unmarked);

        bool Handled(Symbol routine) => IsHandler(routine) || unmarked.Contains(routine);

        HashSet<Symbol> Reach(IEnumerable<Symbol> from)
        {
            var reached = new HashSet<Symbol>();
            var pending = new Stack<Symbol>(from);
            while (pending.TryPop(out var routine))
            {
                if (!reached.Add(routine))
                    continue;
                foreach (var callee in targets.GetValueOrDefault(routine) ?? [])
                {
                    if (!Handled(callee))
                        pending.Push(callee);
                }
            }
            return reached;
        }
    }

    /// <summary>Returns whether <paramref name="routine"/> is marked <c>interrupt</c>.</summary>
    /// <param name="routine">The routine, as the program has it now.</param>
    public static bool IsHandler(Symbol routine) => routine.Signature?.IsInterrupt == true;

    /// <summary>
    /// Returns whether <paramref name="routine"/> is walked as an interrupt handler. It is where
    /// the routine is marked <c>interrupt</c>, and where <see cref="IsUnmarkedHandler"/> says so.
    /// </summary>
    /// <param name="routine">The routine, as the program has it now.</param>
    public bool Handles(Symbol routine) => IsHandler(routine) || unmarked.Contains(routine);

    /// <summary>
    /// Returns whether <paramref name="routine"/> is walked as an interrupt handler without being
    /// marked <c>interrupt</c>, because nothing calls it and it returns with <c>rti</c>.
    /// </summary>
    /// <param name="routine">The routine, as the program has it now.</param>
    public bool IsUnmarkedHandler(Symbol routine) => unmarked.Contains(routine);

    /// <summary>Returns where <paramref name="routine"/> runs from.</summary>
    /// <param name="routine">The routine, as the program has it now.</param>
    public RoutineContext Of(Symbol routine) => contexts.GetValueOrDefault(routine);

    /// <summary>
    /// Returns the interrupt handlers that reach <paramref name="routine"/>, in the order the files
    /// declare them. A handler is among its own.
    /// </summary>
    /// <param name="routine">The routine, as the program has it now.</param>
    public IReadOnlyList<Symbol> HandlersOf(Symbol routine) => handlers.GetValueOrDefault(routine) ?? [];

    /// <summary>
    /// Returns the lines of <paramref name="routine"/> that hand control to somewhere nt65 cannot
    /// identify, where the walk stops. Each is the span of the line in the routine's own file.
    /// </summary>
    /// <param name="routine">The routine, as the program has it now.</param>
    public IReadOnlyList<TextSpan> Unfollowed(Symbol routine) => unfollowed.GetValueOrDefault(routine) ?? [];
}
