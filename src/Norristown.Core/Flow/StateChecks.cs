using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Reports what the processor-state analysis finds wrong, in the words its messages use.
/// <see cref="StateAnalysis"/> works out what each statement does to the state. This class
/// reports what is wrong with the state it found at a call, a return, a jump into another
/// routine, a width-dependent immediate, and an operand that reaches memory through D or B.
/// <para>
/// Nothing is reported until the states have reached a fixed point. Only the final walk, over
/// the converged states, is given the checks, so a state on its way to a fixed point is never
/// checked or reported on.
/// </para>
/// </summary>
internal sealed class StateChecks
{
    private readonly SemanticModel model;
    private readonly CodeLayout layout;

    // The banks an absolute constant address in each range may be reached from.
    private readonly IReadOnlyList<Project.AccessRange> ranges;

    private readonly List<Diagnostic> diagnostics = [];

    public StateChecks(SemanticModel model, CodeLayout layout, IReadOnlyList<Project.AccessRange> ranges)
    {
        this.model = model;
        this.layout = layout;
        this.ranges = ranges;
    }

    /// <summary>Gets what the checks have found, in the order they were reported.</summary>
    public IReadOnlyList<Diagnostic> Found => diagnostics;

    /// <summary>Returns a known width in the words a message uses.</summary>
    public static string Format(Width width) => width == Width.Sixteen ? "16-bit" : "8-bit";

    /// <summary>Returns the register an immediate's width comes from, as a message names it.</summary>
    public static string Format(WidthRegister register) => StateRegister.Of(register).Name;

    /// <summary>Returns a known mode in the words a message uses.</summary>
    public static string Mode(ProcessorMode mode) => mode == ProcessorMode.Native ? "native" : "emulation";

    /// <summary>Returns whether a width is one a message can name, rather than unknown or unchanged.</summary>
    public static bool IsKnown(Width width) => width is Width.Eight or Width.Sixteen;

    /// <summary>Returns whether a mode is one a message can name, rather than unknown or unchanged.</summary>
    public static bool IsKnown(ProcessorMode mode) => mode is ProcessorMode.Native or ProcessorMode.Emulation;

    /// <summary>
    /// Returns the state a routine returns with. That is its declared exit, with the parts it
    /// declares unchanged taken from <paramref name="state"/>.
    /// </summary>
    public static ProcessorState Exited(Signature callee, ProcessorState state)
    {
        var exited = new ProcessorState(
            callee.Exit.A == Width.Unchanged ? state.A : callee.Exit.A,
            callee.Exit.Index == Width.Unchanged ? state.Index : callee.Exit.Index,
            callee.Exit.E == ProcessorMode.Unchanged ? state.E : callee.Exit.E,
            callee.Exit.D.IsEntered ? state.D : callee.Exit.D,
            callee.Exit.B.IsEntered ? state.B : callee.Exit.B);

        // Emulation mode pins both widths at 8, as a `.state emu` does, so a routine that
        // returns in emulation mode returns with 8-bit widths even where it says `a*` or `i*`.
        return exited.E == ProcessorMode.Emulation
            ? exited with { A = Width.Eight, Index = Width.Eight }
            : exited;
    }

    /// <summary>
    /// Returns the operand an instruction has in this expansion of it. Where the body names an
    /// <c>operand</c> parameter, that is the operand the call gave.
    /// </summary>
    public SyntaxNode? OperandOf(Step step)
    {
        var operand = (step.Statement as InstructionStatementSyntax)?.Operand;
        return Operands.Substituted(model, operand, step.On)?.Operand ?? operand;
    }

    /// <summary>
    /// Returns whether <paramref name="target"/> is in another address space than the code at
    /// <paramref name="step"/>.
    /// </summary>
    public bool InAnotherSpace(Step step, Symbol? target) =>
        target?.Segment is { } targetSegment && model.Segments.SpaceOf(targetSegment)?.Name != model.Segments.SpaceOf(step.Segment)?.Name;

