using VOCALOIDPatcher.Mcp.Core;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public class NoteLanguageTests
{
    [Theory]
    [InlineData(null, -1, 1, 1)]
    [InlineData(null, -1, -1, 0)]
    [InlineData(null, 4, 1, 4)]
    [InlineData(0, -1, 1, 0)]
    [InlineData(-1, 0, 1, -1)]
    public void UsesExplicitThenPartThenVoicebankThenFallback(int? requested, int part, int native, int expected)
        => Assert.Equal(expected, NoteLanguage.Resolve(requested, part, () => native, 0));

    [Fact]
    public void EstablishedLanguageDoesNotQueryVoicebank()
    {
        Assert.Equal(1, NoteLanguage.Resolve(null, 1, () => throw new Exception(), 0));
        Assert.Equal(0, NoteLanguage.Resolve(0, -1, () => throw new Exception(), 0));
    }
}
