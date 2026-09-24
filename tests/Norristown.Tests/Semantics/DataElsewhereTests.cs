namespace Norristown.Tests.Semantics;

/// <summary>
/// Checks data found elsewhere, <c>.data name = address</c>, which takes the element type at its
/// address unless it states one. A stated element type may read the same bytes another way, and
/// a name with no element type is still an address.
/// </summary>
public sealed class DataElsewhereTests
{
    private const string Declarations = """
        .module main
        .struct Pos {
            x: .byte
            y: .word
        }
        .union Either {
            b: .byte
            w: .word
        }
        .segment ZEROPAGE
        .data FAC:    .byte[5]
        .data STRNG1: .word
        .data P:      .type Pos[2]
        .data U:      .type Either
        .data MSG:    .strz "hi"

        """;

    /// <summary>
    /// The name of data gives all of it. An offset that lands on the start of an element gives
    /// one element, an offset into a record gives the field it lands in, and an offset anywhere
    /// else gives no element type.
    /// </summary>
    [Theory]
    [InlineData("FAC", 5L, 5L)]
    [InlineData("FAC + 4", 1L, 1L)]
    [InlineData("FAC[3]", 1L, 1L)]
    [InlineData("P + 3", 3L, 1L)]
    [InlineData("P + 4", 2L, 1L)]
    [InlineData("P::y", 2L, 1L)]
    [InlineData("STRNG1 + 1", null, null)]
    [InlineData("P + 5", null, null)]
    [InlineData("FAC + 5", null, null)]
    [InlineData("U + 1", null, null)]
    [InlineData("MSG + 1", null, null)]
    [InlineData("$0200", null, null)]
    public void TheElementTypeIsTheOneWhereTheAddressLands(string address, long? size, long? count)
    {
        var model = Analysis.Model(Declarations + $".data T = {address}\n");

        Assert.Empty(model.Problems());
        Assert.Equal(size, model.Symbol("T").Size);
        Assert.Equal(count, model.Symbol("T").Count);
    }

    /// <summary>
    /// A name with no element type is an address that instructions can use. Only what needs its
    /// element type is reported, at the use.
    /// </summary>
    [Fact]
    public void ANameWithNoElementTypeIsReportedOnlyWhereItIsMeasured()
    {
        var model = Analysis.Model(Declarations + """
            .data EXT = STRNG1 + 1
            .const SIZE  = .sizeof(EXT)
            .const COUNT = .countof(EXT)
            .data NEXT = EXT[1]
            .segment CODE
            .proc main {
                lda EXT
                rts
            }
            """);

        Assert.Equal([
            "17: `EXT` has no element type; give it one on its declaration, such as `: .byte`",
            "18: `EXT` has no element type; give it one on its declaration, such as `: .byte`",
            "19: `EXT` has no element type; give it one on its declaration, such as `: .byte`",
        ], model.Problems());
    }

    /// <summary>
    /// A stated element type may differ from the one at the address, but taking more bytes than
    /// the data there has left is reported.
    /// </summary>
    [Fact]
    public void AStatedElementTypeMayDifferButNotRunPastTheData()
    {
        var model = Analysis.Model(Declarations + """
            .data SIGN: .byte = STRNG1
            .data HIGH: .byte = STRNG1 + 1
            .data WIDE: .dword = STRNG1
            .data PAST: .byte = STRNG1 + 2
            """);

        Assert.Equal([
            "18: `WIDE` is 4 bytes, but only 2 bytes of `STRNG1` are left at its address",
            "19: `PAST` is 1 byte, but its address is past the end of `STRNG1`",
        ], model.Problems());
        Assert.Equal(Severity.Warning, Assert.Single(model.Diagnostics, d => d.Message.StartsWith("`WIDE`", StringComparison.Ordinal)).Severity);
    }

    /// <summary>
    /// Data found elsewhere may be found at other data found elsewhere, and takes the element type
    /// that one took. Two that are found at each other are a cycle.
    /// </summary>
    [Fact]
    public void DataFoundAtOtherDataFoundElsewhereTakesItsElementType()
    {
        var chained = Analysis.Model(Declarations + ".data LAST = SAME + 4\n.data SAME = FAC\n");
        var ring = Analysis.Model(Declarations + ".data A1 = A2 + 1\n.data A2 = A1 + 1\n");

        Assert.Empty(chained.Problems());
        Assert.Equal(1, chained.Symbol("LAST").Size);
        Assert.Equal(["16: `A1` is defined in terms of itself"], ring.Problems());
    }

    /// <summary>
    /// The fields of data found elsewhere are named while the declarations are read, before any
    /// offset is evaluated. They can be reached through a name whose address is the plain name of
    /// data, or an element of it, and through one that states its type.
    /// </summary>
    [Fact]
    public void FieldsNeedTheTypeKnownWhenTheDeclarationsAreRead()
    {
        var model = Analysis.Model(Declarations + """
            .data SAME = P
            .data SECOND = P[1]
            .data STATED: .type Pos = P + 3
            .data MOVED = P + 3
            .data A1 = SAME::y
            .data A2 = SECOND::y
            .data A3 = STATED::y
            .data A4 = MOVED::y
            """);

        Assert.Equal(
            ["23: `MOVED` states no type, so `::` cannot reach its fields; give it one on its declaration, such as `: .type T`"],
            model.Problems());
    }
}
