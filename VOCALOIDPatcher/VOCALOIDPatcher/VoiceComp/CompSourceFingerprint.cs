using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using VOCALOIDPatcher.BreathVolume;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.ConsonantTiming;
using VOCALOIDPatcher.PhonemeVolume;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.VoiceComp;

internal static class CompSourceFingerprint
{
    private static readonly JsonSerializerOptions Options = new() { IncludeFields = true };
    internal static bool IsSynthesisController(VSMControllerType type) => !AceParameters.AceParameterRegistry.TryGetByCarrier(type, out _);

    // Capture on the Dispatcher. A native Part revision schedules the latest
    // composition; each stem's cache depends only on its actual synthesis input.
    internal static string Capture(WIVSMMidiPart part, int[] indices)
    {
        var source = part.Sequence!; var selected = indices.Select(i => part.GetNote((ulong)i)!).ToArray();
        var addresses = selected.Select(SampledTimingVsmBinding.Address).ToHashSet();
        var session = SampledTimingHost.Find(source);
        string timing = session == null ? "" : string.Join(";", session.Document.Snapshot().Where(p => addresses.Contains(p.Key))
            .OrderBy(p => p.Key.Note).Select(p => p.Value.Fingerprint));
        var archive = session?.Transitions.Snapshot;
        string transitions = archive?.Data is { } data ? TransitionArchiveData.From(data.KeepNotes(n => addresses.Contains(n.Address))).Fingerprint : archive?.Fingerprint ?? "";
        var input = new
        {
            Position = part.AbsPosTick.Value, Duration = part.DurationTick.Value, part.VoiceBankID, part.SecondaryVoiceBankID,
            part.StyleName, part.StylePresetID, source.MainTuning, source.GlobalTempo, source.IsGlobalTempoEnabled,
            Rate = (int)source.GetSamplingRate(), source.NumSampleInFrame,
            Tempos = source.Tempos.Select(t => new { Position = t.RelPosTick.Value, t.Value }).ToArray(),
            Effects = part.EffectManager?.MidiEffects.Select(e => new
            {
                e.Id, e.IsBypassed, Values = e.GetEffectValues.Select(v => new
                {
                    v.Name, v.Type, Value = v.Type switch
                    {
                        VSMEffectValueType.Int => v.RawInt.ToString(CultureInfo.InvariantCulture),
                        VSMEffectValueType.Float => v.RawFloat.ToString("R", CultureInfo.InvariantCulture),
                        _ => v.RawString
                    }
                }).ToArray()
            }).ToArray(),
            Controllers = Enum.GetValues<VSMControllerType>().Where(IsSynthesisController).Select(t => new
            { Type = (int)t, Points = part.GetControlParametersEnumerator(t).Select(p => new { Position = p.RelPosTick.Value, p.Value }).ToArray() }).ToArray(),
            Notes = selected.Select(n => new
            {
                Position = n.RelPosTick.Value, Duration = n.DurationTick.Value, n.Lyric, n.Phonemes, n.LangID, n.IsValidPhonemes,
                n.NoteNumber, n.NoteVelocity, Expression = n.GetNoteExpression(), n.ConsonantOffset,
                n.PreHmmWeight, n.PostHmmWeight, n.HmmWeightDuration, n.VibratoType, VibratoDuration = n.VibratoDurationTick.Value,
                DirectPitch = n.DirectPitches.ToArray(),
                Vibrato = Enum.GetValues<VSMVibratoEventType>().Select(t => new
                {
                    Type = (int)t, Points = Enumerable.Range(0, checked((int)n.GetNumVibratoEvent(t)))
                        .Select(i => n.GetVibratoEvent(t, (ulong)i)!).Select(p => new { Position = p.RelPosTick.Value, p.Value }).ToArray()
                }).ToArray(),
                Attack = Dvqm(n, true), Release = Dvqm(n, false),
                Breath = BreathVolumeService.GetValue(n), Volume = PhonemeVolumeHost.Get(n).Values,
                Register = RegisterShift.RegisterShiftService.GetValue(n.CppObjPtr), Evec = Evec.EvecService.GetState(n)
            }).ToArray(),
            timing, transitions, Settings.EvecEnabled, Settings.ExtendedChinesePinyin, Settings.IndividualBreathVolume,
            Settings.IndividualPhonemeVolume, Settings.RegisterShift, Settings.TransitionSmoothingEnabled,
            Smoothing = Settings.GetTransitionSmoothingConfig()
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input, Options)));
    }

    private static object? Dvqm(WIVSMNote note, bool attack) => note.GetDvqm(attack) is { IsEmpty: false } value
        ? new { value.DBID, value.NameID, value.Speed, value.TopFactor, value.IsProtect } : null;
}
