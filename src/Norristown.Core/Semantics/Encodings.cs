using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Provides queries about <c>.encoded</c>, which gives the opcode byte the instruction below it is
/// written as. Several bytes run as one instruction on some CPUs, as the NMOS 6502's undocumented
/// <c>nop</c> encodings do, and ca65 writes only one of them. An <c>.encoded</c> qualifies the
/// instruction itself, so it stands before it, as <c>.allow</c> does, with only blank lines, bare
/// labels and <c>.allow</c> lines between them.
/// </summary>
public static class Encodings
{
    /// <summary>
    /// Returns the <c>.encoded</c> that applies to <paramref name="instruction"/>, or null where
    /// none does.
    /// </summary>
    public static EncodedDirectiveSyntax? Of(InstructionStatementSyntax instruction)
    {
        if (instruction.Ancestors().OfType<LineSyntax>().FirstOrDefault() is not { Parent: { } container } line)
            return null;
        var siblings = container.ChildNodes;
        for (var i = siblings.IndexOf(line) - 1; i >= 0; i--)
        {
            if (siblings[i] is not LineSyntax { Statement: var statement })
                return null;
            if (statement is EncodedDirectiveSyntax encoded)
                return encoded;
            if (!Passes(statement))
                return null;
        }
        return null;
    }

    /// <summary>
    /// Returns the instruction the <c>.encoded</c> on <paramref name="line"/> applies to, or null
    /// where no instruction follows it.
    /// </summary>
    public static InstructionStatementSyntax? Target(LineSyntax line)
    {
        if (line.Parent is not { } container)
            return null;
        var siblings = container.ChildNodes;
        for (var i = siblings.IndexOf(line) + 1; i < siblings.Length; i++)
        {
            if (siblings[i] is not LineSyntax { Statement: var statement })
                return null;
            switch (statement)
            {
                case InstructionStatementSyntax instruction:
                    return instruction;
                case LabeledLineSyntax { Statement: InstructionStatementSyntax labelled }:
                    return labelled;
            }
            if (!Passes(statement))
                return null;
        }
        return null;
    }

    /// <summary>
    /// Returns a value indicating whether <paramref name="statement"/> may stand between an
    /// <c>.encoded</c> and its instruction.
    /// </summary>
    private static bool Passes(StatementSyntax statement) =>
        statement is BlankLineSyntax or AllowDirectiveSyntax or LabeledLineSyntax { Statement: null };
}
