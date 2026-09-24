using Norristown.Cli;

namespace Norristown.Tests.Cli;

/// <summary>
/// Tests <c>nt65 fmt</c> on real directories. The tests cover what it writes, what
/// <c>--check</c> reports without writing, and what it formats when no file is named.
/// </summary>
public sealed class FormatCommandTests : IDisposable
{
    private const string Crooked = ".module main\n.segment CODE\n  .proc main {\nrts   \n   }\n";
    private const string Straight = ".module main\n.segment CODE\n.proc main {\n    rts\n}\n";

    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("nt65-fmt-");

    public void Dispose() => root.Delete(recursive: true);

    /// <summary>A file named on the command line is rewritten in the standard layout, and nothing is printed.</summary>
    [Fact]
    public void ANamedFileIsWrittenInTheOneLayout()
    {
        File("main.nt65", Crooked);

        var (code, printed) = Run(root.FullName, "fmt", "main.nt65");

        Assert.Equal((0, ""), (code, printed));
        Assert.Equal(Straight, Read("main.nt65"));
    }

    /// <summary>
    /// <c>--check</c> writes nothing and lists the files it would have written, which is what a
    /// gate wants. The exit code shows whether the tree is formatted, and the list shows where it
    /// is not.
    /// </summary>
    [Fact]
    public void CheckListsWhatIsNotFormattedAndWritesNothing()
    {
        File("src/main.nt65", Crooked);
        File("src/hw.nt65", ".module hw\n");

        var (code, printed) = Run(root.FullName, "fmt", "--check", "src/main.nt65", "src/hw.nt65");

        Assert.Equal((1, "src/main.nt65\n"), (code, printed));
        Assert.Equal(Crooked, Read("src/main.nt65"));

        Assert.Equal((0, ""), Run(root.FullName, "fmt", "src/main.nt65"));
        Assert.Equal((0, ""), Run(root.FullName, "fmt", "--check", "src/main.nt65", "src/hw.nt65"));
    }

    /// <summary>
    /// Given no files, it formats the files of the nearest project at or above the directory it
    /// runs in. This is how a whole repository is formatted.
    /// </summary>
    [Fact]
    public void NamedNothingItFormatsWhatTheProjectNames()
    {
        File("app/nt65.json", """{ "cpu": "6502", "files": ["src/*.nt65"] }""");
        File("app/src/main.nt65", Crooked);
        File("app/src/hw.nt65", "  .module hw\n");
        File("app/notes.nt65", Crooked);

        Assert.Equal((0, ""), Run(Path.Combine(root.FullName, "app", "src"), "fmt"));

        Assert.Equal(Straight, Read("app/src/main.nt65"));
        Assert.Equal(".module hw\n", Read("app/src/hw.nt65"));

        // The project's `files` are the program, and a file it does not name is not part of it.
        Assert.Equal(Crooked, Read("app/notes.nt65"));
    }

    /// <summary>
    /// Formatting needs no program, so a named file that belongs to no project still formats.
    /// With no file named and no project to take files from, there is nothing to format, and nt65
    /// reports that before printing the usage text.
    /// </summary>
    [Fact]
    public void WhatItCannotFormatIsReported()
    {
        var (code, printed) = Run(root.FullName, "fmt");
        Assert.Equal(2, code);
        Assert.StartsWith("nt65: no files to format, and no nt65.json\nusage: nt65 build", printed);

        (code, printed) = Run(root.FullName, "fmt", "gone.nt65");
        Assert.Equal((1, "gone.nt65: error: file not found\n"), (code, printed));

        (code, printed) = Run(root.FullName, "fmt", "--write");
        Assert.Equal((2, "nt65: `--write` is not an option\nsee `nt65 --help`\n"), (code, printed));
    }

    private static (int Code, string Printed) Run(string directory, params string[] arguments)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var code = Commands.Run(arguments, directory, output, error,
            cancellation: TestTimeout.Token());
        return (code, output.ToString() + error.ToString());
    }

    private void File(string path, string text) => Repo.WriteText(Path.Combine(root.FullName, path), text);

    private string Read(string path) => System.IO.File.ReadAllText(Path.Combine(root.FullName, path));
}
