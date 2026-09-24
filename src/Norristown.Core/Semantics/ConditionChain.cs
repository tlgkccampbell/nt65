using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Tracks which branch of an <c>.if</c> chain is included, as a walk over a container's
/// children reaches each branch. A chain is a run of sibling blocks, made up of the <c>.if</c>
/// that starts it followed by any <c>.elseif</c> and <c>.else</c> blocks that continue it. Code
/// that walks the siblings therefore keeps a little state, namely whether a chain is open and
/// whether one of its branches has already been taken.
/// <para>
/// Most conditions are evaluated once for the build, before any declaration is looked up. The
/// ones inside a macro body, a <c>.repeat</c> or an <c>.each</c> cannot be, because they may name
/// what the <see cref="Expansion"/> binds. This type evaluates those once per expansion.
/// </para>
/// </summary>
public sealed class ConditionChain
{
    private bool chaining;
    private bool taken;

    /// <summary>
    /// Determines whether <paramref name="block"/> is included in the expansion
    /// <paramref name="on"/>. A block that is not part of a chain is always included, and it ends
    /// any chain that came before it.
    /// </summary>
    /// <param name="model">The model of the file the block is in.</param>
    /// <param name="block">The block, which may open a branch of a chain.</param>
    /// <param name="on">The expansion the block is in.</param>
    /// <param name="diagnostics">
    /// The list that receives the problems in each condition evaluated here, or null to report
    /// nothing. No pass over the symbols reaches a condition in an expansion, so the one pass
    /// that walks every expansion once passes its list, and every other caller passes null.
    /// </param>
    public bool Includes(SemanticModel model, BlockSyntax block, Expansion? on, List<Diagnostic>? diagnostics = null)
    {
        var opener = block.Opener.Statement;
        switch (opener)
        {
            case IfDirectiveSyntax:
                chaining = true;
                taken = Holds(model, block, opener, already: false, on, diagnostics);
                return taken;

            case ElseIfDirectiveSyntax:
            case ElseDirectiveSyntax:
                // A continuation with no chain to continue has already been reported, so its
                // block is left out.
                if (!chaining)
                    return false;
                var take = Holds(model, block, opener, taken, on, diagnostics);
                taken |= take;
                return take;

            default:
                chaining = false;
                return true;
        }
    }

    /// <summary>Ends the chain, for a sibling that is a line rather than a block.</summary>
    public void Break() => chaining = false;

    private static bool Holds(
        SemanticModel model, BlockSyntax block, StatementSyntax opener, bool already, Expansion? on,
        List<Diagnostic>? diagnostics)
    {
        if (model.Configuration.Answered(block))
            return model.Configuration.Includes(block);
        if (already)
            return false;
        if (opener is not ConditionalDirectiveSyntax conditional)
            return opener is ElseDirectiveSyntax;
        if (diagnostics is not null)
            model.Check(conditional.Condition, diagnostics, on);
        return model.ValueOf(conditional.Condition, on).AsNumber() is { } value && value != 0;
    }
}
