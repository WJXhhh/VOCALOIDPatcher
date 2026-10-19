using System.IO.Compression;
using VOCALOIDPatcher.AceParameters;

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

try { Directory.Delete(tempDir, true); } catch { }

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
