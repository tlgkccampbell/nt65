using System.Text.Json;
using Norristown.Cli;
using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.Tests.Cli;

/// <summary>
/// <c>nt65 init</c>: the two files a minimal program consists of, written so that the next
/// command run is a build that works, and never written over files that already exist.
/// </summary>
public sealed class InitCommandTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("nt65-init-");

    public void Dispose() => root.Delete(recursive: true);

    /// <summary>What it writes builds, and it says what it wrote.</summary>
    [Fact]
    public void WhatItWritesBuilds()
    {
        var (code, said) = Run(root.FullName, "init");

        Assert.Equal((0, "nt65.json\nsrc/main.nt65\n"), (code, said));
        Assert.Equal((0, ""), Run(root.FullName, "build"));
        Assert.True(File.Exists(Path.Combine(root.FullName, "build", "main.s")));
    }

    /// <summary>
    /// The project file holds keys nt65 reads and nothing else, and the source is already in the
    /// standard layout, so a first `nt65 fmt` on a new project changes nothing.
    /// </summary>
    [Fact]
    public void WhatItWritesIsWhatNt65WouldWrite()
    {
        Assert.Equal(0, Run(root.FullName, "init").Code);

        var project = Read(ProjectFile.Name);
        foreach (var key in JsonDocument.Parse(project).RootElement.EnumerateObject().Select(each => each.Name))
            Assert.Contains(key, ProjectFile.Keys);

        var main = Read("src/main.nt65");
        Assert.Equal(main, Formatter.Format(SyntaxTree.Parse("src/main.nt65", main)));
        Assert.Equal((0, ""), Run(root.FullName, "fmt", "--check"));
    }

    /// <summary>The files are written into the directory named, which is created if it does not exist.</summary>
    [Fact]
    public void ItWritesIntoTheDirectoryItIsGiven()
    {
        var (code, said) = Run(root.FullName, "init", "game", "--cpu", "65816");

        Assert.Equal((0, "game/nt65.json\ngame/src/main.nt65\n"), (code, said));
        Assert.Contains("\"cpu\": \"65816\"", Read("game/nt65.json"));
        Assert.Equal(0, Run(Path.Combine(root.FullName, "game"), "build").Code);
    }

    /// <summary>
    /// A directory that already holds either file is left exactly as it was, whichever of the
    /// two is the one that is there.
    /// </summary>
    [Fact]
    public void ItRefusesToWriteOverWhatIsThere()
    {
        Repo.WriteText(Path.Combine(root.FullName, "src", "main.nt65"), ".module mine\n");

        var (code, said) = Run(root.FullName, "init");

        Assert.Equal((1, "nt65: src/main.nt65 exists already\n"), (code, said));
        Assert.Equal(".module mine\n", Read("src/main.nt65"));
        Assert.False(File.Exists(Path.Combine(root.FullName, ProjectFile.Name)));

        File.Delete(Path.Combine(root.FullName, "src", "main.nt65"));
        Repo.WriteText(Path.Combine(root.FullName, ProjectFile.Name), "{}");
        Assert.Equal((1, "nt65: nt65.json exists already\n"), Run(root.FullName, "init"));
        Assert.Equal("{}", Read(ProjectFile.Name));
    }

    /// <summary>A processor nt65 does not have, and a second directory, are command-line mistakes.</summary>
    [Fact]
    public void WhatItCannotBeAskedForSaysSo()
    {
        Assert.Equal(
            (2, "nt65: --cpu takes 6502, 6502x, 65sc02, r65c02, 65c02 or 65816\nsee `nt65 --help`\n"),
            Run(root.FullName, "init", "--cpu", "z80"));
        Assert.Equal((2, "nt65: init takes one directory\nsee `nt65 --help`\n"), Run(root.FullName, "init", "a", "b"));
        Assert.Equal((2, "nt65: `--force` is not an option\nsee `nt65 --help`\n"), Run(root.FullName, "init", "--force"));
        Assert.Empty(Directory.GetFileSystemEntries(root.FullName));
    }

    private static (int Code, string Said) Run(string directory, params string[] arguments)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var code = Commands.Run(arguments, directory, output, error,
            cancellation: TestContext.Current.CancellationToken);
        return (code, output.ToString() + error.ToString());
    }

    private string Read(string path) => File.ReadAllText(Path.Combine(root.FullName, path));
}
