namespace Norristown.Tests.Semantics;

/// <summary>
/// Checks how the arguments of a <c>.func</c> call are matched to its parameters: by position,
/// then by name, with a default for a parameter the call leaves out.
/// </summary>
public sealed class FunctionCallTests
{
    /// <summary>
    /// Only a macro call takes a braced operand. In a function call the parser reports one as a
    /// missing expression, whether it is given by position or by name, and the matcher does not
    /// then also report the parameter as missing.
    /// </summary>
    [Theory]
    [InlineData("twice({buf,x})")]
    [InlineData("twice(1, w = {buf,x})")]
    public void ABracedOperandIsNotAnExpression(string call)
    {
        var program = Analysis.Program(("main.nt65", """
            .module main
            .cpu 6502
            .func twice(v, w = 1) = v * 2 + w
            .segment RODATA
            .data table: .byte[] {
            """ + call + " }\n"));

        Assert.Equal(["main.nt65:5: expected `)`", "main.nt65:5: expected an expression"], program.Problems());
    }

    /// <summary>A parameter the call leaves out takes its default, and one it names is bound by name.</summary>
    [Fact]
    public void ANamedArgumentBindsThatParameter()
    {
        var output = Analysis.Compiled("""
            .module main
            .cpu 6502
            .func twice(v, w = 1) = v * 2 + w
            .segment RODATA
            .data table: .byte[] { twice(1), twice(1, w = 5), twice(w = 5, v = 2) }
            """);

        Assert.Contains(".byte $03, $07, $09", output, StringComparison.Ordinal);
    }
}
