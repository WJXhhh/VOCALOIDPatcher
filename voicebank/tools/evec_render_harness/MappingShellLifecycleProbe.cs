using System.Collections;
using System.Reflection;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.DSE;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    // Exercise the native owners implicated in the Editor setDuration crash.
    // A returned mapping must retain no Track/Part/score, even after callbacks
    // reenter Dispose. Keep a live preparation while other shells are recycled.
    private static void ValidateMappingShellLifecycle(WIVSMSequenceManager manager, DSEManager dse,
        DatabaseManager database)
    {
        using var backend = new LocalConsonantTimingBackend(manager, dse, database);
        using var heldSource = CreateStandaloneFixture(manager, 3, 480, 0, 7680);
        using var held = backend.PrepareCoefficient(heldSource.Notes[1], 0);
        var expected = held.Query(.5f, 0).Boundaries;
        var poolField = typeof(LocalConsonantTimingBackend).GetField("_coefficientMappings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var windowsField = typeof(LocalConsonantTimingBackend.PreparedCoefficientTiming).GetField("_windows", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var seenShells = new HashSet<nint>();
        int reentries = 0;
        for (int cycle = 0; cycle < 96; cycle++)
        {
            int count = new[] { 18, 1, 9, 3 }[cycle % 4];
            using var source = CreateStandaloneFixture(manager, count, cycle % 2 == 0 ? 480 : 120, 0, 12000);
            if (!source.Part.SetVoiceBankID(cycle % 3 == 0 ? "BKKP765AEHXWSKDB" : "BCNFCY43LB2LZCD4") ||
                !source.Sequence.Commit(false))
                throw new InvalidOperationException("Cannot initialize mapping lifecycle source.");
            using var prepared = backend.PrepareCoefficient(source.Notes[count / 2], cycle + 1);
            _ = prepared.QueryTransitions(new(source.Notes[count / 2].Phonemes, []), cycle + 1);
            _ = prepared.Query(cycle % 2 == 0 ? .5f : .65f, cycle + 1);
            var windows = (LocalConsonantTimingBackend.CoefficientWindow[])windowsField.GetValue(prepared)!;
            seenShells.UnionWith(windows.Select(w => (nint)w.Sequence));
            // Native Commit synchronously invokes this observer during return.
            var sequence = windows[0].Sequence;
            using var observer = new WIVSMUpdateObserver();
            observer.Notified += _ => { reentries++; prepared.Dispose(); };
            if (!sequence.AddUpdateObserver(observer)) throw new InvalidOperationException("Cannot observe mapping retirement.");
            try { prepared.Dispose(); prepared.Dispose(); }
            finally { sequence.RemoveUpdateObserver(observer); }

            var pooled = ((IEnumerable)poolField.GetValue(backend)!).Cast<object>().ToArray();
            if (pooled.Length is < 1 or > 5 || pooled.Any(item => item is not WIVSMSequence) ||
                pooled.Cast<WIVSMSequence>().Any(s => !s.IsOpen || !manager.HasSequence((nint)s) || s.IsStaged || s.NumTrack != 0) ||
                pooled.Cast<WIVSMSequence>().Select(s => (nint)s).Distinct().Count() != pooled.Length)
                throw new InvalidOperationException("Mapping pool retained native children or duplicate/released owners.");
            if (!held.Query(.5f, 0).Boundaries.SequenceEqual(expected))
                throw new InvalidOperationException("Mapping retirement changed a live preparation.");
            if (cycle % 8 == 0)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            }
        }
        if (reentries == 0 || seenShells.Count > 5)
            throw new InvalidOperationException("Mapping lifecycle did not exercise reentry or bounded shell reuse.");
        Console.WriteLine($"timing.mapping_shell_lifecycle=passed;cycles=96;reentries={reentries};shells={seenShells.Count}");
    }
}
