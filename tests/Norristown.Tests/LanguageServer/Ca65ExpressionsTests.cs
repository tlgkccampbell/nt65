using Norristown.LanguageServer;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests the rewrite of ca65 expressions as nt65 expressions with the same meaning, which reading
/// a selection as nt65 uses for every line.
/// <para>
/// Each expected line is derived by hand from ca65's precedence table in its documentation, from
/// loosest to tightest: <c>.not</c>; <c>.or</c>; <c>.and</c> and <c>.xor</c>; the comparisons,
/// including <c>=</c> and <c>&lt;&gt;</c>; <c>+</c>, <c>-</c> and <c>|</c>; then <c>*</c>,
/// <c>/</c>, <c>.mod</c>, <c>&amp;</c>, <c>^</c>, <c>&lt;&lt;</c> and <c>&gt;&gt;</c>, all left
/// to right. The ca65 grouping is then written with nt65's C precedence, adding the parentheses
/// that keep it and those nt65 requires.
/// </para>
/// </summary>
public sealed class Ca65ExpressionsTests
{
    /// <summary>
    /// An operator whose level moves between ca65 and nt65 is parenthesized so that the
    /// expression keeps ca65's meaning.
    /// </summary>
    [Theory]

    // `.not` takes the whole comparison in ca65, and only N5 in nt65.
    [InlineData(".assert .not N5 <> 1", ".assert !(N5 != 1)")]
    [InlineData(".if .not A .and B", ".if !(A && B)")]
    [InlineData(".if A .and .not B .or C", ".if A && !(B || C)")]
    [InlineData(".if ! A = 1", ".if !(A == 1)")]

    // ca65 binds the shifts, `&` and `^` with `*`, and `|` with `+`.
    [InlineData(".byte 1 .shl 2 + 1", ".byte (1 << 2) + 1")]
    [InlineData(".byte A - B .shr 1", ".byte A - (B >> 1)")]
    [InlineData(".byte A + B .bitor C", ".byte (A + B) | C")]
    [InlineData(".byte A .bitor B + C", ".byte (A | B) + C")]
    [InlineData(".byte A * B .bitand C", ".byte (A * B) & C")]
    [InlineData(".byte A .bitxor B * C", ".byte (A ^ B) * C")]
    [InlineData(".byte A & B << 2", ".byte (A & B) << 2")]
    [InlineData(".if A .bitand $0F = 0", ".if (A & $0F) == 0")]

    // ca65's comparisons share one level, and nt65 puts `==` below `<`.
    [InlineData(".if A = B < C", ".if (A == B) < C")]

    // ca65's `.and` and `.xor` share one level, and nt65 requires parentheses to mix them.
    [InlineData(".if A .and B .xor C", ".if (A && B) ^^ C")]
    [InlineData(".if A = 1 .or B .and C", ".if A == 1 || (B && C)")]

    // A byte operator binds to its operand in both, and nt65 asks to see that.
    [InlineData("lda #<label+1", "lda #(<label)+1")]
    public void APrecedenceThatMovesIsParenthesized(string ca65, string nt65) =>
        Assert.Equal(nt65, Ca65Expressions.Line(ca65));

    /// <summary>
    /// An expression whose meaning is the same in both is only respelled, and the text around
    /// it is kept as it was.
    /// </summary>
    [Theory]
    [InlineData(".byte A .mod B * C", ".byte A .mod B * C")]
    [InlineData(".byte A .shl B .shl C", ".byte A << B << C")]
    [InlineData(".byte .bitnot A .bitand B", ".byte ~A & B")]
    [InlineData(".if A < B = C", ".if A < B == C")]
    [InlineData(".if .defined(X) .and X = 1", ".if .defined(X) && X == 1")]
    [InlineData("lda table .bitor 1, x", "lda table | 1, x")]
    [InlineData("lda #>(label+1)", "lda #>(label+1)")]
    [InlineData("lda (ptr),y", "lda (ptr),y")]
    [InlineData("lda (ptr,x)", "lda (ptr,x)")]
    [InlineData("bne :+", "bne :+")]
    [InlineData(".const TRACE = 1 .bitand 3", ".const TRACE = 1 & 3")]
    [InlineData(".func rgb(r, g) = (r .bitor g)", ".func rgb(r, g) = (r | g)")]
    [InlineData("* = $1000", "* = $1000")]
    [InlineData(".asciiz \"a .not b\"", ".strz \"a .not b\"")]
    public void AnUnchangedMeaningIsOnlyRespelled(string ca65, string nt65) =>
        Assert.Equal(nt65, Ca65Expressions.Line(ca65));
}
