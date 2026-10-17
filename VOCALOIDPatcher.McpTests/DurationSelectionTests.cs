using System.IO.Compression;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.DurationSelection;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class DurationSelectionTests
{
    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(0)] [InlineData(-1)] [InlineData(33)]
    public void InvalidRatiosCannotEnterDocument(double ratio) => Assert.Throws<InvalidDataException>(() => new DurationSelectionPlan("k a", ratio));

    [Fact]
    public void CanonicalPreferenceIncludesPolicyAndExactTopology()
    {
        Assert.Equal(new DurationSelectionPlan("k a", .50000001).Fingerprint, new DurationSelectionPlan("k a", .5).Fingerprint);
        Assert.NotEqual(new DurationSelectionPlan("k a", .5).Fingerprint, new DurationSelectionPlan("k a", .5, DurationCandidatePolicy.ExpandedExperimental).Fingerprint);
        Assert.NotEqual(new DurationSelectionPlan("k a", .5).Fingerprint, new DurationSelectionPlan("k a i", .5).Fingerprint);
        Assert.True(new DurationSelectionPlan("k a").IsDefault);
        Assert.False(new DurationSelectionPlan("k a", 1, DurationCandidatePolicy.ExpandedExperimental).IsDefault);
    }
    [Fact]
    public void UndoRedoAreOrderedAndNeverAttachToReusedPointer()
    {
        var doc = new DurationSelectionDocument();
        var old = new DurationNoteKey(123, 1); var reused = new DurationNoteKey(123, 2);
        var history = new List<ICustomParameterHistoryEdit>();
        doc.Edit(old, new("k a", .5), history.Add);
        doc.Edit(reused, new("k a", 2), history.Add);
        doc.MarkSaved(); long revision = doc.Revision;
        history[1].ApplyBefore(); history[1].AfterApply();
        Assert.Null(doc.Get(reused)); Assert.Equal(.5f, doc.Get(old)!.Ratio); Assert.True(doc.IsDirty);
        history[1].ApplyAfter(); history[1].AfterApply();
        Assert.Equal(2f, doc.Get(reused)!.Ratio); Assert.False(doc.IsDirty); Assert.True(doc.Revision > revision);
        Assert.Equal(2, history.Count);
    }
    [Fact]
    public void FailedHistoryPushRollsBackValueWithoutPublishing()
    {
        var doc = new DurationSelectionDocument(); int notifications = 0;
        doc.Changed += _ => notifications++;
        Assert.Throws<InvalidOperationException>(() => doc.Edit(new(1, 1), new("a", 2), _ => throw new InvalidOperationException()));
        Assert.Empty(doc.Snapshot()); Assert.False(doc.IsDirty); Assert.Equal(0, notifications);
    }
    [Fact]
    public void AuthorizedNativeRecreationPreservesDirtyIdentityAndOldHistoryCommands()
    {
        var doc = new DurationSelectionDocument(); var history = new List<ICustomParameterHistoryEdit>();
        var before = new DurationNoteKey(101, 1); var after = new DurationNoteKey(202, 2);
        doc.Edit(before, new("a", .5), history.Add); doc.MarkSaved();
        doc.Rekey(new Dictionary<DurationNoteKey, DurationNoteKey> { [before] = after });
        Assert.False(doc.IsDirty); Assert.Equal(.5f, doc.Get(after)!.Ratio);
        history[0].ApplyBefore(); history[0].AfterApply(); Assert.Null(doc.Get(after));
        history[0].ApplyAfter(); history[0].AfterApply(); Assert.Equal(.5f, doc.Get(after)!.Ratio); Assert.False(doc.IsDirty);
    }
    [Fact]
    public void ArchivePreservesOtherEntriesAndRejectsAlgorithmMismatchBeforeWrite()
    {
        string folder = Path.Combine(Path.GetTempPath(), "duration-archive-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "project.vpr");
        try
        {
            using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
            using (var stream = new StreamWriter(zip.CreateEntry("Project/sequence.json").Open())) stream.Write("{\"keep\":true}");
            var entry = new DurationSelectionArchive.Entry(new(0, 1, 2, 960, 480, 60, "bank", "@N x 7"), .5f, DurationCandidatePolicy.Native, 1);
            DurationSelectionArchive.Write(file, [entry]);
            Assert.Equal(entry, Assert.Single(DurationSelectionArchive.Read(file)));
            byte[] saved = File.ReadAllBytes(file);
            Assert.Throws<InvalidDataException>(() => DurationSelectionArchive.Write(file, [entry with { Algorithm = 999 }]));
            Assert.Equal(saved, File.ReadAllBytes(file));
            using (var zip = ZipFile.OpenRead(file)) Assert.NotNull(zip.GetEntry("Project/sequence.json"));
            DurationSelectionArchive.Write(file, []); Assert.Empty(DurationSelectionArchive.Read(file));
            Assert.Single(Directory.GetFiles(folder));
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public void ArchiveRejectsDuplicateLocatorsEvenWhenIdentityDiffers()
    {
        string folder = Path.Combine(Path.GetTempPath(), "duration-duplicate-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); string file = Path.Combine(folder, "project.vpr");
        try
        {
            using (ZipFile.Open(file, ZipArchiveMode.Create)) { }
            var entry = new DurationSelectionArchive.Entry(new(0, 0, 0, 0, 480, 60, "bank", "a"), .5f, DurationCandidatePolicy.Native, 1);
            Assert.Throws<InvalidDataException>(() => DurationSelectionArchive.Write(file, [entry, entry with { Address = entry.Address with { Pitch = 61 } }]));
            Assert.Empty(DurationSelectionArchive.Read(file));
        }
        finally { Directory.Delete(folder, true); }
    }
}
