using System.Collections.Immutable;
using System.IO.Compression;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.VoiceComp;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class VoiceCompTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "v6-comp-tests", Guid.NewGuid().ToString("N"));
    public VoiceCompTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData(CompTrim.KeepHead, 100)]
    [InlineData(CompTrim.KeepTail, 700)]
    [InlineData(CompTrim.Middle, 400)]
    internal void CropKeepsOutputLengthAndSelectsSource(CompTrim trim, long expected)
        => Assert.Equal(expected, CompAlignment.Crop(100, 1000, 300, trim));

    [Fact]
    public void LongConsonantExpandsBeforeLockedVowel()
    {
        var aligned = CompAlignment.AtVowel(100, 310, 1000);
        Assert.Equal(790, aligned.ConsonantBegin); Assert.Equal(1000, aligned.VowelBegin);
        Assert.Throws<InvalidDataException>(() => CompAlignment.AtVowel(100, 310, 50));
    }

    [Fact]
    public void InvalidSourceDoesNotBecomeAnImplicitStretch()
    {
        Assert.Throws<InvalidDataException>(() => CompAlignment.Crop(0, 100, 101, CompTrim.Middle));
        Assert.Throws<InvalidDataException>(() => new SourceRecipe(LengthMs: double.NaN).Validate());
        Assert.Throws<InvalidDataException>(() => new VoiceCompPlan(Consonant: new()).Validate());
        Assert.Throws<InvalidDataException>(() => new SourceRecipe(Context: new(Before: " ")).Validate());
        Assert.Throws<InvalidDataException>(() => new SourceRecipe(Context: new(NoteMs: double.PositiveInfinity)).Validate());
    }

    [Fact]
    public void ActionsComposeAndRepeatWithoutDiscardingSources()
    {
        var plan = new VoiceCompPlan().WithAction(CompAction.LongSlice).WithAction(CompAction.PhonemeSplice)
            .WithAction(CompAction.IndependentSustain);
        Assert.NotNull(plan.Body); Assert.NotNull(plan.Consonant); Assert.NotNull(plan.Vowel); Assert.True(plan.Isolation!.After);
        Assert.Equal(plan, plan.WithAction(CompAction.LongSlice));
        Assert.False((plan with { Bypass = true }).Active);
    }

    [Fact]
    public void IsolationDoesNotDelayThePronunciationByAnImplicitTail()
    {
        Assert.Equal(400, new ContextBreak(true).Duration(400));
        Assert.Equal(480, new ContextBreak(true, EndOffsetMs: 80).Duration(400));
        Assert.Equal(400, new ContextBreak(true, EndOffsetMs: -80).Duration(400));
        Assert.Equal(450, new ContextBreak(true, EndOffsetMs: 80, SourceLengthMs: 450).Duration(400));
        var legacy = System.Text.Json.JsonSerializer.Deserialize<ContextBreak>("{\"After\":true,\"EndOffsetMs\":0,\"FadeMs\":5}")!;
        Assert.Equal(0, legacy.SourceLengthMs); Assert.Equal(400, legacy.Duration(400));
        Assert.Throws<InvalidDataException>(() => new ContextBreak(SourceLengthMs: double.NaN).Validate());
        Assert.Throws<InvalidDataException>(() => new ContextBreak(SourceLengthMs: -1).Validate());
        Assert.Throws<InvalidDataException>(() => new ContextBreak(SourceLengthMs: 60001).Validate());
    }

    [Fact]
    public void MixMatchesManualCropAndOverlapWithoutDuplicatingTheBase()
    {
        string a = Wave("a.wav", [1000, 2000, 3000, 4000, 5000, 6000]);
        string b = Wave("b.wav", [-1000, -2000, -3000, -4000]);
        string output = Path.Combine(_directory, "mix.wav");
        CompWave.Mix(output, new(44100, 7, [new(a, 1, 1, 4), new(b, 0, 3, 4)]));
        Assert.Equal(new short[] { 0, 2000, 3000, 3000, 3000, -3000, -4000 }, Samples(output));
    }

    [Fact]
    public void NeutralRouteIsExactAndCancellationDoesNotPublish()
    {
        short[] samples = [0, short.MinValue, -11, 0, 135, short.MaxValue];
        string source = Wave("neutral.wav", samples), output = Path.Combine(_directory, "out.wav");
        var plan = new CompRenderPlan(44100, samples.Length, [new(source, 0, 0, samples.Length)]);
        Assert.Equal(0, CompWave.Mix(output, plan)); Assert.Equal(samples, Samples(output));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => CompWave.Mix(output, plan, cancellation.Token));
        Assert.Equal(samples, Samples(output)); Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void CrossfadeIsComplementaryAndInsideDeclaredWindows()
    {
        for (int i = 0; i < 16; i++)
            Assert.Equal(1, CompWave.Envelope(i, 16, 16, 0) + CompWave.Envelope(i, 16, 0, 16), 12);
        Assert.Throws<InvalidDataException>(() => CompWave.Mix(Path.Combine(_directory, "bad.wav"),
            new(44100, 2, [new(Wave("small.wav", [1, 2]), 0, 0, 3)])));
    }

    [Fact]
    public void ArchiveRoundTripAndDuplicateRejectionPreserveNativeSequence()
    {
        string path = Path.Combine(_directory, "project.vpr");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("sequence.json").Open())) writer.Write("native");
        var address = new TimingNoteAddress(0, 0, 0, 0, 480, 60, "bank", "k a");
        VoiceCompArchive.Entry[] entries = [new(address, new VoiceCompPlan(Consonant: new(Context: new("a", "k a")),
            Vowel: new(Expression: new(64, 50, 50, 0, 0, 127, false, false)), Isolation: new(true, SourceLengthMs: 450)))];
        VoiceCompArchive.Write(path, entries); Assert.Equal(entries, VoiceCompArchive.Read(path));
        Assert.Throws<InvalidDataException>(() => VoiceCompArchive.Write(path, [entries[0], entries[0]]));
        using var read = ZipFile.OpenRead(path); using var text = new StreamReader(read.GetEntry("sequence.json")!.Open());
        Assert.Equal("native", text.ReadToEnd());
    }

    private string Wave(string name, short[] samples)
    {
        string path = Path.Combine(_directory, name);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(0x46464952u); writer.Write(36 + samples.Length * 2); writer.Write(0x45564157u);
        writer.Write(0x20746d66u); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(44100); writer.Write(88200); writer.Write((short)2); writer.Write((short)16);
        writer.Write(0x61746164u); writer.Write(samples.Length * 2); foreach (short sample in samples) writer.Write(sample);
        return path;
    }
    private static short[] Samples(string path)
    {
        var bytes = File.ReadAllBytes(path); return Enumerable.Range(0, (bytes.Length - 44) / 2).Select(i => BitConverter.ToInt16(bytes, 44 + 2 * i)).ToArray();
    }
}
