using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Which branch of an <c>.if</c> chain is written out, as a walk over a container's
/// children meets them. A chain is a run of sibling blocks — the <c>.if</c> that starts it,
/// then whichever <c>.elseif</c>s and <c>.else</c> continue it — so whoever walks the
/// siblings has to carry the little state that says which of them is still open.
/// <para>
/// Most conditions were answered once for the build, before any declaration was looked up.
/// The ones inside a macro body, a <c>.repeat</c> or an <c>.each</c> could not be: they may
/// name what the expansion binds, so they are answered here, once per expansion.
/// </para>
/// </summary>
public sealed class ConditionChain
{
    private bool chaining;
    private bool taken;

    /// <summary>
    /// Whether <paramref name="block"/> is written out at <paramref name="on"/>. A block that
    /// is not part of a chain is always written, and ends whatever chain came before it.
    /// </summary>
    public bool Includes(SemanticModel model, BlockSyntax block, Expansion? on)
    {
        var opener = block.Opener.Statement;
        switch (opener)
        {
            case IfDirectiveSyntax:
                chaining = true;
                taken = Holds(model, block, opener, already: false, on);
                return taken;

            case ElseIfDirectiveSyntax:
            case ElseDirectiveSyntax:
                // A continuation with no chain to continue has already been reported; it is
                // left out rather than written twice.
                if (!chaining)
                    return false;
                var take = Holds(model, block, opener, taken, on);
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
        SemanticModel model, BlockSyntax block, StatementSyntax opener, bool already, Expansion? on)
    {
        if (model.Configuration.Answered(block))
            return model.Configuration.Includes(block);
        if (already)
            return false;
        if (opener is ElseDirectiveSyntax)
            return true;
        return opener is ConditionalDirectiveSyntax conditional
            && model.ValueOf(conditional.Condition, on).AsNumber() is { } value && value != 0;
    }
}
