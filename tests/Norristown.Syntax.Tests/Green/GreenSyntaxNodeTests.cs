using Norristown.Syntax.Green;

namespace Norristown.Syntax.Tests.Green
{
    public sealed class GreenSyntaxNodeTests
    {
        private sealed class FakeGreenNode(SyntaxKind kind, Int32 fullWidth) 
            : GreenNode(kind, fullWidth);

        private sealed class FakeGreenNodeWithChildren(SyntaxKind kind, GreenNode?[] slots) 
            : GreenNode(kind, GreenNodeExtensions.SumFullWidths(slots))
        {
            private readonly GreenNode?[] _slots = slots;

            public override Int32 SlotCount => _slots.Length;
            public override GreenNode? GetSlot(Int32 index) =>
                (UInt32)index < (UInt32)_slots.Length ? _slots[index] : null;
        }

        [Fact]
        public void Constructor_SetsKindAndSlots()
        {
            var slots = new GreenNode?[]
            {
                new GreenToken(SyntaxKind.NewlineToken, null, "\r\n", GreenTokenAttributes.None),
                new GreenToken(SyntaxKind.EndOfFileToken, null, "", GreenTokenAttributes.None),
            };

            var node = new GreenSyntaxNode(SyntaxKind.SkippedTokensTrivia, slots);
            Assert.Equal(SyntaxKind.SkippedTokensTrivia, node.Kind);
            Assert.Equal(slots.Length, node.SlotCount);

            Assert.Null(node.GetSlot(-1));
            Assert.Same(slots[0], node.GetSlot(0));
            Assert.Same(slots[2], node.GetSlot(1));
            Assert.Null(node.GetSlot(2));
        }

        [Fact]
        public void GetSlot_WhenNotOverridden_ReturnsNull()
        {
            var node = new FakeGreenNode(SyntaxKind.None, 0);
            Assert.Null(node.GetSlot(-1));
            Assert.Null(node.GetSlot(0));
            Assert.Null(node.GetSlot(+1));
        }

        [Fact]
        public void SlotCount_WhenNotOverridden_ReturnsZero()
        {
            var node = new FakeGreenNode(SyntaxKind.None, 0);
            Assert.Equal(0, node.SlotCount);
        }

        [Fact]
        public void IsToken_WhenNotOverridden_ReturnsFalse()
        {
            var node = new FakeGreenNode(SyntaxKind.None, 0);
            Assert.False(node.IsToken);
        }

        [Fact]
        public void ToFullString_WhenWriteToIsNotOverridden_AppendsAllChildren()
        {
            var slots = new GreenNode?[]
            {
                new GreenToken(SyntaxKind.None, null, "this is", GreenTokenAttributes.None),
                null,
                new GreenToken(SyntaxKind.None, null, " a test", GreenTokenAttributes.None),
            };

            var node = new FakeGreenNodeWithChildren(SyntaxKind.None, slots);
            Assert.Equal("this is a test", node.ToFullString());
        }
    }
}
