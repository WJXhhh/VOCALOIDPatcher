using VOCALOIDPatcher.Rendering;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class WaveProcessingReadScopeTests
{
    [Fact]
    public void CompletedDownstreamAudioCannotFeedBackIntoEarlierProcessors()
    {
        // Model the real getter's BVL -> phoneme gain -> smoothing -> ACE chain.
        // All caches are ready; a source probe must neither consume downstream
        // output nor report an input mismatch that schedules another rebuild.
        string[] inputs = ["bvl", "phoneme", "smooth"];
        string[] outputs = ["phoneme", "smooth", "ace"];
        int rebinds = 0;
        string Getter()
        {
            string path = "bvl";
            for (int i = 0; i < inputs.Length; i++)
            {
                if (!WaveProcessingReadScope.ShouldApply((WaveProcessingStage)i)) continue;
                if (path == inputs[i]) path = outputs[i];
                else rebinds++;
            }
            return path;
        }

        for (int n = 0; n < 100; n++)
        {
            Assert.Equal("bvl", WaveProcessingReadScope.ReadBefore(WaveProcessingStage.PhonemeVolume, Getter));
            Assert.Equal("phoneme", WaveProcessingReadScope.ReadBefore(WaveProcessingStage.TransitionSmoothing, Getter));
            Assert.Equal("smooth", WaveProcessingReadScope.ReadBefore(WaveProcessingStage.AceAcoustic, Getter));
            Assert.Equal("ace", Getter());
        }
        Assert.Equal(0, rebinds);

        // A real upstream publication must still invalidate downstream caches.
        inputs[0] = "new-bvl";
        Getter();
        Assert.True(rebinds > 0);
    }

    [Theory]
    [InlineData((int)WaveProcessingStage.PhonemeVolume)]
    [InlineData((int)WaveProcessingStage.TransitionSmoothing)]
    [InlineData((int)WaveProcessingStage.AceAcoustic)]
    public void InputReadsApplyOnlyEarlierStages(int readerValue)
    {
        var reader = (WaveProcessingStage)readerValue;
        WaveProcessingReadScope.ReadBefore(reader, () =>
        {
            foreach (var stage in Enum.GetValues<WaveProcessingStage>())
                Assert.Equal(stage < reader, WaveProcessingReadScope.ShouldApply(stage));
            return "input";
        });
        AssertAllEnabled();
    }

    [Fact]
    public void NestedReadsCannotReenableDownstreamAndRestoreAfterExceptions()
    {
        WaveProcessingReadScope.ReadBefore(WaveProcessingStage.TransitionSmoothing, () =>
        {
            Assert.Throws<IOException>(() => WaveProcessingReadScope.ReadBefore(WaveProcessingStage.PhonemeVolume, () =>
            {
                Assert.False(WaveProcessingReadScope.ShouldApply(WaveProcessingStage.PhonemeVolume));
                throw new IOException();
            }));
            Assert.True(WaveProcessingReadScope.ShouldApply(WaveProcessingStage.PhonemeVolume));
            WaveProcessingReadScope.ReadBefore(WaveProcessingStage.AceAcoustic, () =>
            {
                Assert.False(WaveProcessingReadScope.ShouldApply(WaveProcessingStage.TransitionSmoothing));
                return "nested";
            });
            return "input";
        });
        AssertAllEnabled();
    }

    [Fact]
    public void ScopeDoesNotSuppressOtherThreads()
    {
        WaveProcessingReadScope.ReadBefore(WaveProcessingStage.PhonemeVolume, () =>
        {
            Exception? error = null;
            var worker = new Thread(() =>
            {
                try { AssertAllEnabled(); }
                catch (Exception e) { error = e; }
            });
            worker.Start();
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(error);
            Assert.False(WaveProcessingReadScope.ShouldApply(WaveProcessingStage.PhonemeVolume));
            return "input";
        });
        AssertAllEnabled();
    }

    private static void AssertAllEnabled()
    {
        foreach (var stage in Enum.GetValues<WaveProcessingStage>())
            Assert.True(WaveProcessingReadScope.ShouldApply(stage));
    }
}
