namespace Norristown.Syntax;

/// <summary>
/// A <see cref="SyntaxVisitor"/> that goes on down the tree: a node whose method is not
/// overridden has its children visited in source order, so an override of one kind still sees
/// everything below it. An override that wants the same keeps it by calling
/// <c>base.VisitXxx(node)</c>.
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
