using Xunit;

public sealed class ProbeManagerIdentityTests
{
    [Fact]
    public void ConcurrentProbesNeverShareTheEditorOrEachOthersTemporaryRoot()
    {
        string first = ProbeManagerIdentity.Create(null, 123);
        string second = ProbeManagerIdentity.Create(null, 456);
        Assert.NotEqual("VOCALOID6", first);
        Assert.NotEqual(first, second);
        Assert.Equal(first, ProbeManagerIdentity.Create(" ", 123));
        Assert.NotEqual("VOCALOID6", ProbeManagerIdentity.Create("VOCALOID6", 123));
        Assert.Equal("VOCALOIDPatcher.Probe.123.render-6.13_3", ProbeManagerIdentity.Create("render-6.13_3", 123));
    }

    [Theory]
    [InlineData("../VOCALOID6")]
    [InlineData("..\\VOCALOID6")]
    [InlineData("C:\\VOCALOID6")]
    [InlineData("/VOCALOID6")]
    [InlineData("label ")]
    public void LabelsCannotEscapeThePerProcessNamespace(string label)
    {
        Assert.Throws<ArgumentException>(() => ProbeManagerIdentity.Create(label, 123));
    }

    [Fact]
    public void IdentityInputsAreBounded()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProbeManagerIdentity.Create(null, 0));
        Assert.Throws<ArgumentException>(() => ProbeManagerIdentity.Create(new string('a', 81), 123));
    }
}
