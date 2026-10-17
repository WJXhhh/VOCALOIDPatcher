using System.IO.Compression;
using VOCALOIDPatcher.ConsonantTiming;
using Xunit;

namespace VOCALOIDPatcher.McpTests;

public sealed class TimingBackendCompatibilityTests
{
    private sealed class Backend : IDisposable
    {
        internal int Disposals;
        public void Dispose() => Disposals++;
    }

    [Fact]
    public void UnsupportedBackendCannotPreventDocumentLoadSaveOrHistory()
    {
        int attempts = 0;
        using var backend = new TimingBackendLease<Backend>(() => { attempts++; throw new NotSupportedException("unknown component tuple"); });
        var address = new TimingNoteAddress(0, 0, 0, 960, 480, 60, "bank", "k a");
        var plan = new SampledTimingPlan(address.Topology, [new(TimingAdjustment.NoteCoefficient, -1, .375f), new("future.transition.gain", 0, .3f)]);
        var doc = new SampledTimingDocument();
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".vpr");
        try
        {
            using (ZipFile.Open(path, ZipArchiveMode.Create)) { }
            doc.Load(new Dictionary<TimingNoteAddress, SampledTimingPlan> { [address] = plan });
            backend.IfCreated(_ => throw new Exception("Must not invalidate a nonexistent engine"));
            SampledTimingProjectArchive.Write(path, doc.Snapshot());
            Assert.Equal(0, attempts);
            Assert.Throws<NotSupportedException>(() => backend.Require());
            Assert.False(backend.IsCreated);
            Assert.NotNull(backend.Fault);
            var edit = doc.Edit(address, null, _ => { })!;
            edit.ApplyBefore(); edit.AfterApply();
            SampledTimingProjectArchive.Write(path, doc.Snapshot());
            Assert.Equal(plan.Fingerprint, SampledTimingProjectArchive.Read(path)[address].Fingerprint);
            Assert.Equal(1, attempts);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FactoryCanRecoverAfterFailureAndOwnsOnlyOneSuccessfulInstance()
    {
        int attempts = 0;
        var instance = new Backend();
        var lease = new TimingBackendLease<Backend>(() => ++attempts == 1 ? throw new NotSupportedException() : instance);
        Assert.Throws<NotSupportedException>(() => lease.Require());
        Assert.Same(instance, lease.Require());
        Assert.Same(instance, lease.Require());
        Assert.Null(lease.Fault);
        Assert.Equal(2, attempts);
        lease.Dispose(); lease.Dispose();
        Assert.Equal(1, instance.Disposals);
        Assert.Throws<ObjectDisposedException>(() => lease.Require());
    }

    [Fact]
    public void ClosingDataOnlyLeaseDoesNotInitializeBackend()
    {
        var lease = new TimingBackendLease<Backend>(() => throw new Exception("Must not initialize"));
        lease.Dispose();
        var instance = new Backend();
        new TimingBackendLease<Backend>(() => instance, instance: instance).Dispose();
        Assert.Equal(1, instance.Disposals);
        new TimingBackendLease<Backend>(() => instance, ownsInstance: false, instance: instance).Dispose();
        Assert.Equal(1, instance.Disposals);
    }

    [Fact]
    public void ProfileRequiresBothComponentHashesAndKeepsLayoutSeparateFromEditorVersion()
    {
        var profile = TimingNativeProfile.Known;
        Assert.True(profile.Matches(profile.DseSha256, profile.VsmSha256));
        Assert.False(profile.Matches(new string('0', 64), profile.VsmSha256));
        Assert.False(profile.Matches(profile.DseSha256, new string('0', 64)));
        Assert.False(profile.Matches(profile.VsmSha256, profile.DseSha256));
        Assert.NotEqual(0UL, profile.Fingerprint);
        Assert.Equal(0x1d570, profile.StateParser);
    }

    [Fact]
    public void CertifiedProfilesRejectMixedComponentTuples()
    {
        Assert.Equal(2, TimingNativeProfile.Supported.Count);
        var old = TimingNativeProfile.Supported[0];
        var current = TimingNativeProfile.Supported[1];
        Assert.Same(old, TimingNativeProfile.Identify(old.DseSha256, old.VsmSha256));
        Assert.Same(current, TimingNativeProfile.Identify(current.DseSha256, current.VsmSha256));
        Assert.Null(TimingNativeProfile.Identify(old.DseSha256, current.VsmSha256));
        Assert.Null(TimingNativeProfile.Identify(current.DseSha256, old.VsmSha256));
        Assert.NotEqual(old.Fingerprint, current.Fingerprint);
        Assert.NotEqual(old.VsmPrepare, current.VsmPrepare);
    }
}
