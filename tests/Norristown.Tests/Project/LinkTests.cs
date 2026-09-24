using Norristown.Processor;
using Norristown.Project;
using Norristown.Semantics;

namespace Norristown.Tests.Project;

/// <summary>
/// Checks the segments a project declares through <c>links</c>: what each linked config gives
/// them, what the project file adds, and what several links must agree on.
/// </summary>
public sealed class LinkTests
{
    private const string Cartridge = """
        MEMORY {
            ZP:     start = $0000, size = $0100;
            WRAM:   start = $7E2000, size = $E000;
            ROM0:   start = $808000, size = $8000, file = %O;
            ROM1:   start = $818000, size = $8000, file = %O;
            BIG:    start = $7F0000, size = $20000;
            SPCRAM: start = $0200, size = $FDC0;
        }
        SEGMENTS {
            ZEROPAGE: load = ZP, type = zp;
            BSS:      load = WRAM, type = bss;
            CODE:     load = ROM0, type = ro;
            HEADER:   load = ROM0, type = ro, start = $80FFC0;
            BANK1:    load = ROM1, type = ro;
            HUGE:     load = BIG, type = bss;
            SPCIMAGE: load = ROM1, run = SPCRAM, define = yes;
        }
        """;

    private const string Sound = """
        MEMORY {
            SPCZP:  start = $0010, size = $00E0;
            SPCRAM: start = $0200, size = $FDC0;
        }
        SEGMENTS {
            SPCIMAGE: load = SPCRAM;
            SPCZP:    load = SPCZP, type = zp;
        }
        """;

    [Fact]
    public void AConfigGivesEachSegmentItsSizeAndBank()
    {
        var project = Read("""
            { "links": { "rom": { "config": "rom.cfg" } } }
            """, ("rom.cfg", Cartridge));

        Assert.Empty(project.Diagnostics);
        Assert.Equal(
            [
                ("BANK1", AddressSize.Absolute, 0x81L), ("BSS", AddressSize.Absolute, 0x7eL),
                ("CODE", AddressSize.Absolute, 0x80L), ("HEADER", AddressSize.Absolute, 0x80L),
                ("HUGE", AddressSize.Absolute, null), ("SPCIMAGE", AddressSize.Absolute, 0L),
                ("ZEROPAGE", AddressSize.ZeroPage, 0L),
            ],
            project.Segments.Select(segment => (segment.Name, segment.Size, segment.Bank)));

        var code = project.Segments.Single(segment => segment.Name == "CODE");
        Assert.Equal([new Span("rom.cfg", 12, 5, 9)], code.Placements);
        Assert.Equal(code.Placements[0], code.Declaration);
        Assert.False(code.IsDefined);
        Assert.True(project.Segments.Single(segment => segment.Name == "SPCIMAGE").IsDefined);
    }

    [Fact]
    public void AMemoryAreaGivesTheSegmentsInItTheirSpaceAndMirrors()
    {
        var project = Read("""
            {
              "spaces": { "spc": "data" },
              "links": {
                "rom": {
                  "config": "rom.cfg",
                  "memory": {
                    "WRAM": { "mirrors": ["$00-$3f"] },
                    "SPCRAM": { "space": "spc" }
                  }
                }
              }
            }
            """, ("rom.cfg", Cartridge));

        Assert.Empty(project.Diagnostics);
        Assert.Equal([(0L, 0x3fL)], Find(project, "BSS").Mirrors);
        Assert.Equal("spc", Find(project, "SPCIMAGE").Space);
        Assert.Null(Find(project, "CODE").Space);
    }

    [Fact]
    public void LinksThatPlaceTheSameSegmentAgreeOnIt()
    {
        var project = Read("""
            {
              "spaces": { "spc": "data" },
              "links": {
                "rom": { "config": "rom.cfg", "memory": { "SPCRAM": { "space": "spc" } } },
                "spc": { "config": "spc/spc.cfg", "space": "spc" }
              }
            }
            """, ("rom.cfg", Cartridge), ("spc/spc.cfg", Sound));

        Assert.Empty(project.Diagnostics);
        var image = Find(project, "SPCIMAGE");
        Assert.Equal(["rom.cfg", "spc/spc.cfg"], image.Placements.Select(span => span.File));
        Assert.True(image.IsDefined);
        Assert.Equal("spc", Find(project, "SPCZP").Space);
    }

