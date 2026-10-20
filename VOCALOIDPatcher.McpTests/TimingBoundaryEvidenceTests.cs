using VOCALOIDPatcher.ConsonantTiming;
using Xunit;

public sealed class TimingBoundaryEvidenceTests
{
    private static readonly TimingBoundaryEvidence Left = new(3362,3418,3395,"i","iE_r");
    private static readonly TimingBoundaryEvidence Right = new(3418,3433,3431,"@N","Sil");

    [Fact]
    public void IndependentSamplesProveTheLingFinalPhonemeJunction()
    {
        Assert.False(Left.MatchesSplit(3418,"iE_r","@N"));
        Assert.False(Right.MatchesSplit(3418,"iE_r","@N"));
        Assert.True(TimingBoundaryEvidence.MatchesJoin(3418,"iE_r","@N",Left,Right));
        Assert.True(Left.MatchesSplit(3395,"i","iE_r"));
        Assert.True(Right.MatchesSplit(3431,"@N",null));
    }

    [Fact]
    public void GapsOverlapsWrongLabelsAndMissingSidesDoNotProveAJunction()
    {
        Assert.False(TimingBoundaryEvidence.MatchesJoin(3418,"iE_r","@N",Left,Right with { Begin=3419 }));
        Assert.False(TimingBoundaryEvidence.MatchesJoin(3418,"iE_r","@N",Left with { End=3419 },Right));
        Assert.False(TimingBoundaryEvidence.MatchesJoin(3418,"i","@N",Left,Right));
        Assert.False(TimingBoundaryEvidence.MatchesJoin(3418,"iE_r","n",Left,Right));
        Assert.False(TimingBoundaryEvidence.MatchesJoin(3418,null,"@N",Left,Right));
        Assert.False(TimingBoundaryEvidence.MatchesJoin(3418,"iE_r",null,Left,Right));
    }

    [Fact]
    public void StationaryEndingUsesItsSinglePhonemeAsTheLeftSide()
    {
        Assert.True(TimingBoundaryEvidence.MatchesJoin(3418,"iE_r","@N",
            Left with { From="iE_r",To=null },Right));
        Assert.False(TimingBoundaryEvidence.MatchesJoin(3418,"iE_r","@N",
            Left with { From=null,To=null },Right));
    }

    [Fact]
    public void ConsecutiveIncomingUnitsProveTheShortFricativeJunction()
    {
        var left = new TimingBoundaryEvidence(3253, 3257, 3255, "Sil", "f");
        var right = new TimingBoundaryEvidence(3257, 3291, 3259, "Sil", "7");
        Assert.True(TimingBoundaryEvidence.MatchesJoin(3257, "f", "7", left, right));
        Assert.False(right.MatchesSplit(3257, "f", "7"));
        Assert.False(TimingBoundaryEvidence.MatchesJoin(3259, "f", "7", left, right));
    }

    [Theory]
    [InlineData("f", "7", "Sil", "a", 3257, 3259)]
    [InlineData("f", "7", "s", "7", 3257, 3259)]
    [InlineData("s", "7", "Sil", "7", 3257, 3259)]
    [InlineData("f", "7", "Sil", "7", 3258, 3259)]
    [InlineData("f", "7", "Sil", "7", 3257, 3256)]
    [InlineData("f", "7", "Sil", "7", 3257, 3291)]
    public void IncomingUnitStillRequiresExactAdjacencyLabelsAndANonemptyRightSide(
        string leftPhone, string rightPhone, string from, string to, int begin, int split)
    {
        Assert.False(TimingBoundaryEvidence.MatchesJoin(3257, leftPhone, rightPhone,
            new(3253, 3257, 3255, "Sil", "f"), new(begin, 3291, split, from, to)));
    }

    [Theory]
    [InlineData("s", "t", 3290, 3292, 3293, 3294, 3295)]
    [InlineData("t_h", "i@U", 3450, 3452, 3454, 3456, 3485)]
    [InlineData("k", "o", 3556, 3558, 3560, 3562, 3581)]
    public void MissingDiphoneUsesOutgoingSilenceSplitWithExactIncomingEvidence(
        string left, string right, int begin, int split, int join, int nextSplit, int end)
    {
        var outgoing = new TimingBoundaryEvidence(begin, join, split, left, "Sil");
        var incoming = new TimingBoundaryEvidence(join, end, nextSplit, "Sil", right);
        Assert.True(TimingBoundaryEvidence.MatchesSilenceBridge(split, left, right, outgoing, incoming));
        Assert.False(TimingBoundaryEvidence.MatchesSilenceBridge(join, left, right, outgoing, incoming));
        Assert.False(TimingBoundaryEvidence.MatchesSilenceBridge(nextSplit, left, right, outgoing, incoming));
        Assert.False(TimingBoundaryEvidence.MatchesSilenceBridge(split, null, right, outgoing, incoming));
        Assert.False(TimingBoundaryEvidence.MatchesSilenceBridge(split, left, null, outgoing, incoming));
        Assert.False(TimingBoundaryEvidence.MatchesSilenceBridge(split, "wrong", right, outgoing, incoming));
        Assert.False(TimingBoundaryEvidence.MatchesSilenceBridge(split, left, "wrong", outgoing, incoming));
        Assert.False(TimingBoundaryEvidence.MatchesSilenceBridge(split, left, right, outgoing, incoming with { Begin = join + 1 }));
        Assert.False(TimingBoundaryEvidence.MatchesSilenceBridge(split, left, right, outgoing, incoming with { Begin = join - 1 }));
        Assert.False(TimingBoundaryEvidence.MatchesSilenceBridge(split, left, right, outgoing with { To = right }, incoming));
        Assert.False(TimingBoundaryEvidence.MatchesSilenceBridge(split, left, right, outgoing, incoming with { From = left }));
        Assert.False(TimingBoundaryEvidence.MatchesSilenceBridge(split, left, right, outgoing, incoming with { Split = join - 1 }));
        Assert.False(TimingBoundaryEvidence.MatchesSilenceBridge(split, left, right, outgoing, incoming with { Split = end }));
    }

    [Fact]
    public void CompressedIncomingHalfRequiresNonemptyExactContinuationOfTheSamePhoneme()
    {
        var outgoing = new TimingBoundaryEvidence(3290, 3294, 3293, "s", "Sil");
        var incoming = new TimingBoundaryEvidence(3294, 3295, 3295, "Sil", "t");
        var continuation = new TimingBoundaryEvidence(3295, 3311, 3296, "t", "ei");
        Assert.True(TimingBoundaryEvidence.MatchesSilenceBridge(3293, "s", "t", outgoing, incoming, continuation));
        Assert.False(TimingBoundaryEvidence.MatchesSilenceBridge(3293, "s", "t", outgoing, incoming));
        foreach (var invalid in new[]
        {
            continuation with { Begin = 3296 }, continuation with { Begin = 3294 },
            continuation with { From = "k" }, continuation with { Split = 3295 },
            continuation with { Split = 3312 }, continuation with { End = 3295 }
        })
            Assert.False(TimingBoundaryEvidence.MatchesSilenceBridge(3293, "s", "t", outgoing, incoming, invalid));
        Assert.False(TimingBoundaryEvidence.MatchesSilenceBridge(3293, "s", "t", outgoing,
            incoming with { Split = 3296 }, continuation));
    }
}
