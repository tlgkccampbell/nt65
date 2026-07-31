using Norristown.Syntax.Text;

namespace Norristown.Syntax.Tests.Text
{
    public sealed partial class SourceTextTests
    {
        private sealed class FakeSourceText(String text) : SourceText(0L)
        {
            private readonly String _text = text;

            public override Int32 Length => _text.Length;

            protected override SourceText ApplyChanges(ReadOnlySpan<TextChange> changes) =>
                throw new NotImplementedException();

            protected override void CopyToCore(TextSpan span, Span<Char> destination) =>
                _text.AsSpan(span.Start, span.Length).CopyTo(destination);

            protected override Char GetCharCore(Int32 position) =>
                _text[position];
        }
    }
}
