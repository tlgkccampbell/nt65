using Norristown.Syntax;

namespace Norristown.Standard;

/// <summary>
/// The modules that come with nt65, under the root <c>nt65</c>: charmaps for the machines
/// whose text has no standard of its own. They are ordinary nt65, kept beside this class and
/// compiled into it, and they join a program as files of it do, so a path, a hover and a
/// definition reach them as they reach any module.
/// <para>
/// They join only a program that could name them: one with a file whose text mentions
/// <c>nt65</c>. What they declare crosses modules by value, so they write no output either way,
/// and a program that never names them is analyzed as if they did not exist.
/// </para>
/// </summary>
public static class StandardModules
{
    /// <summary>The root of the modules' names, which no other module may use.</summary>
    public const string Root = "nt65";

    /// <summary>What the logical path of each module starts with. No file on disk has it.</summary>
    private const string Prefix = "(nt65)/";

    /// <summary>
    /// The modules, parsed once: the same trees serve every analysis, so an editor that
    /// analyzes again after an edit finds them unchanged.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<SyntaxTree>> trees = new(Load);

    /// <summary>The modules, as trees whose paths start with <c>(nt65)/</c>.</summary>
    public static IReadOnlyList<SyntaxTree> Trees => trees.Value;

    /// <summary>Whether <paramref name="path"/> is the logical path of one of the modules.</summary>
    public static bool IsStandard(string path) => path.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The file name in a module's logical path: <c>cbm.nt65</c> for <c>(nt65)/cbm.nt65</c>.</summary>
    public static string FileOf(string path) => path[Prefix.Length..];

    /// <summary>The logical path of the module in the file <paramref name="file"/>, such as <c>cbm.nt65</c>.</summary>
    public static string PathOf(string file) => Prefix + file;

    /// <summary>Whether a module name is under the reserved root.</summary>
    public static bool IsReserved(string module) =>
        module == Root || module.StartsWith(Root + "::", StringComparison.Ordinal);

    /// <summary>
    /// Whether a program of <paramref name="files"/> could name the modules: whether a file has
    /// the name <c>nt65</c> in it. Only a file whose text contains the word has its tokens
    /// looked at, so a program that never mentions nt65 pays for a text search and no more, and
    /// one that mentions it only in a comment does not bring the modules in.
    /// </summary>
    public static bool Wanted(IEnumerable<SyntaxTree> files) =>
        files.Any(file => !IsStandard(file.Path) && file.Text.Contains(Root, StringComparison.Ordinal)
            && file.Root.DescendantTokens().Any(token => token.Kind == SyntaxKind.Identifier && token.Text == Root));

    /// <summary>The source of the module at <paramref name="path"/>, or null when it is not one.</summary>
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
