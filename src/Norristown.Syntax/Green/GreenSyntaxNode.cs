namespace Norristown.Syntax.Green
{
    /// <summary>
    /// A green node with children.
    /// </summary>
    /// <param name="kind">A <see cref="SyntaxKind"/> value that describes what kind of syntax node this is.</param>
    /// <param name="slots">The node's collection of child nodes, with <see langword="null"/> representing omitted children.</param>
    internal sealed class GreenSyntaxNode(SyntaxKind kind, GreenNode?[] slots) : GreenNode(kind, GreenNodeExtensions.SumFullWidths(slots))
    {
        private readonly GreenNode?[] _slots = slots;

        /// <inheritdoc/>
        public override GreenNode? GetSlot(Int32 index) =>
            (UInt32)index < (UInt32)_slots.Length ? _slots[index] : null;

        /// <inheritdoc/>
        public override Int32 SlotCount => _slots.Length;
    }
}