    /// <summary>Returns the segment a symbol is in, as the program's table declares it.</summary>
    public Segment? SegmentOf(Symbol symbol) => symbol.Segment is { } name ? model.Segments.Find(name) : null;

    /// <summary>Returns the bank a segment declares it lives in, which is the program bank for code in it.</summary>
    public StateValue BankOf(string? segment) =>
        segment is not null && model.Segments.Find(segment)?.Bank is { } bank ? StateValue.Of(bank) : StateValue.Unknown;

    /// <summary>
    /// Reports a diagnostic for a width-dependent immediate whose width is not known here, or that
    /// is 16-bit in emulation mode. ca65 sizes such an immediate from the width it is told, and it
    /// is this analysis that tells it, so the width has to be known here.
    /// </summary>
    public void CheckImmediate(
        Step step, MnemonicKind mnemonic, WidthRegister register, ProcessorState state, Cause? why, Symbol routine)
    {
        var width = state.Of(register);
        var text = SyntaxFacts.TextOf(mnemonic);
        var item = StateRegister.Of(register).Item;
        if (width == Width.Unchanged)
        {
            Report(step, Catalogue.WidthUnknown.Message(
                text,
                Format(register),
                $"`{Owner(step, routine)}` declares `{item}*`, which assumes nothing about it"), Declares(step, item, routine));
        }
        else if (!IsKnown(width))
        {
            Report(
                step,
                Catalogue.WidthUnknown.Message(
                    text,
                    Format(register),
                    "it is not known here" + (why is null ? ": a `.state` declares what it is" : Cause.Because(why))),
                Ensure(step, item));
        }
        else if (width == Width.Sixteen && state.E == ProcessorMode.Emulation)
        {
            Report(step, Catalogue.ImmediateInEmulation.Message(text));
        }
    }

    /// <summary>
    /// Reports a diagnostic where the memory an operand reaches through the direct page or the
    /// data bank disagrees with what the segments and the project's <c>ranges</c> declare. It also
    /// reports a near transfer to a segment in another bank. Where either side is not declared or
    /// not known, nothing is reported, because the checks are opt-in by declaration.
    /// </summary>
    public void CheckMemory(Step step, MnemonicKind mnemonic, AddressingMode? mode, ProcessorState state, Symbol routine)
    {
        if (mode is not { } chosen || OperandOf(step) is not { } operand
            || CodeLayout.Expression(operand) is not { } expression)
        {
            return;
        }

        if (Instructions.Width(chosen) == AddressSize.ZeroPage)
        {
            if (CodeLayout.ThroughDirectPage(operand))
            {
                CheckThroughDirectPage(step, expression, state, routine);
                return;
            }
            if (!state.D.IsKnown)
                return;
            foreach (var symbol in AddressSymbols.In(model, expression, step.On))
            {
                if (SegmentOf(symbol) is { DirectPage: { } page } segment && page != state.D.Value)
                {
                    Report(step, Catalogue.DirectPageMismatch.Message(
                        symbol.DisplayName, segment.Name, StateValue.Hex(page, 4), StateValue.Hex(state.D.Value, 4)));
                }
            }
            return;
        }

        // A near transfer stays in the program bank, so a target in a segment in another bank
        // is out of its reach.
        if (mnemonic != MnemonicKind.Per && (chosen is AddressingMode.Relative or AddressingMode.RelativeLong
            || (chosen == AddressingMode.Absolute && Instructions.Facts(mnemonic).Control is Control.Jumps or Control.Calls)))
        {
            CheckNearBank(step, mnemonic, chosen);
            return;
        }

        // Only an absolute operand of an instruction that reads or writes data uses B. A long
        // operand names its bank, `jmp` and `jsr` use the program bank, and `pea` and `per`
        // access no memory at all.
        if (chosen is not (AddressingMode.Absolute or AddressingMode.AbsoluteX or AddressingMode.AbsoluteY)
            || Instructions.Facts(mnemonic).Control is Control.Jumps or Control.Calls
            || mnemonic is MnemonicKind.Pea or MnemonicKind.Per || !state.B.IsBounded)
        {
            return;
        }

        // Where B is one of a set of banks, the operand has to reach its memory from every one.
        var banks = state.B.Values.ToList();
        foreach (var symbol in AddressSymbols.In(model, expression, step.On))
        {
            if (SegmentOf(symbol) is { Bank: not null } segment && !banks.TrueForAll(segment.IsSeenFrom))
            {
                Report(step, Catalogue.BankMismatch.Message(
                    symbol.DisplayName, segment.Name, segment.FormatBanks(), state.B.Describe(2)));
            }
        }
        if (model.ValueOf(expression, step.On).AsNumber() is { } address
            && ranges.FirstOrDefault(range => range.Covers(address)) is { } covering && !banks.TrueForAll(covering.Permits))
        {
            Report(step, Catalogue.RangeBankMismatch.Message(
                StateValue.Hex(address, 4), covering.FormatBanks(), state.B.Describe(2)));
        }
    }

