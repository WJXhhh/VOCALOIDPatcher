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
}
