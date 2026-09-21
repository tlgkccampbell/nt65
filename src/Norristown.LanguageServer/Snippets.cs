using Norristown.Project;
using Norristown.Semantics;

namespace Norristown.LanguageServer;

/// <summary>
/// What a completion writes for the directives that open a block, for a client that takes
/// stops. They are the only completions written as snippets: a block is a shape with a brace to
/// close, and writing the shape is worth more than writing the word. The name is the first stop
/// and the body is the last, so that choosing one leaves the caret where the name goes and
/// tabbing to the end leaves it inside.
/// <para>
/// Instructions are deliberately not among them. <c>lda ${1:operand}</c> fights the typing of
/// someone who knows what they are writing, which in assembly is everyone.
/// </para>
/// </summary>
internal static class Snippets
{
    /// <summary>
    /// What each block opener writes, by the directive that begins it. <c>.proc</c> is not here
    /// because on the 65816 it depends on how the program's other routines are declared.
    /// </summary>
    private static readonly Dictionary<string, string> Written = new(StringComparer.Ordinal)
    {
        [".macro"] = ".macro ${1:name}(${2:parameters}) {\n    $0\n}",
        // A `.func` is one line rather than a block, and is here because its shape — a name, a
        // parameter list and what it works out to — is the same thing worth writing.
        [".func"] = ".func ${1:name}(${2:parameters}) = $0",
        [".struct"] = ".struct ${1:Name} {\n    $0\n}",
        [".union"] = ".union ${1:Name} {\n    $0\n}",
        [".enum"] = ".enum ${1:Name} {\n    $0\n}",
        [".scope"] = ".scope ${1:name} {\n    $0\n}",
        [".segment"] = ".segment ${1:NAME} {\n    $0\n}",
        [".if"] = ".if ${1:condition} {\n    $0\n}",
        [".else"] = ".else {\n    $0\n}",
        [".elseif"] = ".elseif ${1:condition} {\n    $0\n}",
        [".repeat"] = ".repeat ${1:count} {\n    $0\n}",
        [".each"] = ".each ${1:List}, ${2:item} {\n    $0\n}",
        [".multiproc"] = ".multiproc ${1:Enum}, ${2:item} {\n    $0\n}",
    };

    /// <summary>
    /// What <paramref name="directive"/> writes as a snippet, or null for one that opens no
    /// block and so is written as the word it is.
    /// </summary>
    /// <param name="directive">The directive, as the completion lists it.</param>
    /// <param name="program">The program, for what its routines' signatures mostly start with.</param>
    /// <param name="cpu">The processor, since only the 65816 has a signature worth a stop.</param>
    public static string? Of(string directive, ProgramModel program, Cpu cpu)
    {
        if (directive != ".proc")
            return Written.GetValueOrDefault(directive);

        // A routine on the 65816 is declared with the state it assumes, and a program declares
        // most of its the same way; the one it uses most is the one a new routine most likely
        // wants, and is a stop rather than a decision made for the programmer.
        var usual = cpu == Cpu.Wdc65816 ? Usual(program) : null;
        return usual is null
            ? ".proc ${1:name} {\n    $0\n}"
            : $".proc ${{1:name}}: ${{2:{usual}}} {{\n    $0\n}}";
    }

    /// <summary>
    /// How most of the program's routines start their signature, or null where none of them
    /// writes one. It is the first item as it is written rather than what it resolves to,
    /// because a program that writes <c>std</c> wants <c>std</c> and not what <c>std</c> means.
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
            .OrderByDescending(written => written.Value)
            .ThenBy(written => written.Key, StringComparer.Ordinal)
            .Select(written => written.Key)
            .FirstOrDefault();
    }

    /// <summary>
    /// The first item of the signature a routine is declared with, as it is written, or null
    /// where it is declared with none.
    /// </summary>
    private static string? Starts(Symbol symbol)
    {
        var tree = symbol.Tree;
        var index = tree.GetLineIndex(symbol.NameSpan.Start);
        var start = tree.LineStarts[index];
        var end = index + 1 < tree.LineStarts.Length ? tree.LineStarts[index + 1] : tree.Text.Length;
        var line = tree.Text[start..end];
        var at = symbol.NameSpan.End - start;
        if (at < 0 || at >= line.Length || line[at] != ':')
            return null;
        var item = line[(at + 1)..];
        foreach (var mark in (ReadOnlySpan<string>)[",", "->", "{", ";"])
        {
            var found = item.IndexOf(mark, StringComparison.Ordinal);
            if (found >= 0)
                item = item[..found];
        }
        return item.Trim() is { Length: > 0 } written ? written : null;
    }
}