    /// <summary>
    /// Reports a diagnostic for each part of the state here that does not match what a routine's
    /// entry declares.
    /// </summary>
    public void CheckEntry(Step step, string what, Signature callee, ProcessorState state)
    {
        Width(StateRegister.A, callee.Entry.A, state.A);
        Width(StateRegister.Index, callee.Entry.Index, state.Index);
        if (IsKnown(callee.Entry.E) && callee.Entry.E != state.E)
        {
            Report(step, Catalogue.CallStateMismatch.Message(
                what,
                ProcessorState.Format(callee.Entry.E),
                IsKnown(state.E) ? $"the processor is in {Mode(state.E)} here" : "the mode is not known here"));
        }
        Value(StateRegister.DirectPage, callee.Entry.D, state.D);
        Value(StateRegister.DataBank, callee.Entry.B, state.B);

        void Value(StateRegister register, StateValue needed, StateValue here)
        {
            if (!needed.IsBounded || here.Meets(needed))
                return;
            Report(step, Catalogue.CallStateMismatch.Message(
                what,
                needed.Format(register),
                here.IsBounded
                    ? $"{register.Name} is {here.Describe(register.Digits)} here"
                    : $"{register.Name} is not known here"));
        }

        void Width(StateRegister register, Width needed, Width here)
        {
            if (!IsKnown(needed) || needed == here)
                return;
            Report(step, Catalogue.CallStateMismatch.Message(
                what,
                ProcessorState.Format(register, needed),
                IsKnown(here)
                    ? $"{register.Name} {register.Is} {Format(here)} here"
                    : $"the width of {register.Name} is not known here"));
        }
    }

