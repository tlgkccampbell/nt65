using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Reads the module paths that a file's <c>.module</c> and <c>.use</c> items give, as the source
/// spells them and before anything is resolved. The binder and <see cref="Configuration"/> both
/// read them here, so that a condition and the rest of the program take a <c>.use</c> to mean
/// the same names.
/// </summary>
internal static class ModuleSyntax
{
    /// <summary>
    /// Returns the path that <paramref name="name"/> spells, with its parts joined by <c>::</c>.
    /// </summary>
    public static string PathOf(NameExpressionSyntax name) => string.Join("::", name.Names.Select(part => part.Text));

    /// <summary>
    /// Returns the module that the first <c>.module</c> at the top level of
    /// <paramref name="tree"/> names, or null when there is none.
    /// </summary>
    public static string? ModuleOf(SyntaxTree tree)
    {
        foreach (var child in tree.Root.Members)
        {
            if (child is LineSyntax { Statement: ModuleDirectiveSyntax module })
                return PathOf(module.Name);
        }
        return null;
    }

    /// <summary>
    /// Returns each name that <paramref name="use"/> brings in, under the name it is brought in
    /// as, with the path from the root of the modules that the name leads to. A
    /// <c>.use module::*</c> and a <c>.use</c> with no path bring in no single name, so nothing
    /// is returned for them.
    /// </summary>
    public static IEnumerable<ProgramSymbols.Reexport> Brought(UseDirectiveSyntax use)
    {
        var path = use.Path.Names;
        if (path.Length == 0 || use.StarToken is not null)
            yield break;
        if (use.Items.Count == 0)
        {
            yield return new ProgramSymbols.Reexport((use.Alias ?? path[^1]).Text, [.. path.Select(part => part.Text)]);
            yield break;
        }
        foreach (var item in use.Items)
            yield return new ProgramSymbols.Reexport((item.Alias ?? item.Name).Text, [.. path.Select(part => part.Text), item.Name.Text]);
    }
}
