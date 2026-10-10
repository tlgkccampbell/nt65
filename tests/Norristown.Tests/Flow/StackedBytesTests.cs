using Norristown.Project;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.Flow;

/// <summary>
/// Tests what a store into the bytes on the stack does to what a pull or an <c>rti</c> restores.
/// nt65 does not follow which stacked byte such a store changes, so what is restored after it is
/// not known, and a <c>keeps</c> that relies on it is broken.
/// </summary>
public sealed class StackedBytesTests
{
    /// <summary>
    /// A handler that edits the flags the interrupt pushed, through a store relative to S, to a
    /// fixed address on the stack's page, or indexed by a register that holds S, cannot keep C.
    /// </summary>
    [Theory]
    [InlineData("6502", "    tsx\n    lda $0101,x\n    ora #1\n    sta $0101,x\n")]
    [InlineData("6502", "    tsx\n    txa\n    tay\n    lda #1\n    sta $0101,y\n")]
    [InlineData("6502", "    lda #1\n    sta $01FD\n")]
    [InlineData("65816", "    lda 1,s\n    ora #1\n    sta 1,s\n")]
    public void AStoreIntoTheStackedFlagsLosesThem(string cpu, string body)
    {
        var diagnostic = Assert.Single(Diagnostics(cpu, Handler(cpu, body)));

        Assert.Equal("keeps-broken", diagnostic.Id);
        Assert.Contains("writes into the bytes on the stack", diagnostic.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A store indexed by a register that no longer holds S, one to memory outside the stack's
    /// page, and one through a pointer on the stack leave the stacked flags alone.
    /// </summary>
    [Theory]
    [InlineData("6502", "    tsx\n    ldx $10\n    lda #1\n    sta $0101,x\n")]
    [InlineData("6502", "    tsx\n    lda #1\n    sta $0201,x\n    sta $10,x\n")]
    [InlineData("6502", "    lda #1\n    sta $0200\n")]
    [InlineData("65816", "    lda #1\n    ldy #0\n    sta (1,s),y\n")]
    public void AStoreElsewhereKeepsTheStackedFlags(string cpu, string body)
    {
        Assert.Empty(Diagnostics(cpu, Handler(cpu, body)));
    }

    /// <summary>
    /// A routine that saves A on the stack and then stores into the stack cannot know that the
    /// pull gives A back.
    /// </summary>
    [Fact]
    public void AStoreIntoTheStackLosesASavedRegister()
    {
        const string Text = ".export .proc main: keeps a {\n    pha\n    tsx\n    lda #0\n    sta $0101,x\n    pla\n    rts\n}\n";

        var diagnostic = Assert.Single(Diagnostics("6502", Text));

        Assert.Equal("keeps-broken", diagnostic.Id);
    }

    /// <summary>Returns an interrupt handler with <paramref name="body"/> that promises to keep C.</summary>
    private static string Handler(string cpu, string body) => cpu == "65816"
        ? $".export .proc handler: interrupt, native, keeps c {{\n    .ensure a8, i8\n{body}    rti\n}}\n"
        : $".export .proc handler: interrupt, keeps c {{\n{body}    rti\n}}\n";

    /// <summary>Returns the warnings and errors nt65 reports for <paramref name="text"/> on <paramref name="cpu"/>.</summary>
    private static IReadOnlyList<Diagnostic> Diagnostics(string cpu, string text) =>
        FlowFragment.Analyze(ProjectSettings.None, cpu, (Analysis.Path, text)).Diagnostics;
}
