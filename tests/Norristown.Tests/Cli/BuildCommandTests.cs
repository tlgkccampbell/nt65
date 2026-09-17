using Norristown.Cli;

namespace Norristown.Tests.Cli;

/// <summary>
/// <c>nt65 build</c> on real directories: finding the project, where output goes, the named
/// configurations, what is deleted and what dependencies are written. Each test builds a small
/// project in a directory of its own and throws it away.
/// </summary>
public sealed class BuildCommandTests : IDisposable
{
    private const string Main = ".module main\n.use hw::BORDER\n.segment CODE\n.proc main {\n    lda #DEBUG\n    sta BORDER\n    rts\n}\n";
    private const string Hw = ".module hw::vic\n.export BORDER = $d020\n";

    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("nt65-build-");

    public void Dispose() => root.Delete(recursive: true);

    /// <summary>
    /// The project is found from a directory below it, and an output is named after its module
    /// under the project's <c>out</c>, wherever its source is, including above the project.
    /// </summary>
    [Fact]
    public void OutputIsNamedByModuleUnderTheProjectsOut()
    {
        Project("""{ "cpu": "6502", "files": ["src/*.nt65", "../lib/*.nt65"], "out": "build", "defines": { "DEBUG": 0 } }""");
        File("app/src/main.nt65", Main.Replace("hw::BORDER", "hw::vic::BORDER", StringComparison.Ordinal));
        File("lib/vic.nt65", Hw);

        var (code, said) = Run(Path.Combine(root.FullName, "app", "src"), "build");

        Assert.Equal((0, ""), (code, said));
        Assert.True(Exists("app/build/main.s"));
        Assert.True(Exists("app/build/hw/vic.s"));
        Assert.Contains(".dbg file, \"../lib/vic.nt65\"", Read("app/build/hw/vic.s"));
        Assert.Contains(".dbg file, \"src/main.nt65\"", Read("app/build/main.s"));
    }

    /// <summary><c>--config</c> gives the configuration's defines over the project's, and its output directory.</summary>
    [Fact]
    public void AConfigurationChoosesDefinesAndOutput()
    {
        Project("""
            {
              "cpu": "6502",
              "files": ["*.nt65"],
              "out": "build/release",
              "defines": { "DEBUG": 0 },
              "configurations": { "debug": { "defines": { "DEBUG": 1 }, "out": "build/debug" } }
            }
            """);
        File("app/main.nt65", Main.Replace(".use hw::BORDER", "BORDER = $d020", StringComparison.Ordinal));
        var app = Path.Combine(root.FullName, "app");

        Assert.Equal(0, Run(app, "build").Code);
        Assert.Equal(0, Run(app, "build", "--config", "debug").Code);
        Assert.Contains("lda #$00", Read("app/build/release/main.s"));
        Assert.Contains("lda #$01", Read("app/build/debug/main.s"));

        var (code, said) = Run(app, "build", "--config", "ntsc");
        Assert.Equal(1, code);
        Assert.Contains("--config:1:1: error: `ntsc` is not a configuration: nt65.json names `debug`", said);
    }

    /// <summary>
    /// An output whose module left the program is deleted on the next build, and a file nt65
    /// did not write is left alone, even where outputs go.
    /// </summary>
    [Fact]
    public void AnOutputWhoseModuleIsGoneIsDeleted()
    {
        Project("""{ "cpu": "6502", "files": ["*.nt65"], "out": "build", "defines": { "DEBUG": 0 } }""");
        File("app/main.nt65", Main.Replace("hw::BORDER", "hw::vic::BORDER", StringComparison.Ordinal));
        File("app/vic.nt65", Hw);
        File("app/build/notes.txt", "mine");
        var app = Path.Combine(root.FullName, "app");
        Assert.Equal(0, Run(app, "build").Code);
        Assert.True(Exists("app/build/hw/vic.s"));

        File("app/main.nt65", Main.Replace(".use hw::BORDER", "BORDER = $d020", StringComparison.Ordinal));
        System.IO.File.Delete(Path.Combine(app, "vic.nt65"));
        var (code, said) = Run(app, "build");

        Assert.Equal(0, code);
        Assert.Equal("nt65: deleted build/hw/vic.s, whose module is not in the program\n", said);
        Assert.False(Exists("app/build/hw/vic.s"));
        Assert.False(Directory.Exists(Path.Combine(app, "build", "hw")));
        Assert.True(Exists("app/build/main.s"));
        Assert.True(Exists("app/build/notes.txt"));
    }

