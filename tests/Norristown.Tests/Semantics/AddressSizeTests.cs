using Norristown.Semantics;

namespace Norristown.Tests.Semantics;

/// <summary>Where a symbol's address size comes from: its segment, or its value.</summary>
public sealed class AddressSizeTests
{
    [Theory]
    [InlineData("$ff", AddressSize.ZeroPage)]
    [InlineData("$0400", AddressSize.Absolute)]
    [InlineData("$7e0000", AddressSize.Far)]
    public void AConstantIsSizedByItsValue(string value, AddressSize expected)
    {
        var model = Analysis.Model($"VALUE = {value}\n");

        Assert.Equal(expected, model.Symbol("VALUE").AddressSize);
    }

    [Fact]
    public void ALabelIsSizedByItsSegment()
    {
        var model = Analysis.Model("""
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

    /// <summary>A region places everything after it, up to the next one: there is no default segment.</summary>
    [Fact]
    public void ARegionPlacesWhatFollowsIt()
    {
        var model = Analysis.Model("SIZE = 1\n.segment ZEROPAGE\n.data ptr: .word\n.proc early {\nrts\n}\n"
            + ".segment CODE\n.proc main {\nrts\n}\n");

        Assert.Null(model.Symbol("SIZE").Segment);
        Assert.Equal("ZEROPAGE", model.Symbol("ptr").Segment);
        Assert.Equal("ZEROPAGE", model.Symbol("early").Segment);
        Assert.Equal("CODE", model.Symbol("main").Segment);
        Assert.Equal(AddressSize.Absolute, model.Symbol("main").AddressSize);
    }

    /// <summary>What has an address needs a segment to have it in, and a constant does not.</summary>
    [Fact]
    public void BytesOutsideEverySegmentAreAnError()
    {
        var program = Analysis.Program(("main.nt65", "SIZE = 1\n.proc main {\n    rts\n}\n.data table: .byte SIZE\n"));

        Assert.Equal(
            [
                "main.nt65:2: `main` is outside every segment: a `.segment NAME` region or block places it",
                "main.nt65:5: `table` is outside every segment: a `.segment NAME` region or block places it",
            ],
            program.Problems());
    }

    /// <summary>
    /// An expression naming addresses takes the widest of them, whatever its own value
    /// would say.
    /// </summary>
    [Fact]
    public void AnAliasTakesTheWidestAddressItNames()
    {
        var model = Analysis.Model("""
            .segment LONG: far

            .segment ZEROPAGE
            .data ptr:    .word
            .segment LONG
            .data away:   .byte 0
            NEXT    = ptr + 1
            MIXED   = ptr + away
            """);

        Assert.Empty(model.Problems());
        Assert.Equal(AddressSize.ZeroPage, model.Symbol("NEXT").AddressSize);
        Assert.Equal(AddressSize.Far, model.Symbol("MIXED").AddressSize);
    }

    /// <summary><c>.addrsize</c> is that size in bytes.</summary>
    [Fact]
    public void AddrsizeIsTheSizeInBytes()
    {
        var model = Analysis.Model("""
            .segment ZEROPAGE
            .data ptr:    .word
            SCREEN  = $0400
            NARROW  = .addrsize(ptr)
            WIDE    = .addrsize(SCREEN)
            """);

        Assert.Empty(model.Problems());
        Assert.Equal(1, model.Symbol("NARROW").Value.Number);
        Assert.Equal(2, model.Symbol("WIDE").Value.Number);
    }

    /// <summary>A block naming a segment nothing declares is an error, and sizes nothing.</summary>
    [Fact]
    public void AnUndeclaredSegmentIsReported()
    {
        var model = Analysis.Model(".segment NOWHERE\n.data lost:   .byte 0\n");

        Assert.Equal(["1: segment \"NOWHERE\" is not declared"], model.Problems());
        Assert.Null(model.Symbol("lost").AddressSize);
    }
}
