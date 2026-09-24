using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Records what the registers hold at each statement of one file, for an editor to show where
/// the caret is. It is recorded on a final walk after the analysis has reached a fixed point, so
/// it holds the finished answer and not an intermediate round.
/// </summary>
public sealed class RegisterStates
{
    private readonly Dictionary<(int Position, Expansion? On), RegisterState> reaching = [];
    private readonly SyntaxTree tree;

    internal RegisterStates(SyntaxTree tree) => this.tree = tree;

    /// <summary>
    /// Returns what the registers hold before <paramref name="statement"/> in the expansion
    /// <paramref name="on"/>, or null where nothing reaches it or it is in no routine.
    /// </summary>
    public RegisterState? Before(SyntaxNode statement, Expansion? on = null) =>
        reaching.GetValueOrDefault((statement.Position, on));

    /// <summary>
    /// Returns what every <see cref="Expansion"/> of <paramref name="statement"/> agrees the
    /// registers may hold. An editor asks about a line, and a line in a macro body is emitted once
    /// per call, each copy with its own answer. What they agree on is the one thing true of the
    /// line itself. The result is a merge rather than the first answer, because the expansions
    /// are held in no particular order. A hover that depended on which one was found first would
    /// not be the same hover twice. An expansion's line belongs to the file that contains the
    /// body it expands, which for a macro declared in another file is that other file.
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
