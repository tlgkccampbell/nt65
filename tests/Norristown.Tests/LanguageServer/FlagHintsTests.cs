using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests the suggestions that the flag analysis makes possible, and the branch-over-jump
/// suggestion that needs only the layout. Each holds on any 6502 system, because the flags are
/// followed from what the instructions themselves set. No build reports any of them.
/// </summary>
public sealed class FlagHintsTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    private static Range Whole => new(new Position(0, 0), new Position(1000, 0));

    /// <summary>
    /// A <c>.next</c> that says a branch is always taken is not needed where the flags prove it,
    /// and the fix removes it.
    /// </summary>
    [Fact]
    public void ANextTheFlagsProveCanGo()
    {
        const string Body = ".export .proc main {\n    lda #1\n    bne main\n    .next main\n}\n";

        var text = Applied(Body, "next-proved", "Remove it", out var suggestion);

        Assert.Equal("the `.next` is not needed: `bne main` is always taken, because Z is 0 here", suggestion.Message);
        Assert.Equal(".export .proc main {\n    lda #1\n    bne main\n}\n", text);
    }

    /// <summary>A branch that is never taken is usually a mistake, and is pointed out.</summary>
    [Fact]
    public void ABranchNeverTakenIsPointedOut()
    {
        var (analysis, path) = Analyzed(".export .proc main {\n    lda #0\n    bne @x\n    nop\n@x:\n    rts\n}\n");

        var suggestion = Assert.Single(analysis.SuggestionsFor(path), suggestion => suggestion.Id == "branch-never-taken");
        Assert.Equal("`bne @x` is never taken, because Z is 1 here", suggestion.Message);
        Assert.Empty(analysis.Diagnostics);
    }

    /// <summary>
    /// A <c>jmp</c> where a flag is known can be the branch on that flag, which is a byte shorter.
    /// On the 65C02 it can be <c>bra</c>, which needs no flag.
    /// </summary>
    [Theory]
    [InlineData("6502", "`jmp main` can be `bcs main`, which saves a byte, because C is 1 here", "bcs")]
    [InlineData("65C02", "`jmp main` can be `bra main`, which saves a byte", "bra")]
    public void AJumpCanBeABranch(string cpu, string message, string branch)
    {
        const string Body = ".export .proc main {\n    lda $10\n    bne @x\n    sec\n    jmp main\n@x:\n    rts\n}\n";

        var text = Applied(Body, "jump-as-branch", $"Branch with `{branch}`", out var suggestion, cpu);

        Assert.Equal(message, suggestion.Message);
        Assert.Equal(Body.Replace("jmp main", $"{branch} main", StringComparison.Ordinal), text);
    }

    /// <summary>A jump to a target past a branch's reach stays a jump.</summary>
    [Fact]
    public void AJumpPastABranchsReachStays()
    {
        var (analysis, path) = Analyzed(
            ".export .proc main {\n    sec\n    jmp far\n}\n.data pad: .byte[200]\n.proc far {\n    rts\n}\n");

        Assert.DoesNotContain(analysis.SuggestionsFor(path), suggestion => suggestion.Id == "jump-as-branch");
    }

    /// <summary>
    /// A branch over a <c>jmp</c> can be the opposite branch to the jump's target, and the label it
    /// went to goes too where nothing else names it.
    /// </summary>
    [Fact]
    public void ABranchOverAJumpCanBeOneBranch()
    {
        const string Body = ".export .proc main {\n    lda $10\n    bcc @skip\n    jmp done\n@skip:\n    rts\n}\n"
            + ".proc done {\n    rts\n}\n";

        var text = Applied(Body, "branch-over-jump", "Branch with `bcs done`", out var suggestion);

        Assert.Equal("`bcc @skip` over `jmp done` can be the one branch `bcs done`, which saves 3 bytes", suggestion.Message);
        Assert.Equal(".export .proc main {\n    lda $10\n    bcs done\n    rts\n}\n.proc done {\n    rts\n}\n", text);
    }

    /// <summary>A <c>sec</c> where C is already 1 changes nothing, and can go.</summary>
    [Fact]
    public void ACarryAlreadySetCanGo()
    {
        const string Body = ".export .proc main {\n    lda $10\n    bcc @x\n    sec\n    sbc #1\n@x:\n    rts\n}\n";

        var text = Applied(Body, "carry-already-set", "Remove it", out var suggestion);

        Assert.Equal("`sec` changes nothing: C is already 1 here", suggestion.Message);
        Assert.Equal(Body.Replace("    sec\n", "", StringComparison.Ordinal), text);
    }

    /// <summary>
    /// A <c>clc</c> where C is 1 can be folded into the <c>adc</c> after it, whose operand is made
    /// one less, written the way it was.
    /// </summary>
    [Theory]
    [InlineData("40", "39")]
    [InlineData("$2A", "$29")]
    [InlineData("WIDTH", "WIDTH-1")]
    public void ACarryCanBeFoldedIntoTheAdd(string operand, string folded)
    {
        var body = ".export .const WIDTH = 40\n.export .proc main {\n    lda $10\n    bcc @x\n    clc\n    adc #" + operand
            + "\n    sta $10\n@x:\n    rts\n}\n";

        var text = Applied(body, "carry-folded", $"Fold the carry into `#{folded}`", out var suggestion);

        Assert.Equal($"`clc` then `adc #{operand}` can be `adc #{folded}`, because C is 1 here, which saves a byte and 2 cycles", suggestion.Message);
        Assert.Equal(body.Replace("    clc\n", "", StringComparison.Ordinal).Replace("#" + operand, "#" + folded, StringComparison.Ordinal), text);
    }

    /// <summary>
    /// The carry is not folded where the operand one less has the other sign or other decimal
    /// digits, since the flags or the decimal result would then differ.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("$80")]
    [InlineData("$10")]
    public void ACarryIsNotFoldedWhereTheResultWouldDiffer(string operand)
    {
        var (analysis, path) = Analyzed(
            ".export .proc main {\n    lda $10\n    bcc @x\n    clc\n    adc #" + operand + "\n    sta $10\n@x:\n    rts\n}\n");

        Assert.DoesNotContain(analysis.SuggestionsFor(path), suggestion => suggestion.Id == "carry-folded");
    }

    /// <summary>
    /// Returns the text after the fix titled <paramref name="title"/> is applied to the one
    /// suggestion named <paramref name="id"/>.
    /// </summary>
    private static string Applied(string body, string id, string title, out Diagnostic suggestion, string cpu = "6502")
    {
        var (analysis, path) = Analyzed(body, cpu);
        suggestion = Assert.Single(analysis.SuggestionsFor(path), suggestion => suggestion.Id == id);
        Assert.Empty(analysis.Diagnostics);
        var model = analysis.ModelFor(path)!;
        var line = suggestion.Span.LineIndex;
        var action = Assert.Single(
            CodeActions.In(analysis, model, new Range(new Position(line, 0), new Position(line, 0))),
            action => action.Title == title);
        var header = Header(cpu);
        var applied = Editing.Apply(header + body, action.Edit!.Changes[Uri]);
        Assert.StartsWith(header, applied, StringComparison.Ordinal);
        return applied[header.Length..];
    }

    private static (ProgramAnalysis Analysis, string Path) Analyzed(string body, string cpu = "6502")
    {
        var workspace = new Workspace();
        var document = workspace.Open(new TextDocumentItem(Uri, "nt65", 1, Header(cpu) + body));
        var analysis = workspace.AnalysisForAsync(document.Tree.Path, TestTimeout.Token()).GetAwaiter().GetResult();
        return (analysis, document.Tree.Path);
    }

    private static string Header(string cpu) => $".module main\n.cpu {cpu}\n.segment CODE\n";
}
