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

    // Returns the signature each routine is analyzed with, and records that the analysis took it.
    private readonly Func<Symbol, Signature?> signatureOf;
    private readonly InferredSignatures signatures;

    private readonly List<Diagnostic> diagnostics = [];

    // The routines and registers whose callers disagree that have been reported, once each.
    private readonly HashSet<(Symbol, WidthRegister)> disagreed = [];

    public StateChecks(
        SemanticModel model, CodeLayout layout, IReadOnlyList<Project.AccessRange> ranges,
        Func<Symbol, Signature?> signatureOf, InferredSignatures signatures)
    {
        this.model = model;
        this.layout = layout;
        this.ranges = ranges;
        this.signatureOf = signatureOf;
        this.signatures = signatures;
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
    /// Returns the words a message uses for a mode that is not known at <paramref name="step"/>.
    /// Where <paramref name="why"/> says why it is not known, the words give that cause and its
    /// fix. Where the mode is the one the routine was entered with and its callers disagree on it,
    /// the words name what they call it with.
    /// </summary>
    public string ModeUnknown(Step step, ProcessorState state, Cause? why = null)
    {
        if (why is not null)
            return "the mode is not known here" + Cause.Because(why);
        if (state.E != ProcessorMode.Unchanged || step.Routine is not { } routine || Owner(step, routine) != routine.DisplayName
            || signatures.DisagreementOn(routine, StateParts.Mode) is not { } callers)
        {
            return "the mode is not known here";
        }
        var modes = callers.Select(caller => $"`{caller.State}`").Distinct().Order(StringComparer.Ordinal);
        return $"the mode is not known here, because `{routine.DisplayName}` is called with {string.Join(" and ", modes)}";
    }

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
        if (width == Width.Unchanged && Owner(step, routine) == routine.DisplayName
            && signatures.DisagreementOn(routine, register == WidthRegister.A ? StateParts.A : StateParts.Index) is { } callers)
        {
            ReportDisagreement(step, text, register, routine, callers);
        }
        else if (width == Width.Unchanged)
        {
            Report(step, Catalogue.WidthUnknown.Message(
                text,
                Format(register),
                $"{Assumes(step, routine, register == WidthRegister.A ? StateParts.A : StateParts.Index, item + "*")}, which assumes nothing about it"),
                Declares(step, item, routine));
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
    /// Reports, once for each routine and register, that the routine's callers disagree on a
    /// width its body depends on. It is reported at the routine's name, with each caller and the
    /// immediate that depends on the width beside it. The fixes declare either width.
    /// </summary>
    private void ReportDisagreement(
        Step step, string mnemonic, WidthRegister register, Symbol routine, IReadOnlyList<(string State, Span At)> callers)
    {
        if (!disagreed.Add((routine, register)))
            return;
        var states = callers.Select(caller => caller.State).Distinct().Order(StringComparer.Ordinal).ToList();
        var related = new List<RelatedSpan>
        {
            new(step.Statement.Tree.GetSpan(step.Statement.Span), "the width is needed here"),
        };
        related.AddRange(callers.Select(caller => new RelatedSpan(caller.At, $"called with `{caller.State}`")));
        var own = routine.Tree == model.Tree;
        diagnostics.Add(new Diagnostic(
            routine.DeclarationSpan,
            Catalogue.CallersDisagree.Message(
                routine.DisplayName, $"`{states[0]}`", $"`{states[^1]}`", mnemonic, Format(register)),
            related)
        {
            Fix = own ? new DiagnosticFix(FixKind.Signature, states[0], routine.DeclarationSpan) : null,
            Also = own ? new DiagnosticFix(FixKind.Signature, states[^1], routine.DeclarationSpan) : null,
        });
    }

    /// <summary>
    /// Reports a diagnostic where the memory an operand reaches through the direct page or the
    /// data bank disagrees with what the segments and the project's <c>ranges</c> declare.
    /// <paramref name="whyD"/> says why D is unknown, where it is and the analysis can tell. Where
    /// the segment or range declares nothing, nothing is reported, because the checks are opt-in
    /// by declaration. A direct operand on a symbol whose segment declares <c>dp</c> also needs D
    /// to be known, because the operand reaches the symbol only when D holds that value. Where B is
    /// not known, nothing is reported. A near transfer to a segment in another bank is a matter of
    /// reach, which layout checks.
    /// </summary>
    public void CheckMemory(
        Step step, MnemonicKind mnemonic, AddressingMode? mode, ProcessorState state, Cause? whyD, Symbol routine)
    {
        if (mode is not { } chosen || StepOperands.Of(model, step) is not { } operand
            || CodeLayout.Expression(operand) is not { } expression)
        {
            return;
        }

        if (Instructions.Width(chosen) == AddressSize.ZeroPage)
        {
            if (CodeLayout.ThroughDirectPage(operand))
            {
                CheckThroughDirectPage(step, expression, state, whyD, routine);
                return;
            }
            foreach (var symbol in AddressSymbols.In(model, expression, step.On))
            {
                if (SegmentOf(symbol) is not { DirectPage: { } page } segment)
                    continue;
                var what = $"`{symbol.DisplayName}` is in segment `{segment.Name}`, which expects the direct page at {StateValue.Hex(page, 4)}";
                if (state.D.Kind == StateValueKind.Unchanged)
                {
                    Report(step, Catalogue.DirectPageUnknown.Message(
                        what, $"{Assumes(step, routine, StateParts.DirectPage, "dp*")}, which assumes nothing about D"));
                }
                else if (!state.D.IsKnown)
                {
                    Report(step, Catalogue.DirectPageUnknown.Message(
                        what, Unknown(whyD)));
                }
                else if (page != state.D.Value)
                {
                    Report(step, Catalogue.DirectPageMismatch.Message(
                        symbol.DisplayName, segment.Name, StateValue.Hex(page, 4), StateValue.Hex(state.D.Value, 4)));
                }
            }
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
    /// entry needs. Where <paramref name="target"/> is given and its signature does not write a
    /// part, the message says that the routine takes that part by default. Where
    /// <paramref name="whyMode"/> is given, a mode that is not known is reported with that cause.
    /// </summary>
    public void CheckEntry(
        Step step, string what, Signature callee, ProcessorState state, Symbol? target = null, Cause? whyMode = null)
    {
        Width(StateRegister.A, StateParts.A, callee.Entry.A, state.A);
        Width(StateRegister.Index, StateParts.Index, callee.Entry.Index, state.Index);
        if (IsKnown(callee.Entry.E) && callee.Entry.E != state.E)
        {
            Report(step, Catalogue.CallStateMismatch.Message(
                what,
                Needs(StateParts.Mode, ProcessorState.Format(callee.Entry.E)),
                IsKnown(state.E) ? $"the processor is in {Mode(state.E)} here" : ModeUnknown(step, state, whyMode)));
        }
        Value(StateRegister.DirectPage, StateParts.DirectPage, callee.Entry.D, state.D);
        Value(StateRegister.DataBank, StateParts.DataBank, callee.Entry.B, state.B);

        // An item the routine's signature writes is what it declares. Any other is a default.
        string Needs(StateParts part, string item) =>
            target is not null && (callee.Written & part) == 0
                ? $"`{item}`, which `{target.DisplayName}` takes by default"
                : $"`{item}`";

        void Value(StateRegister register, StateParts part, StateValue needed, StateValue here)
        {
            if (!needed.IsBounded || here.Meets(needed))
                return;
            Report(step, Catalogue.CallStateMismatch.Message(
                what,
                Needs(part, needed.Format(register)),
                here.IsBounded
                    ? $"{register.Name} is {here.Describe(register.Digits)} here"
                    : $"{register.Name} is not known here"));
        }

        void Width(StateRegister register, StateParts part, Width needed, Width here)
        {
            if (!IsKnown(needed) || needed == here)
                return;
            var item = ProcessorState.Format(register, needed);
            Report(step, Catalogue.CallStateMismatch.Message(
                what,
                Needs(part, item),
                IsKnown(here)
                    ? $"{register.Name} {register.Is} {Format(here)} here"
                    : $"the width of {register.Name} is not known here"),
                EnsuresBefore(step) ? Ensured(step, item, state) : null);
        }
    }

    /// <summary>
    /// Reports a diagnostic for each part of <paramref name="state"/> that is not what the routine
    /// or macro <paramref name="name"/> declares it returns with.
    /// </summary>
    /// <remarks>
    /// <paramref name="where"/> says where <paramref name="state"/> holds, as the message puts it.
    /// Only the parts of <paramref name="held"/> are checked, which for a routine are the parts its
    /// signature declares, since the others are inferred from what its returns leave.
    /// Where <paramref name="returning"/> is given, the step is that routine's return, and each
    /// mismatch offers two fixes. One sets the width the routine declares with an <c>.ensure</c>,
    /// and the other declares what the analysis finds here. Where <paramref name="whyMode"/> is
    /// given, a mode that is not known is reported with that cause.
    /// </remarks>
    public void CheckExit(
        Step step, string what, string where, ProcessorState exit, ProcessorState state, string name, Symbol? returning = null,
        StateParts held = StateParts.All, Cause? whyMode = null)
    {
        var lead = what.Length == 0 ? "" : what + " ";
        if ((held & StateParts.A) != 0)
            Part(StateRegister.A, exit.A, state.A);
        if ((held & StateParts.Index) != 0)
            Part(StateRegister.Index, exit.Index, state.Index);
        if ((held & StateParts.Mode) == 0)
        {
            // The mode is inferred from what the returns leave, so there is nothing to check.
        }
        else if (exit.E == ProcessorMode.Unchanged && state.E != ProcessorMode.Unchanged)
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
                    : $"the mode is not known {where}{Cause.Because(whyMode)}"),
                Declared(IsKnown(state.E) ? ProcessorState.Format(state.E) : null),
                null);
        }

        if ((held & StateParts.DirectPage) != 0)
            Value(StateRegister.DirectPage, exit.D, state.D);
        if ((held & StateParts.DataBank) != 0)
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
                        : $"{register.Name} is not known {where}"),
                    Declared(here.IsBounded ? here.Format(register) : null),
                    null);
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
                var item = ProcessorState.Format(register, declared);
                Report(step, Catalogue.ReturnStateMismatch.Message(
                    lead,
                    name,
                    $"with `{item}`",
                    IsKnown(here)
                        ? $"{register.Name} {register.Is} {Format(here)} {where}"
                        : $"the width of {register.Name} is not known {where}"),
                    returning is null ? null : Ensured(step, item, state),
                    Declared(IsKnown(here) ? ProcessorState.Format(register, here) : null));
            }
        }

        // The routine may instead declare what the analysis finds, where it finds one thing.
        DiagnosticFix? Declared(string? found) =>
            returning is not null && found is not null && Own(step, FixKind.Exit, found) is not null
                && returning.Tree == model.Tree
                ? new DiagnosticFix(FixKind.Exit, found, returning.DeclarationSpan)
                : null;
    }

    /// <summary>
    /// Reports a diagnostic where a return does not leave the way the routine is called, or leaves
    /// in a state other than the one the routine declares. <paramref name="whyMode"/> says why the
    /// mode is not known here, where the analysis can tell.
    /// </summary>
    public void CheckReturn(Step step, MnemonicKind mnemonic, ProcessorState state, Symbol routine, Cause? whyMode = null)
    {
        // A return inside another instruction's bytes has no mnemonic of its own to replace.
        var signature = signatureOf(routine) ?? Signature.Default;
        var written = step.Statement is InstructionStatementSyntax;
        if (mnemonic == MnemonicKind.Rts && signature.IsFar)
        {
            Report(step, Catalogue.ReturnDistanceMismatch.Message(routine.DisplayName, "far", "rtl"),
                written ? Own(step, FixKind.Return, "rtl") : null);
        }
        else if (mnemonic == MnemonicKind.Rtl && !signature.IsFar)
        {
            Report(step, Catalogue.ReturnDistanceMismatch.Message(routine.DisplayName, "near", "rts"),
                written ? Own(step, FixKind.Return, "rts") : null);
        }
        CheckExit(step, $"`{SyntaxFacts.TextOf(mnemonic)}`:", "here", signature.Exit, state, routine.DisplayName, routine,
            signature.Declared, whyMode);
    }

    /// <summary>
    /// Reports a diagnostic for a call, or a jump to a constant address, whose target is neither
    /// a routine with a signature nor a label inside one. A signature is what would say what
    /// state the target takes and what it hands back. The message names the declaration that
    /// makes the target a routine.
    /// </summary>
    public void CheckCallTarget(Step step, MnemonicKind mnemonic, Symbol? target) =>
        Report(step, target is null
            ? Catalogue.CallTargetUnknown.Message(SyntaxFacts.TextOf(mnemonic))
            : Catalogue.CallTargetNotARoutine.Message(
                target.DisplayName,
                target.Kind == SymbolKind.ImportedAddress
                    ? $"import `{target.DisplayName}` with `proc(...)`, which says what state it takes"
                    : $"declare an extern proc at its address, `.proc name = {target.DisplayName}: ...`, which says what state it takes"));

    /// <summary>
    /// Reports a diagnostic where a call is not made the way the routine is reached, or not in the
    /// state the routine expects. <paramref name="whyMode"/> says why the mode is not known here,
    /// where the analysis can tell.
    /// </summary>
    public void CheckCall(
        Step step, MnemonicKind mnemonic, Symbol target, Signature callee, ProcessorState state, Cause? whyMode = null)
    {
        if (mnemonic == MnemonicKind.Jsr && callee.IsFar)
            Report(step, Catalogue.CallDistanceMismatch.Message(
                target.DisplayName, "far", "jsl"), Mnemonic(step, "jsl"));
        else if (mnemonic == MnemonicKind.Jsl && !callee.IsFar)
            Report(step, Catalogue.CallDistanceMismatch.Message(
                target.DisplayName, "near", "jsr"), Mnemonic(step, "jsr"));
        CheckEntry(step, $"`{SyntaxFacts.TextOf(mnemonic)} {target.DisplayName}`", callee, state, target, whyMode);
    }

    /// <summary>
    /// Reports a diagnostic where a call made with <c>per</c> and a branch does not suit the routine
    /// it calls. The call is checked as <c>jsr</c> or, with a <c>phk</c> before it, as <c>jsl</c>.
    /// <paramref name="whyMode"/> says why the mode is not known here, where the analysis can tell.
    /// </summary>
    public void CheckRelativeCall(
        Step step, MnemonicKind mnemonic, RelativeCall call, Signature callee, ProcessorState state, Cause? whyMode = null)
    {
        var target = call.Routine;
        if (callee.IsFar && !call.IsFar)
            Report(step, Catalogue.RelativeCallNeedsPhk.Message(target.DisplayName), BankPush(step, call.Push, "phk"));
        else if (!callee.IsFar && call.IsFar && call.Bank is { } bank)
            Report(step, Catalogue.RelativeCallExtraPhk.Message(target.DisplayName), BankPush(step, bank, null));
        CheckEntry(step, $"`{SyntaxFacts.TextOf(mnemonic)} {target.DisplayName}`", callee, state, target, whyMode);
    }

    /// <summary>
    /// Reports a diagnostic where a jump to a routine's entry does not suit that routine. The
    /// routine returns to this routine's caller, so it has to take the state here, return the way
    /// this routine returns, and hand back what this routine promises. Where nothing returns, only
    /// the target's entry is checked. That is the case when this routine never returns or leaves by
    /// <c>rti</c>, or when the target never returns.
    /// <paramref name="via"/> is the source text that makes the jump. It is the mnemonic, or the
    /// <c>.next</c> or <c>.fallthrough</c> that says where the path goes, in which case
    /// <paramref name="mnemonic"/> is <see cref="MnemonicKind.None"/>. <paramref name="whyMode"/>
    /// says why the mode is not known here, where the analysis can tell.
    /// </summary>
    public void CheckTailCall(
        Step step, string via, MnemonicKind mnemonic, Symbol target, Signature callee, ProcessorState state, Symbol routine,
        Cause? whyMode = null)
    {
        var own = signatureOf(routine) ?? Signature.Default;
        var what = $"`{via} {target.DisplayName}`";
        var returns = !own.HasNoCaller && !callee.NeverReturns && signatures.IsExitKnown(target);

        // A long jump to a near routine is how code enters another bank. The routine's own `rts`
        // then returns within that bank, so the jump is only valid when nothing returns.
        if (mnemonic == MnemonicKind.Jml && !callee.IsFar && !callee.IsInterrupt)
        {
            if (!EntersAnotherBank(step, target))
                Report(step, Catalogue.JumpDistanceMismatch.Message(
                    target.DisplayName, "near", "jmp", target.DisplayName), Mnemonic(step, "jmp"));
            else if (returns)
            {
                Report(step, Catalogue.JumpAcrossBanks.Message(target.DisplayName, routine.DisplayName));
            }
        }
        else if (Instructions.Facts(mnemonic).Control == Control.Branches && callee.IsFar)
        {
            Report(step, Catalogue.BranchToFarRoutine.Message(target.DisplayName), Own(step, FixKind.FarBranch, OppositeOf(mnemonic)));
        }
        else if (mnemonic is not (MnemonicKind.Jml or MnemonicKind.None) && callee.IsFar)
        {
            Report(step, Catalogue.JumpDistanceMismatch.Message(target.DisplayName, "far", "jml", target.DisplayName),
                Mnemonic(step, "jml"));
        }
        CheckEntry(step, what, callee, state, target, whyMode);
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
            own.Exit, Exited(callee, state), routine.DisplayName, held: own.Declared, whyMode: whyMode);
    }

    /// <summary>
    /// Reports a diagnostic where a jump into a label inside another routine does not suit this
    /// routine. That routine returns to this routine's caller, so it has to return the way this one
    /// does and hand back what this one declares, exactly as a tail call to it does. The label's
    /// own declaration gives what the state has to be at the label, and is checked separately.
    /// Where <paramref name="whyMode"/> says why the mode is not known here, the exit check gives it
    /// as the reason.
    /// </summary>
    public void CheckJumpInto(
        Step step, string via, Symbol label, Symbol owner, ProcessorState state, Symbol routine, Cause? whyMode = null)
    {
        var callee = signatureOf(owner) ?? Signature.Default;
        var own = signatureOf(routine) ?? Signature.Default;
        if (own.HasNoCaller || callee.NeverReturns || !signatures.IsExitKnown(owner))
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
            own.Exit, Exited(callee, state), routine.DisplayName, held: own.Declared, whyMode: whyMode);
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
            if (StepOperands.Of(model, step) is { } operand && CodeLayout.ThroughDirectPage(operand))
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
    /// Reports a problem with <paramref name="step"/>'s statement, in the expansion the step
    /// belongs to, and attaches the two fixes its message allows. Either fix may be null.
    /// </summary>
    public void Report(Step step, DiagnosticMessage message, DiagnosticFix? fix, DiagnosticFix? also)
    {
        Report(step, message);
        diagnostics[^1] = (fix, also) switch
        {
            (null, null) => diagnostics[^1],
            (null, { } only) => diagnostics[^1] with { Fix = only },
            _ => diagnostics[^1] with { Fix = fix, Also = also },
        };
    }

    /// <summary>
    /// Reports a problem with <paramref name="node"/>, in the expansion that
    /// <paramref name="step"/> belongs to. A line of a macro body is wrong only for the call that
    /// expanded it, so it is reported at that call, as
    /// <see cref="Expansion.Problem(SyntaxTree, SyntaxNode, Expansion?, Severity?, DiagnosticMessage, DiagnosticFix?)"/>
    /// describes. A line a call gave as a block argument is the caller's own, and is reported where
    /// it appears.
    /// </summary>
    /// <remarks>
    /// <paramref name="fix"/> is attached only where the node is a line of this file outside
    /// every expansion, since a fix elsewhere would change a line that serves more than this one.
    /// </remarks>
    public void ReportAt(SyntaxNode node, Step step, DiagnosticMessage message, DiagnosticFix? fix = null) =>
        diagnostics.Add(Expansion.Problem(model.Tree, node, step.On, Severity.Error, message, step.On is null ? fix : null));

    /// <summary>
    /// Reports a diagnostic for <c>d:</c> on a constant address, which reaches it through the
    /// direct page, unless D is known here and the address lies in the 256 bytes starting at D.
    /// </summary>
    private void CheckThroughDirectPage(Step step, SyntaxNode expression, ProcessorState state, Cause? whyD, Symbol routine)
    {
        if (model.ValueOf(expression, step.On).AsNumber() is not { } address)
            return;
        var what = $"`d:{StateValue.Hex(address, 4)}` is reached through the direct page";
        if (state.D.Kind == StateValueKind.Unchanged)
        {
            Report(step, Catalogue.DirectPageUnknown.Message(
                what, $"{Assumes(step, routine, StateParts.DirectPage, "dp*")}, which assumes nothing about D"));
        }
        else if (!state.D.IsKnown)
        {
            Report(step, Catalogue.DirectPageUnknown.Message(
                what, Unknown(whyD)));
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
    /// Returns why D is not known, in the words a message ends with, from the cause the analysis
    /// found, if any.
    /// </summary>
    private static string Unknown(Cause? whyD) =>
        "D is not known here" + (whyD is null ? ": a `.state dp = ...` declares what it is" : Cause.Because(whyD));

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
    /// innermost macro with a signature whose body the step is part of, or else the routine. A
    /// block given to a macro is its caller's code, so that macro is passed over.
    /// </summary>
    private string Owner(Step step, Symbol routine) => OwnerOf(step, routine).Name;

    /// <summary>
    /// Returns the name and signature of whatever declares the <c>*</c> items in force at a step,
    /// as <see cref="Owner"/> finds it.
    /// </summary>
    private (string Name, Signature? Signature) OwnerOf(Step step, Symbol routine)
    {
        foreach (var level in Expansion.Enclosing(step.On))
        {
            if (level.Call is { } call && model.MacroAt(call) is { MacroSignature: { } signature } macro)
                return (macro.DisplayName + "!", signature);
        }
        return (routine.DisplayName, signatureOf(routine));
    }

    /// <summary>
    /// Returns how a message says that the <c>*</c> item <paramref name="item"/> for
    /// <paramref name="part"/> is in force at a step. It is declared where the owner's signature
    /// writes the part, and is otherwise the default the owner takes.
    /// </summary>
    private string Assumes(Step step, Symbol routine, StateParts part, string item)
    {
        var (name, signature) = OwnerOf(step, routine);
        return signature is not null && (signature.Written & part) != 0
            ? $"`{name}` declares `{item}`"
            : $"`{name}` takes the default `{item}`";
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
    private DiagnosticFix? Mnemonic(Step step, string mnemonic) => Own(step, FixKind.Mnemonic, mnemonic);

    /// <summary>
    /// Returns a value indicating whether an <c>.ensure</c> before <paramref name="step"/>'s
    /// statement sets the state for it alone. That holds for a call, a jump and a macro call. A
    /// conditional branch also falls through to the next line, which the <c>.ensure</c> would
    /// change too. A relative call's branch must follow its <c>per</c> directly.
    /// </summary>
    private static bool EnsuresBefore(Step step) => step.Statement switch
    {
        MacroCallSyntax => true,
        InstructionStatementSyntax instruction =>
            Instructions.Facts(instruction.MnemonicKind).Control is Control.Calls or Control.Jumps,
        _ => false,
    };

    /// <summary>
    /// Returns a fix that adds <c>.ensure</c> <paramref name="item"/> before the statement, where
    /// the statement is in this file and outside any expansion. An <c>.ensure</c> needs native
    /// mode, so none is offered where the processor is not known to be in it.
    /// </summary>
    private DiagnosticFix? Ensured(Step step, string item, ProcessorState state) =>
        state.E == ProcessorMode.Native ? Own(step, FixKind.Ensure, item) : null;

    /// <summary>
    /// Returns a fix of <paramref name="kind"/> with <paramref name="text"/>, where the statement
    /// is in this file and outside any expansion. A fix elsewhere would change a line that serves
    /// more than this statement.
    /// </summary>
    private DiagnosticFix? Own(Step step, FixKind kind, string? text) =>
        step.On is null && step.Statement.Tree == model.Tree ? new DiagnosticFix(kind, text) : null;

    /// <summary>
    /// Returns a fix that inserts <paramref name="text"/> before <paramref name="at"/>, or removes
    /// <paramref name="at"/> where <paramref name="text"/> is null. The relative call
    /// <paramref name="step"/> makes and <paramref name="at"/> must both be in this file and
    /// outside any expansion.
    /// </summary>
    private DiagnosticFix? BankPush(Step step, Step at, string? text) =>
        Own(step, FixKind.BankPush, text) is not null && at.On is null && at.Statement.Tree == model.Tree
            ? new DiagnosticFix(FixKind.BankPush, text, at.Statement.Tree.GetSpan(at.Statement.Span))
            : null;

    /// <summary>
    /// Returns the conditional branch that is taken exactly when <paramref name="mnemonic"/> is
    /// not, or null for a branch that is always taken.
    /// </summary>
    private static string? OppositeOf(MnemonicKind mnemonic) =>
        (Instructions.LongFormOf(mnemonic) ?? mnemonic) is var conditional
            && conditional is not (MnemonicKind.Bra or MnemonicKind.Brl)
            ? SyntaxFacts.TextOf(Instructions.FormsOf(conditional).Skipped)
            : null;
}
