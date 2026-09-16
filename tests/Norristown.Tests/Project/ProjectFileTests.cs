using Norristown.Project;
using Norristown.Semantics;

namespace Norristown.Tests.Project;

/// <summary>Reading <c>nt65.json</c> and the command line that adds to it.</summary>
public sealed class ProjectFileTests
{
    [Fact]
    public void ReadsEveryKeyOfTheDesignsExample()
    {
        var project = Read("""
            {
              "cpu": "65c02",
              "files": ["src/**/*.nt65", "main.nt65"],
              "out": "build",
              "defines": { "DEBUG": 1, "VERSION": "$0102" },
              "segments": {
                "ZP2":  { "size": "zp" },
                "WRAM": { "size": "abs" }
              }
            }
            """);

        Assert.Empty(project.Diagnostics);
        Assert.Equal(Cpu.Wdc65C02, project.Cpu);
        Assert.Equal(["src/**/*.nt65", "main.nt65"], project.Files);
        Assert.Equal("build", project.Out);
        Assert.Equal([("DEBUG", 1L), ("VERSION", 0x0102L)],
            project.Defines.Select(define => (define.Name, define.Value)));
        // Segments come out in name order, so a project always reads the same way.
        Assert.Equal([("WRAM", AddressSize.Absolute), ("ZP2", AddressSize.ZeroPage)],
            project.Segments.Select(segment => (segment.Name, segment.Size)));
    }

    /// <summary>An empty file is a project that says nothing, not a broken one.</summary>
    [Fact]
    public void AnEmptyObjectIsAProjectThatSaysNothing()
    {
        var project = Read("{}");

        Assert.Empty(project.Diagnostics);
        Assert.Null(project.Cpu);
        Assert.Empty(project.Files);
    }

    /// <summary>A value of the wrong shape is reported where it is written, and the rest is still read.</summary>
    [Theory]
    [InlineData("""{ "cpu": 6502 }""", "`cpu` is a string")]
    [InlineData("""{ "cpu": "z80" }""", "`z80` is not a processor nt65 knows: 6502, 65c02 or 65816")]
    [InlineData("""{ "files": "main.nt65" }""", "`files` is a list of strings")]
    [InlineData("""{ "defines": [] }""", "`defines` is an object")]
    [InlineData("""{ "defines": { "N": true } }""", "`N` is not a number, and a define is a number")]
    [InlineData("""{ "defines": { "2N": 1 } }""", "`2N` is not a name")]
    [InlineData("""{ "segments": { "X": "zp" } }""", "segment \"X\" is an object with a `size`")]
    [InlineData("""{ "segments": { "X": {} } }""", "segment \"X\" needs a `size` of \"zp\", \"abs\" or \"far\"")]
    [InlineData("""{ "nope": 1 }""", "`nope` is not a nt65.json key")]
    public void WhatIsWrongWithTheFileIsReported(string text, string message)
    {
        Assert.Equal(message, Assert.Single(Read(text).Diagnostics).Message);
    }

    /// <summary>The diagnostic sits on the key it is about, so an editor can squiggle it.</summary>
    [Fact]
    public void ADiagnosticPointsAtItsKey()
    {
        var project = Read("{\n  \"files\": [],\n  \"cpu\": \"z80\"\n}");

        var diagnostic = Assert.Single(project.Diagnostics);
        Assert.Equal("nt65.json", diagnostic.Span.File);
        Assert.Equal(3, diagnostic.Span.Line);
        Assert.Equal(3, diagnostic.Span.StartColumn);
    }

    /// <summary>A file that is not JSON at all is one diagnostic, not a crash.</summary>
    [Fact]
    public void AFileThatIsNotJsonIsReportedOnce()
    {
        Assert.Single(Read("this is not json").Diagnostics);
    }

    /// <summary>Comments and trailing commas are allowed, so a project file can be annotated.</summary>
    [Fact]
    public void CommentsAndTrailingCommasAreAllowed()
    {
        var project = Read("{\n  // why\n  \"out\": \"build\",\n}");

        Assert.Empty(project.Diagnostics);
        Assert.Equal("build", project.Out);
    }

    /// <summary><c>-D NAME=value</c> takes a number in nt65's syntax; a bare name is a flag.</summary>
    [Theory]
    [InlineData("DEBUG=1", "DEBUG", 1L)]
    [InlineData("DEBUG=$ff", "DEBUG", 255L)]
    [InlineData("DEBUG=%1010", "DEBUG", 10L)]
    [InlineData("DEBUG=-1", "DEBUG", -1L)]
    [InlineData("DEBUG", "DEBUG", 1L)]
    public void ADefinitionOnTheCommandLineIsANumber(string argument, string name, long value)
    {
        var problems = new List<Diagnostic>();
        var define = ProjectFile.Definition(argument, problems);

        Assert.Empty(problems);
        Assert.NotNull(define);
        Assert.Equal(name, define.Name);
        Assert.Equal(value, define.Value);
    }

    [Theory]
    [InlineData("DEBUG=yes", "`yes` is not a number, and a define is a number")]
    [InlineData("2DEBUG=1", "`2DEBUG` is not a name")]
    public void ADefinitionThatIsNotOneIsReported(string argument, string message)
    {
        var problems = new List<Diagnostic>();

        Assert.Null(ProjectFile.Definition(argument, problems));
        Assert.Equal(message, Assert.Single(problems).Message);
    }

    /// <summary><c>-D</c> adds a define or overrides one the file gives.</summary>
    [Fact]
    public void TheCommandLineOverridesTheFile()
    {
        var project = Read("""{ "defines": { "DEBUG": 0, "KEEP": 7 } }""")
            .With([new Define("DEBUG", 1, new Span("-D", 1, 1, 2)), new Define("EXTRA", 3, new Span("-D", 1, 1, 2))]);

        Assert.Equal([("DEBUG", 1L), ("EXTRA", 3L), ("KEEP", 7L)],
            project.Defines.Select(define => (define.Name, define.Value)));
    }

    private static ProjectSettings Read(string text) => ProjectFile.Read(ProjectFile.Name, text);
}
