using System.Text.Json;
using Norristown.Cli;

namespace Norristown.Tests.Cli;

/// <summary>
/// Tests <c>nt65 build</c> on real directories. The tests cover finding the project, where
/// output goes, the named configurations, what is deleted and what dependencies are written.
/// Each test builds a small project in a directory of its own and throws it away.
/// </summary>
public sealed class BuildCommandTests : IDisposable
{
    private const string Main = ".module main\n.use hw::BORDER\n.segment CODE\n.export .proc main {\n    lda #DEBUG\n    sta BORDER\n    rts\n}\n.const DEBUG ?= 0\n";
    private const string Hw = ".module hw::vic\n.export .const BORDER = $d020\n";

    private readonly TempFolder root = new("nt65-build-");

    public void Dispose() => root.Dispose();

    /// <summary>
    /// The project is found from a directory below it, and an output is named after its module
    /// under the project's <c>out</c>, wherever its source is, including above the project.
    /// </summary>
    [Fact]
    public void OutputIsNamedByModuleUnderTheProjectsOut()
    {
        Project("""{ "cpu": "6502", "files": ["src/*.nt65", "../lib/*.nt65"], "out": "build", "settings": { "DEBUG": 0 } }""");
        root.Write("app/src/main.nt65", Main.Replace("hw::BORDER", "hw::vic::BORDER", StringComparison.Ordinal));
        root.Write("lib/vic.nt65", Hw);

        var (code, printed) = Run(Path.Combine(root.FullName, "app", "src"), "build");

        Assert.Equal((ExitCode.Success, ""), (code, printed));
        Assert.True(Exists("app/build/main.s"));
        Assert.True(Exists("app/build/hw/vic.s"));
        Assert.Contains("from ../lib/vic.nt65.", root.Read("app/build/hw/vic.s"));
        Assert.Contains("file 0, \"src/main.nt65\"", root.Read("app/build/main.s.lines"));

        // A module of nothing but constants generates no bytes, so nothing maps back to it.
        Assert.False(Exists("app/build/hw/vic.s.lines"));
    }

    /// <summary>
    /// <c>--config</c> applies the configuration's defines over the project's, and its output
    /// directory.
    /// </summary>
    [Fact]
    public void AConfigurationChoosesDefinesAndOutput()
    {
        Project("""
            {
              "cpu": "6502",
              "files": ["*.nt65"],
              "out": "build/release",
              "settings": { "DEBUG": 0 },
              "configurations": { "debug": { "settings": { "DEBUG": 1 }, "out": "build/debug" } }
            }
            """);
        root.Write("app/main.nt65", Main.Replace(".use hw::BORDER", ".const BORDER = $d020", StringComparison.Ordinal));
        var app = Path.Combine(root.FullName, "app");

        Assert.Equal(ExitCode.Success, Run(app, "build").Code);
        Assert.Equal(ExitCode.Success, Run(app, "build", "--config", "debug").Code);
        Assert.Contains("lda #$00", root.Read("app/build/release/main.s"));
        Assert.Contains("lda #$01", root.Read("app/build/debug/main.s"));

        var (code, printed) = Run(app, "build", "--config", "ntsc");
        Assert.Equal(ExitCode.InputError, code);
        Assert.Contains("--config:1:1: error: `ntsc` is not a configuration: nt65.json names `debug`", printed);
    }

