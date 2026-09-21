using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// What the processor-state analysis reports, and the words its messages are written in.
/// <see cref="StateAnalysis"/> works out what each statement does to the state; this says what
/// is wrong with the state it found, at a call, a return, a jump into another routine, a
/// width-dependent immediate, and an operand that reaches memory through D or B.
/// <para>
/// Nothing is reported until the states have settled: a walk that is not the final one runs
/// the same checks and throws their messages away, so a state on its way to a fixed point is
/// never reported on.
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

    /// <summary>What the checks have found, in the order they were reported.</summary>
    public IReadOnlyList<Diagnostic> Found => diagnostics;

    /// <summary>
    /// Whether the walk is the last one, over converged states, which is the only one that
    /// reports.
    /// </summary>
    public bool Final { get; set; }

    /// <summary>A known width as a message says it.</summary>
    public static string Spell(Width width) => width == Width.Sixteen ? "16-bit" : "8-bit";

    /// <summary>The register an immediate's width comes from, as a message names it.</summary>
    public static string Spell(WidthRegister register) => register == WidthRegister.A ? "A" : "X and Y";

    /// <summary>A known mode as a message says it.</summary>
    public static string Mode(ProcessorMode mode) => mode == ProcessorMode.Native ? "native" : "emulation";

    /// <summary>Whether a width is one a message can name, rather than unknown or unchanged.</summary>
    public static bool IsKnown(Width width) => width is Width.Eight or Width.Sixteen;

    /// <summary>The same for the emulation flag.</summary>
    public static bool IsKnown(ProcessorMode mode) => mode is ProcessorMode.Native or ProcessorMode.Emulation;

    /// <summary>What a routine hands back: its exit, with the parts it declares unchanged kept from <paramref name="state"/>.</summary>
    public static ProcessorState Exited(Signature callee, ProcessorState state)
    {
        var exited = new ProcessorState(
            callee.Exit.A == Width.Unchanged ? state.A : callee.Exit.A,
            callee.Exit.Index == Width.Unchanged ? state.Index : callee.Exit.Index,
            callee.Exit.E == ProcessorMode.Unchanged ? state.E : callee.Exit.E,
            callee.Exit.D.Kind == StateValueKind.Unchanged ? state.D : callee.Exit.D,
            callee.Exit.B.Kind == StateValueKind.Unchanged ? state.B : callee.Exit.B);

        // Emulation mode pins both widths at 8, as a `.state emu` does, so a routine returning
        // in it returns with them there however its `a*` and `i*` read.
        return exited.E == ProcessorMode.Emulation
            ? exited with { A = Width.Eight, Index = Width.Eight }
            : exited;
    }

    /// <summary>The operand an instruction has on this writing of it: what a call gave, where the body names an <c>operand</c> parameter.</summary>
    public SyntaxNode? OperandOf(Step step)
    {
        var written = (step.Statement as InstructionStatementSyntax)?.Operand;
        return Operands.Substituted(model, written, step.On)?.Operand ?? written;
    }

    /// <summary>The segment a placed symbol is in, as the program's table declares it.</summary>
    public Segment? SegmentOf(Symbol symbol) => symbol.Segment is { } name ? model.Segments.Find(name) : null;

    /// <summary>The bank a segment declares it lives in, which is the program bank for code in it.</summary>
    public StateValue BankOf(string? segment) =>
        segment is not null && model.Segments.Find(segment)?.Bank is { } bank ? StateValue.Of(bank) : StateValue.Unknown;

    /// <summary>
    /// A width-dependent immediate, which ca65 sizes from the width it is told. The analysis
    /// is what tells it, so the width has to be known here.
    /// </summary>
    public void CheckImmediate(
        Step step, string mnemonic, WidthRegister register, ProcessorState state, WidthCause? why, Symbol routine)
    {
        var width = state.Of(register);
        var item = register == WidthRegister.A ? "a" : "i";
        if (width == Width.Unchanged)
        {
            Report(step, $"`{mnemonic} #` needs the width of {Spell(register)}, and `{Owner(step, routine)}` says "
                + $"`{item}*`, which assumes nothing about it", Declares(step, item, routine));
        }
        else if (!IsKnown(width))
        {
            Report(step, $"`{mnemonic} #` needs the width of {Spell(register)}, and it is not known here"
                + (why is null ? ": a `.state` says what it is" : $", because {why.Reason}: {why.Fix}"),
                Ensure(step, item));
        }
        else if (width == Width.Sixteen && state.E == ProcessorMode.Emulation)
        {
            Report(step, $"`{mnemonic} #` would be 16 bits in emulation mode, where both widths are 8");
        }
    }

    /// <summary>
    /// What memory an operand reaches through the direct page or the data bank, checked
    /// against what the segments and the project's <c>ranges</c> declare. Where either side is
    /// not declared or not known, nothing is reported: the checks are opt-in by declaration.
    /// </summary>
    public void CheckMemory(Step step, string mnemonic, AddressingMode? mode, ProcessorState state, Symbol routine)
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
                    Report(step, $"`{symbol.DisplayName}` is in \"{segment.Name}\", which is reached through the direct "
                        + $"page at {StateValue.Hex(page, 4)}, and D is {StateValue.Hex(state.D.Value, 4)} here");
                }
            }
            return;
        }

        // A near transfer stays in the program bank, so a target in a segment in another bank
        // is out of its reach.
        if (mnemonic != "per" && (chosen is AddressingMode.Relative or AddressingMode.RelativeLong
            || (chosen == AddressingMode.Absolute && mnemonic is "jmp" or "jsr")))
        {
            CheckNearBank(step, mnemonic, chosen);
            return;
        }

        // Only an absolute operand of an instruction that reads or writes data uses B: a long
        // operand names its bank, `jmp` and `jsr` use the program bank, and `pea` and `per`
        // reach no memory at all.
        if (chosen is not (AddressingMode.Absolute or AddressingMode.AbsoluteX or AddressingMode.AbsoluteY)
            || mnemonic is "jmp" or "jsr" or "pea" or "per" || !state.B.IsKnown)
        {
            return;
        }
        var bank = state.B.Value;
        foreach (var symbol in AddressSymbols.In(model, expression, step.On))
        {
            if (SegmentOf(symbol) is { Bank: not null } segment && !segment.IsSeenFrom(bank))
            {
                Report(step, $"`{symbol.DisplayName}` is in \"{segment.Name}\", which is {segment.SpellBanks()}, "
                    + $"and B is {StateValue.Hex(bank, 2)} here");
            }
        }
        if (model.ValueOf(expression, step.On).AsNumber() is { } address
            && ranges.FirstOrDefault(range => range.Covers(address)) is { } covering && !covering.Permits(bank))
        {
            Report(step, $"{StateValue.Hex(address, 4)} is reached only from banks {covering.SpellBanks()}, and B is "
                + $"{StateValue.Hex(bank, 2)} here");
        }
    }

    /// <summary>That the state here is what a routine's entry declares.</summary>
    public void CheckEntry(Step step, string what, Signature callee, ProcessorState state)
    {
        Width("a", "A", callee.Entry.A, state.A);
        Width("i", "X and Y", callee.Entry.Index, state.Index);
        if (IsKnown(callee.Entry.E) && callee.Entry.E != state.E)
        {
            Report(step, $"{what} needs `{ProcessorState.Spell(callee.Entry.E)}`, and "
                + (IsKnown(state.E) ? $"the processor is in {Mode(state.E)} here" : "the mode is not known here"));
        }
        Value("dp", "D", callee.Entry.D, state.D);
        Value("dbr", "B", callee.Entry.B, state.B);

        void Value(string item, string register, StateValue needed, StateValue here)
        {
            if (!needed.IsKnown || needed == here)
                return;
            Report(step, $"{what} needs `{needed.Spell(item)}`, and "
                + (here.IsKnown ? $"{register} is {StateValue.Hex(here.Value, register == "D" ? 4 : 2)} here" : $"{register} is not known here"));
        }

        void Width(string item, string register, Width needed, Width here)
        {
            if (!IsKnown(needed) || needed == here)
                return;
            Report(step, $"{what} needs `{ProcessorState.Spell(item, needed)}`, and "
                + (IsKnown(here) ? $"{register} {(register == "A" ? "is" : "are")} {Spell(here)} here"
                    : $"the width of {register} is not known here"));
        }
    }

    /// <summary>That <paramref name="state"/> is what the routine or macro <paramref name="name"/> declares it returns with.</summary>
    /// <remarks><paramref name="where"/> says where <paramref name="state"/> holds, as the message puts it.</remarks>
    public void CheckExit(
        Step step, string what, string where, ProcessorState exit, ProcessorState state, string name)
    {
        var lead = what.Length == 0 ? "" : what + " ";
        Part("a", "A", exit.A, state.A);
        Part("i", "X and Y", exit.Index, state.Index);
        if (exit.E == ProcessorMode.Unchanged && state.E != ProcessorMode.Unchanged)
        {
            Report(step, $"{lead}`{name}` says `e*`, so the mode must be what it was on entry, "
                + $"and {where} it may not be");
        }
        else if (IsKnown(exit.E) && exit.E != state.E)
        {
            Report(step, $"{lead}`{name}` returns in {Mode(exit.E)} mode, and "
                + (IsKnown(state.E)
                    ? $"the processor is in {Mode(state.E)} mode {where}"
                    : $"the mode is not known {where}"));
        }

        Value("dp", "D", exit.D, state.D);
        Value("dbr", "B", exit.B, state.B);

        void Value(string item, string register, StateValue declared, StateValue here)
        {
            if (declared.Kind == StateValueKind.Unchanged && here.Kind != StateValueKind.Unchanged)
            {
                Report(step, $"{lead}`{name}` says `{item}*`, so {register} must be what it was on entry, "
                    + $"and {where} it may not be");
            }
            else if (declared.IsKnown && declared != here)
            {
                Report(step, $"{lead}`{name}` returns with `{declared.Spell(item)}`, and "
                    + (here.IsKnown ? $"{register} is {StateValue.Hex(here.Value, register == "D" ? 4 : 2)} {where}" : $"{register} is not known {where}"));
            }
        }

        void Part(string item, string register, Width declared, Width here)
        {
            if (declared == Width.Unchanged && here != Width.Unchanged)
            {
                Report(step, $"{lead}`{name}` says `{item}*`, so {register} must be as wide as it "
                    + $"was on entry, and {where} it may not be");
            }
            else if (IsKnown(declared) && declared != here)
            {
                Report(step, $"{lead}`{name}` returns with `{ProcessorState.Spell(item, declared)}`, and "
                    + (IsKnown(here) ? $"{register} {(register == "A" ? "is" : "are")} {Spell(here)} {where}"
                        : $"the width of {register} is not known {where}"));
            }
        }
    }

    /// <summary>A return: it has to leave the way the routine is called, in the state it declares.</summary>
    public void CheckReturn(Step step, string mnemonic, ProcessorState state, Symbol routine)
    {
        var signature = routine.Signature ?? Signature.Default;
        if (mnemonic == "rts" && signature.IsFar)
            Report(step, $"`{routine.DisplayName}` is far, and returns with `rtl`");
        else if (mnemonic == "rtl" && !signature.IsFar)
            Report(step, $"`{routine.DisplayName}` is near, and returns with `rts`");
        CheckExit(step, $"`{mnemonic}`:", "here", signature.Exit, state, routine.DisplayName);
    }

    /// <summary>
    /// That a call names something with a signature, which is what says what state it takes and
    /// what it hands back.
    /// </summary>
    public void CheckCallTarget(Step step, string mnemonic, Symbol? target) =>
        Report(step, target is null
            ? $"`{mnemonic}` needs a routine to call: on the 65816 a call's target is a proc, an extern "
                + "proc or a `proc(...)` import, whose signature says what state it takes"
            : $"`{target.DisplayName}` is not a routine: on the 65816 a call's target is a proc, an "
                + "extern proc or a `proc(...)` import, whose signature says what state it takes");

    /// <summary>A call: it is made the way the routine is reached, in the state the routine expects.</summary>
    public void CheckCall(Step step, string mnemonic, Symbol target, Signature callee, ProcessorState state)
    {
        if (mnemonic == "jsr" && callee.IsFar)
            Report(step, $"`{target.DisplayName}` is far, and is called with `jsl`", Mnemonic(step, "jsl"));
        else if (mnemonic == "jsl" && !callee.IsFar)
            Report(step, $"`{target.DisplayName}` is near, and is called with `jsr`", Mnemonic(step, "jsr"));
        CheckEntry(step, $"`{mnemonic} {target.DisplayName}`", callee, state);
    }

    /// <summary>
    /// A call written as <c>per</c> and a branch, checked as <c>jsr</c> or, with a <c>phk</c>
    /// before it, as <c>jsl</c>.
    /// </summary>
    public void CheckRelativeCall(Step step, string mnemonic, RelativeCall call, ProcessorState state)
    {
        var target = call.Routine;
        var callee = target.Signature!;
        if (callee.IsFar && !call.IsFar)
            Report(step, $"`{target.DisplayName}` is far, and a relative call to it pushes the bank with `phk` before the `per`");
        else if (!callee.IsFar && call.IsFar)
            Report(step, $"`{target.DisplayName}` is near, and a relative call to it pushes no bank: the `phk` is one byte too many");
        CheckEntry(step, $"`{mnemonic} {target.DisplayName}`", callee, state);
    }

    /// <summary>
    /// A jump to a routine's entry. The routine returns to this routine's caller, so it has to
    /// take the state here, return the way this routine returns, and hand back what this
    /// routine promises. Where nothing returns — this routine never does, or leaves by
    /// <c>rti</c>, or the target never returns — only the target's entry is checked.
    /// </summary>
    public void CheckTailCall(
        Step step, string mnemonic, Symbol target, Signature callee, ProcessorState state, Symbol routine)
    {
        var own = routine.Signature ?? Signature.Default;
        var what = mnemonic == ".next"
            ? $"`.next {target.DisplayName}`"
            : $"`{mnemonic} {target.DisplayName}`";
        var returns = !own.HasNoCaller && !callee.NeverReturns;

        // A long jump to a near routine is how code enters another bank, which is where the
        // routine's own `rts` then stays: that is somewhere to go only when nothing returns.
        if (mnemonic == "jml" && !callee.IsFar && !callee.IsInterrupt)
        {
            if (!EntersAnotherBank(step, target))
                Report(step, $"`{target.DisplayName}` is near: a jump to it is `jmp {target.DisplayName}`");
            else if (returns)
            {
                Report(step, $"`{target.DisplayName}` is near and in another bank, and would return with `rts` in its own bank "
                    + $"to `{routine.DisplayName}`'s caller: only a routine that never returns, or an interrupt handler, "
                    + "enters another bank this way");
            }
        }
        else if (mnemonic is not ("jml" or ".next") && callee.IsFar)
        {
            Report(step, $"`{target.DisplayName}` is far: a jump to it is `jml {target.DisplayName}`");
        }
        CheckEntry(step, what, callee, state);
        if (!returns)
            return;

        if (callee.IsInterrupt)
        {
            Report(step, $"{what} is a tail call, and `{target.DisplayName}` is an interrupt handler, which leaves by `rti`: "
                + "only a routine that never returns, or another interrupt handler, may jump to one");
            return;
        }
        if (callee.IsFar != own.IsFar)
        {
            Report(step, $"{what} is a tail call, and `{target.DisplayName}` is {callee.Distance} while "
                + $"`{routine.DisplayName}` is {own.Distance}: it would return to the caller the wrong way");
        }
        CheckExit(step, $"{what} is a tail call:", $"when `{target.DisplayName}` returns",
            own.Exit, Exited(callee, state), routine.DisplayName);
    }

    /// <summary>
    /// A long transfer to a routine's address in a bank of its choosing, which has to be the bank
    /// the routine's segment lives in or one of its mirrors.
    /// </summary>
    public void CheckMirror(Step step, AddressingMode? mode)
    {
        if (Targets.MirrorOf(model, Transfers.TargetOf(step.Statement, mode), step.On) is not { } mirror
            || SegmentOf(mirror.Routine) is not { Bank: not null } segment || segment.IsSeenFrom(mirror.Bank))
        {
            return;
        }
        Report(step, $"`{mirror.Routine.DisplayName}` is in \"{segment.Name}\", {segment.SpellBanks()}, and this "
            + $"reaches it in bank {StateValue.Hex(mirror.Bank, 2)}");
    }

    /// <summary>
    /// A call to a routine that takes <c>args n</c>: the caller pushes those bytes first, so where
    /// what this routine pushed is known there have to be at least that many, beneath the
    /// <paramref name="pushed"/> bytes a relative call pushes for itself.
    /// </summary>
    public void CheckArguments(Step step, Symbol? target, AnalysisStack? stack, int pushed)
    {
        if (target?.Signature is not { Arguments: > 0 and var needed } || stack is not { IsAnchored: true } known
            || known.Depth - pushed >= needed)
        {
            return;
        }
        var have = known.Depth - pushed;
        Report(step, $"`{target.DisplayName}` takes `args {needed}`, pushed before the call, and "
            + (have == 0 ? "nothing is pushed here" : $"only {(have == 1 ? "1 byte is" : $"{have} bytes are")} pushed here"));
    }

    /// <summary>
    /// Outside any routine there is no processor state, so a directive that describes a point
    /// in one describes nothing. An instruction there has been reported already: code belongs
    /// in a proc.
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
                Report(step, $"`{directive.Text.ToLowerInvariant()}` describes a point in a routine, "
                    + "and this is outside any `.proc`");
            }
        }
    }

    /// <summary>
    /// A block no path from the routine's entry reaches, and no <c>.state</c> declares. Its
    /// immediates cannot be sized, because nothing says how wide anything is there.
    /// </summary>
    public void Unreached(BasicBlock block, FlowRegion region)
    {
        foreach (var step in block.Steps)
        {
            if (step.Statement is not InstructionStatementSyntax statement)
                continue;
            if (OperandOf(step) is { } operand && CodeLayout.ThroughDirectPage(operand))
            {
                Report(step, $"`d:` is reached through the direct page, and no path from `{region.Routine.DisplayName}`'s "
                    + "entry reaches it. A `.state` after its label declares what the state is there");
                continue;
            }
            if (layout.Of(statement, step.On)?.Mode != AddressingMode.Immediate
                || Instructions.SizedBy(statement.Mnemonic.Text) is not { } register)
            {
                continue;
            }
            Report(step, $"`{statement.Mnemonic.Text.ToLowerInvariant()} #` needs the width of "
                + $"{Spell(register)}, and no path from `{region.Routine.DisplayName}`'s entry reaches it. "
                + "A `.state` after its label declares what the state is there");
        }
    }

    /// <summary>Reports what is wrong with the statement <paramref name="step"/> is the writing of.</summary>
    public void Report(Step step, string message) => ReportAt(step.Statement, step, message);

    /// <summary>The same, with the fix its message names.</summary>
    public void Report(Step step, string message, DiagnosticFix? fix)
    {
        var count = diagnostics.Count;
        Report(step, message);
        if (fix is not null && diagnostics.Count > count)
            diagnostics[^1] = diagnostics[^1] with { Fix = fix };
    }

    /// <summary>
    /// Reports what is wrong with <paramref name="node"/> on the writing <paramref name="step"/>
    /// is. A line of a macro body is wrong only for the call that expanded it, so it is
    /// reported at that call, which is the side that can change, with the body line named
    /// beside it. A line a call gave as a block argument is the caller's own, and is reported
    /// where it stands.
    /// </summary>
    public void ReportAt(SyntaxNode node, Step step, string message)
    {
        if (!Final)
            return;

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
    /// <c>jsr</c>, <c>jmp</c> or a branch to a label or a routine whose segment declares a bank
    /// other than the one the code around it declares. Both sides have to declare a bank.
    /// </summary>
    private void CheckNearBank(Step step, string mnemonic, AddressingMode mode)
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
            "jsr" => "`jsl` reaches it",
            "jmp" or "bra" or "brl" => "`jml` reaches it",
            _ => "no branch leaves the bank: branching the other way over a `jml` to it does",
        };
        Report(step, $"`{mnemonic}` stays in bank {StateValue.Hex(here.Value, 2)}, and `{target.DisplayName}` is in "
            + $"\"{segment.Name}\", in bank {StateValue.Hex(there, 2)}: {reaches}");
    }

    /// <summary>
    /// <c>d:</c> on a constant address, which reaches it through the direct page: D has to be
    /// known here, and the address in the page it starts.
    /// </summary>
    private void CheckThroughDirectPage(Step step, SyntaxNode expression, ProcessorState state, Symbol routine)
    {
        if (model.ValueOf(expression, step.On).AsNumber() is not { } address)
            return;
        var what = $"`d:{StateValue.Hex(address, 4)}` is reached through the direct page";
        if (state.D.Kind == StateValueKind.Unchanged)
        {
            Report(step, $"{what}, and `{Owner(step, routine)}` says `dp*`, which assumes nothing about D");
        }
        else if (!state.D.IsKnown)
        {
            Report(step, $"{what}, and D is not known here: a `.state dp = ...` says what it is");
        }
        else if (address < state.D.Value || address > state.D.Value + 0xff)
        {
            Report(step, $"{what} at {StateValue.Hex(state.D.Value, 4)}, which reaches only "
                + $"{StateValue.Hex(state.D.Value, 4)} to {StateValue.Hex(state.D.Value + 0xff, 4)}");
        }
    }

    /// <summary>
    /// Whether a long jump lands in a bank other than the one the code making it is taken to run
    /// in: the routine's home bank, or the mirror bank its address is written in. Both have to
    /// be declared.
    /// </summary>
    private bool EntersAnotherBank(Step step, Symbol target)
    {
        var mode = layout.Of(step.Statement, step.On)?.Mode;
        var landing = Targets.MirrorOf(model, Transfers.TargetOf(step.Statement, mode), step.On)?.Bank
            ?? SegmentOf(target)?.Bank;
        return BankOf(step.Segment) is { IsKnown: true } here && landing is { } there && there != here.Value;
    }

    /// <summary>
    /// What says a <c>*</c> item holds at a step: the innermost macro with a signature it was
    /// expanded from, or the routine.
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
    /// An <c>.ensure</c> of <paramref name="item"/>'s width before the statement, where it is
    /// written in this file. Which width it is is the programmer's to say, so the fix names the
    /// register and an editor offers both.
    /// </summary>
    private DiagnosticFix? Ensure(Step step, string item) =>
        step.On is null && step.Statement.Tree == model.Tree ? new DiagnosticFix(FixKind.Width, item) : null;

    /// <summary>
    /// The width written into the routine's signature, which is where a routine that assumes
    /// nothing about it says what it assumes. The routine has to be one this file declares,
    /// because its signature is what a caller anywhere reads.
    /// </summary>
    private DiagnosticFix? Declares(Step step, string item, Symbol routine) =>
        step.On is null && step.Statement.Tree == model.Tree && routine.Tree == model.Tree
            ? new DiagnosticFix(FixKind.Signature, item, routine.DeclarationSpan)
            : Ensure(step, item);

    /// <summary>The statement's mnemonic written as <paramref name="mnemonic"/>, where it is written in this file.</summary>
    private DiagnosticFix? Mnemonic(Step step, string mnemonic) =>
        step.On is null && step.Statement.Tree == model.Tree ? new DiagnosticFix(FixKind.Mnemonic, mnemonic) : null;
}
