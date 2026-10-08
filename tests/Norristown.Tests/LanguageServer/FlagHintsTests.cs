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
    /// A compare with zero straight after an instruction that set N and Z from the same register
    /// changes only C. It can go where nothing reads C before it changes, or where C is already 1.
    /// </summary>
    [Theory]
    [InlineData("    lda $10\n    cmp #0\n    beq @x\n    sta $11\n@x:\n    sec\n    rts\n",
        "`cmp #0` changes nothing that is read: N and Z already reflect A here, and nothing reads the C it sets")]
    [InlineData("    sec\n    lda $10\n    cmp #0\n    beq @x\n    sta $11\n@x:\n    rts\n",
        "`cmp #0` changes nothing that is read: N and Z already reflect A here, and C is already 1")]
    [InlineData("    ldx $10\n@loop:\n    dex\n    cpx #0\n    bne @loop\n    clc\n    rts\n",
        "`cpx #0` changes nothing that is read: N and Z already reflect X here, and nothing reads the C it sets")]
    [InlineData("    lda $10\n    tay\n    cpy #0\n    beq @x\n    sta $11\n@x:\n    clc\n    rts\n",
        "`cpy #0` changes nothing that is read: N and Z already reflect Y here, and nothing reads the C it sets")]
    public void ACompareWithZeroCanGo(string lines, string message)
    {
        var body = ".export .proc main {\n" + lines + "}\n";

        var text = Applied(body, "zero-compare", "Remove it", out var suggestion);

        Assert.Equal(message, suggestion.Message);
        Assert.DoesNotContain("#0", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The compare stays where something reads the C it sets, where N and Z come from another
    /// register, and where a routine that promises no flags still returns with the C it sets.
    /// </summary>
    [Theory]
    [InlineData("    lda $10\n    cmp #0\n    adc #1\n    sta $11\n    rts\n")]
    [InlineData("    lda $10\n    ldx $11\n    cmp #0\n    beq @x\n    sta $11\n@x:\n    sec\n    rts\n")]
    [InlineData("    lda $10\n    cmp #0\n    beq @x\n    sta $11\n@x:\n    rts\n")]
    public void ACompareWhoseCarryIsReadStays(string lines)
    {
        var (analysis, path) = Analyzed(".export .proc main {\n" + lines + "}\n");

        Assert.DoesNotContain(analysis.SuggestionsFor(path), suggestion => suggestion.Id == "zero-compare");
    }

    /// <summary>
    /// A routine that declares the flags it returns with promises nothing about the rest, so a
    /// return reads only those.
    /// </summary>
    [Fact]
    public void AReturnReadsOnlyTheFlagsItPromises()
    {
        var (analysis, path) = Analyzed(".export .proc main: -> z {\n    lda $10\n    cmp #0\n    rts\n}\n");

        Assert.Contains(analysis.SuggestionsFor(path), suggestion => suggestion.Id == "zero-compare");
    }

    /// <summary>
    /// A load of a constant a register already holds can go, where the flags already say what it
    /// would set them to, or nothing reads them. A branch that found Z set after a count shows
    /// the count is 0.
    /// </summary>
    [Theory]
    [InlineData("    ldx #0\n    stx $10\n    ldx #0\n    stx $11\n    rts\n",
        "`ldx #0` changes nothing that is read: X already holds $00 here, and N and Z already say what it would")]
    [InlineData("    ldx #8\n@loop:\n    dex\n    bne @loop\n    ldx #0\n    stx $10\n    rts\n",
        "`ldx #0` changes nothing that is read: X already holds $00 here, and N and Z already say what it would")]
    [InlineData("    lda #$40\n    asl a\n    sta $10\n    ldx $11\n    lda #$80\n    sta $12,x\n    lda #1\n    rts\n",
        "`lda #$80` changes nothing that is read: A already holds $80 here, and nothing reads the N and Z it sets")]
    public void ALoadOfAHeldConstantCanGo(string lines, string message)
    {
        var body = ".export .proc main {\n" + lines + "}\n";

        var text = Applied(body, "load-already-held", "Remove it", out var suggestion);

        Assert.Equal(message, suggestion.Message);
        var expected = body.Split('\n').ToList();
        expected.RemoveAt(suggestion.Span.LineIndex - 3);
        Assert.Equal(string.Join('\n', expected), text);
    }

    /// <summary>
    /// A load of a constant another register holds can be a transfer, and one of a constant one
    /// more or less than the register holds an increment or a decrement. Each is a byte shorter.
    /// </summary>
    [Theory]
    [InlineData("6502", "    lda #0\n    sta $10\n    ldx #0\n    stx $11\n    rts\n", "ldx #0", "tax", "A holds $00 here")]
    [InlineData("6502", "    ldy #7\n    sty $10\n    lda #7\n    sta $11\n    rts\n", "lda #7", "tya", "Y holds $07 here")]
    [InlineData("6502", "    ldx #4\n    stx $10\n    ldx #5\n    stx $11\n    rts\n", "ldx #5", "inx", "X holds $04 here")]
    [InlineData("6502", "    ldy #0\n    sty $10\n    ldy #$FF\n    sty $11\n    rts\n", "ldy #$FF", "dey", "Y holds $00 here")]
    [InlineData("65C02", "    lda #9\n    sta $10\n    lda #10\n    sta $11\n    rts\n", "lda #10", "inc a", "A holds $09 here")]
    public void ALoadCanComeFromARegister(string cpu, string lines, string load, string shorter, string why)
    {
        var body = ".export .proc main {\n" + lines + "}\n";

        var text = Applied(body, "load-from-register", $"Change it to `{shorter}`", out var suggestion, cpu);

        Assert.Equal($"`{load}` can be `{shorter}`, because {why}, which saves a byte", suggestion.Message);
        Assert.Equal(body.Replace(load, shorter, StringComparison.Ordinal), text);
    }

    /// <summary>
    /// A load stays where a store may rewrite it, where the register's value came through a call
    /// that does not promise to keep it, and where it comes from memory.
    /// </summary>
    [Theory]
    [InlineData("    ldx #0\n    stx $10\n@load:\n    ldx #0\n    stx $11\n    inc @load+1\n    .patch @load\n    rts\n")]
    [InlineData("    ldx #0\n    jsr other\n    ldx #0\n    stx $10\n    lda #1\n    rts\n")]
    [InlineData("    ldx $12\n    stx $10\n    ldx $12\n    stx $11\n    rts\n")]
    public void ALoadTheAnalysisCannotVouchForStays(string lines)
    {
        var (analysis, path) = Analyzed(".export .proc main {\n" + lines + "}\n.proc other {\n    rts\n}\n");

        Assert.DoesNotContain(analysis.SuggestionsFor(path), suggestion => suggestion.Id is "load-already-held" or "load-from-register");
    }

    /// <summary>A call to a routine that promises to keep a register keeps its constant.</summary>
    [Fact]
    public void ACallThatPromisesToKeepARegisterKeepsItsConstant()
    {
        var (analysis, path) = Analyzed(
            ".export .proc main {\n    ldx #0\n    jsr other\n    ldx #0\n    stx $10\n    lda #1\n    rts\n}\n.proc other: keeps x {\n    rts\n}\n");

        Assert.Contains(analysis.SuggestionsFor(path), suggestion => suggestion.Id == "load-already-held");
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