    /// <summary>
    /// Reports a diagnostic for each part of <paramref name="state"/> that is not what the routine
    /// or macro <paramref name="name"/> declares it returns with.
    /// </summary>
    /// <remarks><paramref name="where"/> says where <paramref name="state"/> holds, as the message puts it.</remarks>
    public void CheckExit(
        Step step, string what, string where, ProcessorState exit, ProcessorState state, string name)
    {
        var lead = what.Length == 0 ? "" : what + " ";
        Part(StateRegister.A, exit.A, state.A);
        Part(StateRegister.Index, exit.Index, state.Index);
        if (exit.E == ProcessorMode.Unchanged && state.E != ProcessorMode.Unchanged)
        {
            Report(step, Catalogue.AssertedItemNotRestored.Message(
                lead, name, "e*", "the mode", "what it was on entry", where));
        }
        else if (IsKnown(exit.E) && exit.E != state.E)
        {
            Report(step, Catalogue.ReturnStateMismatch.Message(
                lead,
                name,
                $"in {Mode(exit.E)} mode",
                IsKnown(state.E)
                    ? $"the processor is in {Mode(state.E)} mode {where}"
                    : $"the mode is not known {where}"));
        }

        Value(StateRegister.DirectPage, exit.D, state.D);
        Value(StateRegister.DataBank, exit.B, state.B);

        void Value(StateRegister register, StateValue declared, StateValue here)
        {
            if (declared.IsEntered && here != declared)
            {
                Report(step, Catalogue.AssertedItemNotRestored.Message(
                    lead,
                    name,
                    declared.Kind == StateValueKind.Unchanged ? $"{register.Item}*" : declared.Format(register),
                    register.Name,
                    "what it was on entry",
                    where));
            }
            else if (declared.IsBounded && !here.Meets(declared))
            {
                Report(step, Catalogue.ReturnStateMismatch.Message(
                    lead,
                    name,
                    $"with `{declared.Format(register)}`",
                    here.IsBounded
                        ? $"{register.Name} is {here.Describe(register.Digits)} {where}"
                        : $"{register.Name} is not known {where}"));
            }
        }

        void Part(StateRegister register, Width declared, Width here)
        {
            if (declared == Width.Unchanged && here != Width.Unchanged)
            {
                Report(step, Catalogue.AssertedItemNotRestored.Message(
                    lead,
                    name,
                    $"{register.Item}*",
                    register.Name,
                    register.IsPlural ? "as wide as they were on entry" : "as wide as it was on entry",
                    where));
            }
            else if (IsKnown(declared) && declared != here)
            {
                Report(step, Catalogue.ReturnStateMismatch.Message(
                    lead,
                    name,
                    $"with `{ProcessorState.Format(register, declared)}`",
                    IsKnown(here)
                        ? $"{register.Name} {register.Is} {Format(here)} {where}"
                        : $"the width of {register.Name} is not known {where}"));
            }
        }
    }

    /// <summary>
    /// Reports a diagnostic where a return does not leave the way the routine is called, or leaves
    /// in a state other than the one the routine declares.
    /// </summary>
    public void CheckReturn(Step step, MnemonicKind mnemonic, ProcessorState state, Symbol routine)
    {
        var signature = routine.Signature ?? Signature.Default;
        if (mnemonic == MnemonicKind.Rts && signature.IsFar)
            Report(step, Catalogue.ReturnDistanceMismatch.Message(routine.DisplayName, "far", "rtl"));
        else if (mnemonic == MnemonicKind.Rtl && !signature.IsFar)
            Report(step, Catalogue.ReturnDistanceMismatch.Message(routine.DisplayName, "near", "rts"));
        CheckExit(step, $"`{SyntaxFacts.TextOf(mnemonic)}`:", "here", signature.Exit, state, routine.DisplayName);
    }

    /// <summary>
    /// Reports a diagnostic for a call whose target is not a routine with a signature. A signature
    /// is what would say what state the target takes and what it hands back.
    /// </summary>
    public void CheckCallTarget(Step step, MnemonicKind mnemonic, Symbol? target) =>
        Report(step, target is null
            ? Catalogue.CallTargetUnknown.Message(SyntaxFacts.TextOf(mnemonic))
            : Catalogue.CallTargetNotARoutine.Message(target.DisplayName));

    /// <summary>
    /// Reports a diagnostic where a call is not made the way the routine is reached, or not in the
    /// state the routine expects.
    /// </summary>
    public void CheckCall(Step step, MnemonicKind mnemonic, Symbol target, Signature callee, ProcessorState state)
    {
        if (mnemonic == MnemonicKind.Jsr && callee.IsFar)
            Report(step, Catalogue.CallDistanceMismatch.Message(
                target.DisplayName, "far", "jsl"), Mnemonic(step, "jsl"));
        else if (mnemonic == MnemonicKind.Jsl && !callee.IsFar)
            Report(step, Catalogue.CallDistanceMismatch.Message(
                target.DisplayName, "near", "jsr"), Mnemonic(step, "jsr"));
        CheckEntry(step, $"`{SyntaxFacts.TextOf(mnemonic)} {target.DisplayName}`", callee, state);
    }

