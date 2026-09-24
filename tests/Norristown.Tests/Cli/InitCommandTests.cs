using System.Text.Json;
using Norristown.Cli;
using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.Tests.Cli;

/// <summary>
/// Tests <c>nt65 init</c>, which writes the two files a minimal program consists of. They are
/// written so that the next command run is a build that works, and they never replace files
/// that already exist.
/// </summary>
public sealed class InitCommandTests : IDisposable
{
    private readonly TempFolder root = new("nt65-init-");

    public void Dispose() => root.Dispose();

    /// <summary>The files it writes build, and it reports which files it wrote.</summary>
    [Fact]
    public void WhatItWritesBuilds()
    {
        var (code, printed) = Run(root.FullName, "init");

        Assert.Equal((ExitCode.Success, "nt65.json\nsrc/main.nt65\n"), (code, printed));
        Assert.Equal((ExitCode.Success, ""), Run(root.FullName, "build"));
        Assert.True(File.Exists(Path.Combine(root.FullName, "build", "main.s")));
    }

    /// <summary>
    /// The project file holds keys nt65 reads and nothing else, and the source is already in the
    /// standard layout, so a first <c>nt65 fmt</c> on a new project changes nothing.
    /// </summary>
    [Fact]
    public void WhatItWritesIsWhatNt65WouldWrite()
    {
        Assert.Equal(ExitCode.Success, Run(root.FullName, "init").Code);

        var project = root.Read(ProjectFile.Name);
        foreach (var key in JsonDocument.Parse(project).RootElement.EnumerateObject().Select(each => each.Name))
            Assert.Contains(key, ProjectFile.Keys);

        var main = root.Read("src/main.nt65");
        Assert.Equal(main, Formatter.Format(SyntaxTree.Parse("src/main.nt65", main)));
        Assert.Equal((ExitCode.Success, ""), Run(root.FullName, "fmt", "--check"));
    }

    /// <summary>The files are written into the directory named, which is created if it does not exist.</summary>
    [Fact]
    public void ItWritesIntoTheDirectoryItIsGiven()
    {
        var (code, printed) = Run(root.FullName, "init", "game", "--cpu", "65816");

        Assert.Equal((ExitCode.Success, "game/nt65.json\ngame/src/main.nt65\n"), (code, printed));
        Assert.Contains("\"cpu\": \"65816\"", root.Read("game/nt65.json"));
        Assert.Equal(ExitCode.Success, Run(Path.Combine(root.FullName, "game"), "build").Code);
    }

    /// <summary>
    /// A directory that already holds either file is left exactly as it was, no matter which of
    /// the two it holds.
    /// </summary>
    [Fact]
    public void ItRefusesToWriteOverWhatIsThere()
    {
        Repo.WriteText(Path.Combine(root.FullName, "src", "main.nt65"), ".module mine\n");

        var (code, printed) = Run(root.FullName, "init");

        Assert.Equal((ExitCode.InputError, "nt65: src/main.nt65 exists already\n"), (code, printed));
        Assert.Equal(".module mine\n", root.Read("src/main.nt65"));
        Assert.False(File.Exists(Path.Combine(root.FullName, ProjectFile.Name)));

        File.Delete(Path.Combine(root.FullName, "src", "main.nt65"));
        Repo.WriteText(Path.Combine(root.FullName, ProjectFile.Name), "{}");
        Assert.Equal((ExitCode.InputError, "nt65: nt65.json exists already\n"), Run(root.FullName, "init"));
        Assert.Equal("{}", root.Read(ProjectFile.Name));
    }

    /// <summary>A processor nt65 does not have, and a second directory, are command-line mistakes.</summary>
    [Fact]
    public void WhatItCannotBeAskedForIsReportedAndNothingIsWritten()
    {
        Assert.Equal(
            (ExitCode.UsageError, "nt65: `z80` is not a processor nt65 knows; `--cpu` takes 6502, 6502x, 65sc02, r65c02, 65c02 or 65816\nsee `nt65 --help`\n"),
            Run(root.FullName, "init", "--cpu", "z80"));
        Assert.Equal((ExitCode.UsageError, "nt65: `init` takes at most one directory\nsee `nt65 --help`\n"), Run(root.FullName, "init", "a", "b"));
        Assert.Equal((ExitCode.UsageError, "nt65: `--force` is not an option\nsee `nt65 --help`\n"), Run(root.FullName, "init", "--force"));
        Assert.Empty(Directory.GetFileSystemEntries(root.FullName));
    }

    private static (ExitCode Code, string Printed) Run(string directory, params string[] arguments)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var code = Commands.Run(arguments, directory, output, error,
            cancellation: TestTimeout.Token());
        return (code, output.ToString() + error.ToString());
    }
}
