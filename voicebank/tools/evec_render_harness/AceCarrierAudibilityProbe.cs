using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming;
using Yamaha.VOCALOID.VSM;

internal static partial class Program
{
    // ACE research probe: are the carrier controller types that the ACE
    // parameters use for curve storage actually inert in the native renderer?
    //
    // For each voicebank and VSMControllerType the probe renders one isolated
    // part (three identical "a" notes) and compares:
    //   1. baseline, rendered twice (determinism)
    //   2. a single controller at part start with value 50 (the ACE neutral)
    //   3. a localized bump 600/720/840 ticks with ACE neutral 50
    //   4. a localized bump with the controller type's native default value
    //
    // A localized bump that stays audible only inside the touched region and
    // leaves the later-note tail bit-identical means the renderer ignores that
    // controller type. A differing tail means the value persists into later
    // notes (which is what "Tension affects following notes" would mean).
    private static int ProbeAceCarrierAudibility(WIVSMSequenceManager manager, string directory)
    {
        Directory.CreateDirectory(directory);
        var report = new List<object>();
        var profile = new List<object>();
        var voicebanks = new (string Id, string Lyric, string Phoneme, int LangId)[]
        {
            ("BCNFCY43LB2LZCD4", "a", "a", 0),
            ("BL8CEAM5N4XN3LFK", "la", "l a", 4),
        };
        var types = new[]
        {
            VSMControllerType.DynamicsHmm, VSMControllerType.PitchBendHmm, VSMControllerType.WeightHmm,
            VSMControllerType.Expression, VSMControllerType.S5Air, VSMControllerType.S5Character,
            VSMControllerType.Dynamics, VSMControllerType.Brightness, VSMControllerType.Breathiness,
            VSMControllerType.Character,
        };
        string? typeFilter = Environment.GetEnvironmentVariable("ACE_PROBE_TYPES");
        if (!string.IsNullOrWhiteSpace(typeFilter))
        {
            var selected = typeFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => Enum.TryParse<VSMControllerType>(item, out var parsed) ? parsed : (VSMControllerType?)null)
                .Where(item => item.HasValue).Select(item => item!.Value).ToArray();
            if (selected.Length > 0) types = selected;
        }
        string? voicebankFilter = Environment.GetEnvironmentVariable("ACE_PROBE_VOICEBANKS");
        if (!string.IsNullOrWhiteSpace(voicebankFilter))
        {
            var selected = new List<(string, string, string, int)>();
            foreach (string entry in voicebankFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] parts = entry.Split('|');
                if (parts.Length == 4 && int.TryParse(parts[3], out int langId))
                    selected.Add((parts[0], parts[1], parts[2], langId));
            }
            if (selected.Count > 0) voicebanks = selected.ToArray();
        }
        foreach (var voicebank in voicebanks)
        {
            try
            {
                using var fixture = CreateAceAudibilityFixture(manager, voicebank.Id, voicebank.Lyric, voicebank.Phoneme, voicebank.LangId);
                string baselinePath = Path.Combine(directory, $"{voicebank.Id}-baseline.wav");
                Require(ProbeRender(fixture, baselinePath, "baseline"), "baseline render failed");
                var baseline = ReadAceWave(baselinePath);
                string repeatPath = Path.Combine(directory, $"{voicebank.Id}-baseline-repeat.wav");
                Require(ProbeRender(fixture, repeatPath, "baseline-repeat"), "baseline repeat render failed");
                var repeat = ReadAceWave(repeatPath);
                bool deterministic = baseline.Hash == repeat.Hash;
                Console.WriteLine($"ace.voicebank={voicebank.Id};baseline_rms={baseline.Rms:F2};deterministic={deterministic}");
                report.Add(new
                {
                    voicebank = voicebank.Id,
                    baseline = baseline.Summary(),
                    baselineRepeat = repeat.Summary(),
                    deterministic,
                });
                foreach (var type in types)
                {
                    int minimum = WIVSMMidiController.GetMinValue(type);
                    int maximum = WIVSMMidiController.GetMaxValue(type);
                    int nativeDefault = Math.Clamp(WIVSMMidiController.GetDefaultValue(type), minimum, maximum);
                    int high50 = Math.Clamp(70, minimum, maximum);
                    int highNative = Math.Clamp(nativeDefault + 20, minimum, maximum);
                    var results = new List<object>();
                    foreach (var scenario in new (string Name, (long Tick, int Value)[] Points)[]
                    {
                        ("bump50", new[] { (600L, Math.Clamp(50, minimum, maximum)), (720L, high50), (840L, Math.Clamp(50, minimum, maximum)) }),
                        ("bumpNative", new[] { (600L, nativeDefault), (720L, highNative), (840L, nativeDefault) }),
                    })
                    {
                        try
                        {
                            RemoveAceControllers(fixture.Part, type);
                            bool inserted = true;
                            foreach (var (tick, value) in scenario.Points)
                            {
                                if (fixture.Part.InsertController(new VSMRelTick(tick), type, value) == null)
                                {
                                    inserted = false;
                                    break;
                                }
                            }
                            fixture.Sequence.Commit(false);
                            if (!inserted)
                            {
                                results.Add(new { scenario = scenario.Name, inserted = false });
                                RemoveAceControllers(fixture.Part, type);
                                fixture.Sequence.Commit(false);
                                continue;
                            }
                            string path = Path.Combine(directory,
                                $"{voicebank.Id}-{(int)type}-{scenario.Name}.wav");
                            bool rendered = ProbeRender(fixture, path, $"{type}/{scenario.Name}");
                            if (!rendered)
                            {
                                results.Add(new { scenario = scenario.Name, rendered = false });
                                RemoveAceControllers(fixture.Part, type);
                                fixture.Sequence.Commit(false);
                                continue;
                            }
                            var wave = ReadAceWave(path);
                            var delta = CompareAceWave(baseline, wave);
                            ProfileAceWindows(baseline, wave, $"{voicebank.Id}/{(int)type}/{scenario.Name}", profile);
                            results.Add(new
                            {
                                scenario = scenario.Name,
                                inserted = true,
                                rendered = true,
                                wave = wave.Summary(),
                                delta,
                            });
                            Console.WriteLine($"ace.{voicebank.Id}.{type}.{scenario.Name};rms={wave.Rms:F2};tailDiff={delta.TailRms:F2};headDiff={delta.HeadRms:F2}");
                        }
                        finally
                        {
                            RemoveAceControllers(fixture.Part, type);
                            try { fixture.Sequence.Commit(false); } catch { }
                        }
                    }
                    report.Add(new
                    {
                        voicebank = voicebank.Id,
                        parameterType = type.ToString(),
                        typeId = (int)type,
                        minimum,
                        maximum,
                        nativeDefault,
                        scenarios = results,
                    });
                    File.WriteAllText(Path.Combine(directory, "ace-carrier.json"),
                        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"ace.voicebank={voicebank.Id};exception={exception.GetType().Name}:{exception.Message}");
                report.Add(new { voicebank = voicebank.Id, error = exception.ToString() });
            }
        }
        File.WriteAllText(Path.Combine(directory, "ace-carrier-windows.json"),
            JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"ace.probe.completed;dir={directory}");
        return 0;
    }

    private sealed record AceWave(int Channels, int SampleRate, short[] Samples, string Hash)
    {
        public double Rms
        {
            get
            {
                if (Samples.Length == 0) return 0;
                double sum = 0;
                foreach (short sample in Samples) sum += sample * (double)sample;
                return Math.Sqrt(sum / Samples.Length);
            }
        }

        public object Summary() => new
        {
            channels = Channels,
            sampleRate = SampleRate,
            frames = Samples.Length,
            rms = Math.Round(Rms, 3),
            peak = Samples.Length == 0 ? 0 : Samples.Max(Math.Abs),
            hash = Hash,
        };
    }

    private sealed record AceWaveDelta(
        double MaxAbsDifference,
        double WholeRms,
        double HeadRms,
        double TailRms,
        bool TailIdentical,
        string HeadHash,
        string TailHash);

    private static AceWaveDelta CompareAceWave(AceWave baseline, AceWave candidate)
    {
        int frames = Math.Min(baseline.Samples.Length, candidate.Samples.Length);
        if (frames == 0)
            return new AceWaveDelta(0, 0, 0, 0, true, string.Empty, string.Empty);
        int head = Math.Min(frames, Math.Max(1, frames / 3));
        int tailStart = Math.Max(head, frames - Math.Max(1, frames / 4));
        int maximum = 0;
        double whole = 0, headSum = 0, tailSum = 0;
        for (int index = 0; index < frames; index++)
        {
            int difference = Math.Abs(baseline.Samples[index] - candidate.Samples[index]);
            if (difference > maximum) maximum = difference;
            whole += difference * (double)difference;
            if (index < head) headSum += difference * (double)difference;
            else if (index >= tailStart) tailSum += difference * (double)difference;
        }
        int headCount = head;
        int tailCount = Math.Max(1, frames - tailStart);
        string headHash = Convert.ToHexString(SHA256.HashData(WaveBytes(candidate, 0, head)));
        string tailHash = Convert.ToHexString(SHA256.HashData(WaveBytes(candidate, tailStart, frames)));
        bool identical = maximum == 0;
        return new AceWaveDelta(
            maximum,
            Math.Round(Math.Sqrt(whole / frames), 3),
            Math.Round(Math.Sqrt(headSum / headCount), 3),
            Math.Round(Math.Sqrt(tailSum / tailCount), 3),
            identical,
            headHash,
            tailHash);
    }

    private static void ProfileAceWindows(AceWave baseline, AceWave candidate, string label, List<object> profile)
    {
        int frames = Math.Min(baseline.Samples.Length, candidate.Samples.Length);
        const int window = 11025; // 250 ms at 44100 Hz
        var windows = new List<object>();
        for (int start = 0; start + window <= frames; start += window)
        {
            double baseSum = 0, candidateSum = 0, diffSum = 0;
            for (int index = start; index < start + window; index++)
            {
                baseSum += baseline.Samples[index] * (double)baseline.Samples[index];
                candidateSum += candidate.Samples[index] * (double)candidate.Samples[index];
                int difference = baseline.Samples[index] - candidate.Samples[index];
                diffSum += difference * (double)difference;
            }
            double baseRms = Math.Sqrt(baseSum / window);
            double candidateRms = Math.Sqrt(candidateSum / window);
            double diffRms = Math.Sqrt(diffSum / window);
            if (diffRms > Math.Max(1.0, baseRms * 0.01))
            {
                windows.Add(new
                {
                    ms = (int)Math.Round(start * 1000.0 / 44100),
                    baseRms = Math.Round(baseRms, 2),
                    candidateRms = Math.Round(candidateRms, 2),
                    diffRms = Math.Round(diffRms, 2),
                });
            }
        }
        if (windows.Count > 0)
            profile.Add(new { label, windows = windows.ToArray() });
    }

    private static byte[] WaveBytes(AceWave wave, int start, int end)
    {
        start = Math.Clamp(start, 0, wave.Samples.Length);
        end = Math.Clamp(end, start, wave.Samples.Length);
        var bytes = new byte[(end - start) * 2];
        for (int index = start; index < end; index++)
        {
            bytes[(index - start) * 2] = (byte)(wave.Samples[index] & 0xFF);
            bytes[(index - start) * 2 + 1] = (byte)((wave.Samples[index] >> 8) & 0xFF);
        }
        return bytes;
    }

    private static AceWave ReadAceWave(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF")
            throw new InvalidDataException("Not a RIFF wave.");
        int channels = BitConverter.ToUInt16(bytes, 22);
        int sampleRate = BitConverter.ToInt32(bytes, 24);
        int bits = BitConverter.ToUInt16(bytes, 34);
        if (bits != 16) throw new InvalidDataException($"Unsupported sample width {bits}.");
        int offset = 12;
        int dataOffset = -1, dataLength = 0;
        while (offset + 8 <= bytes.Length)
        {
            string chunk = Encoding.ASCII.GetString(bytes, offset, 4);
            int length = BitConverter.ToInt32(bytes, offset + 4);
            if (chunk == "data")
            {
                dataOffset = offset + 8;
                dataLength = Math.Min(length, bytes.Length - dataOffset);
                break;
            }
            offset += 8 + length + (length & 1);
        }
        if (dataOffset < 0) throw new InvalidDataException("No data chunk.");
        var samples = new short[dataLength / 2];
        Buffer.BlockCopy(bytes, dataOffset, samples, 0, samples.Length * 2);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        return new AceWave(channels, sampleRate, samples, hash);
    }

    private sealed record AceAudibilityFixture(WIVSMSequence Sequence, WIVSMMidiPart Part, WIVSMNote[] Notes) : IDisposable
    {
        public void Dispose() => Sequence.Close();
    }

    private static AceAudibilityFixture CreateAceAudibilityFixture(
        WIVSMSequenceManager manager, string voicebank, string lyric, string phoneme, int langId)
    {
        var sequence = manager.CreateSequence(new VSMSequenceData
        { SamplingRate = VSMSamplingRate._44100, MaxNumTracks = 4, MaxUndoCount = 0 })!;
        try
        {
            sequence.InsertTimeSig(0, new VSMTimeSigEvent(4, 4));
            sequence.InsertTempo(VSMRelTick.Zero, 12000);
            var track = (WIVSMMidiTrack)sequence.InsertTrackEx(0, VSMTrackType.Midi, "ace")!;
            var part = track.InsertPart(new VSMAbsTick(0), new VSMRelTick(3840), "ace")!;
            part.SetVoiceBankID(voicebank);
            var notes = new WIVSMNote[3];
            for (int index = 0; index < notes.Length; index++)
            {
                notes[index] = part.InsertNote(new VSMRelTick(480 + index * 960),
                    new VSMNoteEvent(960, 60, 64), part.GetDefaultNoteExpression(),
                    part.GetDefaultAiNoteExpression(), lyric, phoneme, true, langId)!;
            }
            sequence.Commit(false);
            return new AceAudibilityFixture(sequence, part, notes);
        }
        catch
        {
            sequence.Close();
            throw;
        }
    }

    private static bool ProbeRender(AceAudibilityFixture fixture, string path, string label)
    {
        try
        {
            SampledTimingVsmBinding.InvalidateRenderedPart(fixture.Part, fixture.Notes);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"ace.render.invalidate_failed={label}:{exception.Message}");
        }
        VSMResult result = fixture.Part.Render(path);
        bool ok = result == VSMResult.NoError && File.Exists(path);
        if (!ok) Console.WriteLine($"ace.render.failed={label};result={result}");
        return ok;
    }

    private static void RemoveAceControllers(WIVSMMidiPart part, VSMControllerType type)
    {
        for (int index = checked((int)part.GetNumController(type)) - 1; index >= 0; index--)
        {
            var controller = part.GetController(type, (ulong)index);
            if (controller != null) part.RemoveController(controller);
        }
    }
}
