using Norristown.Syntax;

namespace Norristown.Standard;

/// <summary>
/// Provides the modules that come with nt65, under the root <c>nt65</c>. They are charmaps for
/// the machines whose text has no standard of its own. They are ordinary nt65 source, kept
/// beside this class and compiled into it, and they join a program as its own files do, so a
/// path, a hover and a definition reach them as they reach any module.
/// <para>
/// They join only a program that could name them, which is one with a file whose text mentions
/// <c>nt65</c>. What they declare crosses modules by value, so they write no output in either
/// case, and a program that never names them is analyzed as if they did not exist.
/// </para>
/// </summary>
public static class StandardModules
{
    /// <summary>The root of the modules' names, which no other module may use.</summary>
    public const string Root = "nt65";

    /// <summary>The prefix of each module's logical path. No file on disk has this prefix.</summary>
    private const string Prefix = "(nt65)/";

    /// <summary>
    /// The modules, parsed once. The same trees serve every analysis, so an editor that
    /// analyzes again after an edit finds them unchanged.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<SyntaxTree>> trees = new(Load);

    /// <summary>Gets the modules, as trees whose paths start with <c>(nt65)/</c>.</summary>
    public static IReadOnlyList<SyntaxTree> Trees => trees.Value;

    /// <summary>
    /// Returns a value indicating whether <paramref name="path"/> is the logical path of one of
    /// the modules.
    /// </summary>
    public static bool IsStandard(string path) => path.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Returns the file name in a module's logical path, such as <c>cbm.nt65</c> for
    /// <c>(nt65)/cbm.nt65</c>.
    /// </summary>
    public static string FileOf(string path) => path[Prefix.Length..];

    /// <summary>
    /// Returns the logical path of the module in <paramref name="file"/>, a file name such as
    /// <c>cbm.nt65</c>.
    /// </summary>
    public static string PathOf(string file) => Prefix + file;

    /// <summary>
    /// Returns a value indicating whether <paramref name="module"/> is the reserved root or a name
    /// under it.
    /// </summary>
    public static bool IsReserved(string module) =>
        module == Root || module.StartsWith(Root + "::", StringComparison.Ordinal);

    /// <summary>
    /// Returns a value indicating whether a program of <paramref name="files"/> could name the
    /// modules, which is whether one of its files uses the name <c>nt65</c>. Only a file whose
    /// text contains the word has its tokens examined, so a program that never mentions nt65
    /// pays for a text search and no more. A program that mentions it only in a comment does not
    /// bring the modules in.
    /// </summary>
    public static bool Wanted(IEnumerable<SyntaxTree> files) =>
        files.Any(file => !IsStandard(file.Path) && file.Text.Contains(Root, StringComparison.Ordinal)
            && file.Root.DescendantTokens().Any(token => token.Kind == SyntaxKind.Identifier && token.Text == Root));

    /// <summary>
    /// Returns the source of the module at <paramref name="path"/>, or null if
    /// <paramref name="path"/> is not a module's path.
    /// </summary>
    public static string? Text(string path) => Trees.FirstOrDefault(tree => tree.Path == path)?.Text;

    private static List<SyntaxTree> Load()
    {
        var assembly = typeof(StandardModules).Assembly;
        const string resources = "Norristown.Standard.";
        return [.. assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(resources, StringComparison.Ordinal) && name.EndsWith(".nt65", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                return SyntaxTree.Parse(new SourceFile(PathOf(name[resources.Length..]), reader.ReadToEnd()));
            })];
    }
}
