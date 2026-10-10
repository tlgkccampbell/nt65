using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

public sealed partial class CodeLayout
{
    /// <summary>
    /// Places the positions a <c>.label</c> names inside an instruction, and follows the bytes
    /// from each. A position is a label at an instruction plus a number of bytes into it, and
    /// stands where those bytes do, so a branch to it is measured as a branch to any label is.
    /// Once the walks have settled, the bytes from each position are decoded as the CPU would run
    /// them, until they reach the start of an instruction as written, and the
    /// <see cref="HiddenPath"/> they make is recorded for the flow analysis.
    /// </summary>
    private sealed partial class Walker
    {
        // How far from one position a decoded instruction may start before nt65 gives up on
        // reaching the start of an instruction. No real trick runs that far inside other
        // instructions.
        private const int MostHiddenBytes = 32;

        // Each `.label` the walk reached, with where it stood.
        private readonly List<InsideLabel> insideLabels = [];

        /// <summary>Records a <c>.label</c>, whose position is worked out once the walk is over.</summary>
        private void Inside(LabelDirectiveSyntax directive)
        {
            if (routine is not null && NameOf(directive) is { } symbol)
                insideLabels.Add(new InsideLabel(directive, symbol, expansion, routine, Stream, segment));
        }

        /// <summary>Records where each position a <c>.label</c> names stands among the bytes.</summary>
        private void PlaceInsideLabels()
        {
            foreach (var inside in insideLabels)
            {
                if (Base(inside, report: false) is { } at)
                    layout.labels[(inside.Symbol, Expansion.Owning(inside.On, inside.Symbol))] = at.Position with { Length = 0 };
            }
        }

        /// <summary>
        /// Decodes the bytes from each position a <c>.label</c> names, and records the path they
        /// run as, with a step of its own in a stream of its own, so that nothing runs into it.
        /// </summary>
        private void DecodeInsideLabels()
        {
            foreach (var inside in insideLabels)
            {
                if (Base(inside, report: true) is not { } at || Decode(inside, at.Position) is not { } path)
                    continue;
                var step = new Step(inside.Directive, inside.On, inside.Routine, nextStream++, inside.Segment, null);
                layout.steps.Add(step);
                layout.hidden[step.Key] = path;
                layout.landings.Add(path.Landing);
                CycleCount? cycles = new CycleCount(0);
                foreach (var instruction in path.Instructions)
                    cycles = cycles is { } total && instruction.Cycles is { } each ? total + each : null;
                layout.lines[step.Key] = new LineLayout(0, null, null, Cycles: cycles);
            }
        }

        /// <summary>
        /// Returns where the position a <c>.label</c> names stands, and the step of the instruction
        /// it is inside. With <paramref name="report"/>, a position that is not inside an
        /// instruction of the same routine is reported.
        /// </summary>
        private (BytePosition Position, Step Instruction)? Base(InsideLabel inside, bool report)
        {
            var directive = inside.Directive;
            if (directive.Value is not BinaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.Plus, Left: NameExpressionSyntax name } sum)
                return Invalid("its value is not a label plus a number of bytes");
            if (model.ValueOf(sum.Right, inside.On).AsNumber() is not (> 0 and var into))
                return Invalid("the number of bytes into the instruction is not a constant above 0");
            if (Located(name, inside.On) is not { } start)
                return Invalid($"`{name.GetText().Trim()}` is not a label in this file's code");
            foreach (var step in layout.steps)
            {
                if (step.Statement is not InstructionStatementSyntax
                    || layout.positions.GetValueOrDefault(step.Key) is not { Length: > 0 } at
                    || at.Stream != start.Stream || at.Offset != start.Offset)
                {
                    continue;
                }
                if (step.Routine != inside.Routine)
                    return Invalid($"`{name.GetText().Trim()}` is in another routine");
                if (into >= at.Length)
                    return Invalid($"the instruction at `{name.GetText().Trim()}` is {Bytes(at.Length)} long");
                return (new BytePosition(at.Stream, at.Offset + (int)into, 0), step);
            }
            return Invalid($"no instruction stands at `{name.GetText().Trim()}`");

            (BytePosition, Step)? Invalid(string why)
            {
                if (report)
                    Report(directive, Catalogue.LabelPositionInvalid.Message(directive.Name.Text, why));
                return null;
            }
        }

