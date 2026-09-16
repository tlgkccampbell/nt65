using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What a macro's syntax says: the pieces of a definition and of a call, and the items a
/// body may not hold. Reading them is shared, because binding, expansion and the editor
/// all ask the same questions of the same lines.
/// </summary>
public static class Macros
{
    /// <summary>The parameters written on a <c>.macro</c> opener, in order.</summary>
    public static IReadOnlyList<SyntaxNode> ParametersOf(SyntaxNode opener) =>
        [.. opener.ChildNodes
            .FirstOrDefault(child => child.Kind == SyntaxKind.MacroParameterList)?.ChildNodes
            .Where(child => child.Kind == SyntaxKind.MacroParameter) ?? []];

    /// <summary>The name of one parameter, which is the first thing written in it.</summary>
    public static SyntaxToken? NameOf(SyntaxNode parameter)
    {
        foreach (var token in parameter.ChildTokens)
        {
            if (token.Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic)
                return token;
        }
        return null;
    }

    /// <summary>What a call that leaves the parameter out gets, or null when it has no default.</summary>
    public static SyntaxNode? DefaultOf(SyntaxNode parameter) =>
        parameter.ChildNodes.FirstOrDefault(child =>
            child.Kind is not (SyntaxKind.ParameterKind or SyntaxKind.EmptyBlock));

    /// <summary>One parameter as the analysis reads it, given the symbol its name declares.</summary>
    public static MacroParameter Describe(SyntaxNode parameter, Symbol symbol) =>
        new(symbol,
            ArgumentKind.Read(parameter.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.ParameterKind)),
            DefaultOf(parameter),
            parameter.ChildNodes.Any(child => child.Kind == SyntaxKind.EmptyBlock));

    /// <summary>The macro a call names.</summary>
    public static SyntaxToken? CalleeOf(SyntaxNode call) =>
        call.ChildTokens.Length > 0 && call.ChildTokens[0].Kind == SyntaxKind.Identifier
            ? call.ChildTokens[0]
            : null;

    /// <summary>The arguments written in a call's parentheses, in order.</summary>
    public static IReadOnlyList<SyntaxNode> ArgumentsOf(SyntaxNode call) =>
        [.. call.ChildNodes
            .FirstOrDefault(child => child.Kind == SyntaxKind.ArgumentList)?.ChildNodes ?? []];

    /// <summary>The call a line holds, whether it stands alone or follows a label.</summary>
    public static SyntaxNode? CallIn(SyntaxNode? statement) => statement switch
    {
        { Kind: SyntaxKind.MacroCall } => statement,
        { Kind: SyntaxKind.LabeledLine } =>
            statement.ChildNodes.FirstOrDefault(child => child.Kind == SyntaxKind.MacroCall),
        _ => null,
    };

    /// <summary>
    /// Why a macro body may not hold this statement, or null when it may. Each of these
    /// would either declare a name in the caller or make something program-wide depend on
    /// how many times the macro is called (§11.3).
    /// </summary>
    public static string? Forbidden(SyntaxNode statement) => statement.Kind switch
    {
        SyntaxKind.ExportDirective =>
            "`.export` belongs outside a macro body: other files resolve names through the "
            + "export map, and a body cannot add to it",
        SyntaxKind.ImportDirective =>
            "`.import` belongs outside a macro body: a body resolves names where the macro is "
            + "declared, and the output imports whatever an expansion uses",
        SyntaxKind.CpuDirective => "`.cpu` belongs outside a macro body: the CPU is program-wide",
        SyntaxKind.SegmentDeclaration =>
            "a segment declaration belongs outside a macro body: a segment is declared exactly "
            + "once for the program, and this one would be declared once per call",
        SyntaxKind.ProcDeclaration or SyntaxKind.ExternProcDeclaration =>
            "`.proc` belongs outside a macro body: a routine's name and signature are part of "
            + "the file's interface. Take a `block` parameter and let the caller declare the routine",
        SyntaxKind.MacroDeclaration =>
            "`.macro` belongs outside a macro body: a definition there could capture the "
            + "enclosing macro's parameters",
        SyntaxKind.FuncDeclaration =>
            "`.func` belongs outside a macro body: a definition there could capture the "
            + "enclosing macro's parameters",
        _ => null,
    };
}
