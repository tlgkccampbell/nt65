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

    /// <summary>
    /// A linker config in the folder becomes the project's link, so it declares the segments, and
    /// the program it writes still builds against it.
    /// </summary>
    [Fact]
    public void ItLinksTheConfigInTheFolder()
    {
        root.Write("c64.cfg", """
            MEMORY { MAIN: file = %O, start = $0801, size = $9000; }
            SEGMENTS { CODE: load = MAIN, type = ro; }
            """);
        root.Write("build/stale.cfg", "MEMORY { M: start = 0, size = 1; } SEGMENTS { OLD: load = M; }");
        root.Write("notes.cfg", "[section]\nkey = value\n");

        var (code, printed) = Run(root.FullName, "init");

        Assert.Equal((ExitCode.Success, "nt65.json\nsrc/main.nt65\nlinks c64.cfg\n"), (code, printed));
        Assert.Contains("\"links\": {\n    \"c64\": { \"config\": \"c64.cfg\" }\n  }", root.Read(ProjectFile.Name));
        Assert.Equal((ExitCode.Success, ""), Run(root.FullName, "build"));
    }

    /// <summary>
    /// When the one config places no <c>CODE</c>, the program's routine goes in the first segment
    /// it places for code, so the build still has nowhere left to fail.
    /// </summary>
    [Fact]
    public void ItPutsTheRoutineInASegmentTheConfigPlaces()
    {
        root.Write("rom.cfg", """
            MEMORY { ZP: start = 0, size = $100; ROM: start = $E000, size = $2000, file = %O; }
            SEGMENTS { ZP: load = ZP, type = zp; KERNEL: load = ROM, type = ro; }
            """);

        Assert.Equal(ExitCode.Success, Run(root.FullName, "init").Code);

        Assert.Contains(".segment KERNEL\n", root.Read("src/main.nt65"));
        Assert.Equal((ExitCode.Success, ""), Run(root.FullName, "build"));
    }

    /// <summary>
    /// Several configs are alternative targets, as msbasic's are, so each becomes a configuration
    /// that links it and builds into a folder of its own. The project's own build links none.
    /// </summary>
    [Fact]
    public void ItMakesAConfigurationForEachOfSeveralConfigs()
    {
        const string Config = "MEMORY { ROM: file = %O, start = $C000, size = $4000; } SEGMENTS { CODE: load = ROM; }";
        root.Write("cfg/osi.cfg", Config);
        root.Write("cfg/apple soft.cfg", Config);
        root.Write("other/osi.cfg", Config);

        var (code, printed) = Run(root.FullName, "init");

        Assert.Equal(
            (ExitCode.Success, "nt65.json\nsrc/main.nt65\n"
                + "configuration apple-soft links cfg/apple soft.cfg\n"
                + "configuration osi links cfg/osi.cfg\n"
                + "configuration osi-2 links other/osi.cfg\n"),
            (code, printed));
        var project = Repo.ReadProject(root.FullName);
        Assert.Empty(project.Diagnostics);
        Assert.Empty(project.Links);
        Assert.Equal(["apple-soft", "osi", "osi-2"], project.Configurations.Select(configuration => configuration.Name));
        Assert.Equal((ExitCode.Success, ""), Run(root.FullName, "build"));
        foreach (var configuration in project.Configurations)
        {
            Assert.Equal((ExitCode.Success, ""), Run(root.FullName, "build", "--config", configuration.Name));
            Assert.True(File.Exists(Path.Combine(root.FullName, "build", configuration.Name, "main.s")));
        }
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
