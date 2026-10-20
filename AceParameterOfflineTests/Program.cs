using System.IO.Compression;
using VOCALOIDPatcher.AceParameters;
using VOCALOIDPatcher.ConsonantTiming.Smoothing;

int failures = 0;

void Check(string name, bool condition, string? detail = null)
{
    if (condition)
    {
        Console.WriteLine($"PASS {name}");
        return;
    }
    failures++;
    Console.WriteLine($"FAIL {name}{(detail == null ? "" : ": " + detail)}");
}

void CheckEqual(string name, int expected, int actual)
    => Check(name, expected == actual, $"expected {expected}, actual {actual}");

// Neutral point maps to the native target default for every parameter.
CheckEqual("dynamics-neutral", 64, AceParameterMapping.Map(AceParameterKind.Energy, AceSoundTarget.Dynamics, 50));
CheckEqual("breathiness-neutral", 0, AceParameterMapping.Map(AceParameterKind.Air, AceSoundTarget.Breathiness, 50));
CheckEqual("native-air-neutral", 0, AceParameterMapping.Map(AceParameterKind.Air, AceSoundTarget.NativeAir, 50));
CheckEqual("brightness-neutral", 64, AceParameterMapping.Map(AceParameterKind.Tension, AceSoundTarget.Brightness, 50));
CheckEqual("character-neutral", 0, AceParameterMapping.Map(AceParameterKind.Falsetto, AceSoundTarget.Character, 50));

// Extremes clamp into the native controller ranges.
CheckEqual("dynamics-min", 0, AceParameterMapping.Map(AceParameterKind.Energy, AceSoundTarget.Dynamics, 0));
CheckEqual("dynamics-max", 127, AceParameterMapping.Map(AceParameterKind.Energy, AceSoundTarget.Dynamics, 100));
CheckEqual("brightness-min", 0, AceParameterMapping.Map(AceParameterKind.Tension, AceSoundTarget.Brightness, 0));
CheckEqual("brightness-max", 127, AceParameterMapping.Map(AceParameterKind.Tension, AceSoundTarget.Brightness, 100));
CheckEqual("character-min", -64, AceParameterMapping.Map(AceParameterKind.Falsetto, AceSoundTarget.Character, 0));
CheckEqual("character-max", 63, AceParameterMapping.Map(AceParameterKind.Falsetto, AceSoundTarget.Character, 100));
CheckEqual("breathiness-below-neutral", 0, AceParameterMapping.Map(AceParameterKind.Air, AceSoundTarget.Breathiness, 25));
CheckEqual("breathiness-max", 127, AceParameterMapping.Map(AceParameterKind.Air, AceSoundTarget.Breathiness, 100));

// Out-of-range curve values are clamped before mapping.
CheckEqual("curve-underflow", 0, AceParameterMapping.Map(AceParameterKind.Energy, AceSoundTarget.Dynamics, -20));
CheckEqual("curve-overflow", 127, AceParameterMapping.Map(AceParameterKind.Energy, AceSoundTarget.Dynamics, 500));

// Air target switch.
Check("air-target-breathiness", AceParameterMapping.MapAirTarget(AceAirMappingTarget.Breathiness) == AceSoundTarget.Breathiness);
Check("air-target-native-air", AceParameterMapping.MapAirTarget(AceAirMappingTarget.NativeAir) == AceSoundTarget.NativeAir);
Check("air-target-switchable", AceParameterMapping.IsAirTargetSwitchable(AceParameterKind.Air));
Check("energy-target-not-switchable", !AceParameterMapping.IsAirTargetSwitchable(AceParameterKind.Energy));