        /// <summary>
        /// Returns the path the bytes from <paramref name="from"/> run as, reporting why where nt65
        /// cannot follow them. The bytes have to be known to nt65, run as instructions the CPU has
        /// that neither move the stack nor change where control goes, and reach the start of an
        /// instruction of the same routine.
        /// <para>
        /// Each instruction is counted in the processor state and with the decimal flag that the
        /// analyses found reaching the position, as an ordinary line is. None of the instructions
        /// followed changes the widths or the mode, but <c>cld</c> and <c>sed</c> change the
        /// decimal flag and <c>tcd</c> changes D, so the count follows those.
        /// </para>
        /// </summary>
        private HiddenPath? Decode(InsideLabel inside, BytePosition from)
        {
            var name = inside.Directive.Name.Text;
            var starts = new Dictionary<int, (Step Step, BytePosition At)>();
            foreach (var step in layout.steps)
            {
                if (layout.positions.GetValueOrDefault(step.Key) is { Length: > 0 } at && at.Stream == from.Stream)
                    starts.TryAdd(at.Offset, (step, at));
            }

            var state = states?.Before(inside.Directive, inside.On);
            var decimalMode = DecimalBefore(inside.Directive, inside.On);
            var decoded = new List<HiddenInstruction>();
            var offset = from.Offset;
            while (true)
            {
                if (offset != from.Offset && starts.TryGetValue(offset, out var landing)
                    && landing.Step.Statement is InstructionStatementSyntax)
                {
                    if (landing.Step.Routine != inside.Routine)
                        return Unfollowed("they reach an instruction of another routine");
                    return new HiddenPath(inside.Symbol, decoded, landing.Step.Key);
                }

                // The limit applies to where each decoded instruction starts, so a last
                // instruction that starts inside it may end past it and still land.
                if (offset - from.Offset >= MostHiddenBytes)
                    return Unfollowed("they do not reach the start of an instruction");
                if (ByteAt(starts, offset, out var why) is not { } opcode)
                    return Unfollowed(why!);
                if (Opcodes.Decode(cpu, (byte)opcode) is not { } form)
                {
                    return Unfollowed($"they run as ${opcode:X2}, which the {CpuNames.Format(cpu)} has no instruction for"
                        + Elsewhere((byte)opcode));
                }
                var (mnemonic, mode) = form;
                if (Unfollowable(mnemonic, mode) is { } reason)
                    return Unfollowed($"they run as `{SyntaxFacts.TextOf(mnemonic)}`, which {reason}");
                var operand = 0L;
                var length = Instructions.Length(mode);
                for (var i = 1; i < length; i++)
                {
                    if (ByteAt(starts, offset + i, out why) is not { } value)
                        return Unfollowed(why!);
                    operand |= (long)value << (8 * (i - 1));
                }
                decoded.Add(new HiddenInstruction(mnemonic, mode, operand, Cycles.Of(cpu, mnemonic, mode, state, decimalMode)?.Count));
                decimalMode = mnemonic switch
                {
                    MnemonicKind.Cld => false,
                    MnemonicKind.Sed => true,
                    _ => decimalMode,
                };
                if (mnemonic == MnemonicKind.Tcd && state is { } known)
                    state = known with { D = StateValue.Unknown };
                offset += length;
            }

            HiddenPath? Unfollowed(string why)
            {
                Report(inside.Directive, Catalogue.HiddenPathUnfollowed.Message(name, why));
                return null;
            }
        }

        /// <summary>
        /// Returns why nt65 cannot follow <paramref name="mnemonic"/> inside another instruction's
        /// bytes, as a message completes <c>they run as `x`, which …</c>, or null where it can.
        /// </summary>
        private string? Unfollowable(MnemonicKind mnemonic, AddressingMode mode)
        {
            var facts = Instructions.Facts(mnemonic);
            if (facts.Control != Control.Through || mnemonic is MnemonicKind.Brk or MnemonicKind.Cop or MnemonicKind.Wai)
                return "changes where control goes";
            if (facts is { Pushes: not null } or { Pulls: not null } || RegisterEffects.SetsStackPointer(mnemonic))
                return "moves the stack";
            if (mnemonic is MnemonicKind.Rep or MnemonicKind.Sep or MnemonicKind.Xce)
                return "changes the processor's widths";
            if (cpu == Cpu.Wdc65816 && mode == AddressingMode.Immediate && Instructions.SizedBy(mnemonic) is not null)
                return "has an immediate as wide as a register whose width nt65 does not follow there";
            return null;
        }

