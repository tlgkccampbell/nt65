using System.Text.Json;
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
        Assert.Contains("from ../lib/vic.nt65.", Read("app/build/hw/vic.s"));
        Assert.Contains("file 0, \"src/main.nt65\"", Read("app/build/main.s.lines"));

        // A module of nothing but constants generates no bytes, so nothing maps back to it.
        Assert.False(Exists("app/build/hw/vic.s.lines"));
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
        Assert.Equal("nt65: note: deleted build/hw/vic.s, whose module is not in the program\n", said);
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
            build/main.s.lines: \
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

    /// <summary>
    /// A project file with something wrong with it is why a build finds no files far more
    /// often than a missing `files` is, so what is wrong with it comes first and the forty
    /// lines of usage stay away: the command line is not what there is to fix.
    /// </summary>
    [Fact]
    public void AProjectFilesOwnProblemIsSaidBeforeThereAreNoFiles()
    {
        Project("""{ "cpu": "6502", "files": ["*.nt65"], "flies": [] }""");
        var app = Path.Combine(root.FullName, "app");

        var (code, said) = Run(app, "build");

        Assert.Equal(2, code);
        Assert.StartsWith("nt65.json:1:39: error: `flies` is not a nt65.json", said);
        Assert.Contains("nt65: no file matched the `files` globs in nt65.json", said);
        Assert.DoesNotContain("usage: nt65 build", said);
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

    /// <summary>
    /// <c>--check</c> says what a build would say and writes none of what a build would write,
    /// which is what a gate or a pre-commit hook wants: the report, the exit code, and no
    /// output tree to clean up afterwards.
    /// </summary>
    [Fact]
    public void CheckReportsAndWritesNothing()
    {
        Project("""{ "cpu": "6502", "files": ["*.nt65"], "out": "build", "defines": { "DEBUG": 0 } }""");
        File("app/main.nt65", Main.Replace(".use hw::BORDER", "BORDER = $d020\nUNUSED = 1", StringComparison.Ordinal));
        var app = Path.Combine(root.FullName, "app");

        var (code, said) = Run(app, "build", "--check", "--depfile", "nt65.d", "--c-header", "nt65.h");

        Assert.Equal(0, code);
        Assert.Contains("main.nt65:3:1: warning: `UNUSED` is never used", said);
        Assert.False(Directory.Exists(Path.Combine(app, "build")));
        Assert.False(System.IO.File.Exists(Path.Combine(app, "nt65.d")));
        Assert.False(System.IO.File.Exists(Path.Combine(app, "nt65.h")));

        // It exits as a build would, so what a build refuses it refuses.
        File("app/main.nt65", Main.Replace("hw::BORDER", "hw::vic::BORDER", StringComparison.Ordinal));
        Assert.Equal(1, Run(app, "build", "--check").Code);
    }

    /// <summary>
    /// <c>--json</c> puts one object per diagnostic on standard output, with the related spans a
    /// line leaves to an editor, and leaves standard error to what nt65 says about itself.
    /// </summary>
    [Fact]
    public void JsonWritesOneObjectPerDiagnostic()
    {
        Project("""{ "cpu": "6502", "files": ["*.nt65"], "out": "build" }""");
        File("app/main.nt65", ".module main\n.export BORDER\nBORDER = $d020\nBORDER = $d021\n");
        var app = Path.Combine(root.FullName, "app");

        var (code, output, error) = Apart(app, false, "build", "--json");

        Assert.Equal((1, ""), (code, error));
        var said = JsonDocument.Parse(Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries))).RootElement;
        Assert.Equal("main.nt65", said.GetProperty("file").GetString());
        Assert.Equal(4, said.GetProperty("line").GetInt32());
        Assert.Equal(1, said.GetProperty("column").GetInt32());
        Assert.Equal(7, said.GetProperty("endColumn").GetInt32());
        Assert.Equal("error", said.GetProperty("severity").GetString());
        Assert.Equal("`BORDER` is already declared in this scope", said.GetProperty("message").GetString());

        var related = Assert.Single(said.GetProperty("related").EnumerateArray());
        Assert.Equal((3, "declared here"),
            (related.GetProperty("line").GetInt32(), related.GetProperty("message").GetString()));

        // A diagnostic with nothing else to point at says nothing about related spans at all.
        File("app/main.nt65", ".module main\n.export BORDER\nBORDER = $d020\nUNUSED = 1\n");
        Assert.DoesNotContain("related", Apart(app, false, "build", "--json").Output);
    }

    /// <summary>
    /// Colour marks what a diagnostic is and nothing else, so the position stays selectable and
    /// the message is not competing with it. Whether there is any is the caller's to decide.
    /// </summary>
    [Fact]
    public void ColourMarksWhatADiagnosticIs()
    {
        Project("""{ "cpu": "6502", "files": ["*.nt65"], "out": "build" }""");
        File("app/main.nt65", ".module main\n.export BORDER\nBORDER = $d020\nUNUSED = 1\n");
        var app = Path.Combine(root.FullName, "app");

        Assert.Equal(
            "main.nt65:4:1: [33mwarning:[0m `UNUSED` is never used: nothing names it, and it is not "
                + "exported [unused-symbol]\n",
            Apart(app, true, "build").Error);
        Assert.Equal(
            "main.nt65:4:1: warning: `UNUSED` is never used: nothing names it, and it is not exported "
                + "[unused-symbol]\n",
            Apart(app, false, "build").Error);

        File("app/main.nt65", ".module main\n.export BORDER\nBORDER = nowhere\n");
        Assert.Equal(
            "main.nt65:3:10: [31merror:[0m `nowhere` is not declared [not-declared]\n",
            Apart(app, true, "build").Error);
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

    /// <summary>
    /// A command or an option nt65 does not have is one line of news and where to read the rest.
    /// The usage text answers <c>nt65</c> on its own and <c>--help</c>, which asked for it.
    /// </summary>
    [Fact]
    public void WhatIsNotACommandSaysSoAndPointsAtTheHelp()
    {
        var (code, said) = Run(root.FullName, "check");
        Assert.Equal(2, code);
        Assert.Equal("nt65: `check` is not a command\nsee `nt65 --help`\n", said);

        (code, said) = Run(root.FullName, "--watch");
        Assert.Equal(2, code);
        Assert.Equal("nt65: `--watch` is not an option\nsee `nt65 --help`\n", said);

        (code, said) = Run(root.FullName, "build", "--rebuild");
        Assert.Equal(2, code);
        Assert.Equal("nt65: `--rebuild` is not an option\nsee `nt65 --help`\n", said);

        (code, said) = Run(root.FullName);
        Assert.Equal(2, code);
        Assert.StartsWith("usage: nt65 build", said);
    }

    private static (int Code, string Said) Run(string directory, params string[] arguments)
    {
        var (code, output, error) = Apart(directory, false, arguments);
        return (code, output + error);
    }

    /// <summary>The two streams kept apart, for what is written to one and not to the other.</summary>
    private static (int Code, string Output, string Error) Apart(
        string directory, bool colour, params string[] arguments)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var code = Commands.Run(arguments, directory, output, error, colour,
            cancellation: TestContext.Current.CancellationToken);
        return (code, output.ToString(), error.ToString());
    }

    private void Project(string text) => File("app/nt65.json", text);

    private void File(string path, string text) => Repo.WriteText(Path.Combine(root.FullName, path), text);

    private string Read(string path) => System.IO.File.ReadAllText(Path.Combine(root.FullName, path));

    private bool Exists(string path) => System.IO.File.Exists(Path.Combine(root.FullName, path));
}
