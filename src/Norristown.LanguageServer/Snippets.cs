using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Provides the snippet a completion inserts for each directive that opens a block, for a client
/// that accepts snippets. These are the only snippet completions, because a block has a fixed
/// shape with a brace to close, and inserting the whole shape is worth more than inserting the
/// directive.
/// The name is the first tab stop and the body is the last, so that choosing one leaves the
/// caret where the name goes and tabbing to the end leaves it inside the body.
/// <para>
/// Instructions are deliberately not among them. <c>lda ${1:operand}</c> fights the typing of
/// someone who knows what they are writing, which in assembly is everyone.
/// </para>
/// </summary>
internal static class Snippets
{
    /// <summary>
    /// The snippet for each block opener, keyed by its directive. <c>.proc</c> is not here
    /// because on the 65816 its snippet depends on how the program's other routines are declared.
    /// </summary>
    private static readonly Dictionary<DirectiveKind, string> ByDirective = new()
    {
        [DirectiveKind.Macro] = ".macro ${1:name}(${2:parameters}) {\n    $0\n}",
        // A `.func` is one line rather than a block, but it is here because its shape (a name, a
        // parameter list and the expression it evaluates) is just as worth inserting whole.
        [DirectiveKind.Func] = ".func ${1:name}(${2:parameters}) = $0",
        [DirectiveKind.Struct] = ".struct ${1:Name} {\n    $0\n}",
        [DirectiveKind.Union] = ".union ${1:Name} {\n    $0\n}",
        [DirectiveKind.Enum] = ".enum ${1:Name} {\n    $0\n}",
        [DirectiveKind.Scope] = ".scope ${1:name} {\n    $0\n}",
        [DirectiveKind.Segment] = ".segment ${1:NAME} {\n    $0\n}",
        [DirectiveKind.If] = ".if ${1:condition} {\n    $0\n}",
        [DirectiveKind.Else] = ".else {\n    $0\n}",
        [DirectiveKind.ElseIf] = ".elseif ${1:condition} {\n    $0\n}",
        [DirectiveKind.Repeat] = ".repeat ${1:count} {\n    $0\n}",
        [DirectiveKind.Each] = ".each ${1:List}, ${2:item} {\n    $0\n}",
        [DirectiveKind.MultiProc] = ".multiproc ${1:Enum}, ${2:item} {\n    $0\n}",
    };

    /// <summary>
    /// Returns the snippet that <paramref name="directive"/> inserts, or null for a directive that
    /// opens no block and is inserted as plain text.
    /// </summary>
    /// <param name="directive">The directive.</param>
    /// <param name="program">
    /// The program, used to find what its routines' signatures mostly start with.
    /// </param>
    /// <param name="cpu">The processor. Only on the 65816 does the signature get a tab stop.</param>
    public static string? Of(DirectiveKind directive, ProgramModel program, Cpu cpu)
    {
        if (directive != DirectiveKind.Proc)
            return ByDirective.GetValueOrDefault(directive);

        // On the 65816 a routine is declared with the processor state it assumes, and most of a
        // program's routines declare the same one. The most common is the likeliest for a new
        // routine, so it is offered pre-filled in a tab stop rather than decided for the
        // programmer.
        var usual = cpu == Cpu.Wdc65816 ? Usual(program) : null;
        return usual is null
            ? ".proc ${1:name} {\n    $0\n}"
            : $".proc ${{1:name}}: ${{2:{usual}}} {{\n    $0\n}}";
    }

    /// <summary>
    /// Returns how most of the program's routines start their signature, or null when none of them
    /// declares one. The result is the first item as the source spells it rather than what it
    /// resolves to, because a program that writes <c>std</c> wants <c>std</c> and not what
    /// <c>std</c> means.
    /// </summary>
    private static string? Usual(ProgramModel program)
    {
        var counted = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in program.Files)
        {
            foreach (var symbol in file.Symbols)
            {
                if (symbol.Kind == SymbolKind.Proc && Starts(symbol) is { } item)
                    counted[item] = counted.GetValueOrDefault(item) + 1;
            }
        }
        return counted
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key)
            .FirstOrDefault();
    }

    /// <summary>
    /// Returns the first item of the signature a routine is declared with, as the source spells
    /// it, or null when the routine is declared with none.
    /// </summary>
    private static string? Starts(Symbol symbol)
    {
        var tree = symbol.Tree;
        var index = tree.GetLineIndex(symbol.NameSpan.Start);
        var start = tree.LineStarts[index];

        // The line's code ends where its trailing trivia starts, so the comment is left out.
        var line = tree.Text[start..LineContext.CodeEnd(tree, index)];
        var at = symbol.NameSpan.End - start;
        if (at < 0 || at >= line.Length || line[at] != ':')
            return null;
        var item = line[(at + 1)..];
        foreach (var mark in (ReadOnlySpan<string>)[",", "->", "{"])
        {
            var found = item.IndexOf(mark, StringComparison.Ordinal);
            if (found >= 0)
                item = item[..found];
        }
        return item.Trim() is { Length: > 0 } trimmed ? trimmed : null;
    }
}
