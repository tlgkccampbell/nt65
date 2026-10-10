using System.Text;
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
    /// <summary>
    /// The built-in functions that get signature help, with their parameter names and a
    /// description of what each returns. A parameter named <c>...</c> stands for any number of
    /// further arguments, and comes last for a built-in that sets no most arguments.
    /// </summary>
    private static readonly Dictionary<BuiltinKind, (string[] Parameters, string Documentation)> Builtins =
        new()
        {
            [BuiltinKind.Select] = (["condition", "chosen", "otherwise"],
                "the second argument when the condition holds, and the third when it does not"),
            [BuiltinKind.Switch] = (["value", "set", "result", "..."],
                "the result after the first set that holds the value, or a last result when no set does"),
            [BuiltinKind.Strlen] = (["text"], "how many bytes the text is"),
            [BuiltinKind.Strat] = (["text", "index"], "the byte of the text at the index, counting from 0"),
            [BuiltinKind.Strsub] = (["text", "start", "count"], "`count` bytes of the text from `start`, counting from 0"),
            [BuiltinKind.Strcat] = (["part", "..."], "the parts joined into one text: a text's bytes, and a number as one byte, 0 to 255"),
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
                && SyntaxFacts.BuiltinKindOf(directive.Text) is var kind
                && Builtins.TryGetValue(kind, out var builtin))
            {
                var parameters = builtin.Parameters;
                // A built-in that takes any number of arguments marks the last one it names as
                // active for every argument from there on.
                var last = SyntaxFacts.Builtin(kind).MaxArguments is { } most ? most - 1 : parameters.Length - 2;
                var active = Math.Min(argument, last);

                // The arms of a `.switch` alternate between a set and a result.
                if (kind == BuiltinKind.Switch && argument > 0)
                    active = argument % 2 == 1 ? 1 : 2;
                return Help($"{SyntaxFacts.TextOf(kind)}(", parameters, ")", builtin.Documentation, active);
            }
            if (open >= 2 && before[open - 1].Kind == SyntaxKind.Bang
                && Completion.Callee(model, line, open - 1) is { Kind: SymbolKind.Macro } macro)
            {
                return ForMacro(macro, before, open, end, argument);
            }
            if (open >= 1 && Completion.Callee(model, line, open) is { Kind: SymbolKind.Func } function)
                return ForFunction(function, before, open, end, argument);
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
        ActiveIn(macro, before, open, end, argument) is var active && active >= 0 && active < macro.Parameters.Count
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
                .Select(parameter => parameter.GetTextOnOneLine()).ToList()
            : [.. macro.Parameters.Select(parameter => parameter.Name)];
        return Help(
            $"{macro.Name}!(", declared, ")", macro.KindText, Math.Max(0, ActiveIn(macro, before, open, end, argument)));
    }

    /// <summary>
    /// Returns signature help that shows a function's parameters, each with its default, and
    /// marks the one the caret's argument is for, which a named argument names.
    /// </summary>
    private static Protocol.SignatureHelp ForFunction(
        Symbol function, IReadOnlyList<(SyntaxKind Kind, string Text, int Start)> before, int open, int end, int argument)
    {
        var parameters = function.ParameterSymbols;
        var declared = parameters
            .Select(parameter => parameter.Default is { } given ? $"{parameter.Name} = {given.GetTextOnOneLine()}" : parameter.Name)
            .ToList();
        var active = Active(
            [.. parameters.Select(parameter => parameter.Name)], [.. Enumerable.Range(0, parameters.Count)],
            before, open, end, argument);
        return Help($"{function.Name}(", declared, ")",
            function.Items is [{ } body] ? $"`= {body.GetText().Trim()}`" : null, Math.Max(0, active));
    }

    /// <summary>
    /// Returns the index among <paramref name="macro"/>'s parameters of the one the caret's
    /// argument is for, or -1 if there is none. A <c>block</c> parameter takes no position.
    /// </summary>
    private static int ActiveIn(
        Symbol macro, IReadOnlyList<(SyntaxKind Kind, string Text, int Start)> before, int open, int end, int argument) =>
        Active(
            [.. macro.Parameters.Select(parameter => parameter.Name)],
            [.. macro.Parameters.Select((parameter, index) => (parameter, index))
                .Where(pair => !pair.parameter.IsBlock).Select(pair => pair.index)],
            before, open, end, argument);

    /// <summary>
    /// Returns the index among <paramref name="names"/> of the parameter the caret's argument is
    /// for, or -1 if there is none. An argument of the form <c>name = value</c> is for the
    /// parameter it names, and any other argument is for the parameter at its position.
    /// </summary>
    /// <param name="names">The parameters' names, in the order they are declared.</param>
    /// <param name="positional">The indexes of the parameters an argument may give by position.</param>
    /// <param name="before">The tokens of the line before the caret.</param>
    /// <param name="open">The index of the call's <c>(</c> among those tokens.</param>
    /// <param name="end">The index where the argument containing the caret ends, exclusive.</param>
    /// <param name="argument">The number of arguments before the one containing the caret.</param>
    private static int Active(
        IReadOnlyList<string> names, IReadOnlyList<int> positional,
        IReadOnlyList<(SyntaxKind Kind, string Text, int Start)> before, int open, int end, int argument)
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
            return names.ToList().IndexOf(before[start].Text);
        return argument < positional.Count ? positional[argument] : positional.Count > 0 ? positional[^1] : -1;
    }

    /// <summary>
    /// Builds signature help whose label is <paramref name="opening"/>, then the parameters
    /// separated by <c>, </c>, then <paramref name="closing"/>.
    /// </summary>
    private static Protocol.SignatureHelp Help(
        string opening, IReadOnlyList<string> parameters, string closing, string? documentation, int active)
    {
        var label = new StringBuilder(opening);
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
