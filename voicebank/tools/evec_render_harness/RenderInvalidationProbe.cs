using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    private sealed class InvalidationCounts
    {
        internal int Commits, StagedCommits, VelocityWrites, StartRequests, StopRequests, RefreshRequests;
        internal readonly Dictionary<(int Part, int Note), int> Writes = new();
    }

    private static nint _invalidationSource;
    private static InvalidationCounts? _invalidationCounts;
    private static readonly Dictionary<nint, (int Part, int Note)> InvalidationNotes = new();
    private static Dictionary<string, int>? _commitNotifications;
    private static int _failInvalidationWriteAt;

    // Invoke the actual private host invalidation method from an explicitly
    // isolated build. Reflection avoids a product/project dependency or loading
    // its module initializer. All sequences belong to this process.
    private static int ProbeRenderInvalidation(WIVSMSequenceManager manager, string directory)
    {
        string patcherPath = Path.GetFullPath(Environment.GetEnvironmentVariable("VOCALOID_SCOPE_PROBE_PATCHER")
            ?? throw new InvalidOperationException("Set VOCALOID_SCOPE_PROBE_PATCHER to an isolated validation assembly."));
        if (AssemblyName.GetAssemblyName(patcherPath).Name != "VOCALOIDPatcher.IsolatedValidation")
            throw new InvalidOperationException("The scope probe requires PatcherIsolatedValidation=true.");
        var patcher = Assembly.LoadFrom(patcherPath);
        var host = patcher.GetType("VOCALOIDPatcher.ConsonantTiming.SampledTimingHost", true)!;
        var entryType = host.GetNestedType("Entry", BindingFlags.NonPublic)!;
        var invalidate = host.GetMethod("InvalidateTargets", BindingFlags.Static | BindingFlags.NonPublic)!;
        var binding = patcher.GetType("VOCALOIDPatcher.ConsonantTiming.SampledTimingVsmBinding", true)!;
        var addressOf = binding.GetMethod("Address", BindingFlags.Static | BindingFlags.NonPublic)!;
        var addressType = patcher.GetType("VOCALOIDPatcher.ConsonantTiming.TimingNoteAddress", true)!;
        var refresh = patcher.GetType("VOCALOIDPatcher.Patch.Patches.RenderedWaveCachePatch", true)!
            .GetMethod("InvalidateAndRefreshPart", BindingFlags.Static | BindingFlags.NonPublic)!;
        var harmonyAssembly = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(patcherPath)!, "0Harmony.dll"));
        var harmonyType = harmonyAssembly.GetType("HarmonyLib.Harmony", true)!;
        var harmonyMethod = harmonyAssembly.GetType("HarmonyLib.HarmonyMethod", true)!;
        object harmony = Activator.CreateInstance(harmonyType, ["v6patch.isolated-render-invalidation-probe"])!;
        var patch = harmonyType.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters()[0].ParameterType == typeof(MethodBase));
        void Install(MethodBase target, MethodInfo? prefix = null, MethodInfo? postfix = null, MethodInfo? finalizer = null)
        {
            object? Method(MethodInfo? method) => method == null ? null : Activator.CreateInstance(harmonyMethod, [method]);
            object?[] arguments = patch.GetParameters().Select(p => p.Name switch
            {
                "original" => (object)target, "prefix" => Method(prefix),
                "postfix" => Method(postfix), "finalizer" => Method(finalizer), _ => null
            }).ToArray();
            patch.Invoke(harmony, arguments);
        }
        MethodInfo Own(string name) => typeof(Program).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
        void Hook(MethodBase target, string prefix, string? postfix = null)
            => Install(target, Own(prefix), postfix == null ? null : Own(postfix));
        Hook(typeof(WIVSMSequence).GetMethod(nameof(WIVSMSequence.Commit), [typeof(bool)])!, nameof(InvalidationCommitBefore), nameof(InvalidationCommitAfter));
        Hook(typeof(WIVSMNote).GetProperty(nameof(WIVSMNote.NoteVelocity))!.SetMethod!, nameof(InvalidationVelocityBefore));
        Hook(typeof(WIVSMSequence).GetMethod(nameof(WIVSMSequence.StartAsyncRendering))!, nameof(InvalidationStartBefore));
        Hook(typeof(WIVSMSequence).GetMethod(nameof(WIVSMSequence.StopAsyncRendering))!, nameof(InvalidationStopBefore));
        Hook(refresh, nameof(InvalidationRefreshBefore));
        Directory.CreateDirectory(directory);
        using var dse = new TimingDseCounters();
        var results = new List<object>();
        IReadOnlyList<object> inputScopes = [];
        IReadOnlyList<object> nativeRenders = [];
        IReadOnlyList<object> notifications = [];
        try
        {
            foreach (int count in new[] { 3, 12, 33, 64 })
            {
                using var fixture = CreateStandaloneFixture(manager, count, 240, 0, count * 240L + 1920);
                var source = fixture.Sequence;
                var part = fixture.Part;
                if (!part.SetVoiceBankID("BL8CEAM5N4XN3LFK")) throw new InvalidOperationException("Cannot select probe voicebank.");
                foreach (var note in fixture.Notes) note.SetPhonemes("a", true, 4);
                if (count == 64) { fixture.Notes[0].NoteVelocity = 0; fixture.Notes[^1].NoteVelocity = 127; }
                var second = part.Parent!.DuplicatePart(new VSMAbsTick(count * 240L + 3840), part)!;
                if (!source.InterpolatedMasterVolume() || !source.Commit(false)) throw new InvalidOperationException("Cannot initialize scope fixture.");
                source.StopAsyncRendering();
                InvalidationNotes.Clear();
                for (int i = 0; i < count; i++)
                {
                    InvalidationNotes[(nint)fixture.Notes[i]] = (0, i);
                    InvalidationNotes[(nint)second.GetNote((ulong)i)!] = (1, i);
                }
                object entry = Activator.CreateInstance(entryType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, [source, null], null)!;
                try
                {
                    foreach (var selection in new[]
                    {
                        (Name: "one-middle", Indices: new[] { count / 2 }),
                        (Name: "three-adjacent", Indices: new[] { count / 2 - 1, count / 2, count / 2 + 1 }),
                        (Name: "all-targets", Indices: Enumerable.Range(0, count).ToArray()),
                        (Name: "two-ends", Indices: new[] { 0, count - 1 }),
                        (Name: "duplicate-targets", Indices: new[] { count / 2, count / 2 })
                    })
                    {
                        Array addresses = Array.CreateInstance(addressType, selection.Indices.Length);
                        for (int i = 0; i < selection.Indices.Length; i++)
                            addresses.SetValue(addressOf.Invoke(null, [fixture.Notes[selection.Indices[i]]]), i);
                        _invalidationSource = (nint)source;
                        var counters = new InvalidationCounts();
                        var dseBefore = dse.Read();
                        var beforeVelocity = fixture.Notes.Select(n => n.NoteVelocity).ToArray();
                        _invalidationCounts = counters;
                        long started = Stopwatch.GetTimestamp();
                        try { invalidate.Invoke(null, [entry, addresses]); }
                        finally { _invalidationCounts = null; }
                        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        var work = dse.Read().Since(dseBefore);
                        var expected = selection.Indices.SelectMany(n => Enumerable.Range(Math.Max(0, n - 16),
                            Math.Min(count - 1, n + 16) - Math.Max(0, n - 16) + 1)).ToHashSet();
                        if (source.IsStaged || !fixture.Notes.Select(n => n.NoteVelocity).SequenceEqual(beforeVelocity) ||
                            counters.Writes.Keys.Any(k => k.Part != 0) || work.Starts != 0 || work.Steps != 0 ||
                            counters.Commits != 1 || counters.StagedCommits != 1 || counters.RefreshRequests != 1 ||
                            !expected.SetEquals(counters.Writes.Keys.Select(k => k.Note)) || counters.Writes.Values.Any(n => n != 2))
                            throw new InvalidOperationException("Invalidation changed persistent input, another Part, or synthesized prematurely.");
                        results.Add(new
                        {
                            count, selection.Name, targets = selection.Indices, elapsedMs = elapsed,
                            commits = counters.Commits, stagedCommits = counters.StagedCommits,
                            velocityWrites = counters.VelocityWrites, startRequests = counters.StartRequests,
                            stopRequests = counters.StopRequests, refreshRequests = counters.RefreshRequests,
                            distinctNotes = counters.Writes.Count,
                            perNoteWrites = counters.Writes.OrderBy(p => p.Key.Note).Select(p => new { part = p.Key.Part, note = p.Key.Note, writes = p.Value }).ToArray(),
                            otherPartTouched = false, nativePcm = work, originalValuesRestored = true, staged = false
                        });
                        Console.WriteLine($"invalidation.notes={count};case={selection.Name};commits={counters.Commits};writes={counters.VelocityWrites};distinct={counters.Writes.Count};pcm_steps={work.Steps}");
                    }
                    if (count == 12)
                    {
                        void InvalidateAll()
                        {
                            Array addresses = Array.CreateInstance(addressType, count);
                            for (int i = 0; i < count; i++) addresses.SetValue(addressOf.Invoke(null, [fixture.Notes[i]]), i);
                            invalidate.Invoke(null, [entry, addresses]);
                        }
                        inputScopes = ProbeObservedInputScopes(host, entryType, entry, fixture, second, dse, InvalidateAll);
                        nativeRenders = ProbeNativeEditRendering(fixture, second, dse, InvalidateAll);
                    }
                    if (count == 64)
                    {
                        void InvalidateAll()
                        {
                            Array addresses = Array.CreateInstance(addressType, count);
                            for (int i = 0; i < count; i++) addresses.SetValue(addressOf.Invoke(null, [fixture.Notes[i]]), i);
                            invalidate.Invoke(null, [entry, addresses]);
                        }
                        // Establish audio equivalence before deliberately
                        // injecting staged edits, setter failures and rollback.
                        // Those cases must not contaminate the render baseline.
                        nativeRenders = nativeRenders.Concat(ProbeNativeEditRendering(fixture, second, dse, InvalidateAll, false)).ToArray();
                        foreach (var invalidContext in new[]
                        {
                            (Name: "oversized-context", Notes: fixture.Notes.ToArray()),
                            (Name: "duplicate-in-context", Notes: new[] { fixture.Notes[0], fixture.Notes[0] }),
                            (Name: "foreign-part-in-context", Notes: new[] { fixture.Notes[0], second.GetNote(0)! })
                        })
                        {
                            var counts = new InvalidationCounts();
                            _invalidationCounts = counts;
                            bool rejected = false;
                            try { binding.GetMethod("InvalidateRenderedPart", BindingFlags.Static | BindingFlags.NonPublic)!
                                .Invoke(null, [part, invalidContext.Notes]); }
                            catch (TargetInvocationException e) when (e.InnerException is ArgumentException) { rejected = true; }
                            finally { _invalidationCounts = null; }
                            if (!rejected || source.IsStaged || counts.Commits != 0 || counts.VelocityWrites != 0)
                                throw new InvalidOperationException("Invalid bounded context mutated native state.");
                            results.Add(new { count, name = invalidContext.Name, rejectedBeforeMutation = true });
                        }
                        fixture.Notes[4].NoteVelocity = 80;
                        _invalidationCounts = new();
                        bool stagedRejected = false;
                        try { InvalidateAll(); }
                        catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { stagedRejected = true; }
                        finally { _invalidationCounts = null; }
                        if (!stagedRejected || !source.IsStaged || fixture.Notes[4].NoteVelocity != 80)
                            throw new InvalidOperationException("Invalidation consumed a staged user edit.");
                        if (!source.Rollback()) throw new InvalidOperationException("Cannot discard staged fixture edit.");
                        results.Add(new { count, name = "staged-user-edit", rejectedWithoutConsumingEdit = true });
                        foreach (string kind in new[] { "empty", "stale-pitch", "missing-track", "missing-note", "two-parts" })
                        {
                            Array addresses = Array.CreateInstance(addressType, kind == "empty" ? 0 : kind == "two-parts" ? 2 : 1);
                            if (addresses.Length > 0)
                            {
                                object address = addressOf.Invoke(null, [fixture.Notes[count / 2]])!;
                                if (kind == "stale-pitch") addressType.GetProperty("Pitch")!.SetValue(address, 99);
                                if (kind == "missing-track") addressType.GetProperty("Track")!.SetValue(address, -1);
                                if (kind == "missing-note") addressType.GetProperty("Note")!.SetValue(address, -1);
                                addresses.SetValue(address, 0);
                                if (kind == "two-parts") addresses.SetValue(addressOf.Invoke(null, [second.GetNote((ulong)(count / 2))]), 1);
                            }
                            var counts = new InvalidationCounts();
                            _invalidationCounts = counts;
                            try { invalidate.Invoke(null, [entry, addresses]); }
                            finally { _invalidationCounts = null; }
                            int expectedParts = kind == "two-parts" ? 2 : 0;
                            if (counts.Commits != expectedParts || counts.RefreshRequests != expectedParts ||
                                counts.VelocityWrites != expectedParts * 66 || source.IsStaged)
                                throw new InvalidOperationException("Empty/stale or cross-Part invalidation failed: " + kind);
                            results.Add(new { count, name = kind, commits = counts.Commits, refreshRequests = counts.RefreshRequests,
                                velocityWrites = counts.VelocityWrites, distinctNotes = counts.Writes.Count });
                        }
                        var originals = fixture.Notes.Select(n => n.NoteVelocity).ToArray();
                        _invalidationCounts = new(); _failInvalidationWriteAt = 2;
                        bool failed = false;
                        try { InvalidateAll(); }
                        catch (TargetInvocationException e) when (e.InnerException is IOException) { failed = true; }
                        finally { _invalidationCounts = null; _failInvalidationWriteAt = 0; }
                        var scopeType = patcher.GetType("VOCALOIDPatcher.ConsonantTiming.TimingCacheInvalidationScope", true)!;
                        if (!failed || !originals.SequenceEqual(fixture.Notes.Select(n => n.NoteVelocity)) ||
                            (bool)scopeType.GetMethod("IsActive", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [(nint)source])!)
                            throw new InvalidOperationException("Failed invalidation did not restore values and notification scope.");
                        if (source.IsStaged && !source.Rollback()) throw new InvalidOperationException("Cannot discard failed probe staging.");
                        results.Add(new { count, name = "setter-failure", originalValuesRestored = true, internalScopeRestored = true });
                        notifications = ProbeInternalCommitNotifications(patcher, source, fixture.Notes[4], InvalidateAll, Install, Own);
                    }
                }
                finally
                {
                    (entryType.GetField("Session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(entry) as IDisposable)?.Dispose();
                    _invalidationSource = 0;
                }
            }
            File.WriteAllText(Path.Combine(directory, "render-invalidation.json"), JsonSerializer.Serialize(new
            {
                date = DateTimeOffset.Now, sdk = typeof(WIVSMSequence).Assembly.GetName().Version?.ToString(),
                patcherSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(patcherPath))),
                scope = "Actual SampledTimingHost.InvalidateTargets in isolated validation assembly; native Commit/setters and renderer observed. Final notification cases install eight actual production Commit patches with counted/stubbed consumers; no Editor UI or full DSP chain.",
                results, inputScopes, nativeRenders, notifications
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally
        {
            _invalidationCounts = null; _invalidationSource = 0; InvalidationNotes.Clear();
            harmonyType.GetMethod("UnpatchAll", [typeof(string)])!.Invoke(harmony, ["v6patch.isolated-render-invalidation-probe"]);
        }
    }

    private static IReadOnlyList<object> ProbeInternalCommitNotifications(Assembly patcher, WIVSMSequence source,
        WIVSMNote note, Action invalidate, Action<MethodBase, MethodInfo?, MethodInfo?, MethodInfo?> install,
        Func<string, MethodInfo> own)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo Method(string type, string name) => patcher.GetType("VOCALOIDPatcher." + type, true)!
            .GetMethods(flags).Single(m => m.Name == name);
        string[] consumers =
        [
            "ConsonantTiming.SampledTimingHost.BeforeSourceChange", "ConsonantTiming.SampledTimingHost.EndInputMutation",
            "ConsonantTiming.ConsonantTimingRuntime.SourceChanged", "PhonemeVolume.PhonemeVolumeHost.SourceChanged",
            "ConsonantTiming.Smoothing.TransitionSmoothingHost.SourceChanged", "AceParameters.AceParameterMappingService.OnSequenceCommit",
            "Evec.EvecRenderCoordinator.SequenceChanged", "Patch.Patches.SegmentedPhonemeRenderCoordinator.SequenceChanged",
            "VoiceComp.VoiceCompHost.NativeChanged", "Mcp.McpRevisionTracker.ChangedForSource",
            "Evec.EvecService.ApplyPendingTempoTiming", "Evec.EvecService.CompletePendingHistory",
            "BreathVolume.CustomParameterHistoryCoordinator.OnNativeCommit"
        ];
        foreach (string consumer in consumers)
        {
            int split = consumer.LastIndexOf('.');
            install(Method(consumer[..split], consumer[(split + 1)..]), own(nameof(CountCommitNotification)), null, null);
        }
        install(Method("Config.Settings", "get_McpEnabled"), own(nameof(ProbeMcpEnabled)), null, null);
        var commit = typeof(WIVSMSequence).GetMethod(nameof(WIVSMSequence.Commit), [typeof(bool)])!;
        foreach (string name in new[] { "ConsonantTimingCommitPatch", "AceParameterCommitMappingPatch", "McpRevisionCommitPatch",
            "EvecRenderSequenceCommitPatch", "SegmentedPhonemeSequenceCommitPatch", "EvecSequenceTempoCommitPatch",
            "BreathVolumeCommitHistoryPatch", "VoiceCompCommitPatch" })
        {
            var type = patcher.GetType("VOCALOIDPatcher.Patch.Patches." + name, true)!;
            install(commit, type.GetMethod("Prefix", flags), type.GetMethod("Postfix", flags), type.GetMethod("Finalizer", flags));
        }
        var results = new List<object>();
        try
        {
            void Check(string kind, Action edit, bool suppressed)
            {
                _commitNotifications = new();
                edit();
                var counts = new Dictionary<string, int>(_commitNotifications);
                if (suppressed ? counts.Count != 0 : consumers.Any(c => !counts.ContainsKey("VOCALOIDPatcher." + c)))
                    throw new InvalidOperationException("Internal commit leaked notifications or a real edit lost notifications: " + kind);
                results.Add(new { kind, suppressed, consumers = counts });
                Console.WriteLine($"commit_notifications.case={kind};consumer_calls={counts.Values.Sum()}");
            }
            Check("internal-invalidation", invalidate, true);
            Check("real-edit-no-history", () => { note.NoteVelocity = 81; if (!source.Commit(false)) throw new InvalidOperationException("Commit failed."); }, false);
            Check("real-edit-with-history", () => { note.NoteVelocity = 82; if (!source.Commit(true)) throw new InvalidOperationException("Commit failed."); }, false);
            Check("internal-invalidation-after-history", invalidate, true);
            source.Undo();
            if (note.NoteVelocity != 81) throw new InvalidOperationException("Internal invalidation introduced an undo step.");
            source.Redo();
            if (note.NoteVelocity != 82) throw new InvalidOperationException("Internal invalidation damaged redo.");
            results.Add(new { kind = "native-undo-redo", undoVelocity = 81, redoVelocity = 82 });
        }
        finally { _commitNotifications = null; }
        return results;
    }

    private static bool CountCommitNotification(MethodBase __originalMethod)
    {
        if (_commitNotifications is { } counts)
        {
            string name = __originalMethod.DeclaringType!.FullName + "." + __originalMethod.Name;
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }
        return false;
    }

    private static bool ProbeMcpEnabled(ref bool __result) { __result = true; return false; }

    private static IReadOnlyList<object> ProbeNativeEditRendering(StandaloneFixture fixture,
        WIVSMMidiPart second, TimingDseCounters counters, Action invalidateAll, bool includeCarriers = true)
    {
        var source = fixture.Sequence;
        var part = fixture.Part;
        int[] started = new int[2], completed = new int[2], canceled = new int[2];
        int Index(WIVSMMidiPart value) => (nint)value == (nint)part ? 0 : (nint)value == (nint)second ? 1 :
            throw new InvalidOperationException("Unexpected render Part.");
        using var observer = new WIVSMRendererObserver();
        observer.Started += (_, e) => started[Index(e.MidiPart)]++;
        observer.Completed += (_, e) => completed[Index(e.MidiPart)]++;
        observer.Canceled += (_, e) => canceled[Index(e.MidiPart)]++;
        if (!source.AddRendererObserver(observer)) throw new InvalidOperationException("Cannot observe native render.");
        var result = new List<object>();
        void Settle()
        {
            var watch = Stopwatch.StartNew();
            long quiet = 0;
            while (watch.Elapsed.TotalSeconds < 30)
            {
                bool activity = false;
                for (int pump = 0; pump < 64 && source.InvokeRendererObserver(); pump++) activity = true;
                if (activity || !source.IsFinishedRendering || !part.HasValidRenderedWave || !second.HasValidRenderedWave)
                    quiet = 0;
                else
                {
                    if (quiet == 0) quiet = Stopwatch.GetTimestamp();
                    if (Stopwatch.GetElapsedTime(quiet).TotalMilliseconds >= 100) return;
                }
                Thread.Sleep(1);
            }
            throw new TimeoutException("Native rendering did not settle.");
        }
        string Hash(WIVSMMidiPart midiPart) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(midiPart.WaveFilePath)));
        try
        {
            source.BlockRenderingEnabled = false;
            source.StartAsyncRendering();
            Settle();
            if (ReadWaveMetrics(part.WaveFilePath).Peak <= .000001 || ReadWaveMetrics(second.WaveFilePath).Peak <= .000001)
                throw new InvalidOperationException("Native scope baseline is silent.");
            foreach (string kind in includeCarriers
                ? new[] { "note-velocity", "WeightHmm", "S5Air", "XSynth", "Expression", "internal-merged-invalidation" }
                : new[] { "internal-merged-invalidation" })
            {
                Array.Clear(started); Array.Clear(completed); Array.Clear(canceled);
                string before = Hash(part), otherBefore = Hash(second);
                int[][] positionsBefore = fixture.Notes.Select(n => n.GetPhonemePositions().ToArray()).ToArray();
                var beforeDse = counters.Read();
                if (kind == "internal-merged-invalidation") invalidateAll();
                else if (kind == "note-velocity") fixture.Notes[4].NoteVelocity = 74;
                else if (part.InsertController(new VSMRelTick(700), Enum.Parse<VSMControllerType>(kind), 80) == null)
                    throw new InvalidOperationException("Cannot insert carrier controller.");
                if (kind != "internal-merged-invalidation" && !source.Commit(false))
                    throw new InvalidOperationException("Cannot commit native render input.");
                // No managed StartAsyncRendering call: Commit wakes the already
                // enabled native service, as ordinary Editor transactions do.
                Settle();
                var work = counters.Read().Since(beforeDse);
                string after = Hash(part), otherAfter = Hash(second);
                bool changed = after != before, otherChanged = otherAfter != otherBefore;
                bool positionsChanged = fixture.Notes.Where((n, i) => !n.GetPhonemePositions().SequenceEqual(positionsBefore[i])).Any();
                if (canceled.Any(n => n != 0) || !source.CanAsyncRendering() || otherChanged)
                    throw new InvalidOperationException("Native edit canceled rendering or changed another Part's audio.");
                if (kind == "internal-merged-invalidation" && (changed || positionsChanged || !started.SequenceEqual(new[] { 1, 0 }) ||
                    !completed.SequenceEqual(new[] { 1, 0 })))
                    throw new InvalidOperationException($"Merged invalidation mismatch: notes={fixture.Notes.Length}, audioChanged={changed}, " +
                        $"positionsChanged={positionsChanged}, started={string.Join(',', started)}, completed={string.Join(',', completed)}, " +
                        $"before={before}, after={after}.");
                result.Add(new { kind, noteCount = fixture.Notes.Length, started = started.ToArray(), completed = completed.ToArray(),
                    canceled = canceled.ToArray(), explicitManagedStartRequests = 0, dse = work,
                    editedAudioChanged = changed, otherAudioChanged = otherChanged, phonemePositionsChanged = positionsChanged,
                    beforeSha256 = before, afterSha256 = after, otherBeforeSha256 = otherBefore, otherAfterSha256 = otherAfter });
                Console.WriteLine($"native_edit.kind={kind};started={string.Join(',', started)};completed={string.Join(',', completed)};steps={work.Steps};audio_changed={changed}");
            }
        }
        finally
        {
            source.StopAsyncRendering(); source.RemoveRendererObserver(observer);
        }
        return result;
    }

    private static IReadOnlyList<object> ProbeObservedInputScopes(Type host, Type entryType, object entry,
        StandaloneFixture fixture, WIVSMMidiPart second, TimingDseCounters counters, Action invalidate)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var source = fixture.Sequence;
        var part = fixture.Part;
        var scope = entryType.GetField("InputScope", fields)!.GetValue(entry)!;
        var dirty = (HashSet<nint>)scope.GetType().GetProperty("DirtyParts", fields)!.GetValue(scope)!;
        var notices = (ICollection<string>)entryType.GetField("InputNotices", fields)!.GetValue(entry)!;
        var result = new List<object>();
        host.GetMethod("ObserveNativeInputs", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [entry]);
        try
        {
            void Edit(string name, Action mutation)
            {
                dirty.Clear(); notices.Clear();
                scope.GetType().GetMethod("EndMutation", fields)!.Invoke(scope, null);
                var before = counters.Read();
                mutation();
                bool staged = source.IsStaged;
                bool committed = source.Commit(false);
                var pcm = counters.Read().Since(before);
                if (entryType.GetField("InputObserverFault", fields)!.GetValue(entry) is Exception fault)
                    throw new InvalidOperationException("Native input observer failed.", fault);
                if (pcm.Starts != 0 || pcm.Steps != 0 || source.IsStaged || staged && !committed)
                    throw new InvalidOperationException("Input observation unexpectedly rendered or failed to commit.");
                int[] parts = dirty.Select(p => p == (nint)part ? 0 : p == (nint)second ? 1 : -1).Order().ToArray();
                result.Add(new { name, staged, committed, dirtyParts = parts,
                    notices = notices.GroupBy(n => n).ToDictionary(g => g.Key, g => g.Count()), pcm });
                Console.WriteLine($"input_scope.case={name};dirty_parts={string.Join(',', parts)};notices={notices.Count};pcm_steps={pcm.Steps}");
            }
            Edit("note-pitch", () => fixture.Notes[4].SetNoteNumber(65));
            Edit("note-velocity", () => fixture.Notes[4].NoteVelocity = 73);
            WIVSMMidiController? controller = null;
            Edit("insert-controller", () => controller = part.InsertController(new VSMRelTick(700), VSMControllerType.Dynamics, 80)
                ?? throw new InvalidOperationException("Cannot insert controller."));
            Edit("remove-controller-recorded-owner", () =>
            {
                scope.GetType().GetMethod("BeforeControllerRemoval", fields)!.Invoke(scope, [(nint)controller!, (nint)part]);
                if (!part.RemoveController(controller!)) throw new InvalidOperationException("Cannot remove controller.");
            });
            Edit("insert-controller-for-unobserved-removal", () => controller = part.InsertController(new VSMRelTick(700), VSMControllerType.Dynamics, 80)
                ?? throw new InvalidOperationException("Cannot insert controller."));
            Edit("remove-controller-unknown-owner", () =>
            {
                if (!part.RemoveController(controller!)) throw new InvalidOperationException("Cannot remove controller.");
            });
            Edit("part-name", () => part.SetName("scope-name"));
            Edit("tempo", () => source.InsertTempo(new VSMRelTick(960), 14000));
            Edit("no-op-commit", () => { });
            Edit("internal-cache-invalidation", invalidate);
            if (dirty.Count != 0 || notices.Count != 0)
                throw new InvalidOperationException("Cache invalidation fed back into native input observation.");
            Edit("real-edit-after-invalidation", () => fixture.Notes[4].NoteVelocity = 72);
            if (!dirty.SetEquals(new[] { (nint)part }))
                throw new InvalidOperationException("Input observer suppressed a subsequent real edit.");
        }
        finally
        {
            var observer = (WIVSMUpdateObserver)entryType.GetField("InputObserver", fields)!.GetValue(entry)!;
            source.RemoveUpdateObserver(observer); observer.Dispose();
        }
        return result;
    }

    private static void InvalidationCommitBefore(WIVSMSequence __instance, out bool __state)
    {
        __state = _invalidationCounts != null && (nint)__instance == _invalidationSource && __instance.IsStaged;
        if (_invalidationCounts != null && (nint)__instance == _invalidationSource) _invalidationCounts.Commits++;
    }

    private static void InvalidationCommitAfter(bool __state, bool __result)
    {
        if (__state && __result && _invalidationCounts != null) _invalidationCounts.StagedCommits++;
    }

    private static void InvalidationVelocityBefore(WIVSMNote __instance)
    {
        if (_invalidationCounts is { } counters && InvalidationNotes.TryGetValue((nint)__instance, out var key))
        {
            counters.VelocityWrites++;
            counters.Writes[key] = counters.Writes.GetValueOrDefault(key) + 1;
            if (_failInvalidationWriteAt == counters.VelocityWrites)
            {
                _failInvalidationWriteAt = 0;
                throw new IOException("Injected native setter boundary failure.");
            }
        }
    }

    private static void InvalidationStartBefore(WIVSMSequence __instance)
    {
        if (_invalidationCounts != null && (nint)__instance == _invalidationSource) _invalidationCounts.StartRequests++;
    }

    private static void InvalidationStopBefore(WIVSMSequence __instance)
    {
        if (_invalidationCounts != null && (nint)__instance == _invalidationSource) _invalidationCounts.StopRequests++;
    }

    private static void InvalidationRefreshBefore(WIVSMMidiPart part)
    {
        if (_invalidationCounts != null && (nint)part.Sequence! == _invalidationSource) _invalidationCounts.RefreshRequests++;
    }
}
