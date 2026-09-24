using Norristown.Project;

namespace Norristown.Tests.Project;

/// <summary>Checks reading the parts of an ld65 linker config that nt65 uses.</summary>
public sealed class LinkerConfigTests
{
    [Fact]
    public void ReadsMemoryAndSegments()
    {
        var config = LinkerConfig.Parse("c64.cfg", """
            # A PRG that loads where BASIC programs do.
            FEATURES {
                STARTADDRESS: default = $0801;
            }
            SYMBOLS {
                __LOADADDR__: type = import;
                __STACKSIZE__: type = weak, value = $0800;
            }
            MEMORY {
                ZP:       file = "", start = $00FB, size = $0004;
                LOADADDR: file = %O, start = $07FF, size = $0002;
                MAIN:     file = %O  start = $0801  size = $9800 - __STACKSIZE__;
            }
            FILES { %O: format = bin; }
            SEGMENTS {
                ZEROPAGE: load = ZP, type = zp;
                LOADADDR: load = LOADADDR, type = ro;
                CODE:     load = MAIN, type = ro;
                CHRGET:   load = MAIN, run = ZP, type = rw, define = yes;
                VECTORS:  load = MAIN, type = ro, start = $FFFA;
            }
            """);

        Assert.Empty(config.Diagnostics);
        Assert.Equal<(long?, long?)>((0x0801L, 0x9000L), (config.Area("MAIN")!.Start, config.Area("MAIN")!.Size));
        Assert.Equal(["ZEROPAGE", "LOADADDR", "CODE", "CHRGET", "VECTORS"], config.Segments.Select(segment => segment.Name));

        var chrget = config.Segments[3];
        Assert.Equal(("MAIN", "ZP", "rw", true), (chrget.Load, chrget.RunsIn, chrget.Type, chrget.Defines));
        Assert.Equal(new Span("c64.cfg", 19, 5, 11), chrget.Declaration);
        Assert.Equal(0xfffaL, config.Segments[4].Start!.Value.Value);
    }

    /// <summary>A value that only ld65's command line gives is unknown rather than guessed.</summary>
    [Fact]
    public void AValueFromTheCommandLineIsUnknown()
    {
        var config = LinkerConfig.Parse("x.cfg", """
            MEMORY {
                MAIN: start = %S, size = __HIMEM__ - %S;
                LOOP: start = LOOP_START, size = 1;
            }
            SYMBOLS { LOOP_START: value = LOOP_START + 1; }
            """);

        Assert.Empty(config.Diagnostics);
        Assert.Null(config.Area("MAIN")!.Start);
        Assert.Null(config.Area("MAIN")!.Size);
        Assert.Null(config.Area("LOOP")!.Start);
    }

    [Fact]
    public void ReportsTextLd65WouldRejectWhereItIs()
    {
        var config = LinkerConfig.Parse("x.cfg", """
            MEMORY {
                ROM: start = $8000 size = $8000
            }
            """);

        var diagnostic = Assert.Single(config.Diagnostics);
        Assert.Equal(("linked-config-invalid", 3), (diagnostic.Id, diagnostic.Span.Line));
    }
}
