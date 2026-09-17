using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// What the call the caret is in takes: a macro's parameters as its declaration writes them, a
/// function's, or the three parts of a <c>.select</c>, with the one the caret is in the argument for.
/// </summary>
internal static class CallHelp
{
    /// <summary>The call at <paramref name="position"/>, or null when the caret is in none.</summary>
    public static Protocol.SignatureHelp? At(ProgramModel program, SemanticModel model, int position)
    {
        var line = LineContext.At(model.Tree, position);
        var scope = model.ScopeAt(position);
        var before = line.Before;

        // The innermost call is the one being written; one that is not a call nt65 can describe
        // is looked through to the call around it, so an argument's parentheses do not hide it.
        for (var end = before.Count; ;)
        {
            if (line.OpenCall(end) is not { } call)
                return null;
            var argument = call.Argument;
            var open = call.Open;
            if (open >= 1 && before[open - 1] is { Kind: SyntaxKind.Directive } directive
                && directive.Text.Equals(".select", StringComparison.OrdinalIgnoreCase))
            {
                return Help(".select(", ["condition", "chosen", "otherwise"], ")",
                    "the second argument when the condition holds, and the third when it does not", Math.Min(argument, 2));
            }
            if (open >= 2 && before[open - 1].Kind == SyntaxKind.Bang
                && Completion.Callee(program, model, scope, line, open - 1) is { Kind: SymbolKind.Macro } macro)
            {
                return ForMacro(macro, before, open, end, argument);
            }
            if (open >= 1 && Completion.Callee(program, model, scope, line, open) is { Kind: SymbolKind.Func } function)
            {
                return Help($"{function.Name}(", [.. function.ParameterSymbols.Select(parameter => parameter.Name)], ")",
                    function.Items is [{ } body] ? $"`= {body.GetText().Trim()}`" : null,
                    Math.Min(argument, Math.Max(0, function.ParameterSymbols.Count - 1)));
            }
            end = open;
        }
    }

    /// <summary>
    /// A macro's parameters as it writes them. An argument written <c>name = value</c> is for the
    /// parameter it names, and one written in order for the parameter in that place; a
    /// <c>block</c> parameter takes the block after the parentheses, not a place in them.
    /// </summary>
    private static Protocol.SignatureHelp ForMacro(
        Symbol macro, IReadOnlyList<(SyntaxKind Kind, string Text, int Start)> before, int open, int end, int argument)
    {
        var written = macro.Definition?.ChildNodes.FirstOrDefault()?.Statement is { } opener
            ? Macros.ParametersOf(opener).Select(parameter => parameter.GetText().Trim()).ToList()
            : [.. macro.Parameters.Select(parameter => parameter.Name)];

        // The argument the caret is in starts after the last comma at this depth.
        var start = end;
        var depth = 0;
        for (var i = end - 1; i > open; i--)
        {
            depth += before[i].Kind switch
            {
                SyntaxKind.CloseParen or SyntaxKind.CloseBrace or SyntaxKind.CloseBracket => 1,
                SyntaxKind.OpenParen or SyntaxKind.OpenBrace or SyntaxKind.OpenBracket => -1,
                _ => 0,
            };
            if (depth == 0 && before[i].Kind == SyntaxKind.Comma)
                break;
            start = i;
        }
        int active;
        if (start + 1 < end && before[start + 1].Kind == SyntaxKind.Equals)
        {
            active = macro.Parameters.ToList().FindIndex(parameter => parameter.Name == before[start].Text);
        }
        else
        {
            var inParentheses = macro.Parameters.Select((parameter, index) => (parameter, index))
                .Where(pair => !pair.parameter.IsBlock).Select(pair => pair.index).ToList();
            active = argument < inParentheses.Count ? inParentheses[argument] : inParentheses.Count > 0 ? inParentheses[^1] : 0;
        }
        return Help($"{macro.Name}!(", written, ")", macro.KindText, Math.Max(0, active));
    }

    /// <summary>A signature written as <paramref name="opening"/>, the parameters with <c>, </c> between them, and <paramref name="closing"/>.</summary>
    private static Protocol.SignatureHelp Help(
        string opening, IReadOnlyList<string> parameters, string closing, string? documentation, int active)
    {
        var label = new System.Text.StringBuilder(opening);
        var offsets = new List<Protocol.ParameterInformation>();
        foreach (var parameter in parameters)
        {
            if (offsets.Count > 0)
                label.Append(", ");
            offsets.Add(new Protocol.ParameterInformation([label.Length, label.Length + parameter.Length], null));
            label.Append(parameter);
        }
        label.Append(closing);
        var signature = new Protocol.SignatureInformation(
            label.ToString(), documentation is null ? null : Protocol.MarkupContent.Markdown(documentation), offsets);
        return new Protocol.SignatureHelp([signature], 0, active);
    }
}