    /// <summary>
    /// Reports a diagnostic where a call made with <c>per</c> and a branch does not suit the routine
    /// it calls. The call is checked as <c>jsr</c> or, with a <c>phk</c> before it, as <c>jsl</c>.
    /// </summary>
    public void CheckRelativeCall(Step step, MnemonicKind mnemonic, RelativeCall call, ProcessorState state)
    {
        var target = call.Routine;
        var callee = target.Signature!;
        if (callee.IsFar && !call.IsFar)
            Report(step, Catalogue.RelativeCallNeedsPhk.Message(target.DisplayName));
        else if (!callee.IsFar && call.IsFar)
            Report(step, Catalogue.RelativeCallExtraPhk.Message(target.DisplayName));
        CheckEntry(step, $"`{SyntaxFacts.TextOf(mnemonic)} {target.DisplayName}`", callee, state);
    }

    /// <summary>
    /// Reports a diagnostic where a jump to a routine's entry does not suit that routine. The
    /// routine returns to this routine's caller, so it has to take the state here, return the way
    /// this routine returns, and hand back what this routine promises. Where nothing returns, only
    /// the target's entry is checked. That is the case when this routine never returns or leaves by
    /// <c>rti</c>, or when the target never returns.
    /// <paramref name="via"/> is the source text that makes the jump. It is the mnemonic, or the
    /// <c>.next</c> or <c>.fallthrough</c> that says where the path goes, in which case
    /// <paramref name="mnemonic"/> is <see cref="MnemonicKind.None"/>.
    /// </summary>
    public void CheckTailCall(
        Step step, string via, MnemonicKind mnemonic, Symbol target, Signature callee, ProcessorState state, Symbol routine)
    {
        var own = routine.Signature ?? Signature.Default;
        var what = $"`{via} {target.DisplayName}`";
        var returns = !own.HasNoCaller && !callee.NeverReturns;

        // A long jump to a near routine is how code enters another bank. The routine's own `rts`
        // then returns within that bank, so the jump is only valid when nothing returns.
        if (mnemonic == MnemonicKind.Jml && !callee.IsFar && !callee.IsInterrupt)
        {
            if (!EntersAnotherBank(step, target))
                Report(step, Catalogue.JumpDistanceMismatch.Message(
                    target.DisplayName, "near", "jmp", target.DisplayName));
            else if (returns)
            {
                Report(step, Catalogue.JumpAcrossBanks.Message(target.DisplayName, routine.DisplayName));
            }
        }
        else if (mnemonic is not (MnemonicKind.Jml or MnemonicKind.None) && callee.IsFar)
        {
            Report(step, Catalogue.JumpDistanceMismatch.Message(target.DisplayName, "far", "jml", target.DisplayName));
        }
        CheckEntry(step, what, callee, state);
        if (!returns)
            return;

        if (callee.IsInterrupt)
        {
            Report(step, Catalogue.TailCallToHandler.Message(what, target.DisplayName));
            return;
        }
        if (callee.IsFar != own.IsFar)
        {
            Report(step, Catalogue.TailCallDistanceMismatch.Message(
                what, target.DisplayName, callee.Distance, routine.DisplayName, own.Distance));
        }
        CheckExit(step, $"{what} is a tail call:", $"when `{target.DisplayName}` returns",
            own.Exit, Exited(callee, state), routine.DisplayName);
    }

