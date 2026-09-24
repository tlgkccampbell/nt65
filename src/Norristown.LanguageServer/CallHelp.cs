using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Provides signature help for the call the caret is in. The call may be to a macro, whose
/// parameters are shown as its declaration gives them, to a function, or to a built-in such as
/// <c>.select</c> or <c>.strsub</c>. The parameter whose argument contains the caret is marked
/// active.
/// </summary>
internal static class CallHelp
{
    // The built-in functions that get signature help, with their parameter names and a
    // description of what each returns. A parameter named `...` stands for any number of
    // further arguments.
    private static readonly Dictionary<string, (string[] Parameters, string Documentation)> builtins =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".select"] = (["condition", "chosen", "otherwise"],
                "the second argument when the condition holds, and the third when it does not"),
            [".strlen"] = (["text"], "how many bytes the text is"),
            [".strat"] = (["text", "index"], "the byte of the text at the index, counting from 0"),
            [".strsub"] = (["text", "start", "count"], "`count` bytes of the text from `start`, counting from 0"),
            [".strcat"] = (["part", "..."], "the parts joined into one text: a text's bytes, and a number as one byte, 0 to 255"),
        };

    /// <summary>
    /// Returns signature help for the call at <paramref name="position"/>, or null when the caret
    /// is not in a call.
    /// </summary>
    public static Protocol.SignatureHelp? At(ProgramModel program, SemanticModel model, int position)
    {
        var line = LineContext.At(model.Tree, position);
        var before = line.Before;

        // The innermost open parenthesis is the call being written. When it is not a call nt65
        // can describe, such as grouping parentheses in an argument, look outward to the
        // enclosing call.
        for (var end = before.Count; ;)
        {
            if (line.OpenCall(end) is not { } call)
                return null;
            var argument = call.Argument;
            var open = call.Open;
            if (open >= 1 && before[open - 1] is { Kind: SyntaxKind.Directive } directive
                && builtins.TryGetValue(directive.Text, out var builtin))
            {
                var parameters = builtin.Parameters;
                var last = parameters[^1] == "..." ? parameters.Length - 2 : parameters.Length - 1;
                return Help($"{directive.Text.ToLowerInvariant()}(", parameters, ")", builtin.Documentation,
                    Math.Min(argument, last));
            }
            if (open >= 2 && before[open - 1].Kind == SyntaxKind.Bang
                && Completion.Callee(model, line, open - 1) is { Kind: SymbolKind.Macro } macro)
            {
                return ForMacro(macro, before, open, end, argument);
            }
            if (open >= 1 && Completion.Callee(model, line, open) is { Kind: SymbolKind.Func } function)
            {
                return Help($"{function.Name}(", [.. function.ParameterSymbols.Select(parameter => parameter.Name)], ")",
                    function.Items is [{ } body] ? $"`= {body.GetText().Trim()}`" : null,
                    Math.Min(argument, Math.Max(0, function.ParameterSymbols.Count - 1)));
            }
            end = open;
        }
    }

    /// <summary>
    /// Returns the parameter of <paramref name="macro"/> that the caret's argument is for. An
    /// argument of the form <c>name = value</c> is for the parameter it names, and any other
    /// argument is for the parameter at its position. A <c>block</c> parameter takes the block
    /// after the parentheses, not a position inside them.
    /// </summary>
    /// <param name="macro">The macro called.</param>
    /// <param name="before">The tokens of the line before the caret.</param>
    /// <param name="open">The index of the call's <c>(</c> among those tokens.</param>
    /// <param name="end">The index where the argument containing the caret ends, exclusive.</param>
    /// <param name="argument">The number of arguments before the one containing the caret.</param>
    public static MacroParameter? ParameterAt(
        Symbol macro, IReadOnlyList<(SyntaxKind Kind, string Text, int Start)> before, int open, int end, int argument) =>
        Active(macro, before, open, end, argument) is var active && active >= 0 && active < macro.Parameters.Count
            ? macro.Parameters[active]
            : null;

    /// <summary>
    /// Returns signature help that shows a macro's parameters as its declaration gives them and
    /// marks the one the caret's argument is for.
    /// </summary>
    private static Protocol.SignatureHelp ForMacro(
        Symbol macro, IReadOnlyList<(SyntaxKind Kind, string Text, int Start)> before, int open, int end, int argument)
    {
        var declared = macro.Definition is BlockSyntax definition
            ? ((definition.Opener.Statement as MacroDeclarationSyntax)?.Parameters?.Parameters ?? [])
                .Select(parameter => parameter.GetText().Trim()).ToList()
            : [.. macro.Parameters.Select(parameter => parameter.Name)];
        return Help(
            $"{macro.Name}!(", declared, ")", macro.KindText, Math.Max(0, Active(macro, before, open, end, argument)));
    }

    /// <summary>
    /// Returns the index among <paramref name="macro"/>'s parameters of the one the caret's
    /// argument is for, or -1 if there is none.
    /// </summary>
    private static int Active(
        Symbol macro, IReadOnlyList<(SyntaxKind Kind, string Text, int Start)> before, int open, int end, int argument)
    {
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
        if (start + 1 < end && before[start + 1].Kind == SyntaxKind.Equals)
            return macro.Parameters.ToList().FindIndex(parameter => parameter.Name == before[start].Text);
        var inParentheses = macro.Parameters.Select((parameter, index) => (parameter, index))
            .Where(pair => !pair.parameter.IsBlock).Select(pair => pair.index).ToList();
        return argument < inParentheses.Count ? inParentheses[argument] : inParentheses.Count > 0 ? inParentheses[^1] : -1;
    }

    /// <summary>
    /// Builds signature help whose label is <paramref name="opening"/>, then the parameters
    /// separated by <c>, </c>, then <paramref name="closing"/>.
    /// </summary>
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
