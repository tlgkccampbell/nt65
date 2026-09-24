using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// Identifies a routine by its file and its flattened name, rather than by its symbol. An
/// analysis that kept a file's results from before an edit holds a different symbol object for
/// the same routine. The key uses the name and not the position, because a kept file still
/// refers to the routines of an edited file at the positions they had before the edit.
/// </summary>
/// <param name="Path">The path of the file that declares the routine.</param>
/// <param name="Name">The routine's flattened name.</param>
internal readonly record struct RoutineKey(string Path, string Name)
{
    /// <summary>Returns the key that identifies <paramref name="routine"/>.</summary>
    public static RoutineKey Of(Symbol routine) => new(routine.Tree.Path, routine.FlatName);
}
