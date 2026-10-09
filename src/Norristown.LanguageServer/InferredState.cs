using Norristown.Semantics;

namespace Norristown.LanguageServer;

/// <summary>
/// Describes the parts of a routine's state that it does not declare and that are inferred for
/// it, as signature items. The hover shows them, and a refactoring declares them.
/// </summary>
internal static class InferredState
{
    /// <summary>
    /// Returns the items <paramref name="inferred"/> gives the parts that
    /// <paramref name="declared"/> leaves out. The entry's items are what the routine's callers
    /// agree on, and the exit's are what its returns leave. A part that is as the routine was
    /// entered with, or not known, has no item, since there is nothing to say about it.
    /// <para>
    /// The program bank is the exception. Nearly every routine runs in the bank its segment
    /// declares, so the bank has an item only where it was inferred to be another bank, or not
    /// known although the segment declares one.
    /// </para>
    /// </summary>
    /// <param name="declared">The routine's declared signature.</param>
    /// <param name="inferred">The routine's inferred signature.</param>
    /// <param name="home">The bank the routine's segment declares, or null if it declares none.</param>
    public static (IReadOnlyList<string> Entry, IReadOnlyList<string> Exit) Items(
        Signature declared, Signature inferred, long? home)
    {
        var undeclared = StateParts.All & ~declared.Declared;
        var entry = new List<string>();
        var exit = new List<string>();
        if ((undeclared & StateParts.A) != 0)
        {
            AddWidth(inferred.Entry.A, entry, StateRegister.A);
            AddWidth(inferred.Exit.A, exit, StateRegister.A);
        }
        if ((undeclared & StateParts.Index) != 0)
        {
            AddWidth(inferred.Entry.Index, entry, StateRegister.Index);
            AddWidth(inferred.Exit.Index, exit, StateRegister.Index);
        }
        if ((undeclared & StateParts.Mode) != 0)
        {
            if (inferred.Entry.E == ProcessorMode.Emulation)
                entry.Add(ProcessorState.Format(inferred.Entry.E));
            if (inferred.Exit.E is ProcessorMode.Native or ProcessorMode.Emulation)
                exit.Add(ProcessorState.Format(inferred.Exit.E));
        }
        if ((undeclared & StateParts.DirectPage) != 0)
        {
            AddValue(inferred.Entry.D, entry, StateRegister.DirectPage);
            AddValue(inferred.Exit.D, exit, StateRegister.DirectPage);
        }
        if ((undeclared & StateParts.DataBank) != 0)
        {
            AddValue(inferred.Entry.B, entry, StateRegister.DataBank);
            AddValue(inferred.Exit.B, exit, StateRegister.DataBank);
        }
        if ((declared.Declared & StateParts.ProgramBank) == 0 && Away(inferred.ProgramBank, home))
            entry.Add(inferred.ProgramBank.Format(StateRegister.ProgramBank));
        return (entry, inferred.NeverReturns ? [] : exit);
    }

    /// <summary>
    /// Returns the inferred items as a signature writes them, such as <c>a8, i16 -&gt; a16</c>, or
    /// null where nothing is inferred.
    /// </summary>
    public static string? Format(IReadOnlyList<string> entry, IReadOnlyList<string> exit) =>
        (entry.Count, exit.Count) switch
        {
            (0, 0) => null,
            (_, 0) => string.Join(", ", entry),
            (0, _) => "-> " + string.Join(", ", exit),
            _ => $"{string.Join(", ", entry)} -> {string.Join(", ", exit)}",
        };

    /// <summary>
    /// Returns a value indicating whether an inferred program bank differs from the bank the
    /// routine's segment declares. A bank that is not known differs only from a declared one.
    /// </summary>
    private static bool Away(StateValue bank, long? home) => bank.Kind switch
    {
        StateValueKind.Unchanged => false,
        StateValueKind.Known => bank.Value != home,
        _ => home is not null,
    };

    private static void AddWidth(Width width, List<string> items, StateRegister register)
    {
        if (width is Width.Eight or Width.Sixteen)
            items.Add(ProcessorState.Format(register, width));
    }

    private static void AddValue(StateValue value, List<string> items, StateRegister register)
    {
        if (value.IsBounded)
            items.Add(value.Format(register));
    }
}
