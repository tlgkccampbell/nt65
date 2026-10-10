using System.Globalization;
using Norristown.Flow;
using Norristown.Semantics;

namespace Norristown.LanguageServer;

/// <summary>
/// Describes where a routine runs from, as <see cref="RoutineContexts"/> works it out, for the
/// outline, the hover and the semantic tokens. Running under an interrupt is what a reader needs
/// to notice, so the outline and the tokens mark only that, and the hover says it in full.
/// </summary>
internal static class RunsFrom
{
    /// <summary>
    /// Returns the words the outline adds after a routine's signature, or null where the routine
    /// does not run under an interrupt. A handler marked <c>interrupt</c> gets none, because its
    /// signature already says so, and one walked as a handler without the mark gets
    /// <c>interrupt handler</c>.
    /// </summary>
    /// <param name="contexts">The contexts of the program.</param>
    /// <param name="routine">The routine, as the program has it now.</param>
    public static string? Outline(RoutineContexts contexts, Symbol routine) =>
        RoutineContexts.IsHandler(routine) ? null
            : contexts.IsUnmarkedHandler(routine) ? "interrupt handler"
            : Under(contexts, routine);

    /// <summary>
    /// Returns the value of the hover's <c>context</c> row for a routine, or null where it does not
    /// run under an interrupt. Most routines run only in main, so saying so would add a row to
    /// nearly every hover.
    /// </summary>
    /// <param name="contexts">The contexts of the program.</param>
    /// <param name="routine">The routine, as the program has it now.</param>
    public static string? Hover(RoutineContexts contexts, Symbol routine) =>
        RoutineContexts.IsHandler(routine) ? "interrupt handler"
            : contexts.IsUnmarkedHandler(routine) ? "interrupt handler, by its `rti`; not marked `interrupt`"
            : Under(contexts, routine);

    /// <summary>
    /// Returns the value of the hover's <c>not followed</c> row, which lists the routine's lines
    /// that the walk from a handler stops at, or null where there are none. It is given only for
    /// a routine that runs under an interrupt, since only there do those lines leave routines
    /// unmarked that may run under one.
    /// </summary>
    /// <param name="contexts">The contexts of the program.</param>
    /// <param name="routine">The routine, as the program has it now.</param>
    public static string? Unfollowed(RoutineContexts contexts, Symbol routine)
    {
        if (!contexts.Of(routine).HasFlag(RoutineContext.Interrupt) || contexts.Unfollowed(routine) is not { Count: > 0 } spans)
            return null;
        var lines = spans.Select(span => (routine.Tree.GetLineIndex(span.Start) + 1).ToString(CultureInfo.InvariantCulture));
        return $"line{(spans.Count == 1 ? "" : "s")} {string.Join(", ", lines)}, where nt65 cannot tell where control goes";
    }

    /// <summary>
    /// Returns a value indicating whether a name is colored as a routine that runs under an
    /// interrupt.
    /// </summary>
    /// <param name="contexts">The contexts of the program.</param>
    /// <param name="routine">The routine, as the program has it now.</param>
    public static bool IsUnderInterrupt(RoutineContexts contexts, Symbol routine) =>
        contexts.Of(routine).HasFlag(RoutineContext.Interrupt);

    /// <summary>
    /// Returns the routines named in <paramref name="model"/>'s file whose names the semantic
    /// tokens mark as running under an interrupt, as one string. Two analyses that give a file the
    /// same string give its names the same modifiers.
    /// </summary>
    /// <param name="analysis">The analysis of the program that holds the file.</param>
    /// <param name="model">The model of the file.</param>
    public static string Marked(ProgramAnalysis analysis, SemanticModel model)
    {
        var contexts = analysis.Contexts();
        return string.Join(' ', model.References
            .Where(reference => reference.Symbol.Signature is not null)
            .Select(reference => analysis.Program.Current(reference.Symbol))
            .Where(routine => IsUnderInterrupt(contexts, routine))
            .Select(routine => $"{routine.Tree.Path}:{routine.FlatName}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Returns the words that say which handlers reach a routine, and whether the rest of the
    /// program does too, or null where no handler reaches it.
    /// </summary>
    private static string? Under(RoutineContexts contexts, Symbol routine)
    {
        if (contexts.HandlersOf(routine) is not { Count: > 0 } handlers)
            return null;
        var names = string.Join(", ", handlers.Select(handler => handler.Name));
        return contexts.Of(routine).HasFlag(RoutineContext.Main) ? $"under {names} and main" : $"under {names}";
    }
}
