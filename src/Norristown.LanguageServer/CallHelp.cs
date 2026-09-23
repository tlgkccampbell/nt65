using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Signature help for the call the caret is in: a macro's parameters as its declaration writes
/// them, a function's, or the parameters of a built-in such as <c>.select</c> or <c>.strsub</c>,
/// with the parameter whose argument the caret is in marked active.
/// </summary>
internal static class CallHelp
{
    // The built-in functions that get signature help, with their parameter names and a
    // description of what each returns. A parameter written `...` stands for any number of
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

    /// <summary>The call at <paramref name="position"/>, or null when the caret is in none.</summary>
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
    /// The parameter of <paramref name="macro"/> that the caret's argument is for: the one it
    /// names when written as <c>name = value</c>, and otherwise the one at its position. A
    /// <c>block</c> parameter takes the block after the parentheses, not a place in them.
    /// </summary>
    /// <param name="macro">The macro called.</param>
    /// <param name="before">The tokens of the line before the caret.</param>
    /// <param name="open">Where among them the call's <c>(</c> is.</param>
    /// <param name="end">Where the argument the caret is in ends, exclusive.</param>
    /// <param name="argument">How many arguments come before it.</param>
    public static MacroParameter? ParameterAt(
        Symbol macro, IReadOnlyList<(SyntaxKind Kind, string Text, int Start)> before, int open, int end, int argument) =>
        Active(macro, before, open, end, argument) is var active && active >= 0 && active < macro.Parameters.Count
            ? macro.Parameters[active]
            : null;

    /// <summary>A macro's parameters as it writes them, with the one the caret's argument is for.</summary>
    private static Protocol.SignatureHelp ForMacro(
        Symbol macro, IReadOnlyList<(SyntaxKind Kind, string Text, int Start)> before, int open, int end, int argument)
    {
        var written = macro.Definition is BlockSyntax definition
            ? ((definition.Opener.Statement as MacroDeclarationSyntax)?.Parameters?.Parameters ?? [])
                .Select(parameter => parameter.GetText().Trim()).ToList()
            : [.. macro.Parameters.Select(parameter => parameter.Name)];
        return Help($"{macro.Name}!(", written, ")", macro.KindText, Math.Max(0, Active(macro, before, open, end, argument)));
    }

    /// <summary>The index among <paramref name="macro"/>'s parameters of the one the caret's argument is for; -1 for none.</summary>
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
