using Norristown.Processor;
using Norristown.Project;

namespace Norristown.Tests.Project;

/// <summary>Checks reading <c>nt65.json</c> and the command line that adds to it.</summary>
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

    /// <summary>An empty object is a project that sets nothing, not a broken one.</summary>
    [Fact]
    public void AnEmptyObjectIsAProjectThatSaysNothing()
    {
        var project = Read("{}");

        Assert.Empty(project.Diagnostics);
        Assert.Null(project.Cpu);
        Assert.Empty(project.Files);
    }

    /// <summary>
    /// A value of the wrong shape is reported where it appears, and the rest is still read.
    /// </summary>
    [Theory]
    [InlineData("""{ "cpu": 6502 }""", "`cpu` must be a string")]
    [InlineData("""{ "cpu": "z80" }""", "`z80` is not a supported `cpu`: use `6502`, `6502x`, `65sc02`, `r65c02`, `65c02` or `65816`")]
    [InlineData("""{ "files": "main.nt65" }""", "`files` must be a list of strings")]
    [InlineData("""{ "defines": [] }""", "`defines` must be an object")]
    [InlineData("""{ "defines": { "N": true } }""", "`N`: a define's value must be a number")]
    [InlineData("""{ "defines": { "2N": 1 } }""", "`2N` is not a valid define name: use letters, digits and `_`, optionally after a module path such as `hw::`")]
    [InlineData("""{ "segments": { "X": "zp" } }""", "segment \"X\" must be an object with a `size`")]
    [InlineData("""{ "segments": { "X": {} } }""", "segment \"X\" needs a `size` of \"zp\", \"abs\" or \"far\"")]
    [InlineData("""{ "nope": 1 }""", "`nope` is not a key of nt65.json")]
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

    /// <summary>
    /// A named configuration overrides the project's defines by name, and can set its own output
    /// directory. With no configuration chosen, the project's own settings are used.
    /// </summary>
    [Fact]
    public void AConfigurationOverridesTheDefinesAndTheOutput()
    {
        var project = Read("""
            {
              "out": "build",
              "defines": { "DEBUG": 0, "LEVEL": 3 },
              "configurations": {
                "pal":   { "defines": { "hw::PAL": 1 } },
                "debug": { "defines": { "DEBUG": 1 }, "out": "build/debug" }
              }
            }
            """);

        Assert.Empty(project.Diagnostics);
        Assert.Equal(["debug", "pal"], project.Configurations.Select(configuration => configuration.Name));

        var debug = project.Configured("debug", default);
        Assert.Equal("build/debug", debug.Out);
        Assert.Equal([("DEBUG", 1L), ("LEVEL", 3L)], debug.Defines.Select(define => (define.Name, define.Value)));

        var pal = project.Configured("pal", default);
        Assert.Equal("build", pal.Out);
        Assert.Equal([("DEBUG", 0L), ("LEVEL", 3L), ("hw::PAL", 1L)], pal.Defines.Select(define => (define.Name, define.Value)));
    }

    [Theory]
    [InlineData("""{ "configurations": { "debug": {} } }""", "ntsc", "`ntsc` is not a configuration: nt65.json names `debug`")]
    [InlineData("{}", "ntsc", "`ntsc` is not a configuration: nt65.json names none")]
    public void AConfigurationTheProjectDoesNotNameIsReported(string text, string name, string message)
    {
        var configured = Read(text).Configured(name, new Span("--config", 1, 1, 2));

        Assert.Equal(message, Assert.Single(configured.Diagnostics).Message);
    }

    /// <summary>A define a configuration gets wrong is reported where the configuration gives it.</summary>
    [Fact]
    public void AConfigurationsDefineIsReportedWhereItIsWritten()
    {
        var project = Read("""
            {
              "defines": { "N": 1 },
              "configurations": {
                "a": { "defines": { "N": true }, "cpu": "65816" }
              }
            }
            """);

        Assert.Equal(
            [(4, "`N`: a define's value must be a number"),
                (4, "configuration `a` cannot set `cpu`: a configuration may set only `defines`, "
                    + "`diagnostics` and `out`")],
            project.Diagnostics.Select(diagnostic => (diagnostic.Span.Line, diagnostic.Message)).Order());
    }

    /// <summary><c>-D NAME=value</c> takes a number in nt65's syntax. A bare name is a flag.</summary>
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
    [InlineData("DEBUG=yes", "`yes`: a define's value must be a number")]
    [InlineData("2DEBUG=1", "`2DEBUG` is not a valid define name: use letters, digits and `_`, optionally after a module path such as `hw::`")]
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

    /// <summary>
    /// A project sets each diagnostic's severity by name, and a configuration can override the
    /// project's setting, so that a release build can be stricter than the everyday one.
    /// </summary>
    [Fact]
    public void ADiagnosticIsReportedAsTheProjectAndItsConfigurationSay()
    {
        var project = Read("""
            {
              "diagnostics": { "unused-symbol": "off", "mnemonic-name": "warning" },
              "configurations": {
                "release": { "diagnostics": { "unused-symbol": "error" } }
              }
            }
            """);

        Assert.Empty(project.Diagnostics);
        Assert.Equal([("mnemonic-name", Severity.Warning), ("unused-symbol", null)], Said(project));
        Assert.Equal(
            [("mnemonic-name", Severity.Warning), ("unused-symbol", Severity.Error)],
            Said(project.Configured("release", default)));
    }

    /// <summary>
    /// A warning set to off is dropped, and one set to error becomes an error. A diagnostic
    /// reported as an error is never changed, so a construct that is an error here, even if only
    /// a warning on another processor, cannot be switched off.
    /// </summary>
    [Fact]
    public void WhatTheProjectSaysIsWhatEachDiagnosticIsReportedAs()
    {
        var project = Read("""{ "diagnostics": { "unused-symbol": "off", "mnemonic-name": "error" } }""");
        Diagnostic Said(DiagnosticDescriptor descriptor, Severity severity) =>
            new(new Span("main.nt65", 1, 1, 2), severity, descriptor.Says("x", "y", "z"));

        var reported = Norristown.Diagnostics.WithSeverities(
            [
                Said(Catalogue.UnusedSymbol, Severity.Warning),
                Said(Catalogue.MnemonicName, Severity.Warning),
                Said(Catalogue.UnusedSymbol, Severity.Error),
            ],
            project.Severities);
        Assert.Equal(
            [("mnemonic-name", Severity.Error), ("unused-symbol", Severity.Error)],
            reported.Select(d => (d.Id, d.Severity)));
    }

    [Theory]
    [InlineData("""{ "diagnostics": { "unused-symbols": "off" } }""",
        "`unused-symbols` is not a diagnostic nt65 reports; did you mean `unused-symbol`?")]
    [InlineData("""{ "diagnostics": { "unused-symbol": "quiet" } }""",
        "the severity of `unused-symbol` must be \"off\", \"warning\" or \"error\"")]
    [InlineData("""{ "diagnostics": { "not-declared": "off" } }""",
        "`not-declared` is an error, and a project cannot turn an error into a warning or off")]
    public void WhatIsWrongWithADiagnosticsEntryIsReported(string text, string message)
    {
        var project = Read(text);

        Assert.Equal(message, Assert.Single(project.Diagnostics).Message);
        Assert.Empty(project.Severities);
    }

    private static IEnumerable<(string Id, Severity? Level)> Said(ProjectSettings project) =>
        project.Severities.Select(pair => (pair.Key, pair.Value)).OrderBy(said => said.Key, StringComparer.Ordinal);

    private static ProjectSettings Read(string text) => ProjectFile.Read(ProjectFile.Name, text);
}
