using Norristown.Processor;
using Norristown.Project;
using Norristown.Tests.Fixtures;

namespace Norristown.Tests.Oracle;

/// <summary>
/// Represents a realistic program under <c>tests/corpus</c> or <c>examples</c>, built the way
/// its build script builds it. The program consists of its <c>nt65.json</c>, the sources its
/// globs name (which may be outside its directory), one linker configuration, and any
/// hand-written ca65, include files and binaries beside them. Its <c>build/</c> directory is
/// output and is never read. A directory with no <c>nt65.json</c> is not a program; it holds
/// only sources that programs share.
/// </summary>
/// <param name="Name">The program's directory name.</param>
/// <param name="Directory">The program's directory.</param>
/// <param name="Sources">
/// Its nt65 sources, with paths relative to <paramref name="Directory"/>. Some of them may be
/// above it.
/// </param>
/// <param name="Project">Its <c>nt65.json</c>.</param>
/// <param name="LinkerConfig">The text of its one <c>.cfg</c> file.</param>
/// <param name="HandWritten">Its ca65 sources, relative to <paramref name="Directory"/>.</param>
/// <param name="Other">
/// Every other file, such as includes and binaries, relative to <paramref name="Directory"/>.
/// </param>
internal sealed record CorpusProgram(
    string Name,
    string Directory,
    IReadOnlyList<SourceFile> Sources,
    ProjectSettings Project,
    string LinkerConfig,
    IReadOnlyList<(string Name, string Source)> HandWritten,
    IReadOnlyList<(string Name, byte[] Content)> Other)
{
    /// <summary>
    /// Names the programs with build steps that this loader does not model. The LoROM template
    /// converts its assets with Python before nt65 can read their sizes, links a second image
    /// with a second configuration, and assembles its hand-written ca65 with no CPU. The msbasic
    /// example is ten programs, one per configuration, each with its own linker configuration
    /// and a matching image. The gate builds both end to end.
    /// </summary>
    private static readonly HashSet<string> BuiltOnlyByTheirScripts = new(StringComparer.Ordinal) { "lorom-template", "msbasic" };

    /// <summary>
    /// Returns every corpus program and example, or only those whose name contains NT65_FIXTURE
    /// (<c>scripts/test.ps1 -Ca65 -Fixture</c>). It leaves out the programs that only their own
    /// build scripts can build, which <c>scripts/corpus.ps1</c> runs.
    /// </summary>
    public static IReadOnlyList<CorpusProgram> All()
    {
        var filter = Repo.Selection;
        return [.. new[] { Repo.Path("tests", "corpus"), Repo.Path("examples") }
            .SelectMany(System.IO.Directory.GetDirectories)
            .Where(dir => File.Exists(Path.Combine(dir, ProjectFile.Name)))
            .Where(dir => !BuiltOnlyByTheirScripts.Contains(Path.GetFileName(dir)))
            .Where(dir => string.IsNullOrEmpty(filter)
                || Path.GetFileName(dir).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .Select(Load)];
    }

    public static CorpusProgram Load(string directory)
    {
        var build = Path.Combine(directory, "build") + Path.DirectorySeparatorChar;
        var files = System.IO.Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(build, StringComparison.Ordinal))
            .Select(path => (Path: path, Relative: FixtureCase.RelativePath(directory, path)))
            .OrderBy(file => file.Relative, StringComparer.Ordinal)
            .ToList();

        var project = Repo.ReadProject(directory);
        return new CorpusProgram(
            Path.GetFileName(directory),
            directory,
            [.. project.Files.SelectMany(glob => SourceGlobs.Matching(directory, glob))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(path => new SourceFile(path, Repo.ReadText(Path.GetFullPath(path, directory))))],
            project,
            Repo.ReadText(files.Single(f => f.Relative.EndsWith(".cfg", StringComparison.Ordinal)).Path),
            [.. files.Where(f => f.Relative.EndsWith(".s", StringComparison.Ordinal))
                .Select(f => (f.Relative, Repo.ReadText(f.Path)))],
            [.. files.Where(f => !f.Relative.EndsWith(".nt65", StringComparison.Ordinal)
                    && !f.Relative.EndsWith(".s", StringComparison.Ordinal))
                .Select(f => (f.Relative, File.ReadAllBytes(f.Path)))]);
    }

    /// <summary>Compiles the program, with <paramref name="values"/> overriding its own settings.</summary>
    public Compilation Compile(params SettingValue[] values) =>
        Compiler.Compile(Sources, Project.With(values), BinaryLength);

    private long? BinaryLength(string path)
    {
        var file = Path.Combine(Directory, path.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(file) ? new FileInfo(file).Length : null;
    }
}