    /// <summary>
    /// Reports a diagnostic where a jump into a label inside another routine does not suit this
    /// routine. That routine returns to this routine's caller, so it has to return the way this one
    /// does and hand back what this one declares, exactly as a tail call to it does. The label's
    /// own declaration gives what the state has to be at the label, and is checked separately.
    /// </summary>
    public void CheckJumpInto(
        Step step, string via, Symbol label, Symbol owner, ProcessorState state, Symbol routine)
    {
        var callee = owner.Signature ?? Signature.Default;
        var own = routine.Signature ?? Signature.Default;
        if (own.HasNoCaller || callee.NeverReturns)
            return;
        var what = $"`{via} {label.DisplayName}`";
        if (callee.IsInterrupt)
        {
            Report(step, Catalogue.TailCallToHandler.Message(what, owner.DisplayName));
            return;
        }
        if (callee.IsFar != own.IsFar)
        {
            Report(step, Catalogue.TailCallDistanceMismatch.Message(
                what, owner.DisplayName, callee.Distance, routine.DisplayName, own.Distance));
        }
        CheckExit(step, $"{what} leaves `{routine.DisplayName}`:", $"when `{owner.DisplayName}` returns",
            own.Exit, Exited(callee, state), routine.DisplayName);
    }

    /// <summary>
    /// Reports a diagnostic for a long transfer to a routine's address in a bank of its choosing,
    /// where that bank is neither the bank the routine's segment lives in nor one of its mirrors.
    /// </summary>
    public void CheckMirror(Step step, AddressingMode? mode)
    {
        if (Targets.MirrorOf(model, Transfers.TargetOf(step.Statement, mode), step.On) is not { } mirror
            || SegmentOf(mirror.Routine) is not { Bank: not null } segment || segment.IsSeenFrom(mirror.Bank))
        {
            return;
        }
        Report(step, Catalogue.MirrorBankMismatch.Message(
            mirror.Routine.DisplayName, segment.Name, segment.FormatBanks(), StateValue.Hex(mirror.Bank, 2)));
    }

    /// <summary>
    /// Reports a diagnostic for a call to a routine that takes <c>args n</c> when too few bytes are
    /// pushed. The caller pushes those bytes first. Where what this routine pushed is known, there
    /// have to be at least that many beneath the <paramref name="pushed"/> bytes a relative call
    /// pushes for itself.
    /// </summary>
    public void CheckArguments(Step step, Symbol? target, AnalysisStack? stack, int pushed)
    {
        if (target?.Signature is not { Arguments: > 0 and var needed } || stack is not { IsAnchored: true } known
            || known.Depth - pushed >= needed)
        {
            return;
        }
        var have = known.Depth - pushed;
        Report(step, Catalogue.ArgsNotPushed.Message(
            target.DisplayName,
            needed,
            have == 0
                ? "nothing is pushed here"
                : $"only {(have == 1 ? "1 byte is" : $"{have} bytes are")} pushed here"));
    }

    /// <summary>
    /// Reports each processor-state or <c>.frame</c> directive outside any routine. Outside a
    /// routine there is no processor state, so a directive that describes a point in a routine
    /// describes nothing. An instruction there has been reported already, because code belongs in
    /// a proc.
    /// </summary>
    public void CheckOutsideRoutines()
    {
        foreach (var step in layout.Steps)
        {
            if (step.Routine is not null || step.Label is not null)
                continue;
            var keyword = step.Statement switch
            {
                StateListDirectiveSyntax list => list.Keyword,
                FrameDirectiveSyntax frame => frame.Keyword,
                _ => (SyntaxToken?)null,
            };
            if (keyword is { } directive)
            {
                Report(step, Catalogue.StateOutsideARoutine.Message(directive.Text.ToLowerInvariant()));
            }
        }
    }