// Derived curve: sorted ticks, duplicate ticks collapse with the later point winning.
var curve = new List<AceCurvePoint>
{
    new(0, 50),
    new(120, 100),
    new(120, 0),
    new(240, 75),
};
var derived = AceParameterMapping.BuildDerivedCurve(AceParameterKind.Energy, AceSoundTarget.Dynamics, curve);
CheckEqual("derived-count", 3, derived.Count);
CheckEqual("derived-tick0", 0, (int)derived[0].Tick);
CheckEqual("derived-duplicate-collapsed-value", 0, derived[1].Value);
CheckEqual("derived-tick240", 240, (int)derived[2].Tick);
CheckEqual("derived-tick240-value", 96, derived[2].Value);

// Negative ticks (StartX/EndX sentinels) are skipped.
var withNegative = new List<AceCurvePoint> { new(-1, 50), new(10, 50) };
var filtered = AceParameterMapping.BuildDerivedCurve(AceParameterKind.Air, AceSoundTarget.Breathiness, withNegative);
CheckEqual("negative-tick-skipped-count", 1, filtered.Count);
CheckEqual("negative-tick-skipped-tick", 10, (int)filtered[0].Tick);

// Target ranges stay in sync with the native defaults documented in the plan.
Check("dynamics-range", AceParameterMapping.TryGetTargetRange(AceSoundTarget.Dynamics, out var dynMin, out var dynMax, out var dynDefault) &&
      dynMin == 0 && dynMax == 127 && dynDefault == 64);
Check("character-range", AceParameterMapping.TryGetTargetRange(AceSoundTarget.Character, out var charMin, out var charMax, out var charDefault) &&
      charMin == -64 && charMax == 63 && charDefault == 0);
Check("unknown-target-rejected", !AceParameterMapping.TryGetTargetRange((AceSoundTarget)900, out _, out _, out _));

