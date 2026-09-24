namespace Norristown.Tests.Oracle;

/// <summary>Tests the oracle logic that needs no ca65, so that it runs in the fast suite.</summary>
public sealed class OracleUnitTests
{
    private const string Pin = "e11fb5c39371046ebe25485f984f644c5a0d65d3";

    [Fact]
    public void AcceptsThePinnedCommit() => Ca65Oracle.CheckVersion("ca65 V2.19 - Git e11fb5c\n", Pin);

    [Theory]
    [InlineData("ca65 V2.19 - Git 547d923")]
    [InlineData("ca65 V2.19")]
    [InlineData("")]
    public void RejectsAnythingElse(string version) =>
        Assert.Throws<InvalidOperationException>(() => Ca65Oracle.CheckVersion(version, Pin));

    [Fact]
    public void ListingCountsBytesAcrossContinuationRows()
    {
        const string listing = """
            ca65 V2.19 - Git e11fb5c
            Main file   : oracle-wrapper.s
            Current file: oracle-wrapper.s

            000000r 1               .listbytes unlimited
            000000r 1               .include "a.s"
            000000r 2               foo:
            000000r 2  01 02 03 04      .byte 1,2,3,4,5,6
            000004r 2  05 06
            000006r 2
            000006r 2  A9 01            lda #1
            000008r 2  xx xx xx xx      .res 4
            00000Cr 2  4C rr rr         jmp foo
            00000Fr 2
            00000Fr 1
            """;
        Assert.Equal([0, 6, 0, 2, 4, 3], Ca65Oracle.ParseListing(listing, sourceLines: 6));
    }
}