    /// <summary>
    /// Reports each width-dependent immediate and each <c>d:</c> operand in a block that no path
    /// from the routine's entry reaches and no <c>.state</c> declares. The block's immediates
    /// cannot be sized, because nothing says how wide anything is there.
    /// </summary>
    public void Unreached(BasicBlock block, FlowRegion region)
    {
        foreach (var step in block.Steps)
        {
            if (step.Statement is not InstructionStatementSyntax statement)
                continue;
            if (OperandOf(step) is { } operand && CodeLayout.ThroughDirectPage(operand))
            {
                Report(step, Catalogue.DirectPageUnknown.Message(
                    "`d:` is reached through the direct page",
                    $"no path from `{region.Routine.DisplayName}`'s entry reaches it. A `.state` after its label declares what the state is there"));
                continue;
            }
            if (layout.Of(statement, step.On)?.Mode != AddressingMode.Immediate
                || Instructions.SizedBy(statement.MnemonicKind) is not { } register)
            {
                continue;
            }
            Report(step, Catalogue.WidthUnknown.Message(
                SyntaxFacts.TextOf(statement.MnemonicKind),
                Format(register),
                $"no path from `{region.Routine.DisplayName}`'s entry reaches it. A `.state` after its label declares what the state is there"));
        }
    }

    /// <summary>
    /// Reports a problem with <paramref name="step"/>'s statement, in the expansion the step
    /// belongs to.
    /// </summary>
    public void Report(Step step, DiagnosticMessage message) => ReportAt(step.Statement, step, message);

    /// <summary>
    /// Reports a problem with <paramref name="step"/>'s statement, in the expansion the step
    /// belongs to, and attaches the fix its message names.
    /// </summary>
    public void Report(Step step, DiagnosticMessage message, DiagnosticFix? fix)
    {
        Report(step, message);
        if (fix is not null)
            diagnostics[^1] = diagnostics[^1] with { Fix = fix };
    }

    /// <summary>
    /// Reports a problem with <paramref name="node"/>, in the expansion that
    /// <paramref name="step"/> belongs to. A line of a macro body is wrong only for the call that
    /// expanded it. It is reported at that call, which is the side that can change, with the body
    /// line named beside it. A line a call gave as a block argument is the caller's own, and is
    /// reported where it appears.
    /// </summary>
    public void ReportAt(SyntaxNode node, Step step, DiagnosticMessage message)
    {
        var inBody = node.Tree != model.Tree;
        MacroCallSyntax? call = null;
        for (var level = step.On; level is not null; level = level.Outer)
        {
            if (level.Call is null)
                continue;
            call = level.Call;
            if (level.Body is { } body && body.Tree == node.Tree
                && node.Position >= body.Position && node.Position < body.FullSpan.End)
            {
                inBody = true;
            }
        }

        if (!inBody || call is null)
        {
            diagnostics.Add(new Diagnostic(node.Tree.GetSpan(node.Span), Severity.Error, message));
            return;
        }
        diagnostics.Add(new Diagnostic(call.Tree.GetSpan(call.Span), Severity.Error, message,
            [new RelatedSpan(node.Tree.GetSpan(node.Span), "in the macro body")]));
    }

    /// <summary>
    /// Reports a <c>jsr</c>, <c>jmp</c> or branch to a label or a routine whose segment declares a
    /// bank other than the one the code around it declares. Both sides have to declare a bank.
    /// </summary>
    private void CheckNearBank(Step step, MnemonicKind mnemonic, AddressingMode mode)
    {
        if (BankOf(step.Segment) is not { IsKnown: true } here
            || Targets.Of(model, Transfers.TargetOf(step.Statement, mode), step.On)?.Symbol is not { } target
            || SegmentOf(target) is not { Bank: { } there } segment || there == here.Value)
        {
            return;
        }
        // A conditional branch has no long form to reach with, so what reaches the other bank
        // is a `jml` the opposite branch skips.
        var reaches = mnemonic switch
        {
            MnemonicKind.Jsr => "use `jsl`",
            MnemonicKind.Jmp or MnemonicKind.Bra or MnemonicKind.Brl => "use `jml`",
            _ => "a branch cannot leave its bank, so branch the other way around a `jml` to it",
        };
        Report(step, Catalogue.JumpLeavesBank.Message(
            SyntaxFacts.TextOf(mnemonic),
            StateValue.Hex(here.Value, 2),
            target.DisplayName,
            segment.Name,
            StateValue.Hex(there, 2),
            reaches));
    }

