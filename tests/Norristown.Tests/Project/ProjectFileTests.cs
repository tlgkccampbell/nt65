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
              "settings": { "DEBUG": 1, "VERSION": "$0102" },
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
            project.SettingValues.Select(value => (value.Name, value.Value)));
        // Segments come out in name order, so a project always reads the same way.
        Assert.Equal([("WRAM", AddressSize.Absolute), ("ZP2", AddressSize.ZeroPage)],
            project.Segments.Select(segment => (segment.Name, segment.Size)));
    }

    /// <summary>An empty object is a project that sets nothing, not a broken one.</summary>
    [Fact]
    public void AnEmptyObjectIsAProjectThatSetsNothing()
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
    [InlineData("""{ "settings": [] }""", "`settings` must be an object")]
    [InlineData("""{ "settings": { "N": true } }""", "`N`: a setting's value must be a number")]
    [InlineData("""{ "settings": { "2N": 1 } }""", "`2N` is not a valid setting name: use letters, digits and `_`, optionally after a module path such as `hw::`")]
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

    /// <summary>
    /// Invalid JSON is reported at the character where it goes wrong. The JSON reader counts
    /// bytes, so text that takes more than one byte earlier on the line must not move the column.
    /// </summary>
    [Theory]
    [InlineData("{\n  \"out\": \"b\" x\n}", 2, 14)]
    [InlineData("{\n  \"out\": \"é\" x\n}", 2, 14)]
    [InlineData("{\n  \"out\": \"日本\" x\n}", 2, 15)]
    [InlineData("{\n  \"out\": \"😀\" x\n}", 2, 15)]
    public void InvalidJsonIsReportedAtItsCharacter(string text, int line, int column)
    {
        var diagnostic = Assert.Single(Read(text).Diagnostics);

        Assert.Equal((line, column), (diagnostic.Span.Line, diagnostic.Span.StartColumn));
    }

    /// <summary>
    /// A diagnostic sits on the key where the JSON puts it. The same text earlier in the file, as
    /// a value, as a key at another level or inside a comment, is passed over.
    /// </summary>
    [Theory]
    [InlineData("{\n  \"out\": \"nope\",\n  \"nope\": 1\n}", 3, 3)]
    [InlineData("{\n  // \"cpu\" names the processor\n  \"cpu\": \"z80\"\n}", 3, 3)]
    [InlineData("{\n  \"settings\": { \"out\": 1 },\n  \"out\": 2\n}", 3, 3)]
    [InlineData("{\n  \"spaces\": { \"spc\": \"code\" },\n  \"segments\": { \"code\": { \"size\": \"zp\", \"x\": 1 } }\n}", 3, 17)]
    [InlineData("{\n  \"out\": \"a\",\n  \"configurations\": { \"a\": { \"settings\": { \"out\": 1 }, \"out\": 2 } }\n}", 3, 56)]
    public void ADiagnosticPointsAtTheKeyTheJsonGives(string text, int line, int column)
    {
        var diagnostic = Assert.Single(Read(text).Diagnostics);

        Assert.Equal((line, column), (diagnostic.Span.Line, diagnostic.Span.StartColumn));
    }

    /// <summary>A segment is declared where its key is, even when its name is also a value.</summary>
    [Fact]
    public void ASegmentIsDeclaredAtItsKey()
    {
        var project = Read("{\n  \"spaces\": { \"spc\": \"code\" },\n  \"segments\": { \"code\": { \"size\": \"zp\" } }\n}");

        Assert.Empty(project.Diagnostics);
        var segment = Assert.Single(project.Segments);
        Assert.Equal(new Span(ProjectFile.Name, 3, 17, 23), segment.Declaration);
    }

    /// <summary>
    /// A key written with an escape is found by the name it spells, and its span covers the key
    /// as written.
    /// </summary>
    [Fact]
    public void AnEscapedKeyIsFoundByItsName()
    {
        var diagnostic = Assert.Single(Read("{ \"c\\u0070u\": \"z80\" }").Diagnostics);

        Assert.Equal((1, 3, 13), (diagnostic.Span.Line, diagnostic.Span.StartColumn, diagnostic.Span.EndColumn));
    }

    /// <summary>The empty severity map that every project without overrides shares cannot be changed.</summary>
    [Fact]
    public void TheSharedEmptySeverityMapIsReadOnly() =>
        Assert.False(ProjectSettings.NoSeverities is IDictionary<string, Severity?> { IsReadOnly: false });

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
    /// A named configuration overrides the project's setting values by name, and can set its own output
    /// directory. With no configuration chosen, the project's own settings are used.
    /// </summary>
    [Fact]
    public void AConfigurationOverridesTheDefinesAndTheOutput()
    {
        var project = Read("""
            {
              "out": "build",
              "settings": { "DEBUG": 0, "LEVEL": 3 },
              "configurations": {
                "pal":   { "settings": { "hw::PAL": 1 } },
                "debug": { "settings": { "DEBUG": 1 }, "out": "build/debug" }
              }
            }
            """);

        Assert.Empty(project.Diagnostics);
        Assert.Equal(["debug", "pal"], project.Configurations.Select(configuration => configuration.Name));

        var debug = project.Configured("debug", default);
        Assert.Equal("build/debug", debug.Out);
        Assert.Equal([("DEBUG", 1L), ("LEVEL", 3L)], debug.SettingValues.Select(value => (value.Name, value.Value)));

        var pal = project.Configured("pal", default);
        Assert.Equal("build", pal.Out);
        Assert.Equal([("DEBUG", 0L), ("LEVEL", 3L), ("hw::PAL", 1L)], pal.SettingValues.Select(value => (value.Name, value.Value)));
    }

    [Theory]
    [InlineData("""{ "configurations": { "debug": {} } }""", "ntsc", "`ntsc` is not a configuration: nt65.json names `debug`")]
    [InlineData("{}", "ntsc", "`ntsc` is not a configuration: nt65.json names none")]
    public void AConfigurationTheProjectDoesNotNameIsReported(string text, string name, string message)
    {
        var configured = Read(text).Configured(name, new Span("--config", 1, 1, 2));

        Assert.Equal(message, Assert.Single(configured.Diagnostics).Message);
    }

    /// <summary>A setting value a configuration gets wrong is reported where the configuration gives it.</summary>
    [Fact]
    public void AConfigurationsSettingValueIsReportedWhereItIsDeclared()
    {
        var project = Read("""
            {
              "settings": { "N": 1 },
              "configurations": {
                "a": { "settings": { "N": true }, "cpu": "65816" }
              }
            }
            """);

        Assert.Equal(
            [(4, "`N`: a setting's value must be a number"),
                (4, "configuration `a` cannot set `cpu`: a configuration may set only `settings`, "
                    + "`diagnostics`, `links` and `out`")],
            project.Diagnostics.Select(diagnostic => (diagnostic.Span.Line, diagnostic.Message)).Order());
    }

    /// <summary><c>-D NAME=value</c> takes a number in nt65's syntax. A bare name is a flag.</summary>
    [Theory]
    [InlineData("DEBUG=1", "DEBUG", 1L)]
    [InlineData("DEBUG=$ff", "DEBUG", 255L)]
    [InlineData("DEBUG=%1010", "DEBUG", 10L)]
    [InlineData("DEBUG=-1", "DEBUG", -1L)]
    [InlineData("DEBUG", "DEBUG", 1L)]
    public void ASettingValueOnTheCommandLineIsANumber(string argument, string name, long expected)
    {
        var problems = new List<Diagnostic>();
        var value = ProjectFile.SettingValue(argument, problems);

        Assert.Empty(problems);
        Assert.NotNull(value);
        Assert.Equal(name, value.Name);
        Assert.Equal(expected, value.Value);
    }

    [Theory]
    [InlineData("DEBUG=yes", "`DEBUG`: a setting's value must be a number")]
    [InlineData("2DEBUG=1", "`2DEBUG` is not a valid setting name: use letters, digits and `_`, optionally after a module path such as `hw::`")]
    public void ASettingValueThatIsNotOneIsReported(string argument, string message)
    {
        var problems = new List<Diagnostic>();

        Assert.Null(ProjectFile.SettingValue(argument, problems));
        Assert.Equal(message, Assert.Single(problems).Message);
    }

    /// <summary><c>-D</c> adds a setting value or overrides one the file gives.</summary>
    [Fact]
    public void TheCommandLineOverridesTheFile()
    {
        var project = Read("""{ "settings": { "DEBUG": 0, "KEEP": 7 } }""")
            .With([new SettingValue("DEBUG", 1, new Span("-D", 1, 1, 2)), new SettingValue("EXTRA", 3, new Span("-D", 1, 1, 2))]);

        Assert.Equal([("DEBUG", 1L), ("EXTRA", 3L), ("KEEP", 7L)],
            project.SettingValues.Select(value => (value.Name, value.Value)));
    }

    /// <summary>
    /// A project sets each diagnostic's severity by name, and a configuration can override the
    /// project's setting, so that a release build can be stricter than the everyday one.
    /// </summary>
    [Fact]
    public void ADiagnosticIsReportedAsTheProjectAndItsConfigurationSet()
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
        Assert.Equal([("mnemonic-name", Severity.Warning), ("unused-symbol", null)], Severities(project));
        Assert.Equal(
            [("mnemonic-name", Severity.Warning), ("unused-symbol", Severity.Error)],
            Severities(project.Configured("release", default)));
    }

    /// <summary>
    /// A warning set to off is dropped, and one set to error becomes an error. A diagnostic
    /// reported as an error is never changed, so a construct that is an error here, even if only
    /// a warning on another processor, cannot be switched off.
    /// </summary>
    [Fact]
    public void EachDiagnosticIsReportedAtTheSeverityTheProjectSets()
    {
        var project = Read("""{ "diagnostics": { "unused-symbol": "off", "mnemonic-name": "error" } }""");
        Diagnostic Create(DiagnosticDescriptor descriptor, Severity severity) =>
            new(new Span("main.nt65", 1, 1, 2), severity, descriptor.Message("x", "y", "z"));

        var reported = Norristown.Diagnostics.WithSeverities(
            [
                Create(Catalogue.UnusedSymbol, Severity.Warning),
                Create(Catalogue.MnemonicName, Severity.Warning),
                Create(Catalogue.UnusedSymbol, Severity.Error),
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

    private static IEnumerable<(string Id, Severity? Level)> Severities(ProjectSettings project) =>
        project.Severities.Select(pair => (pair.Key, pair.Value)).OrderBy(entry => entry.Key, StringComparer.Ordinal);

    private static ProjectSettings Read(string text) => ProjectFile.Read(ProjectFile.Name, text);
}