    /// <summary>
    /// An output whose module left the program is deleted on the next build, and a file nt65
    /// did not write is left alone, even where outputs go.
    /// </summary>
    [Fact]
    public void AnOutputWhoseModuleIsGoneIsDeleted()
    {
        Project("""{ "cpu": "6502", "files": ["*.nt65"], "out": "build", "settings": { "DEBUG": 0 } }""");
        root.Write("app/main.nt65", Main.Replace("hw::BORDER", "hw::vic::BORDER", StringComparison.Ordinal));
        root.Write("app/vic.nt65", Hw);
        root.Write("app/build/notes.txt", "mine");
        var app = Path.Combine(root.FullName, "app");
        Assert.Equal(ExitCode.Success, Run(app, "build").Code);
        Assert.True(Exists("app/build/hw/vic.s"));

        root.Write("app/main.nt65", Main.Replace(".use hw::BORDER", ".const BORDER = $d020", StringComparison.Ordinal));
        System.IO.File.Delete(Path.Combine(app, "vic.nt65"));
        var (code, printed) = Run(app, "build");

        Assert.Equal(ExitCode.Success, code);
        Assert.Equal("nt65: note: deleted build/hw/vic.s, which the program no longer writes\n", printed);
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
        Project("""{ "cpu": "6502", "files": ["src/*.nt65"], "out": "build", "settings": { "DEBUG": 0 } }""");
        root.Write("app/src/main.nt65", Main.Replace("hw::BORDER", "hw::vic::BORDER", StringComparison.Ordinal)
            + ".segment RODATA\n.data font: .incbin \"../data/font.bin\"\n.export font\n");
        root.Write("app/src/vic.nt65", Hw);
        root.Write("app/data/font.bin", "ABCD");
        var app = Path.Combine(root.FullName, "app");

        Assert.Equal(ExitCode.Success, Run(app, "build", "--depfile", "build/nt65.d").Code);

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
              src/main.nt65 \
              src/vic.nt65 \
              nt65.json

            data/font.bin:

            nt65.json:

            src/main.nt65:

            src/vic.nt65:

            """.ReplaceLineEndings("\n"), root.Read("app/build/nt65.d"));
    }

    /// <summary>
    /// A file named on the command line is built as part of its project, so names that other
    /// modules export resolve as usual, and only that file's output is written.
    /// </summary>
    [Fact]
    public void ANamedFileIsBuiltWithinItsProject()
    {
        Project("""{ "cpu": "6502", "files": ["*.nt65"], "settings": { "DEBUG": 0 } }""");
        root.Write("app/main.nt65", Main.Replace("hw::BORDER", "hw::vic::BORDER", StringComparison.Ordinal));
        root.Write("app/vic.nt65", Hw);

        var (code, printed) = Run(Path.Combine(root.FullName, "app"), "build", "main.nt65");

        Assert.Equal((ExitCode.Success, ""), (code, printed));
        Assert.True(Exists("app/main.s"));
        Assert.False(Exists("app/hw/vic.s"));
    }

    /// <summary>
    /// Where the file system ignores case, a file named in another case is still the file the
    /// project's globs find, and not a second copy that declares its module again.
    /// </summary>
    [Fact]
    public void AFileNamedInAnotherCaseIsTheOneTheProjectFinds()
    {
        Assert.SkipUnless(FilePaths.IgnoresCase, "only a file system that ignores case has two spellings of one file");
        Project("""{ "cpu": "6502", "files": ["src/*.nt65"], "settings": { "DEBUG": 0 } }""");
        root.Write("app/src/main.nt65", Main.Replace("hw::BORDER", "hw::vic::BORDER", StringComparison.Ordinal));
        root.Write("app/src/vic.nt65", Hw);

        var (code, printed) = Run(Path.Combine(root.FullName, "app"), "build", "SRC/MAIN.nt65");

        Assert.Equal((ExitCode.Success, ""), (code, printed));
        Assert.True(Exists("app/main.s"));
    }

    /// <summary>
    /// When a build finds no files, an error in the project file is a far more common cause than a
    /// missing <c>files</c>. The project file's errors are then the whole report, with no note that
    /// nothing matched and none of the forty lines of usage text. The build fails as a program
    /// with errors does, since fixing the file fixes it, and a watch waits for that.
    /// </summary>
    [Theory]
    [InlineData("""{ "cpu": "6502", "files": ["*.nt65"], "flies": [] }""", "nt65.json:1:39: error: `flies` is not a key of nt65.json")]
    [InlineData("""{ "cpu": "6502", "files": ["*.nt65"] """, "nt65.json:1:")]
    public void AProjectFilesOwnErrorIsReportedInPlaceOfThereBeingNoFiles(string project, string starts)
    {
        Project(project);

        var (code, printed) = Run(Path.Combine(root.FullName, "app"), "build");

        Assert.Equal(ExitCode.InputError, code);
        Assert.StartsWith(starts, printed);
        Assert.DoesNotContain("no file matched", printed);
        Assert.DoesNotContain("usage: nt65 build", printed);
    }

    /// <summary>
    /// A file nt65 cannot write, such as an output an emulator holds open on Windows, is reported
    /// as a problem with the files rather than as a bug in nt65, and leaves no temporary file
    /// beside it.
    /// </summary>
    [Fact]
    public void AnOutputThatCannotBeWrittenIsReported()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows refuses to replace a file another program holds open");
        Project("""{ "cpu": "6502", "files": ["*.nt65"], "out": "build" }""");
        root.Write("app/main.nt65", Main.Replace(".use hw::BORDER", ".const BORDER = $d020", StringComparison.Ordinal));
        var app = Path.Combine(root.FullName, "app");
        Assert.Equal(ExitCode.Success, Run(app, "build").Code);
        root.Write("app/main.nt65", Main.Replace(".use hw::BORDER", ".const BORDER = $d021", StringComparison.Ordinal));

        ExitCode code;
        string printed;
        using (new FileStream(root.PathOf("app/build/main.s"), FileMode.Open, FileAccess.Read, FileShare.None))
            (code, printed) = Run(app, "build");

        Assert.Equal(ExitCode.InputError, code);
        Assert.StartsWith("nt65: error: ", printed);
        Assert.Empty(Directory.GetFiles(root.PathOf("app/build"), "*.tmp"));
    }

    /// <summary>
    /// With no project, a name from a module missing from the build is reported, and a build that
    /// assumes a CPU notes it.
    /// </summary>
    [Fact]
    public void WithoutAProjectWhatIsMissingIsReported()
    {
        root.Write("alone/main.nt65", Main.Replace("#DEBUG", "#1", StringComparison.Ordinal));
        root.Write("alone/other.nt65", ".module other\n.export .const N = 1\n");
        var alone = Path.Combine(root.FullName, "alone");

        var (code, printed) = Run(alone, "build", "main.nt65");
        Assert.Equal(ExitCode.InputError, code);
        Assert.Contains("main.nt65:2:6: error: no module `hw` is in this build", printed);

        (code, printed) = Run(alone, "build", "other.nt65");
        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("nt65: note: nothing declares which processor this program is for, so it is built for the 6502", printed);
    }

    /// <summary>
    /// <c>--check</c> reports what a build would report and writes none of the files a build
    /// would write. A gate or a pre-commit hook wants the report and the exit code, with no
    /// output tree to clean up afterwards.
    /// </summary>
    [Fact]
    public void CheckReportsAndWritesNothing()
    {
        Project("""{ "cpu": "6502", "files": ["*.nt65"], "out": "build", "settings": { "DEBUG": 0 } }""");
        root.Write("app/main.nt65", Main.Replace(".use hw::BORDER", ".const BORDER = $d020\n.const UNUSED = 1", StringComparison.Ordinal));
        var app = Path.Combine(root.FullName, "app");

        var (code, printed) = Run(app, "build", "--check", "--depfile", "nt65.d", "--c-header", "nt65.h");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("main.nt65:3:8: warning: `UNUSED` is never used", printed);
        Assert.False(Directory.Exists(Path.Combine(app, "build")));
        Assert.False(System.IO.File.Exists(Path.Combine(app, "nt65.d")));
        Assert.False(System.IO.File.Exists(Path.Combine(app, "nt65.h")));

        // It exits with the code a build would, so a program a build rejects fails the check too.
        root.Write("app/main.nt65", Main.Replace("hw::BORDER", "hw::vic::BORDER", StringComparison.Ordinal));
        Assert.Equal(ExitCode.InputError, Run(app, "build", "--check").Code);
    }

    /// <summary>
    /// <c>--json</c> puts one object per diagnostic on standard output, including the related spans
    /// the one-line form leaves to an editor, and keeps standard error for nt65's own messages.
    /// </summary>
    [Fact]
    public void JsonWritesOneObjectPerDiagnostic()
    {
        Project("""{ "cpu": "6502", "files": ["*.nt65"], "out": "build" }""");
        root.Write("app/main.nt65", ".module main\n.export BORDER\n.const BORDER = $d020\n.const BORDER = $d021\n");
        var app = Path.Combine(root.FullName, "app");

        var (code, output, error) = Apart(app, false, "build", "--json");

        Assert.Equal((ExitCode.InputError, ""), (code, error));
        var diagnostic = JsonDocument.Parse(Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries))).RootElement;
        Assert.Equal("main.nt65", diagnostic.GetProperty("file").GetString());
        Assert.Equal(4, diagnostic.GetProperty("line").GetInt32());
        Assert.Equal(8, diagnostic.GetProperty("column").GetInt32());
        Assert.Equal(14, diagnostic.GetProperty("endColumn").GetInt32());
        Assert.Equal("error", diagnostic.GetProperty("severity").GetString());
        Assert.Equal("`BORDER` is already declared in this scope", diagnostic.GetProperty("message").GetString());

        var related = Assert.Single(diagnostic.GetProperty("related").EnumerateArray());
        Assert.Equal((3, "declared here"),
            (related.GetProperty("line").GetInt32(), related.GetProperty("message").GetString()));

        // A diagnostic with nothing else to point at has no related spans field at all.
        root.Write("app/main.nt65", ".module main\n.export BORDER\n.const BORDER = $d020\n.const UNUSED = 1\n");
        Assert.DoesNotContain("related", Apart(app, false, "build", "--json").Output);
    }

    /// <summary>
    /// Colour highlights a diagnostic's severity and nothing else, so the position stays
    /// selectable and does not compete with the message. Whether to use colour at all is the
    /// caller's decision.
    /// </summary>
    [Fact]
    public void ColourMarksWhatADiagnosticIs()
    {
        Project("""{ "cpu": "6502", "files": ["*.nt65"], "out": "build" }""");
        root.Write("app/main.nt65", ".module main\n.export BORDER\n.const BORDER = $d020\n.const UNUSED = 1\n");
        var app = Path.Combine(root.FullName, "app");

        Assert.Equal(
            "main.nt65:4:8: [33mwarning:[0m `UNUSED` is never used: nothing names it, and it is not "
                + "exported [unused-symbol]\n",
            Apart(app, true, "build").Error);
        Assert.Equal(
            "main.nt65:4:8: warning: `UNUSED` is never used: nothing names it, and it is not exported "
                + "[unused-symbol]\n",
            Apart(app, false, "build").Error);

        root.Write("app/main.nt65", ".module main\n.export BORDER\n.const BORDER = nowhere\n");
        Assert.Equal(
            "main.nt65:3:17: [31merror:[0m `nowhere` is not declared [not-declared]\n",
            Apart(app, true, "build").Error);
    }

    [Fact]
    public void HelpAndVersionAreAnswered()
    {
        var (code, printed) = Run(root.FullName, "--help");
        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("--depfile <file>", printed);

        (code, printed) = Run(root.FullName, "--version");
        Assert.Equal(ExitCode.Success, code);
        Assert.StartsWith("nt65 ", printed);

        (code, printed) = Run(root.FullName, "build", "--bogus");
        Assert.Equal(ExitCode.UsageError, code);
        Assert.StartsWith("nt65: `--bogus` is not an option", printed);
    }

    /// <summary>
    /// An unknown command or option gets a one-line error and a pointer to <c>--help</c>. The
    /// usage text is printed only for <c>nt65</c> on its own and for <c>--help</c>, which asks for it.
    /// </summary>
    [Fact]
    public void WhatIsNotACommandIsReportedWithAPointerToTheHelp()
    {
        var (code, printed) = Run(root.FullName, "check");
        Assert.Equal(ExitCode.UsageError, code);
        Assert.Equal("nt65: `check` is not a command\nsee `nt65 --help`\n", printed);

        (code, printed) = Run(root.FullName, "--watch");
        Assert.Equal(ExitCode.UsageError, code);
        Assert.Equal("nt65: `--watch` is not an option\nsee `nt65 --help`\n", printed);

        (code, printed) = Run(root.FullName, "build", "--rebuild");
        Assert.Equal(ExitCode.UsageError, code);
        Assert.Equal("nt65: `--rebuild` is not an option\nsee `nt65 --help`\n", printed);

        (code, printed) = Run(root.FullName);
        Assert.Equal(ExitCode.UsageError, code);
        Assert.StartsWith("usage: nt65 build", printed);
    }

    /// <summary>
    /// <c>--stdout</c> writes one file's ca65 and nothing else. The text is the same as the
    /// editor's output preview, with a first line that says the output is incomplete when the
    /// program has errors, and no files are written.
    /// </summary>
    [Fact]
    public void StdoutWritesOneFilesOutputAndNoFiles()
    {
        Project("""{ "cpu": "6502", "files": ["*.nt65"], "out": "build" }""");
        root.Write("app/main.nt65", ".module main\n.segment CODE\n.export .proc main {\n    rts\n}\n");
        var app = Path.Combine(root.FullName, "app");

        var (code, output, error) = Apart(app, false, "build", "--stdout", "main.nt65");
        Assert.Equal((ExitCode.Success, ""), (code, error));
        Assert.StartsWith("; Generated by nt65 from main.nt65.", output, StringComparison.Ordinal);
        Assert.Contains("    rts\n", output, StringComparison.Ordinal);
        Assert.False(Exists("app/build/main.s"));

        // A program with errors still prints what could be written, under a line saying it is
        // incomplete.
        root.Write("app/main.nt65", ".module main\n.segment CODE\n.export .proc main {\n    lda nowhere\n    rts\n}\n");
        var (wrong, incomplete, reported) = Apart(app, false, "build", "--stdout", "main.nt65");
        Assert.Equal(ExitCode.InputError, wrong);
        Assert.Contains("`nowhere` is not declared", reported, StringComparison.Ordinal);
        Assert.StartsWith(
            "; nt65: this output is incomplete because the program has an error at line 4: `nowhere` is not declared\n",
            incomplete,
            StringComparison.Ordinal);

        // It prints one file's output, so it needs exactly one file named.
        Assert.Equal(ExitCode.UsageError, Run(app, "build", "--stdout").Code);
    }

    /// <summary>
    /// A module that another module places has no output of its own. <c>--stdout</c> on its file
    /// writes its part of the translation unit's output, between the comments that open and close
    /// it, as the editor's preview shows it. Naming its file in a build writes the unit it is
    /// placed in. The output it had while it stood alone is deleted by the next whole-program
    /// build, like any output the program no longer writes.
    /// </summary>
    [Fact]
    public void APlacedModuleIsWrittenAsItsPartOfTheUnit()
    {
        Project("""{ "cpu": "6502", "files": ["*.nt65"], "out": "build" }""");
        var app = Path.Combine(root.FullName, "app");
        root.Write("app/main.nt65", ".module main\n\n.segment CODE\n.export .proc start {\n    rts\n}\n");
        root.Write("app/part.nt65", ".module part: placeable\n\n.segment CODE\n.export .proc tail {\n    rts\n}\n");
        Assert.Equal(ExitCode.Success, Run(app, "build").Code);
        Assert.True(Exists("app/build/part.s"));

        root.Write("app/main.nt65", ".module main\n\n.segment CODE\n.export .proc start {\n    rts\n}\n\n.place part\n");
        var (code, output, error) = Apart(app, false, "build", "--stdout", "part.nt65");
        Assert.Equal((ExitCode.Success, ""), (code, error));
        Assert.Equal(
            "; .place part  main.nt65:8\n; .proc tail  part.nt65:4\npart__tail:\n    rts\n; end of tail\n; end of part\n",
            output);

        Assert.Equal(ExitCode.Success, Run(app, "build", "part.nt65").Code);
        Assert.Contains("; .place part  main.nt65:8\n", root.Read("app/build/main.s"), StringComparison.Ordinal);
        Assert.True(Exists("app/build/part.s"));

        var (whole, deleted) = Run(app, "build");
        Assert.Equal(ExitCode.Success, whole);
        Assert.Equal(
            "nt65: note: deleted build/part.s, which the program no longer writes\n"
                + "nt65: note: deleted build/part.s.lines, which the program no longer writes\n",
            deleted);
        Assert.False(Exists("app/build/part.s"));
    }

    private static (ExitCode Code, string Printed) Run(string directory, params string[] arguments)
    {
        var (code, output, error) = Apart(directory, false, arguments);
        return (code, output + error);
    }

    /// <summary>
    /// Runs nt65 with standard output and standard error kept apart, so that a test can check
    /// which stream a message goes to.
    /// </summary>
    private static (ExitCode Code, string Output, string Error) Apart(
        string directory, bool colour, params string[] arguments)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var code = Commands.Run(arguments, directory, output, error, colour,
            cancellation: TestTimeout.Token());
        return (code, output.ToString(), error.ToString());
    }

    private void Project(string text) => root.Write("app/nt65.json", text);

    private bool Exists(string path) => System.IO.File.Exists(root.PathOf(path));
}
