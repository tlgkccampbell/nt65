using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What a call's arguments are checked for once constants have values and the call is being
/// laid out where it stands: that a <c>const</c> with a range is in it, that an enum kind is
/// given a member of its enum, and that an <c>operand</c> that lists its modes is given one of
/// them. The rest of a call is checked when names are resolved (<see cref="MacroInvocation"/>),
/// and a macro's header where the macro is declared (<see cref="CheckHeader"/>).
/// </summary>
public static class ArgumentChecks
{
    /// <summary>
    /// Checks the arguments <paramref name="call"/> writes, read where the caller stands at
    /// <paramref name="caller"/>, in <paramref name="segment"/>.
    /// </summary>
    /// <returns>Whether every argument passed, which is when the body is worth laying out.</returns>
    public static bool Check(
        SemanticModel model, MacroCallSyntax call, Expansion? caller, string? segment,
        Action<SyntaxNode, DiagnosticMessage> report)
    {
        if (model.InvocationAt(call) is not { } invocation)
            return true;
        var passed = true;
        foreach (var argument in invocation.Arguments)
        {
            if (!argument.Written)
                continue;
            var accepts = argument.Parameter.Accepts;
            var name = argument.Parameter.Name;
            if (accepts.Kind == ParameterKind.List && accepts.Element is { } element)
            {
                foreach (var item in argument.Items)
                    One(name, element, item, item is BracedOperandSyntax braced ? braced.Operand : item);
            }
            else if (argument.Value is { } value)
            {
                One(name, accepts, value, argument.Operand);
            }
        }
        return passed;

        void One(string name, ArgumentKind accepts, SyntaxNode value, SyntaxNode? operand)
        {
            switch (accepts.Kind)
            {
                case ParameterKind.Const when Range(model, accepts) is { } range:
                    var given = model.ValueOf(value, caller).AsNumber();
                    if (given is { } number && number >= range.Low && number <= range.High)
                        break;
                    passed = false;
                    report(value, Catalogue.ConstArgumentOutOfRange.Says(
                        name, accepts.Low!.GetText().Trim(), accepts.High!.GetText().Trim(),
                        given is { } wrong ? $"this is {wrong}" : "this is not a constant"));
                    break;

                case ParameterKind.Enum when model.EnumOf(accepts) is { } named:
                    if (model.MemberOf(accepts, value, caller) is null)
                    {
                        passed = false;
                        report(value, Catalogue.EnumArgumentNotAMember.Says(
                            name, named.Name, $"`{value.GetText().Trim()}` is not one"));
                    }
                    break;

                case ParameterKind.Operand when accepts.Words.Count > 0 && operand is not null:
                    if (ModeOf(model, operand, caller, segment) is { } mode
                        && !accepts.Words.Contains(mode.Mode) && (mode.Direct is not { } direct || !accepts.Words.Contains(direct)))
                    {
                        passed = false;
                        report(value, Catalogue.OperandArgumentMode.Says(
                            name, Spell(accepts.Words), mode.Direct ?? mode.Mode));
                    }
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>
    /// What a macro's header is checked for once constants have values: each <c>const</c>
    /// range is two constants with the lower first, each enum kind names an enum, and each mode
    /// an <c>operand</c> lists is one it can take.
    /// </summary>
    public static void CheckHeader(
        Symbol macro, Func<ExpressionSyntax, long?> valueOf, Func<NameExpressionSyntax, Symbol?> symbolOf,
        Action<TextSpan, DiagnosticMessage> report)
    {
        foreach (var parameter in macro.Parameters)
            Kind(parameter.Accepts);

        void Kind(ArgumentKind accepts)
        {
            switch (accepts.Kind)
            {
                case ParameterKind.Const when accepts is { Low: { } low, High: { } high }:
                    if (valueOf(low) is not { } least || valueOf(high) is not { } most || least > most)
                        report(low.Parent!.Span, Catalogue.ParameterRangeInvalid.Says(accepts.ToString()));
                    break;
                case ParameterKind.Enum when accepts.Enum is { } name:
                    if (symbolOf(name) is { Kind: not SymbolKind.Enum } other)
                        report(name.Span, Catalogue.ParameterKindNotAnEnum.Says(name.GetText().Trim(), other.KindPhrase));
                    break;
                case ParameterKind.Operand:
                    foreach (var word in accepts.Words.Where(word => !ArgumentKind.OperandModes.Contains(word)))
                    {
                        var written = macro.Definition is BlockSyntax { Opener.Statement: MacroDeclarationSyntax declaration }
                            ? declaration.DescendantNodes().OfType<IdentifierNameSyntax>()
                                .FirstOrDefault(node => node.Parent is ParameterKindSyntax
                                    && node.Name.Text.Equals(word, StringComparison.OrdinalIgnoreCase))
                            : null;
                        if (written is not null)
                            report(written.Span, Catalogue.OperandModeUnknown.Says(word, Spell(ArgumentKind.OperandModes)));
                    }
                    break;
                case ParameterKind.List when accepts.Element is { } element:
                    Kind(element);
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// What a macro's conditions are checked for once names are resolved: that each word one
    /// compares with what a parameter stands for is one the parameter may be. A comparison with a
    /// word it never is holds for no argument at all, which is true of the definition, not of a call.
    /// </summary>
    public static void CheckComparisons(
        Symbol macro, Func<NameExpressionSyntax, Symbol?> symbolOf, Action<TextSpan, DiagnosticMessage> report)
    {
        if (macro.Definition is not { } definition)
            return;
        foreach (var compared in ComparedWord.In(definition, symbolOf).Where(compared => !compared.CanHold))
        {
            var comparison = compared.Word.Parent!.AncestorsAndSelf().OfType<BinaryExpressionSyntax>().First();
            var why = compared.IsMode && !ComparedWord.Modes.Contains(compared.Word.Text.ToLowerInvariant())
                ? $"`.mode` gives {Spell(ComparedWord.Modes)}"
                : compared.IsMode
                    ? $"`{compared.Name}` takes an operand in {Spell(compared.Choices)}"
                    : $"`{compared.Name}` is {Spell(compared.Choices)}";
            report(compared.Word.Span, Catalogue.ComparisonNeverHolds.Says(
                compared.Compared, compared.Word.Text,
                comparison.OperatorToken.Kind == SyntaxKind.EqualsEquals ? "never holds" : "always holds", why));
        }
    }

    /// <summary>The range a <c>const(low..high)</c> takes, or null when it names none or the header is wrong.</summary>
    private static (long Low, long High)? Range(SemanticModel model, ArgumentKind accepts) =>
        accepts is { Low: { } low, High: { } high }
        && model.ValueOf(low).AsNumber() is { } least && model.ValueOf(high).AsNumber() is { } most && least <= most
            ? (least, most)
            : null;

    /// <summary>
    /// The mode an operand argument is in, as <c>.mode</c> spells it, and for a plain address
    /// that is a direct-page one, the <c>zp</c> word that says so as well. An argument that
    /// passes on another <c>operand</c> parameter is in the mode that one was given.
    /// </summary>
    private static (string Mode, string? Direct)? ModeOf(SemanticModel model, SyntaxNode operand, Expansion? at, string? segment)
    {
        for (var steps = 0; steps < 64; steps++)
        {
            var named = operand switch
            {
                NameExpressionSyntax name => name,
                AbsoluteOperandSyntax { Prefix: null, Second: null, IndexRegister: null, Address: NameExpressionSyntax address } => address,
                _ => null,
            };
            if (named is null || model.SymbolOf(named) is not { Kind: SymbolKind.MacroParameter, Parameter: { Kind: ParameterKind.Operand } passed }
                || model.GivenAt(passed.Symbol, at) is not { Argument.Operand: { } given } found)
            {
                break;
            }
            operand = given;
            at = found.Caller;
        }

        var mode = Operands.ModeOf(operand);
        if (mode is not ("abs" or "absx" or "absy"))
            return (mode, null);
        var direct = "zp" + mode[3..];
        if (Operands.WrittenPrefix(operand) is { } written)
            return written == AddressSize.ZeroPage ? (mode, direct) : (mode, null);
        var expression = operand is AbsoluteOperandSyntax absolute ? absolute.Address : operand;
        return model.AddressSizeOf(expression, segment, at) == AddressSize.ZeroPage ? (mode, direct) : (mode, null);
    }

    /// <summary>Words as a message lists them: <c>`imm`</c>, or <c>one of `imm`, `zp`, `abs`</c>.</summary>
    private static string Spell(IReadOnlyList<string> words) =>
        words.Count == 1 ? $"`{words[0]}`" : "one of " + string.Join(", ", words.Select(word => $"`{word}`"));
}
