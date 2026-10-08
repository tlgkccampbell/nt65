using System.Text;
using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Tests.Oracle;

/// <summary>
/// Tests that the opcode table writes each instruction form as the byte ca65 writes it, by
/// assembling every form of each CPU with the pinned ca65 and reading back the first byte of each.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class OpcodeOracleTests
{
    /// <summary>Gets every CPU nt65 knows.</summary>
    public static TheoryData<Cpu> Cpus => [.. Enum.GetValues<Cpu>()];

    [Theory]
    [MemberData(nameof(Cpus))]
    public void EveryFormIsTheByteCa65Writes(Cpu cpu)
    {
        Assert.SkipWhen(Repo.Selection is not null, "NT65_FIXTURE selects fixtures and programs, and this test is neither");
        var forms = Enum.GetValues<MnemonicKind>()
            .SelectMany(mnemonic => Instructions.Modes(cpu, mnemonic).Order().Select(mode => (Mnemonic: mnemonic, Mode: mode)))
            .ToList();
        var source = new StringBuilder($".setcpu \"{CpuNames.FormatForCa65(cpu)}\"\n.segment \"CODE\"\n");
        foreach (var (mnemonic, mode) in forms)
            source.Append("    ").Append(SyntaxFacts.TextOf(mnemonic)).Append(' ').Append(Operand(mnemonic, mode)).Append('\n');

        var config = Repo.ReadText(Repo.Path("tests", "fixtures", "modules", "link", "link.cfg"));
        var linked = Ca65Oracle.Pinned.Link(config, [("forms.s", source.ToString())]);
        Assert.True(linked.Succeeded, linked.Messages);

        var at = 0;
        var wrong = new List<string>();
        foreach (var (mnemonic, mode) in forms)
        {
            var written = linked.Binary[at];
            if (Opcodes.Encode(cpu, mnemonic, mode) != written)
                wrong.Add($"{SyntaxFacts.TextOf(mnemonic)} {mode}: ca65 writes ${written:X2}, the table ${Opcodes.Encode(cpu, mnemonic, mode):X2}");
            at += Instructions.Length(mode);
        }
        Assert.Equal(linked.Binary.Length, at);
        Assert.Empty(wrong);
    }

    /// <summary>Returns an operand that ca65 reads as <paramref name="mode"/>.</summary>
    private static string Operand(MnemonicKind mnemonic, AddressingMode mode) => mode switch
    {
        AddressingMode.Implied => "",
        AddressingMode.Accumulator => "a",
        AddressingMode.Immediate => "#$12",
        AddressingMode.Direct => "z:$12",
        AddressingMode.DirectX => "z:$12,x",
        AddressingMode.DirectY => "z:$12,y",
        AddressingMode.Absolute when Instructions.IsControlTransfer(mnemonic) || mnemonic == MnemonicKind.Pea => "$1234",
        AddressingMode.Absolute => "a:$1234",
        AddressingMode.AbsoluteX => "a:$1234,x",
        AddressingMode.AbsoluteY => "a:$1234,y",
        AddressingMode.DirectIndirect => "($12)",
        AddressingMode.DirectIndirectX => "($12,x)",
        AddressingMode.DirectIndirectY => "($12),y",
        AddressingMode.AbsoluteIndirect => "($1234)",
        AddressingMode.AbsoluteIndirectX => "($1234,x)",
        AddressingMode.Relative => "*+2",
        AddressingMode.DirectRelative => "$12,*+3",
        AddressingMode.Long when Instructions.IsControlTransfer(mnemonic) => "$123456",
        AddressingMode.Long => "f:$123456",
        AddressingMode.LongX => "f:$123456,x",
        AddressingMode.DirectIndirectLong => "[$12]",
        AddressingMode.DirectIndirectLongY => "[$12],y",
        AddressingMode.StackRelative => "$12,s",
        AddressingMode.StackRelativeIndirectY => "($12,s),y",
        AddressingMode.AbsoluteIndirectLong => "[$1234]",
        AddressingMode.RelativeLong => "*+3",
        AddressingMode.BlockMove => "$12,$34",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "not a mode this test writes"),
    };
}
