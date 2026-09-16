using Norristown.Semantics;

namespace Norristown.Tests.Semantics;

/// <summary>Where a symbol's address size comes from (§7.2): its segment, or its value.</summary>
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
            .segment "LONG": far

            .zeropage {
            ptr:    .res 2
            }
            .code {
            entry:  .byte 0
            }
            .segment "LONG" {
            away:   .byte 0
            }
            """);

        Assert.Empty(model.Problems());
        Assert.Equal(AddressSize.ZeroPage, model.Symbol("ptr").AddressSize);
        Assert.Equal(AddressSize.Absolute, model.Symbol("entry").AddressSize);
        Assert.Equal(AddressSize.Far, model.Symbol("away").AddressSize);
    }

    /// <summary>Items outside any segment block go to CODE (§5.2).</summary>
    [Fact]
    public void TheDefaultSegmentIsCode()
    {
        var model = Analysis.Model("start:\n.proc main {\nrts\n}\n");

        Assert.Equal("CODE", model.Symbol("start").Segment);
        Assert.Equal(AddressSize.Absolute, model.Symbol("main").AddressSize);
    }

    /// <summary>
    /// An expression naming addresses takes the widest of them, whatever its own value
    /// would say (§7.2).
    /// </summary>
    [Fact]
    public void AnAliasTakesTheWidestAddressItNames()
    {
        var model = Analysis.Model("""
            .segment "LONG": far

            .zeropage {
            ptr:    .res 2
            }
            .segment "LONG" {
            away:   .byte 0
            }
            NEXT    = ptr + 1
            MIXED   = ptr + away
            """);

        Assert.Empty(model.Problems());
        Assert.Equal(AddressSize.ZeroPage, model.Symbol("NEXT").AddressSize);
        Assert.Equal(AddressSize.Far, model.Symbol("MIXED").AddressSize);
    }

    /// <summary><c>.addrsize</c> is that size in bytes (§9).</summary>
    [Fact]
    public void AddrsizeIsTheSizeInBytes()
    {
        var model = Analysis.Model("""
            .zeropage {
            ptr:    .res 2
            }
            SCREEN  = $0400
            NARROW  = .addrsize(ptr)
            WIDE    = .addrsize(SCREEN)
            """);

        Assert.Empty(model.Problems());
        Assert.Equal(1, model.Symbol("NARROW").Value.Number);
        Assert.Equal(2, model.Symbol("WIDE").Value.Number);
    }

    /// <summary>A block naming a segment nothing declares is an error, and sizes nothing (§5.2).</summary>
    [Fact]
    public void AnUndeclaredSegmentIsReported()
    {
        var model = Analysis.Model(".segment \"NOWHERE\" {\nlost:   .byte 0\n}\n");

        Assert.Equal(["1: segment \"NOWHERE\" is not declared"], model.Problems());
        Assert.Null(model.Symbol("lost").AddressSize);
    }
}
