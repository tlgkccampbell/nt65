using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests the <c>nt65/margin</c> request, which lists the brackets of every routine's loops and the
/// arrows of the routine at the caret, with the column each is drawn in.
/// </summary>
public sealed class MarginRequestsTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    /// <summary>
    /// An arrow inside another goes nearer the code, and an arrow that overlaps no other shares the
    /// innermost column. The routine spans its opening line to its closing line.
    /// </summary>
    [Fact]
    public async Task ShorterArrowsGoNearerTheCode()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In("""
            .module main
            .segment CODE
            .export .proc main {
                n|op
                bcc @far
                bne @near
                nop
            @near:
                beq @done
                nop
            @far:
                nop
            @done:
                rts
            }
            """);
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, text));

        var routine = Assert.Single((await MarginAsync(client, position, arrows: true, timeout))!.Routines);
        Assert.Equal((2, 14, 2), (routine.First, routine.Last, routine.Columns));
        Assert.Empty(routine.Brackets);
        Assert.Equal(
            [(4, 10, 1), (5, 7, 0), (8, 12, 0)],
            routine.Arrows.Select(arrow => (arrow.From, arrow.To, arrow.Column)));
    }

    /// <summary>
    /// The arrows take at most four columns, and one that finds no free column has none.
    /// </summary>
    [Fact]
    public async Task ArrowsBeyondTheLastColumnHaveNone()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In("""
            .module main
            .segment CODE
            .export .proc main {
                n|op
                bcc @a
                bne @b
                bvc @c
                bmi @d
                bcs @e
            @e:
            @d:
            @c:
            @b:
            @a:
                rts
            }
            """);
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, text));

        var routine = Assert.Single((await MarginAsync(client, position, arrows: true, timeout))!.Routines);
        Assert.Equal(4, routine.Columns);
        Assert.Equal(
            [(4, 13, null), (5, 12, 3), (6, 11, 2), (7, 10, 1), (8, 9, 0)],
            routine.Arrows.Select(arrow => (arrow.From, arrow.To, arrow.Column)));
    }

    /// <summary>
    /// Brackets take the columns furthest from the code, with the outer loop furthest. A bracket
    /// that starts at its header stands for the branch back, which is then not an arrow, and an
    /// exit branch is an arrow nearer the code that crosses both brackets.
    /// </summary>
    [Fact]
    public async Task BracketsGoOutsideTheArrows()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In("""
            .module main
            .segment CODE
            .export .proc main {
                ldy #8
            @row:
                ldx #16
            @col:
                lda $10,x
                b|eq @done
                dex
                bne @col
                dey
                bne @row
            @done:
                rts
            }
            """);
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, text));

        var routine = Assert.Single((await MarginAsync(client, position, arrows: true, timeout))!.Routines);
        Assert.Equal(3, routine.Columns);
        Assert.Equal(
            [(4, 12, true, 2), (6, 10, true, 1)],
            routine.Brackets.Select(bracket => (bracket.Top, bracket.Bottom, bracket.Head, bracket.Column)));
        Assert.Equal([(8, 13, 0)], routine.Arrows.Select(arrow => (arrow.From, arrow.To, arrow.Column)));

        var quiet = Assert.Single((await MarginAsync(client, position, arrows: false, timeout))!.Routines);
        Assert.Equal(2, quiet.Columns);
        Assert.Equal([1, 0], quiet.Brackets.Select(bracket => bracket.Column));
        Assert.Empty(quiet.Arrows);
    }

    /// <summary>
    /// Every routine with loops has its brackets and counts, and only the caret's routine has
    /// arrows. Each latch above the last joins the bracket as a tee.
    /// </summary>
    [Fact]
    public async Task EveryRoutineHasItsBrackets()
    {
        var timeout = TestTimeout.Token();
        var (text, position) = Caret.In("""
            .module main
            .segment CODE
            .export .proc main {
                ldx #16
            @loop:
                sta $0400,x
                dex
                bne @loop
                b|cc @skip
                nop
            @skip:
                rts
            }
            .export .proc other {
            @top:
                lda $10
                bcc @top
                dex
                bne @top
                rts
            }
            """);
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, text));

        var routines = (await MarginAsync(client, position, arrows: true, timeout))!.Routines;
        Assert.Equal([2, 13], routines.Select(routine => routine.First));

        var main = Assert.Single(routines[0].Brackets);
        Assert.Equal((4, 7, true, 16), (main.Top, main.Bottom, main.Head, main.Trips));
        Assert.Equal([(8, 10)], routines[0].Arrows.Select(arrow => (arrow.From, arrow.To)));

        var other = Assert.Single(routines[1].Brackets);
        Assert.Equal((14, 18, null), (other.Top, other.Bottom, other.Trips));
        Assert.Equal([16], other.Tees);
        Assert.Empty(routines[1].Arrows);
    }

    private static Task<MarginResult?> MarginAsync(TestClient client, Position position, bool arrows, CancellationToken timeout) =>
        client.RequestAsync<MarginResult?>("nt65/margin",
            new MarginParams(new TextDocumentIdentifier(Uri), position, arrows), timeout);
}
