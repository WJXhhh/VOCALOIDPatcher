using System.IO.Compression;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.ConsonantTiming;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class SampledTimingModelTests
{
    private static TimingNoteAddress Address => new(0, 0, 0, 960, 480, 60, "test-bank", "k a i");
    private static SampledTimingPlan Plan(float coefficient) => new(Address.Topology,
        [new(TimingAdjustment.NoteCoefficient, -1, coefficient)]);

    [Fact]
    public void UndoRedoAndNewBranchTrackSavedContentAndMonotoneRevision()
    {
        var doc = new SampledTimingDocument();
        var a = doc.Edit(Address, Plan(.12345679f), _ => { })!;
        doc.MarkSaved(); long savedRevision = doc.Revision;
        var b = doc.Edit(Address, Plan(.9876543f), _ => { })!;
        Assert.True(doc.IsDirty);
        b.ApplyBefore(); Assert.False(doc.IsDirty);
        Assert.True(doc.Revision > savedRevision);
        a.ApplyBefore(); Assert.True(doc.IsDirty);
        a.ApplyAfter(); Assert.False(doc.IsDirty);
        doc.Edit(Address, Plan(.4f), _ => { }); Assert.True(doc.IsDirty);
    }

    [Fact]
    public void FailedHistoryRegistrationRestoresValueAndSavedIdentity()
    {
        var doc = new SampledTimingDocument();
        doc.Edit(Address, Plan(.25f), _ => { }); doc.MarkSaved();
        Assert.Throws<InvalidOperationException>(() => doc.Edit(Address, Plan(.75f), _ => throw new InvalidOperationException()));
        Assert.Equal(.25f, doc.Get(Address)!.ExecutableCoefficient(Address.Topology));
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void ReservedAndUnknownTargetsRoundTripButCannotExecute()
    {
        InArchive(path =>
        {
            var plan = new SampledTimingPlan(Address.Topology, [new(TimingAdjustment.PhonemeBoundary, 2, .02f),
                new(TimingAdjustment.Transition, 1, -.03f), new("future.vowel.center", 2, .4f)]);
            SampledTimingProjectArchive.Write(path, new Dictionary<TimingNoteAddress, SampledTimingPlan> { [Address] = plan });
            var read = SampledTimingProjectArchive.Read(path)[Address];
            Assert.Equal(plan.Fingerprint, read.Fingerprint);
            Assert.Throws<NotSupportedException>(() => read.ExecutableCoefficient(Address.Topology));
            using var zip = ZipFile.OpenRead(path);
            using var text = new StreamReader(zip.GetEntry("Project/keep.txt")!.Open());
            Assert.Equal("keep", text.ReadToEnd());
        });
    }

    [Fact]
    public void EmptyDocumentRemovesOnlyTimingEntry()
    {
        InArchive(path =>
        {
            SampledTimingProjectArchive.Write(path, new Dictionary<TimingNoteAddress, SampledTimingPlan> { [Address] = Plan(.5f) });
            SampledTimingProjectArchive.Write(path, new Dictionary<TimingNoteAddress, SampledTimingPlan>());
            Assert.Empty(SampledTimingProjectArchive.Read(path));
            using var zip = ZipFile.OpenRead(path);
            Assert.Null(zip.GetEntry(SampledTimingProjectArchive.EntryPath));
            Assert.NotNull(zip.GetEntry("Project/keep.txt"));
        });
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-.01f)]
    [InlineData(1.01f)]
    public void InvalidCoefficientCannotEnterDocument(float value)
        => Assert.Throws<InvalidDataException>(() => Plan(value));

    [Fact]
    public void DuplicateAxesAndBoundaryIndicesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => new SampledTimingPlan(Address.Topology,
            [new(TimingAdjustment.NoteCoefficient, -1, .1f), new(TimingAdjustment.NoteCoefficient, -1, .2f)]));
        Assert.Throws<InvalidDataException>(() => new SampledTimingPlan(Address.Topology, [new(TimingAdjustment.PhonemeBoundary, -1, .1f)]));
        Assert.Throws<InvalidDataException>(() => new SampledTimingPlan(Address.Topology, [new(TimingAdjustment.PhonemeBoundary, 4, .1f)]));
    }

    [Fact]
    public void AllPhonemeEdgesPersistWithIndependentOffsetsAndCoefficient()
    {
        InArchive(path =>
        {
            var axes = Enumerable.Range(0, 4).Select(i => new TimingAdjustment(TimingAdjustment.PhonemeBoundary, i, (i - 2) * .01f))
                .Append(new(TimingAdjustment.NoteCoefficient, -1, .25f));
            var plan = new SampledTimingPlan(Address.Topology, axes);
            SampledTimingProjectArchive.Write(path, new Dictionary<TimingNoteAddress, SampledTimingPlan> { [Address] = plan });
            var loaded = SampledTimingProjectArchive.Read(path)[Address];
            Assert.Equal(plan.Fingerprint, loaded.Fingerprint);
            Assert.Equal(.25f, loaded.PhonemeCoefficient(Address.Topology));
            Assert.True(loaded.HasPhonemeControls);
            Assert.Throws<NotSupportedException>(() => loaded.PhonemeCoefficient("a k i"));
            var doc = new SampledTimingDocument();
            var edit = doc.Edit(Address, loaded, _ => { })!;
            edit.ApplyBefore(); Assert.Null(doc.Get(Address));
            edit.ApplyAfter(); Assert.Equal(plan.Fingerprint, doc.Get(Address)!.Fingerprint);
        });
    }

    [Fact]
    public void ChangingPhonemeSequenceNeverRebindsByIndexAlone()
    {
        Assert.Throws<NotSupportedException>(() => Plan(.5f).ExecutableCoefficient("k i a"));
        var doc = new SampledTimingDocument(); doc.Edit(Address, Plan(.5f), _ => { });
        Assert.Throws<ArgumentException>(() => doc.Edit(Address with { Pitch = 61 }, Plan(.5f), _ => { }));
    }

    [Fact]
    public void EditingLiveNoteReplacesDetachedLocatorInOneUndoRecord()
    {
        var document = new SampledTimingDocument();
        document.Edit(Address, Plan(.25f), _ => { }); document.MarkSaved();
        var moved = Address with { Position = 1200 };
        var edit = document.Edit(moved, Plan(.75f), _ => { }, replaceDetachedLocator: true)!;
        Assert.Null(document.Get(Address)); Assert.Equal(.75f, document.Get(moved)!.PhonemeCoefficient(moved.Topology));
        edit.ApplyBefore();
        Assert.Null(document.Get(moved)); Assert.Equal(.25f, document.Get(Address)!.PhonemeCoefficient(Address.Topology));
        Assert.False(document.IsDirty);
        edit.ApplyAfter(); Assert.True(document.IsDirty);
        Assert.Single(document.Snapshot());
    }

    [Fact]
    public void FailedReplacementHistoryRestoresDetachedData()
    {
        var document = new SampledTimingDocument();
        document.Edit(Address, Plan(.25f), _ => { }); document.MarkSaved();
        Assert.Throws<InvalidOperationException>(() => document.Edit(Address with { Position = 1200 }, Plan(.75f),
            _ => throw new InvalidOperationException(), replaceDetachedLocator: true));
        Assert.Single(document.Snapshot()); Assert.False(document.IsDirty);
        Assert.Equal(.25f, document.Get(Address)!.PhonemeCoefficient(Address.Topology));
    }

    [Theory]
    [InlineData("{\"Version\":2,\"Entries\":[]}")]
    [InlineData("{\"Version\":1,\"Entries\":null}")]
    [InlineData("{\"Version\":1,\"Entries\":[null]}")]
    public void InvalidArchiveNeverLooksLikeEmptySuccess(string json)
    {
        InArchive(path =>
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            using (var text = new StreamWriter(zip.CreateEntry(SampledTimingProjectArchive.EntryPath).Open())) text.Write(json);
            Assert.Throws<InvalidDataException>(() => SampledTimingProjectArchive.Read(path));
        });
    }

    [Fact]
    public void DuplicateZipEntriesAreRejected()
    {
        InArchive(path =>
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
                for (int i = 0; i < 2; i++)
                    using (var text = new StreamWriter(zip.CreateEntry(SampledTimingProjectArchive.EntryPath).Open())) text.Write("{\"Version\":1,\"Entries\":[]}");
            Assert.Throws<InvalidDataException>(() => SampledTimingProjectArchive.Read(path));
        });
    }

    [Fact]
    public void InvalidSaveDoesNotReplaceExistingProject()
    {
        InArchive(path =>
        {
            byte[] before = File.ReadAllBytes(path);
            Assert.Throws<InvalidDataException>(() => SampledTimingProjectArchive.Write(path,
                new Dictionary<TimingNoteAddress, SampledTimingPlan> { [Address with { Topology = "i a" }] = Plan(.5f) }));
            Assert.Equal(before, File.ReadAllBytes(path));
        });
    }

    [Fact]
    public void OversizeCompressedEntryIsRejectedBeforeDeserialization()
    {
        InArchive(path =>
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            using (var stream = zip.CreateEntry(SampledTimingProjectArchive.EntryPath, CompressionLevel.Optimal).Open())
                stream.Write(new byte[4 * 1024 * 1024 + 1]);
            Assert.Throws<InvalidDataException>(() => SampledTimingProjectArchive.Read(path));
        });
    }

    [Fact]
    public void PlanDoesNotAliasMutableCallerArray()
    {
        TimingAdjustment[] values = [new(TimingAdjustment.NoteCoefficient, -1, .12345679f)];
        var plan = new SampledTimingPlan(Address.Topology, values);
        values[0] = new(TimingAdjustment.NoteCoefficient, -1, .75f);
        Assert.Equal(.12345679f, plan.ExecutableCoefficient(Address.Topology));
    }

    [Fact]
    public void RenderEpochRejectsOtherDocumentsUndoAndClosedLifetimes()
    {
        var revision = new TimingRenderRevision();
        var a = revision.Capture("A"); Assert.True(revision.IsCurrent(a, "A"));
        Assert.False(new TimingRenderRevision().IsCurrent(a, "A"));
        revision.Advance();
        var b = revision.Capture("B"); Assert.False(revision.IsCurrent(a, "A"));
        Assert.True(revision.IsCurrent(b, "B"));
        revision.Advance(); // Undo restores A's values, but not its old render.
        Assert.False(revision.IsCurrent(a, "A"));
        var latest = revision.Capture("A"); revision.Close();
        Assert.False(revision.IsCurrent(latest, "A"));
    }

    private static void InArchive(Action<string> run)
    {
        string directory = Path.Combine(Path.GetTempPath(), "timing-model-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "test.vpr");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var text = new StreamWriter(zip.CreateEntry("Project/keep.txt").Open())) text.Write("keep");
            run(path);
        }
        finally { Directory.Delete(directory, true); }
    }
}
