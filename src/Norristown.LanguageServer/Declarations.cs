using Norristown.Semantics;

namespace Norristown.LanguageServer;

/// <summary>
/// Compares symbols by where they are declared. After an edit, the models kept for the files
/// the edit did not touch may hold a different object for the same routine, so a feature that
/// matches a routine across files cannot compare the objects themselves.
/// </summary>
internal static class Declarations
{
    /// <summary>
    /// Checks whether two symbols are the same declaration, which is one name declared at one
    /// place in one file.
    /// </summary>
    public static bool Same(Symbol a, Symbol b) =>
        a.Tree.Path == b.Tree.Path && a.NameSpan.Start == b.NameSpan.Start && a.Name == b.Name;
}
