using Norristown.Analysis.Text;

namespace Norristown.Analysis.Tests.Text
{
    public sealed class StringSourceTextTests
    {
        [Theory]
        [InlineData("", 0)]
        [InlineData("abc", 12345)]
        public void Constructor_SetsTextAndRevision(String text, Int64 revision)
        {
            var sourceText = new StringSourceText(text, revision);
            Assert.Equal(text, sourceText.ToString());
            Assert.Equal(revision, sourceText.Revision);
        }
    }
}