    /// <summary>
    /// The dependency file names, for each output, its source, the modules whose interfaces it
    /// uses, the binaries included, and the project file, relative to where nt65 runs.
    /// </summary>
    [Fact]
    public void TheDependencyFileNamesWhatEachOutputDependsOn()
    {
        Project("""{ "cpu": "6502", "files": ["src/*.nt65"], "out": "build", "defines": { "DEBUG": 0 } }""");
        File("app/src/main.nt65", Main.Replace("hw::BORDER", "hw::vic::BORDER", StringComparison.Ordinal)
            + ".segment RODATA\n.data font: .incbin \"../data/font.bin\"\n.export font\n");
        File("app/src/vic.nt65", Hw);
        File("app/data/font.bin", "ABCD");
        var app = Path.Combine(root.FullName, "app");

        Assert.Equal(0, Run(app, "build", "--depfile", "build/nt65.d").Code);

        Assert.Equal("""
            build/main.s: \
              data/font.bin \
              src/main.nt65 \
              src/vic.nt65 \
              nt65.json
            build/hw/vic.s: \
              src/vic.nt65 \
              nt65.json

            data/font.bin:

            nt65.json:

            src/main.nt65:

            src/vic.nt65:

            """.ReplaceLineEndings("\n"), Read("app/build/nt65.d"));
    }

    /// <summary>
    /// A file named on the command line is built as part of its project, so a name another module
    /// exports means what it means, and only that file's output is written.
    /// </summary>
    [Fact]
    public void ANamedFileIsBuiltWithinItsProject()
    {
        Project("""{ "cpu": "6502", "files": ["*.nt65"], "defines": { "DEBUG": 0 } }""");
        File("app/main.nt65", Main.Replace("hw::BORDER", "hw::vic::BORDER", StringComparison.Ordinal));
        File("app/vic.nt65", Hw);

        var (code, said) = Run(Path.Combine(root.FullName, "app"), "build", "main.nt65");

        Assert.Equal((0, ""), (code, said));
        Assert.True(Exists("app/main.s"));
        Assert.False(Exists("app/hw/vic.s"));
    }

    /// <summary>With no project, a name from a module the build lacks says so, and an assumed CPU is noted.</summary>
    [Fact]
    public void WithoutAProjectWhatIsMissingIsSaid()
    {
        File("alone/main.nt65", Main.Replace("#DEBUG", "#1", StringComparison.Ordinal));
        File("alone/other.nt65", ".module other\n.export N = 1\n");
        var alone = Path.Combine(root.FullName, "alone");

        var (code, said) = Run(alone, "build", "main.nt65");
        Assert.Equal(1, code);
        Assert.Contains("main.nt65:2:6: error: no module `hw` is in this build", said);

        (code, said) = Run(alone, "build", "other.nt65");
        Assert.Equal(0, code);
        Assert.Contains("nt65: note: nothing says which processor this program is for, so it is built for the 6502", said);
    }

    [Fact]
    public void HelpAndVersionAreAnswered()
    {
        var (code, said) = Run(root.FullName, "--help");
        Assert.Equal(0, code);
        Assert.Contains("--depfile <file>", said);

        (code, said) = Run(root.FullName, "--version");
        Assert.Equal(0, code);
        Assert.StartsWith("nt65 ", said);

        (code, said) = Run(root.FullName, "build", "--bogus");
        Assert.Equal(2, code);
        Assert.StartsWith("nt65: `--bogus` is not an option", said);
    }

    private (int Code, string Said) Run(string directory, params string[] arguments)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var code = BuildCommand.Run(arguments, directory, output, error);
        return (code, output.ToString() + error.ToString());
    }

    private void Project(string text) => File("app/nt65.json", text);

    private void File(string path, string text) => Repo.WriteText(Path.Combine(root.FullName, path), text);

    private string Read(string path) => System.IO.File.ReadAllText(Path.Combine(root.FullName, path));

    private bool Exists(string path) => System.IO.File.Exists(Path.Combine(root.FullName, path));
}
