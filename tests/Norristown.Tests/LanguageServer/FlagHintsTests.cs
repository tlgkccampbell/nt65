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

    /// <summary>The words a jump-as-branch message ends with where a taken branch may cross a page.</summary>
    private const string Page = "; a taken branch may cost one more cycle across a page";

    /// <summary>
    /// The body of a routine that sets the carry and then dispatches through an RTS dispatch
    /// table, whose two entries are <c>add</c> and <c>sub</c>. The table itself is left to each test.
    /// </summary>
    private const string Dispatch = ".export .proc main {\n    sec\n    lda table+1,x\n    pha\n    lda table,x\n    pha\n"
        + "    rts\n    .next table\nadd:\n    sec\n    sbc #1\n    sta $10\n    rts\nsub:\n    lda $11\n    sta $10\n    rts\n}\n";

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
    /// A hint may go by what a called routine's body leaves in a flag, where that routine declares
    /// no flags. The message and the fix's title then say that the routine does not promise it.
    /// </summary>
    [Fact]
    public void AHintFromAnUnpromisedBodySaysSo()
    {
        const string Body = ".export .proc main {\n    jsr g\n    bne @x\n    nop\n@x:\n    rts\n}\n"
            + ".proc g {\n    lda #0\n    rts\n}\n";

        var text = Applied(Body, "branch-never-taken", "Remove it, though `g` does not promise Z", out var suggestion);

        Assert.Equal("`bne @x` is never taken, because Z is 1 here; `g` leaves Z this way but does not promise it", suggestion.Message);
        Assert.Equal(Body.Replace("    bne @x\n", "", StringComparison.Ordinal), text);
    }

    /// <summary>
    /// A carry that a called routine's body sets without promising it makes a <c>sec</c> after the
    /// call unneeded, and the hint says that the routine does not promise it.
    /// </summary>
    [Fact]
    public void ACarryFromAnUnpromisedBodyIsQualified()
    {
        const string Body = ".export .proc main {\n    jsr g\n    sec\n    sbc #1\n    sta $10\n    rts\n}\n"
            + ".proc g {\n    sec\n    rts\n}\n";

        var text = Applied(Body, "carry-already-set", "Remove it, though `g` does not promise C", out var suggestion);

        Assert.Equal("`sec` changes nothing: C is already 1 here; `g` leaves C this way but does not promise it", suggestion.Message);
        Assert.Equal(Body.Replace("    jsr g\n    sec\n", "    jsr g\n", StringComparison.Ordinal), text);
    }

    /// <summary>
    /// A load whose flags a called routine's body leaves as the load would set them can go, and the
    /// hint names both flags the routine does not promise.
    /// </summary>
    [Fact]
    public void ALoadFromAnUnpromisedBodyNamesBothFlags()
    {
        const string Body = ".export .proc main {\n    ldx #0\n    jsr g\n    ldx #0\n    stx $10\n    rts\n}\n"
            + ".proc g: keeps x {\n    lda #0\n    rts\n}\n";

        var (analysis, path) = Analyzed(Body);

        var suggestion = Assert.Single(analysis.SuggestionsFor(path), suggestion => suggestion.Id == "load-already-held");
        Assert.EndsWith("; `g` leaves N and Z this way but does not promise them", suggestion.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A hint that relies on another file's routine body goes when an edit to that body changes
    /// the flag, because the hint's file is analyzed again.
    /// </summary>
    [Fact]
    public void AHintFromAnotherFilesBodyFollowsAnEditToIt()
    {
        const string OtherUri = "file:///c:/work/other.nt65";
        const string Other = ".module other\n.cpu 6502\n.segment CODE\n.export .proc g {\n    sec\n    rts\n}\n";
        const string Main = ".module main\n.cpu 6502\n.use other::g\n.segment CODE\n"
            + ".export .proc main {\n    jsr g\n    sec\n    sbc #1\n    sta $10\n    rts\n}\n";
        var workspace = new Workspace();
        workspace.Open(new TextDocumentItem(OtherUri, "nt65", 1, Other));
        var main = workspace.Open(new TextDocumentItem(Uri, "nt65", 1, Main));
        var path = main.Tree.Path;
        Assert.Contains(Suggested(), found => found.Id == "carry-already-set");

        // `sec` becomes `clc`, so g no longer leaves C set.
        workspace.Change(new VersionedTextDocumentIdentifier(OtherUri, 2), [new TextDocumentContentChangeEvent(
            new Range(new Position(4, 4), new Position(4, 7)), "clc")]);
        Assert.DoesNotContain(Suggested(), found => found.Id == "carry-already-set");

        IReadOnlyList<Diagnostic> Suggested()
        {
            var analysis = workspace.AnalysisForAsync(path, TestTimeout.Token(), settled: true).GetAwaiter().GetResult();
            Assert.Empty(analysis.Diagnostics);
            return analysis.SuggestionsFor(path);
        }
    }

    /// <summary>A flag a called routine promises after <c>-&gt;</c> needs no qualification.</summary>
    [Fact]
    public void AHintFromAPromisedFlagIsPlain()
    {
        const string Body = ".export .proc main {\n    jsr g\n    sec\n    sbc #1\n    sta $10\n    rts\n}\n"
            + ".proc g: -> c = 1 {\n    sec\n    rts\n}\n";

        Applied(Body, "carry-already-set", "Remove it", out var suggestion);

        Assert.Equal("`sec` changes nothing: C is already 1 here", suggestion.Message);
    }

    /// <summary>
    /// An entry of an RTS dispatch table that only this routine's <c>.next</c> names is reached
    /// only by the <c>rts</c> the <c>.next</c> is under. The carry the dispatch sets flows into
    /// the entry as it would along a branch, so a <c>sec</c> there changes nothing.
    /// </summary>
    [Fact]
    public void ACarryFlowsIntoAnEntryOfADispatchTable()
    {
        const string Body = Dispatch + ".segment RODATA\n.data table: .addr main::add - 1, main::sub - 1\n";

        var text = Applied(Body, "carry-already-set", "Remove it", out var suggestion);

        Assert.Equal("`sec` changes nothing: C is already 1 here", suggestion.Message);
        Assert.Equal(Body.Replace("add:\n    sec\n", "add:\n", StringComparison.Ordinal), text);
    }

    /// <summary>
    /// Where something other than this routine's <c>.next</c> may hand control to a table's
    /// labels, nothing says which jump reaches them, so each is entered with every flag unknown.
    /// An exported table may be jumped through from another module, and data that names a label
    /// with no <c>.next</c> naming the data says nothing about who jumps there.
    /// </summary>
    [Theory]
    [InlineData(".segment RODATA\n.export .data table: .addr main::add - 1, main::sub - 1\n")]
    [InlineData(".segment RODATA\n.data table: .addr main::add - 1, main::sub - 1\n.data other: .addr main::add - 1\n")]
    public void ACarryDoesNotFlowIntoATableEntryOthersMayReach(string tables)
    {
        var (analysis, path) = Analyzed(Dispatch + tables);

        Assert.Empty(analysis.Diagnostics);
        Assert.DoesNotContain(analysis.SuggestionsFor(path), found => found.Id == "carry-already-set");
    }

    /// <summary>
    /// A <c>jmp</c> where a flag is known can be the branch on that flag, which is a byte shorter.
    /// On the 65C02 it can be <c>bra</c>, which needs no flag. Either way the message says that a
    /// taken branch may cost a cycle more than the jump across a page.
    /// </summary>
    [Theory]
    [InlineData("6502", "`jmp main` can be `bcs main`, which saves a byte, because C is 1 here" + Page, "bcs")]
    [InlineData("65C02", "`jmp main` can be `bra main`, which saves a byte" + Page, "bra")]
    public void AJumpCanBeABranch(string cpu, string message, string branch)
    {
        const string Body = ".export .proc main {\n    lda $10\n    bne @x\n    sec\n    jmp main\n@x:\n    rts\n}\n";

        var text = Applied(Body, "jump-as-branch", $"Branch with `{branch}`", out var suggestion, cpu);

        Assert.Equal(message, suggestion.Message);
        Assert.Equal(Body.Replace("jmp main", $"{branch} main", StringComparison.Ordinal), text);
    }

    /// <summary>
    /// On the 65816 a taken branch pays for a page only in emulation mode, so in native mode the
    /// message leaves the page out. Where the mode is not known it may be emulation mode.
    /// </summary>
    [Theory]
    [InlineData(": native", "")]
    [InlineData(": emu", Page)]
    [InlineData(": e*", Page)]
    public void AJumpAsABranchSaysThePageOnlyWhereItCosts(string signature, string page)
    {
        var (analysis, path) = Analyzed(
            $".export .proc main{signature} {{\n    lda $10\n    bne @x\n    jmp main\n@x:\n    rts\n}}\n", "65816");

        Assert.Empty(analysis.Diagnostics);
        var suggestion = Assert.Single(analysis.SuggestionsFor(path), suggestion => suggestion.Id == "jump-as-branch");
        Assert.Equal("`jmp main` can be `bra main`, which saves a byte" + page, suggestion.Message);
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
    /// A routine with a line that does not parse gets no suggestions, whether the broken line is
    /// the one suggested or another line of the routine. Another routine of the file still gets
    /// its own.
    /// </summary>
    [Theory]
    [InlineData("    jmp main::\n", "")]
    [InlineData("    jmp main\n", "    lda #\n")]
    public void ARoutineWithASyntaxErrorGetsNoSuggestions(string jump, string broken)
    {
        var (analysis, path) = Analyzed(
            ".export .proc main {\n    lda $10\n    bne @x\n" + broken + "    sec\n" + jump + "@x:\n    rts\n}\n"
            + ".export .proc other {\n    lda $10\n    bne @x\n    jmp other\n@x:\n    rts\n}\n",
            "65C02");

        Assert.NotEmpty(analysis.Diagnostics);
        var suggestion = Assert.Single(analysis.SuggestionsFor(path), suggestion => suggestion.Id == "jump-as-branch");
        Assert.Equal("`jmp other` can be `bra other`, which saves a byte" + Page, suggestion.Message);
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
    /// <c>tdc</c> and <c>tsc</c> set N and Z from all 16 bits they copy, whatever A's width. With
    /// an 8-bit A a compare with zero tests only the low byte, so it stays.
    /// </summary>
    [Theory]
    [InlineData("tdc")]
    [InlineData("tsc")]
    public void ACompareAfterASixteenBitTransferStays(string transfer)
    {
        var (analysis, path) = Analyzed(
            $".export .proc main: a8, i16, native {{\n    {transfer}\n    cmp #0\n    beq @x\n    sta $11\n@x:\n    sec\n    rts\n}}\n",
            "65816");

        Assert.Empty(analysis.Diagnostics);
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
    /// that does not promise to keep it or a software interrupt, and where it comes from memory.
    /// </summary>
    [Theory]
    [InlineData("    ldx #0\n    brk #0\n    ldx #0\n    stx $10\n    rts\n")]
    [InlineData("    ldx #0\n    stx $10\n@load:\n    ldx #0\n    stx $11\n    inc @load+1\n    .patch @load\n    rts\n")]
    [InlineData("    ldx #0\n    jsr other\n    ldx #0\n    stx $10\n    lda #1\n    rts\n")]
    [InlineData("    ldx $12\n    stx $10\n    ldx $12\n    stx $11\n    rts\n")]
    public void ALoadTheAnalysisCannotVouchForStays(string lines)
    {
        var (analysis, path) = Analyzed(".export .proc main {\n" + lines + "}\n.proc other {\n    rts\n}\n");

        Assert.DoesNotContain(analysis.SuggestionsFor(path), suggestion => suggestion.Id is "load-already-held" or "load-from-register");
    }

    /// <summary>
    /// An instruction whose bytes code reads, as <c>lda @op+1</c> reads the operand of the
    /// instruction at <c>@op</c>, keeps its bytes, so no suggestion changes it.
    /// </summary>
    [Theory]
    [InlineData("    lda #5\n    sta $10\n@op:\n    ldx #5\n    stx $11\n    lda @op+1\n    sta $12\n    rts\n", "load-from-register")]
    [InlineData("    ldx #0\n    stx $10\n@op:\n    ldx #0\n    stx $11\n    lda @op\n    sta $12\n    rts\n", "load-already-held")]
    [InlineData("    lda $10\n    bcc @x\n@op:\n    sec\n    sbc #1\n    sta $11\n@x:\n    lda @op\n    sta $12\n    rts\n", "carry-already-set")]
    [InlineData("    lda $10\n    cmp #0\n    beq @x\n    sta $11\n@x:\n    sec\n    lda @x-5\n    sta $12\n    rts\n", "zero-compare")]
    [InlineData("    lda $10\n    bne @x\n    sec\n@op:\n    jmp main\n@x:\n    lda @op+2\n    sta $12\n    rts\n", "jump-as-branch")]
    [InlineData("    lda $10\n    bcc @skip\n    jmp done\n@skip:\n    lda @skip-2\n    sta $12\n    rts\n", "branch-over-jump")]
    [InlineData("    lda @op\n    sta $12\n    jsr done\n@op:\n    rts\n", "tail-call")]
    public void AnInstructionReadAsDataKeepsItsBytes(string lines, string id)
    {
        var (analysis, path) = Analyzed(".export .proc main {\n" + lines + "}\n.export .proc done {\n    rts\n}\n");

        Assert.Empty(analysis.Diagnostics);
        Assert.DoesNotContain(analysis.SuggestionsFor(path), suggestion => suggestion.Id == id);

        // Read from elsewhere, the same instruction is suggested.
        var elsewhere = lines.Replace("lda @op+2", "lda $13", StringComparison.Ordinal)
            .Replace("lda @op+1", "lda $13", StringComparison.Ordinal).Replace("lda @op", "lda $13", StringComparison.Ordinal)
            .Replace("lda @x-5", "lda $13", StringComparison.Ordinal).Replace("lda @skip-2", "lda $13", StringComparison.Ordinal);
        (analysis, path) = Analyzed(".export .proc main {\n" + elsewhere + "}\n.export .proc done {\n    rts\n}\n");
        Assert.Contains(analysis.SuggestionsFor(path), suggestion => suggestion.Id == id);
    }

    /// <summary>
    /// A jump table that a <c>.next</c> hands control through holds where control goes, not bytes
    /// code reads, so the first instruction of each entry keeps its hints. So does an RTS dispatch
    /// table, whose addresses are one less than its labels. Here the branch over a <c>jmp</c>
    /// that starts <c>@w1</c> keeps its hint.
    /// </summary>
    [Theory]
    [InlineData("    jmp (table)\n    .next table\n.data table: .addr @w1, @w2\n")]
    [InlineData("    ldx $10\n    lda table+1,x\n    pha\n    lda table,x\n    pha\n    rts\n    .next table\n.data table: .addr @w1-1, @w2-1\n")]
    public void AnEntryOfATableControlGoesThroughKeepsItsHints(string dispatch)
    {
        var (analysis, path) = Analyzed(".export .proc main {\n" + dispatch
            + "@w1:\n    bcc @w2\n    jmp done\n@w2:\n    rts\n}\n.export .proc done {\n    rts\n}\n");

        Assert.Empty(analysis.Diagnostics);
        Assert.Contains(analysis.SuggestionsFor(path), suggestion => suggestion.Id == "branch-over-jump");
    }

    /// <summary>
    /// A routine named by an <c>.addr</c>, as in a vector table, is where control goes, so its first
    /// instruction keeps its hints. A value that names a byte of it some other way, or a byte past
    /// its start, may be read as data, and the instruction keeps its bytes.
    /// </summary>
    [Theory]
    [InlineData(".addr main", true)]
    [InlineData(".faraddr main", true)]
    [InlineData(".addr main+1", false)]
    [InlineData(".byte <main, >main", false)]
    [InlineData(".word main", false)]
    [InlineData(".word main+1", false)]
    public void ARoutineAVectorNamesKeepsItsHints(string vector, bool suggested)
    {
        var (analysis, path) = Analyzed(
            ".export .proc main {\n    jsr done\n    rts\n}\n.export .proc done {\n    rts\n}\n.data vectors: " + vector + "\n");

        Assert.Empty(analysis.Diagnostics);
        Assert.Equal(suggested, analysis.SuggestionsFor(path).Any(suggestion => suggestion.Id == "tail-call"));
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
    /// A call that keeps a register keeps its constant only where it returns the register at the
    /// width it was entered with. A register made wider holds a high byte the load never set.
    /// </summary>
    [Theory]
    [InlineData("a8, i8, keeps x -> a8, i16", "    rep #$10\n", "i16", false)]
    [InlineData("a8, i8, keeps x", "", "i8", true)]
    public void ACallKeepsAConstantOnlyAtItsWidth(string signature, string body, string exit, bool kept)
    {
        var (analysis, path) = Analyzed(
            $".export .proc main: a8, i8, native -> a8, {exit} {{\n    ldx #0\n    jsr other\n    ldx #0\n    stx $10\n    lda #1\n    rts\n}}\n"
            + $".proc other: {signature} {{\n{body}    rts\n}}\n",
            "65816");

        Assert.Empty(analysis.Diagnostics);
        Assert.Equal(kept, analysis.SuggestionsFor(path).Any(suggestion => suggestion.Id == "load-already-held"));
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
