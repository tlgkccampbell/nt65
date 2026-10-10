using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Answers the questions about one statement that several features ask of the lines they look
/// at.
/// </summary>
internal static class Statements
{
    /// <summary>
    /// Returns the instruction a statement holds, including one after a label, or null for a
    /// statement that holds none.
    /// </summary>
    public static InstructionStatementSyntax? InstructionOf(StatementSyntax? statement) => statement switch
    {
        InstructionStatementSyntax instruction => instruction,
        LabeledLineSyntax labeled => labeled.Statement as InstructionStatementSyntax,
        _ => null,
    };
}
