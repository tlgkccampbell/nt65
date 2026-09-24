using Norristown.Flow;
using Norristown.Processor;
using Norristown.Semantics;

namespace Norristown.LanguageServer;

/// <summary>
/// Adds to an instruction's hover what the routine has pushed, top of the stack first, one row
/// per push. Two models of the stack are combined. The saved-register stack says which
/// register's entry value a push holds, and the processor-state analysis's stack says what a
/// <c>php</c> saved, what a constant push holds and where a <c>.frame</c> is. Each knows things
/// the other cannot see.
/// </summary>
internal static class StackRows
{
    /// <summary>
    /// The number of pushes a hover lists before it says how many more there are. A reader cares
    /// most about the top of the stack, which holds what the routine will pull next.
    /// </summary>
    private const int MaximumPushes = 6;

    /// <summary>
    /// Adds the stack rows to <paramref name="card"/>, or a single <c>unknown</c> row where the
    /// analysis lost track of the stack. Nothing is added where neither analysis reached the line.
    /// </summary>
    public static void Add(HoverCard card, ProgramAnalysis analysis, RegisterState? registers, FlowState? state)
    {
        if (registers is null && state is null)
            return;

        // A missing group would read as an empty stack, and a stack the analysis lost track of
        // is not an empty one.
        if (registers?.Stack is null && state?.Stack is null)
        {
            card.Gap();
            card.Row("stack", "unknown");
            return;
        }
        var rows = Pushes(analysis, registers?.Stack, state?.Stack);
        if (rows.Count == 0)
            return;
        card.Gap();
        for (var i = 0; i < rows.Count && i < MaximumPushes; i++)
            card.Row(i == 0 ? "stack" : "", rows[i]);

        // A routine that has pushed a lot holds more than a reader can take in at a glance, and
        // the pushes it will pull back next are the ones on top, so only those are listed.
        if (rows.Count > MaximumPushes)
            card.Row("", $"and {rows.Count - MaximumPushes} more");
    }

    /// <summary>
    /// Returns one row per push, top first, from whichever of the two stacks knows about it. The
    /// processor-state stack can name a push that the saved-register stack can only call new, so
    /// its name is used where it has one. A push neither stack knows about is shown as unknown
    /// rather than left out.
    /// <para>
    /// The two stacks are merged with one cursor into each. Each row takes the next group of
    /// bytes from the processor-state stack and the saved pushes that group covers.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> Pushes(ProgramAnalysis analysis, SavedStack? saved, AnalysisStack? bytes)
    {
        List<SavedPush> pushes = saved is null ? [] : [.. saved.Pushes.Reverse()];
        IReadOnlyList<StackEntry> entries = bytes?.Entries ?? [];
        var rows = new List<string>();

        // The next saved push, counting from the top, and the next byte of the processor-state
        // stack, which is kept bottom first.
        var next = 0;
        var top = entries.Count - 1;
        while (next < pushes.Count || top >= 0)
        {
            var wide = next < pushes.Count ? Bytes(pushes[next]) : null;
            var group = top >= 0 ? Group(analysis, entries, top, wide) : (Bytes: 0, Name: (string?)null);
            top -= group.Bytes;
            var covered = Covered(pushes, next, group.Bytes);

            // The saved-register stack's description is used only when this row covers exactly
            // one of its pushes.
            var push = covered == 1 ? pushes[next] : (SavedPush?)null;
            next += covered;
            var text = group.Name ?? (push is { } held ? Hovers.Held(held.Value, null) : "unknown");

            // Only the 65816 pushes a register whose width the caller cannot read off the CPU,
            // and only there does the width decide whether a pull gets the value back.
            if (analysis.Cpu == Cpu.Wdc65816 && push is { Size: PushSize.Accumulator or PushSize.Index, Width: var width }
                && width is Width.Eight or Width.Sixteen)
            {
                text += width == Width.Sixteen ? ", 16-bit" : ", 8-bit";
            }
            rows.Add(text);
        }
        return rows;
    }

    /// <summary>
    /// Returns how many saved pushes, starting at <paramref name="first"/>, one row covers. A row
    /// covers at least one push where any is left, and goes on until the pushes add up to
    /// <paramref name="bytes"/>. One group of bytes may span several pushes, because a
    /// <c>.frame</c> names all the pushes it covers and the point of a frame is to read them as
    /// one thing. A push of unknown width ends the row, since the bytes it took cannot be counted.
    /// </summary>
    private static int Covered(IReadOnlyList<SavedPush> pushes, int first, int bytes)
    {
        var count = 0;
        var covered = 0;
        while (first + count < pushes.Count && (count == 0 || covered < bytes))
        {
            var more = Bytes(pushes[first + count]);
            count++;
            if (more is not { } known)
                break;
            covered += known;
        }
        return count;
    }

    /// <summary>
    /// Returns how many bytes a push took, or null where the width it depends on is not known.
    /// </summary>
    private static int? Bytes(SavedPush push) => push.Size switch
    {
        PushSize.OneByte => 1,
        PushSize.TwoBytes => 2,
        _ => push.Width switch
        {
            Width.Eight => 1,
            Width.Sixteen => 2,
            _ => (int?)null,
        },
    };

    /// <summary>
    /// Returns the push whose top byte is <paramref name="top"/>, as how many bytes it took and
    /// what the processor-state analysis knows it holds. The name is null where the analysis
    /// knows nothing.
    /// <paramref name="hint"/> is how many bytes the saved-register stack says the push took,
    /// which is used as the size of a push the processor-state stack knows nothing about.
    /// </summary>
    private static (int Bytes, string? Name) Group(
        ProgramAnalysis analysis, IReadOnlyList<StackEntry> entries, int top, int? hint)
    {
        if (Framed(analysis, entries, top) is { } frame)
            return frame;
        var entry = entries[top];
        if (entry.IsStatus)
            return (1, $"status {ProcessorState.Format(StateRegister.A, entry.A)}, {ProcessorState.Format(StateRegister.Index, entry.Index)}");
        if (entry is { Size: > 0, Byte: 0, Held.IsKnown: true } && entry.Size <= top + 1)
            return (entry.Size, StateValue.Hex(entry.Held.Value, entry.Size * 2));
        return (hint is { } wide && wide <= top + 1 ? wide : 1, null);
    }

    /// <summary>
    /// Returns the <c>.frame</c> the byte at <paramref name="top"/> belongs to, as one push
    /// however many bytes it covers, or null when it belongs to none. A frame is marked on its
    /// lowest byte only, and how far up it extends is the size of the type it was declared with.
    /// </summary>
    private static (int Bytes, string? Name)? Framed(
        ProgramAnalysis analysis, IReadOnlyList<StackEntry> entries, int top)
    {
        for (var i = top; i >= 0; i--)
        {
            if (entries[i].Frame is not { } frame)
                continue;
            return i + (Room(analysis, frame) ?? 1) > top ? (top - i + 1, $"frame {frame.DisplayName}") : null;
        }
        return null;
    }

    /// <summary>
    /// Returns how many bytes a <c>.frame</c> covers, which is the size of the type it was
    /// declared with. The symbol may not carry its resolved type, since nothing else asks for it,
    /// so when it does not the size is looked up in the model of the file that declares the
    /// frame.
    /// </summary>
    private static long? Room(ProgramAnalysis analysis, Symbol frame) => frame.Type?.Size
        ?? (frame.TypeExpression is { } named
            ? analysis.ModelFor(frame.Tree.Path)?.SymbolOf(named)?.Size
            : null);
}
