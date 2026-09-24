namespace Norristown.Syntax;

/// <summary>
/// Represents a <see cref="SyntaxVisitor"/> that descends the whole tree. When a node's method is
/// not overridden, the walker visits its children in source order, so an override for one kind is
/// still reached below nodes of other kinds. An override that should also visit its own node's
/// children calls <c>base.VisitXxx(node)</c>.
/// </summary>
public abstract class SyntaxWalker : SyntaxVisitor
{
    /// <inheritdoc/>
    public override void DefaultVisit(SyntaxNode node)
    {
        foreach (var child in node.ChildNodes)
            Visit(child);
    }
}