        /// <summary>
        /// Returns the byte at <paramref name="offset"/> in the run <paramref name="starts"/>
        /// describes, or null with the reason in <paramref name="why"/> where nt65 does not know it.
        /// </summary>
        private int? ByteAt(Dictionary<int, (Step Step, BytePosition At)> starts, int offset, out string? why)
        {
            why = null;
            foreach (var (_, (step, at)) in starts)
            {
                if (offset < at.Offset || offset >= at.End)
                    continue;
                if (step.Statement is not InstructionStatementSyntax instruction
                    || layout.lines.GetValueOrDefault(step.Key) is not { Mode: { } mode } laid)
                {
                    why = "they run into bytes that are not an instruction";
                    return null;
                }
                var written = $"`{instruction.GetText().Trim()}`";
                if (offset == at.Offset)
                {
                    if ((laid.Opcode ?? (int?)Opcodes.Encode(cpu, instruction.MnemonicKind, mode)) is { } opcode)
                        return opcode;
                    why = $"they run through {written}, whose opcode nt65 does not write";
                    return null;
                }
                if (OperandOf(step, instruction, laid, at) is not { } value)
                {
                    why = $"they run through the operand of {written}, which only the linker knows";
                    return null;
                }
                return (int)((value >> (8 * (offset - at.Offset - 1))) & 0xff);
            }
            why = "they run past the end of the routine's bytes";
            return null;
        }

        /// <summary>
        /// Returns the value of an instruction's operand as its bytes hold it, or null where only
        /// the linker knows it. A branch's operand is the distance to its target.
        /// </summary>
        private long? OperandOf(Step step, InstructionStatementSyntax instruction, LineLayout laid, BytePosition at)
        {
            var operand = Operands.Substituted(model, instruction.Operand, step.On)?.Operand ?? instruction.Operand;
            if (operand is null || Expression(operand) is not { } expression)
                return null;
            if (laid.Mode == AddressingMode.Relative && !laid.Inverted)
                return Located(expression, step.On) is { } target && target.Stream == at.Stream ? target.Offset - at.End : null;
            if (laid.Mode is AddressingMode.Relative or AddressingMode.RelativeLong or AddressingMode.DirectRelative
                or AddressingMode.BlockMove)
            {
                return null;
            }
            return laid.Direct ?? model.ValueOf(expression, step.On).AsNumber();
        }

        /// <summary>
        /// Returns what the other CPUs run <paramref name="opcode"/> as, such as <c>, though the
        /// 6502x runs it as `nop`</c>, or an empty text where none has an instruction with it.
        /// </summary>
        private static string Elsewhere(byte opcode)
        {
            var runs = CpuNames.All
                .Select(other => (Cpu: other, Form: Opcodes.Decode(other, opcode)))
                .Where(other => other.Form is not null)
                .GroupBy(other => SyntaxFacts.TextOf(other.Form!.Value.Mnemonic))
                .Select(group => (Cpus: group.Select(other => "the " + CpuNames.Format(other.Cpu)).ToList(), Mnemonic: group.Key))
                .Select(each => $"{Listed(each.Cpus)} {(each.Cpus.Count == 1 ? "runs" : "run")} it as `{each.Mnemonic}`")
                .ToList();
            return runs.Count == 0 ? "" : ", though " + string.Join(", and ", runs);

            static string Listed(List<string> names) =>
                names.Count == 1 ? names[0] : string.Join(", ", names.SkipLast(1)) + " and " + names[^1];
        }

        private static string Bytes(int count) => count == 1 ? "1 byte" : $"{count} bytes";

        /// <summary>Represents a <c>.label</c> the walk reached, with where it stood.</summary>
        /// <param name="Directive">The <c>.label</c>.</param>
        /// <param name="Symbol">The name it declares.</param>
        /// <param name="On">The expansion it is in, or null outside every expansion.</param>
        /// <param name="Routine">The routine it is in.</param>
        /// <param name="Stream">The stream it stood in.</param>
        /// <param name="Segment">The segment it stood in, or null outside every segment.</param>
        private sealed record InsideLabel(
            LabelDirectiveSyntax Directive, Symbol Symbol, Expansion? On, Symbol Routine, int Stream, string? Segment);
    }
}