    /// <summary>
    /// Reports a diagnostic for <c>d:</c> on a constant address, which reaches it through the
    /// direct page, unless D is known here and the address lies in the 256 bytes starting at D.
    /// </summary>
    private void CheckThroughDirectPage(Step step, SyntaxNode expression, ProcessorState state, Symbol routine)
    {
        if (model.ValueOf(expression, step.On).AsNumber() is not { } address)
            return;
        var what = $"`d:{StateValue.Hex(address, 4)}` is reached through the direct page";
        if (state.D.Kind == StateValueKind.Unchanged)
        {
            Report(step, Catalogue.DirectPageUnknown.Message(
                what, $"`{Owner(step, routine)}` declares `dp*`, which assumes nothing about D"));
        }
        else if (!state.D.IsKnown)
        {
            Report(step, Catalogue.DirectPageUnknown.Message(
                what, "D is not known here: a `.state dp = ...` declares what it is"));
        }
        else if (address < state.D.Value || address > state.D.Value + 0xff)
        {
            Report(step, Catalogue.DirectPageOutOfReach.Message(
                what,
                StateValue.Hex(state.D.Value, 4),
                StateValue.Hex(state.D.Value, 4),
                StateValue.Hex(state.D.Value + 0xff, 4)));
        }
    }

    /// <summary>
    /// Returns whether a long jump lands in a bank other than the one the code making it is taken
    /// to run in. The landing bank is the mirror bank the target's address names, or else the
    /// target's home bank. Both banks have to be declared.
    /// </summary>
    private bool EntersAnotherBank(Step step, Symbol target)
    {
        var mode = layout.Of(step.Statement, step.On)?.Mode;
        var landing = Targets.MirrorOf(model, Transfers.TargetOf(step.Statement, mode), step.On)?.Bank
            ?? SegmentOf(target)?.Bank;
        return BankOf(step.Segment) is { IsKnown: true } here && landing is { } there && there != here.Value;
    }

    /// <summary>
    /// Returns the name of whatever declares the <c>*</c> items in force at a step. That is the
    /// innermost macro with a signature that the step was expanded from, or else the routine.
    /// </summary>
    private string Owner(Step step, Symbol routine)
    {
        for (var level = step.On; level is not null; level = level.Outer)
        {
            if (level.Call is { } call && model.MacroAt(call) is { MacroSignature: not null } macro)
                return macro.DisplayName + "!";
        }
        return routine.DisplayName;
    }

    /// <summary>
    /// Returns a fix that adds an <c>.ensure</c> of <paramref name="item"/>'s width before the
    /// statement, where the statement is in this file and outside any expansion. Which width to
    /// ensure is the programmer's choice, so the fix names only the register and an editor offers
    /// both widths.
    /// </summary>
    private DiagnosticFix? Ensure(Step step, string item) =>
        step.On is null && step.Statement.Tree == model.Tree ? new DiagnosticFix(FixKind.Width, item) : null;

    /// <summary>
    /// Returns a fix that adds the width to the routine's signature, which is where a routine that
    /// assumes nothing about it says what it assumes. The routine has to be one this file declares,
    /// because its signature is what a caller anywhere reads. Otherwise the fix is an
    /// <c>.ensure</c>.
    /// </summary>
    private DiagnosticFix? Declares(Step step, string item, Symbol routine) =>
        step.On is null && step.Statement.Tree == model.Tree && routine.Tree == model.Tree
            ? new DiagnosticFix(FixKind.Signature, item, routine.DeclarationSpan)
            : Ensure(step, item);

    /// <summary>
    /// Returns a fix that replaces the statement's mnemonic with <paramref name="mnemonic"/>,
    /// where the statement is in this file and outside any expansion.
    /// </summary>
    private DiagnosticFix? Mnemonic(Step step, string mnemonic) =>
        step.On is null && step.Statement.Tree == model.Tree ? new DiagnosticFix(FixKind.Mnemonic, mnemonic) : null;
}