// Custom .vpr ZIP entry round-trip (native sequence.json does not persist the
// HMM carrier controllers, so this archive is the source of truth).
string tempDir = Path.Combine(Path.GetTempPath(), "v6p-ace-offline-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
string vprPath = Path.Combine(tempDir, "roundtrip.vpr");
using (var zip = ZipFile.Open(vprPath, ZipArchiveMode.Create))
{
    var nativeEntry = zip.CreateEntry("Project/sequence.json");
    using var writer = new StreamWriter(nativeEntry.Open());
    writer.Write("{\"version\":{}}");
}
var projectData = new AceProjectData();
projectData.Entries.Add(new AceProjectEntry
{
    Track = 0,
    Part = 0,
    AbsPosTick = 0,
    Kind = (int)AceParameterKind.Energy,
    Points =
    {
        new AceProjectPoint { Tick = 120, Value = 10 },
        new AceProjectPoint { Tick = 240, Value = 50 },
        new AceProjectPoint { Tick = 360, Value = 100 },
    },
});
AceParameterProjectArchive.Write(vprPath, projectData);
var readBack = AceParameterProjectArchive.Read(vprPath);
Check("archive-roundtrip-entry-count", readBack?.Entries.Count == 1,
    $"entries={readBack?.Entries.Count}");
Check("archive-roundtrip-points", readBack?.Entries[0].Points.Count == 3);
Check("archive-roundtrip-value", readBack?.Entries[0].Points[2].Value == 100);
Check("archive-roundtrip-kind", readBack?.Entries[0].Kind == (int)AceParameterKind.Energy);

// A newer/unknown archive version must be rejected instead of silently loaded.
using (var zip = ZipFile.Open(vprPath, ZipArchiveMode.Update))
{
    zip.GetEntry(AceParameterProjectArchive.EntryPath)?.Delete();
    var entry = zip.CreateEntry(AceParameterProjectArchive.EntryPath);
    using var writer = new StreamWriter(entry.Open());
    writer.Write("{\"version\":99,\"entries\":[]}");
}
bool rejectedVersion = false;
try
{
    AceParameterProjectArchive.Read(vprPath);
}
catch (InvalidDataException)
{
    rejectedVersion = true;
}
Check("archive-rejects-unknown-version", rejectedVersion);

// --- Transition Audio Smoothing Verification ---

// 1. Equal-Gain Cosine S-Curve: w_in + w_out must be identically 1.0 across all normalized positions
bool equalGainPassed = true;
foreach (int fadeLen in new[] { 16, 64, 441, 1000 })
{
    for (int i = 0; i < fadeLen; i++)
    {
        float u = (i + 0.5f) / fadeLen;
        float angle = (float)(Math.PI * 0.5 * u);
        float sin = (float)Math.Sin(angle);
        float cos = (float)Math.Cos(angle);
        float wIn = sin * sin;
        float wOut = cos * cos;
        if (Math.Abs((wIn + wOut) - 1.0f) > 1e-6f)
        {
            equalGainPassed = false;
            break;
        }
    }
}
Check("equal-gain-cosine-s-curve", equalGainPassed);

// 2. Smoother Config Validation and Clamping
var defaultConfig = TransitionSmootherConfig.Default;
Check("config-default-volume-threshold", Math.Abs(defaultConfig.VolumeThresholdDb - 2.5) < 1e-5);
Check("config-default-crossfade-ms", Math.Abs(defaultConfig.DefaultCrossfadeMs - 10.0) < 1e-5);
Check("config-default-min-crossfade-ms", Math.Abs(defaultConfig.MinCrossfadeMs - 3.0) < 1e-5);
Check("config-default-max-crossfade-ms", Math.Abs(defaultConfig.MaxCrossfadeMs - 25.0) < 1e-5);
Check("config-default-loudness-window-ms", Math.Abs(defaultConfig.LoudnessWindowMs - 60.0) < 1e-5);
Check("config-default-analysis-range-ms", Math.Abs(defaultConfig.AnalysisRangeMs - 75.0) < 1e-5);

var extremeConfig = new TransitionSmootherConfig
{
    VolumeThresholdDb = -50.0,
    MinCrossfadeMs = 0.1,
    MaxCrossfadeMs = 500.0,
    DefaultCrossfadeMs = 999.0
}.Validate();
Check("config-validation-clamped-threshold", extremeConfig.VolumeThresholdDb >= 0.1);
Check("config-validation-clamped-crossfade", extremeConfig.DefaultCrossfadeMs <= extremeConfig.MaxCrossfadeMs);

// 3. Body Gain Preservation (Decision 2):
// Segments with ~8 dB difference (-20 dB vs -12 dB).
// Outside the local loudness window (60 ms), both bodies must retain 100% of their original gain.
const int sampleRate = 44100;
int segmentSamples = sampleRate / 2; // 500 ms each
var leftBody = new float[segmentSamples];
var rightBody = new float[segmentSamples];
float leftAmp = 0.1f;  // ~ -20 dBFS
float rightAmp = 0.25f; // ~ -12 dBFS (diff ~ 8 dB)

for (int i = 0; i < segmentSamples; i++)
{
    leftBody[i] = leftAmp * (float)Math.Sin(2 * Math.PI * 220.0 * i / sampleRate);
    rightBody[i] = rightAmp * (float)Math.Sin(2 * Math.PI * 220.0 * i / sampleRate);
}

var spliceResult = TransitionSmoother.Splice(leftBody, rightBody, sampleRate, defaultConfig);
Check("splice-intervened-on-large-volume-diff", spliceResult.Intervened);
Check("splice-volume-diff-detected", Math.Abs(spliceResult.VolumeDifferenceDb - 8.0f) < 1.0f,
    $"diff={spliceResult.VolumeDifferenceDb}");

// Far left (first 100 ms) must match left body exactly (gain 1.0x)
bool leftBodyPreserved = true;
for (int i = 0; i < sampleRate / 10; i++)
{
    if (Math.Abs(spliceResult.Output[i] - leftBody[i]) > 1e-5f)
    {
        leftBodyPreserved = false;
        break;
    }
}
Check("body-gain-preservation-left", leftBodyPreserved);

// Far right (last 100 ms) must match right body exactly (gain 1.0x)
bool rightBodyPreserved = true;
int outLen = spliceResult.Output.Length;
int rightOffset = outLen - (sampleRate / 10);
int rightBodyOffset = segmentSamples - (sampleRate / 10);
for (int i = 0; i < sampleRate / 10; i++)
{
    if (Math.Abs(spliceResult.Output[rightOffset + i] - rightBody[rightBodyOffset + i]) > 1e-5f)
    {
        rightBodyPreserved = false;
        break;
    }
}
Check("body-gain-preservation-right", rightBodyPreserved);

// 4. Volume Threshold Gating:
// When volume diff is small (< 2.5 dB, e.g. 1.0 dB), automatic smoothing does NOT intervene.
var leftSmall = new float[segmentSamples];
var rightSmall = new float[segmentSamples];
float smallDiffAmp = 0.112f; // ~ 1.0 dB higher than 0.1f
for (int i = 0; i < segmentSamples; i++)
{
    leftSmall[i] = leftAmp * (float)Math.Sin(2 * Math.PI * 440.0 * i / sampleRate);
    rightSmall[i] = smallDiffAmp * (float)Math.Sin(2 * Math.PI * 440.0 * i / sampleRate);
}
var smallDiffResult = TransitionSmoother.Splice(leftSmall, rightSmall, sampleRate, defaultConfig);
Check("threshold-gating-bypasses-small-diff", !smallDiffResult.Intervened);

// 5. A phase-continuous gain change needs only the envelope bridge.
// Applying a time-shift crossfade here used to create two extra clicks.
Check("continuous-gain-change-needs-no-waveform-repair",
    spliceResult.CrossfadeDurationMs == 0,
    $"ms={spliceResult.CrossfadeDurationMs}");

// 5b. Strict Parameter Setting Gating (Option 1):
// A. When physical waveforms are identical, but user sets nominal gains with 8 dB difference (-20 vs -12), it MUST strictly intervene:
var equalWavesA = (float[])leftBody.Clone();
var equalWavesB = (float[])leftBody.Clone();
var nominalInterveneResult = TransitionSmoother.Splice(equalWavesA, equalWavesB, sampleRate, defaultConfig, leftNominalGainDb: -20.0, rightNominalGainDb: -12.0);
Check("nominal-setting-drives-intervention", nominalInterveneResult.Intervened && Math.Abs(nominalInterveneResult.VolumeDifferenceDb - 8.0f) < 1e-4);

// B. When physical waveforms differ by ~8 dB, but user sets both nominal gains to 0 dB, it MUST strictly bypass:
var nominalBypassResult = TransitionSmoother.Splice(leftBody, rightBody, sampleRate, defaultConfig, leftNominalGainDb: 0.0, rightNominalGainDb: 0.0);
Check("nominal-setting-drives-bypass", !nominalBypassResult.Intervened && nominalBypassResult.VolumeDifferenceDb == 0.0f);

// Introducing a sharp transient spike right near splice point should shorten the crossfade
var leftTransient = (float[])leftBody.Clone();
int spikeIndex = leftTransient.Length - 50;
for (int s = 0; s < 10; s++) leftTransient[spikeIndex + s] = 0.9f; // Sharp transient burst
var transientResult = TransitionSmoother.Splice(leftTransient, rightBody, sampleRate, defaultConfig);
Check("adaptive-crossfade-shortens-near-transient",
    transientResult.CrossfadeDurationMs <= spliceResult.CrossfadeDurationMs,
    $"transientMs={transientResult.CrossfadeDurationMs}, voicedMs={spliceResult.CrossfadeDurationMs}");

// 6. Adaptive Splice Selection: avoids placing cut directly on the transient
Check("transient-splice-avoidance",
    transientResult.SpliceOffsetSamples != (spikeIndex - leftTransient.Length),
    $"offset={transientResult.SpliceOffsetSamples}");

// 7. In-Place Continuous Junction Smoothing:
var continuousBuffer = new float[sampleRate];
for (int i = 0; i < sampleRate / 2; i++) continuousBuffer[i] = leftAmp * (float)Math.Sin(2 * Math.PI * 300.0 * i / sampleRate);
for (int i = sampleRate / 2; i < sampleRate; i++) continuousBuffer[i] = rightAmp * (float)Math.Sin(2 * Math.PI * 300.0 * i / sampleRate);

var inPlaceResult = TransitionSmoother.SmoothJunction(
    continuousBuffer.AsSpan(),
    sampleRate / 2,
    sampleRate,
    defaultConfig);
Check("in-place-smoothing-intervened", inPlaceResult.Intervened);
Check("in-place-first-sample-preserved", Math.Abs(continuousBuffer[0] - 0f) < 1e-6f);

// 8. Custom Timing Offset:
var offsetConfig = defaultConfig with { TimingOffsetMs = 15.0 };
var offsetResult = TransitionSmoother.SmoothJunction(
    continuousBuffer.AsSpan(),
    sampleRate / 2,
    sampleRate,
    offsetConfig);
Check("custom-timing-offset-applied", offsetResult.SpliceOffsetMs >= 10.0,
    $"offsetMs={offsetResult.SpliceOffsetMs}");

// 9. Wave File Processing via WaveJunctionProcessor:
string waveDir = Path.Combine(Path.GetTempPath(), "v6p-wave-smoother-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(waveDir);
string srcWave = Path.Combine(waveDir, "source.wav");
string dstWave = Path.Combine(waveDir, "dest.wav");

// Create test 16-bit RIFF/WAVE file
using (var stream = new FileStream(srcWave, FileMode.Create))
using (var writer = new BinaryWriter(stream))
{
    int numSamples = sampleRate / 2;
    int dataBytes = numSamples * 2;
    writer.Write(new[] { 'R', 'I', 'F', 'F' });
    writer.Write(36 + dataBytes);
    writer.Write(new[] { 'W', 'A', 'V', 'E' });
    writer.Write(new[] { 'f', 'm', 't', ' ' });
    writer.Write(16); // chunk size
    writer.Write((short)1); // PCM
    writer.Write((short)1); // Mono
    writer.Write(sampleRate);
    writer.Write(sampleRate * 2); // Byte rate
    writer.Write((short)2); // Block align
    writer.Write((short)16); // Bits per sample
    writer.Write(new[] { 'd', 'a', 't', 'a' });
    writer.Write(dataBytes);

    for (int i = 0; i < numSamples; i++)
    {
        float amp = i < numSamples / 2 ? leftAmp : rightAmp;
        short sampleVal = (short)(amp * 32767f * Math.Sin(2 * Math.PI * 440.0 * i / sampleRate));
        writer.Write(sampleVal);
    }
}

var waveResult = WaveJunctionProcessor.ProcessWaveFile(srcWave, dstWave, sampleRate / 4, defaultConfig);
Check("wave-junction-processor-succeeded", File.Exists(dstWave) && waveResult.Intervened);
Check("wave-junction-processor-file-size", new FileInfo(dstWave).Length == new FileInfo(srcWave).Length);

// 10. Multi-Junction Wave File Processing:
string dstMultiWave = Path.Combine(waveDir, "dest_multi.wav");
var multiJunctions = new List<WaveJunctionProcessor.JunctionInfo>
{
    new(sampleRate / 4, 0.0, 3.8),
    new(sampleRate * 3 / 8, 3.8, 0.0)
};
var multiResults = WaveJunctionProcessor.ProcessWaveFile(srcWave, dstMultiWave, multiJunctions, defaultConfig);
Check("multi-junction-count", multiResults.Count == 2);
Check("multi-junction-first-intervened", multiResults[0].Intervened);
Check("multi-junction-second-intervened", multiResults[1].Intervened);
Check("multi-junction-file-size", File.Exists(dstMultiWave) && new FileInfo(dstMultiWave).Length == new FileInfo(srcWave).Length);

try { Directory.Delete(waveDir, true); } catch { }

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
