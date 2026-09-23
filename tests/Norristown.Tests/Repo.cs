namespace Norristown.Tests;

/// <summary>Paths inside the repository, found from the test binary's location.</summary>
internal static class Repo
{
    public static readonly string Root = FindRoot();

    /// <summary>
    /// The text NT65_FIXTURE gives (<c>scripts/test.ps1 -Fixture</c>), or null. Fixtures and
    /// corpus programs whose name contains it are the only ones run, and a test that needs
    /// one particular case is skipped when it is not selected.
    /// </summary>
    public static string? Selection =>
        Environment.GetEnvironmentVariable("NT65_FIXTURE") is { Length: > 0 } text ? text : null;

    /// <summary>
    /// Skips the test when <see cref="Selection"/> is set and does not select
    /// <paramref name="name"/>, for a test that checks one fixture or program by name. The test
    /// is reported as skipped, so a selected run does not look as if it checked more than it did.
    /// </summary>
    public static void SkipUnlessSelected(string name) =>
        Assert.SkipWhen(
            Selection is { } filter && !name.Contains(filter, StringComparison.OrdinalIgnoreCase),
            $"NT65_FIXTURE=\"{Selection}\" does not select {name}");

    /// <summary>
    /// Checks that a test has something to check. When <see cref="Selection"/> is set and
    /// selects none of <paramref name="items"/>, the test is skipped; otherwise an empty list
    /// fails it.
    /// </summary>
    public static void RequireAny<T>(IReadOnlyCollection<T> items)
    {
        Assert.SkipWhen(items.Count == 0 && Selection is not null, $"NT65_FIXTURE=\"{Selection}\" selects nothing this test checks");
        Assert.NotEmpty(items);
    }

    public static string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    /// <summary>Reads a file as the compiler sees it: UTF-8, with <c>\r\n</c> left in place.</summary>
    public static string ReadText(string path) => File.ReadAllText(path);

    /// <summary>Writes with <c>\n</c> line endings whatever the platform.</summary>
    public static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ReplaceLineEndings("\n"));
    }

    /// <summary>Every nt65 source in the repository: the fixtures, the corpus programs and the examples.</summary>
    public static IReadOnlyList<string> Sources() =>
        [.. new[] { Path("tests"), Path("examples"), Path("docs") }
            .Where(Directory.Exists)
            .SelectMany(root => Directory.GetFiles(root, "*.nt65", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal)];

    /// <summary>A file as a message names it: relative to the repository, with <c>/</c> separators.</summary>
    public static string Named(string file) =>
        System.IO.Path.GetRelativePath(Root, file).Replace(System.IO.Path.DirectorySeparatorChar, '/');

    /// <summary>Runs <paramref name="work"/> over <paramref name="items"/> in parallel and collects failure messages in input order.</summary>
    public static List<string> CollectFailures<T>(IReadOnlyList<T> items, Func<T, IEnumerable<string>> work)
    {
        var results = new List<string>[items.Count];
        Parallel.For(0, items.Count, i => results[i] = [.. work(items[i])]);
        return [.. results.SelectMany(r => r)];
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "Norristown.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("cannot find the repository root (Norristown.slnx) above the test binary");
    }
}
