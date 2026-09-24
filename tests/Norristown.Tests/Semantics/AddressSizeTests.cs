using Norristown.Processor;

namespace Norristown.Tests.Semantics;

/// <summary>Checks where a symbol's address size comes from, which is its segment or its value.</summary>
public sealed class AddressSizeTests
{
    [Theory]
    [InlineData("$ff", AddressSize.ZeroPage)]
    [InlineData("$0400", AddressSize.Absolute)]
    [InlineData("$7e0000", AddressSize.Far)]
    public void AConstantIsSizedByItsValue(string value, AddressSize expected)
    {
        var model = Analysis.Model($".module main\n.const VALUE = {value}\n");

        Assert.Equal(expected, model.Symbol("VALUE").AddressSize);
    }

    [Fact]
    public void ALabelIsSizedByItsSegment()
    {
        var model = Analysis.Model("""
            .module main
            .segment LONG: far

            .segment ZEROPAGE
            .data ptr:    .word
            .segment CODE
            .data entry:  .byte 0
            .segment LONG
            .data away:   .byte 0
            """);

        Assert.Empty(model.Problems());
        Assert.Equal(AddressSize.ZeroPage, model.Symbol("ptr").AddressSize);
        Assert.Equal(AddressSize.Absolute, model.Symbol("entry").AddressSize);
        Assert.Equal(AddressSize.Far, model.Symbol("away").AddressSize);
    }

    /// <summary>
    /// A <c>.segment</c> region puts everything after it in its segment, up to the next region.
    /// Before the first region there is no default segment.
    /// </summary>
    [Fact]
    public void ARegionPutsWhatFollowsItInItsSegment()
    {
        var model = Analysis.Model(".module main\n.const SIZE = 1\n.segment ZEROPAGE\n.data ptr: .word\n.proc early {\nrts\n}\n"
            + ".segment CODE\n.proc main {\nrts\n}\n");

        Assert.Null(model.Symbol("SIZE").Segment);
        Assert.Equal("ZEROPAGE", model.Symbol("ptr").Segment);
        Assert.Equal("ZEROPAGE", model.Symbol("early").Segment);
        Assert.Equal("CODE", model.Symbol("main").Segment);
        Assert.Equal(AddressSize.Absolute, model.Symbol("main").AddressSize);
    }

    /// <summary>Anything with an address must be in a segment. A constant need not be.</summary>
    [Fact]
    public void BytesOutsideEverySegmentAreAnError()
    {
        var program = Analysis.Program(("main.nt65", ".module main\n.const SIZE = 1\n.proc main {\n    rts\n}\n.data table: .byte SIZE\n"));

        Assert.Equal(
            [
                "main.nt65:3: `main` is not in any segment: put a `.segment NAME` line above it, or place it in a `.segment NAME` block",
                "main.nt65:6: `table` is not in any segment: put a `.segment NAME` line above it, or place it in a `.segment NAME` block",
            ],
            program.Problems());
    }

    /// <summary>
    /// A constant whose expression names addresses takes the widest of their address sizes,
    /// regardless of what its own value would suggest.
    /// </summary>
    [Fact]
    public void AnAliasTakesTheWidestAddressItNames()
    {
        var model = Analysis.Model("""
            .module main
            .segment LONG: far

            .segment ZEROPAGE
            .data ptr:    .word
            .segment LONG
            .data away:   .byte 0
            .data NEXT: .byte = ptr + 1
            .const MIXED   = ptr + away
            """);

        Assert.Empty(model.Problems());
        Assert.Equal(AddressSize.ZeroPage, model.Symbol("NEXT").AddressSize);
        Assert.Equal(AddressSize.Far, model.Symbol("MIXED").AddressSize);
    }

    /// <summary><c>.addrsize</c> gives a symbol's address size in bytes.</summary>
    [Fact]
    public void AddrsizeIsTheSizeInBytes()
    {
        var model = Analysis.Model("""
            .module main
            .segment ZEROPAGE
            .data ptr:    .word
            .const SCREEN  = $0400
            .const NARROW  = .addrsize(ptr)
            .const WIDE    = .addrsize(SCREEN)
            """);

        Assert.Empty(model.Problems());
        Assert.Equal(1, model.Symbol("NARROW").Value.Number);
        Assert.Equal(2, model.Symbol("WIDE").Value.Number);
    }

    /// <summary>
    /// The size is the word after the <c>:</c> and nothing else, so an import or a
    /// segment whose own name happens to be <c>zp</c> or <c>abs</c> is only a name.
    /// </summary>
    [Fact]
    public void AnAddressSizeIsOnlyTheWordAfterItsColon()
    {
        var model = Analysis.Model(".module main\n.import zp\n.segment abs: zp\n.segment abs\n.data ptr: .byte 0\n");

        Assert.Empty(model.Problems());
        Assert.Equal(AddressSize.Absolute, model.Symbol("zp").AddressSize);
        Assert.Equal(AddressSize.ZeroPage, model.Symbol("ptr").AddressSize);
    }

    /// <summary>
    /// A block naming a segment nothing declares is an error, and gives nothing an address size.
    /// </summary>
    [Fact]
    public void AnUndeclaredSegmentIsReported()
    {
        var model = Analysis.Model(".module main\n.segment NOWHERE\n.data lost:   .byte 0\n");

        Assert.Equal(["2: segment \"NOWHERE\" is not declared"], model.Problems());
        Assert.Null(model.Symbol("lost").AddressSize);
    }
}