    [Fact]
    public void LinksThatDisagreeAboutASegmentAreReportedAtTheSecond()
    {
        // The sound link leaves SPCRAM in the host's space, which the cartridge puts in `spc`.
        var project = Read("""
            {
              "spaces": { "spc": "data" },
              "links": {
                "rom": { "config": "rom.cfg", "memory": { "SPCRAM": { "space": "spc" } } },
                "spc": { "config": "spc.cfg" }
              }
            }
            """, ("rom.cfg", Cartridge), ("spc.cfg", Sound));

        var diagnostic = Assert.Single(project.Diagnostics);
        Assert.Equal(("linked-segments-disagree", "spc.cfg", 6), (diagnostic.Id, diagnostic.Span.File, diagnostic.Span.Line));
        Assert.Equal(
            "segment \"SPCIMAGE\" is in the host's space here and in space `spc` in `rom.cfg`", diagnostic.Message);
        Assert.Equal("rom.cfg", Assert.Single(diagnostic.Related).Span.File);
    }

    [Fact]
    public void TheProjectFileAddsOnlyWhatNoConfigSays()
    {
        var project = Read("""
            {
              "links": { "rom": { "config": "rom.cfg" } },
              "segments": {
                "ZEROPAGE": { "dp": "$2100" },
                "BANK1":    { "size": "far" },
                "HUGE":     { "bank": "$7f" },
                "CODE":     { "size": "abs", "bank": "$80", "mirrors": ["$00"] },
                "MISSING":  { "size": "abs" }
              }
            }
            """, ("rom.cfg", Cartridge));

        Assert.Equal(0x2100L, Find(project, "ZEROPAGE").DirectPage);
        Assert.Equal(AddressSize.Far, Find(project, "BANK1").Size);
        Assert.Equal(0x7fL, Find(project, "HUGE").Bank);
        Assert.NotNull(Find(project, "HUGE").Addition);
        Assert.Equal(
            [
                ("project-segment-from-link", "segment \"CODE\" is linked, so its `size` comes from `type` in `rom.cfg`, "
                    + "and only an absolute segment may be made `far`"),
                ("project-segment-from-link", "segment \"CODE\" is linked, so its `mirrors` is given on the memory area it "
                    + "runs in, under `links`"),
                ("project-segment-from-link", "segment \"CODE\" is linked, so its `bank` comes from `rom.cfg`, which runs it "
                    + "in bank $80"),
                ("segment-not-linked", "segment \"MISSING\" is not in any linked config, so ld65 has nowhere to put it"),
            ],
            project.Diagnostics.Select(diagnostic => (diagnostic.Id, diagnostic.Message)));
    }

    [Fact]
    public void AConfigurationReplacesTheProjectsLinkOfTheSameName()
    {
        var project = Read("""
            {
              "links": { "rom": { "config": "a.cfg" } },
              "configurations": { "b": { "links": { "rom": { "config": "b.cfg" } } } }
            }
            """,
            ("a.cfg", "MEMORY { M: start = $8000, size = $100; } SEGMENTS { ONLYA: load = M; }"),
            ("b.cfg", "MEMORY { M: start = $8000, size = $100; } SEGMENTS { ONLYB: load = M; }"));

        Assert.Equal(["ONLYA"], project.Segments.Select(segment => segment.Name));
        var configured = project.Configured("b", default);
        Assert.Empty(configured.Diagnostics);
        Assert.Equal(["ONLYB"], configured.Segments.Select(segment => segment.Name));
        Assert.Equal(["a.cfg", "b.cfg"], project.LinkedFiles);
    }

    [Fact]
    public void ALinkThatCannotBeReadIsReported()
    {
        var project = Read("""
            {
              "links": {
                "rom": { "config": "missing.cfg", "memory": { "RAM": { "banks": [] } } },
                "bad": "rom.cfg"
              }
            }
            """);

        Assert.Equal(
            [
                ("link-key-unknown", 3),
                ("link-not-an-object", 4),
                ("linked-config-unreadable", 3),
            ],
            project.Diagnostics.Select(diagnostic => (diagnostic.Id, diagnostic.Span.Line)));
    }

    [Fact]
    public void AMemoryAreaTheConfigDoesNotHaveIsReported()
    {
        var project = Read("""
            { "links": { "rom": { "config": "rom.cfg", "memory": { "WROM": { "mirrors": ["$00"] } } } } }
            """, ("rom.cfg", Cartridge));

        var diagnostic = Assert.Single(project.Diagnostics);
        Assert.Equal("`rom.cfg` has no memory area `WROM`", diagnostic.Message);
    }

    private static Segment Find(ProjectSettings project, string name) =>
        project.Segments.Single(segment => segment.Name == name);

    private static ProjectSettings Read(string text, params (string Path, string Text)[] files) =>
        ProjectFile.Read(ProjectFile.Name, text, path => files.FirstOrDefault(file => file.Path == path).Text);
}
