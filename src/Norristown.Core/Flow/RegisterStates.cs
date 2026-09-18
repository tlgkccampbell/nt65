using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// What the registers hold at each statement of one file, for an editor to show where the
/// caret is. It is recorded on the walk over the settled answer, so what it holds is what the
/// analysis finished with and not a round on the way there.
/// </summary>
public sealed class RegisterStates
{
    private readonly Dictionary<(int Position, Expansion? On), RegisterState> reaching = [];
    private readonly SyntaxTree tree;

    internal RegisterStates(SyntaxTree tree) => this.tree = tree;

    /// <summary>
    /// What the registers hold before <paramref name="statement"/> on the writing
    /// <paramref name="on"/>, or null where nothing reaches it or it is in no routine.
    /// </summary>
    public RegisterState? Before(SyntaxNode statement, Expansion? on = null) =>
        reaching.GetValueOrDefault((statement.Position, on));

    /// <summary>
    /// What every writing of <paramref name="statement"/> agrees the registers may hold. An
    /// editor asks about a line, and a line in a macro body is written out once per call, each
    /// with its own answer; what they agree on is the one thing true of the line itself. It is
    /// a merge rather than the first of them because the writings are held in no particular
    /// order, and a hover that depended on which one was found first would not be the same
    /// hover twice. A writing's line is in the file of the block it writes out, which is
    /// another file's for a macro declared there.
    /// </summary>
    public RegisterState? AnyBefore(StatementSyntax statement)
    {
        RegisterState? merged = null;
        foreach (var (key, state) in reaching)
        {
            if (key.Position == statement.Position && (key.On?.Body?.Tree ?? tree) == statement.Tree)
                merged = RegisterState.Merge(merged, state);
        }
        return merged;
    }

    internal void Record(Step step, RegisterState state) =>
        reaching[(step.Statement.Position, step.On)] = state;
}
