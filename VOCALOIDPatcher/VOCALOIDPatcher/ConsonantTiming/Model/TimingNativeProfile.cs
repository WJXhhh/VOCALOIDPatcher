using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace VOCALOIDPatcher.ConsonantTiming;

// This profile describes one private adapter, not a promise about other editor
// versions. Never select it by editor version or by matching a short prologue.
internal sealed partial class TimingNativeProfile
{
    internal static IReadOnlyList<TimingNativeProfile> Supported { get; } = Array.AsReadOnly(new[] {
        ReadEmbedded("VOCALOIDPatcher.SampledTimingProfile"),
        ReadEmbedded("VOCALOIDPatcher.SampledTimingProfile.6.13.3")
    });
    internal static TimingNativeProfile Known => Supported[0];
    internal static TimingNativeProfile? Identify(string dse, string vsm)
    {
        foreach (var profile in Supported) if (profile.Matches(dse, vsm)) return profile;
        return null;
    }
    private readonly Dictionary<string, string> _values;
    internal string Id => _values["Id"];
    internal string DseSha256 => _values["DseSha256"];
    internal string VsmSha256 => _values["VsmSha256"];
    internal ulong Fingerprint { get; }

    private TimingNativeProfile(byte[] bytes)
    {
        _values = new(StringComparer.Ordinal);
        ulong fingerprint = 14695981039346656037UL;
        foreach (byte value in bytes) fingerprint = unchecked((fingerprint ^ value) * 1099511628211UL);
        Fingerprint = fingerprint;
        foreach (string raw in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int split = line.IndexOf('=');
            if (split <= 0 || !_values.TryAdd(line[..split], line[(split + 1)..]))
                throw new InvalidDataException("Invalid timing compatibility profile.");
        }
        EngineSlot = Offset(nameof(EngineSlot));
        EngineConfigureSlot = Offset(nameof(EngineConfigureSlot));
        EngineStartSlot = Offset(nameof(EngineStartSlot));
        EngineStopSlot = Offset(nameof(EngineStopSlot));
        EngineDestroySlot = Offset(nameof(EngineDestroySlot));
        ScheduleCapacity = Offset(nameof(ScheduleCapacity));
        ScheduleMappedCount = Offset(nameof(ScheduleMappedCount));
        RowBegin = Offset(nameof(RowBegin));
        RowEnd = Offset(nameof(RowEnd));
        RowSplit = Offset(nameof(RowSplit));
        RowFrom = Offset(nameof(RowFrom));
        RowTo = Offset(nameof(RowTo));
        RowLeftFlag = Offset(nameof(RowLeftFlag));
        RowRightFlag = Offset(nameof(RowRightFlag));
        ScoreStride = Offset(nameof(ScoreStride));
        ScoreLeftDistance = Offset(nameof(ScoreLeftDistance));
        ScoreRightDistance = Offset(nameof(ScoreRightDistance));
        ScoreLeftTransition = Offset(nameof(ScoreLeftTransition));
        ScoreRightTransition = Offset(nameof(ScoreRightTransition));
        ScoreFromFlag = Offset(nameof(ScoreFromFlag));
        ScoreToFlag = Offset(nameof(ScoreToFlag));
        ScoreFromPhone = Offset(nameof(ScoreFromPhone));
        ScoreToPhone = Offset(nameof(ScoreToPhone));
        RendererLivePart = Offset(nameof(RendererLivePart));
        RendererSnapshotPart = Offset(nameof(RendererSnapshotPart));
        DurationWorkLeftName = Offset(nameof(DurationWorkLeftName));
        DseCreateEngine = Offset(nameof(DseCreateEngine));
        DseEngineVtable = Offset(nameof(DseEngineVtable));
        DseStates = Offset(nameof(DseStates));
        DseStartMutex = Offset(nameof(DseStartMutex));
        StateCurrentFrame = Offset(nameof(StateCurrentFrame));
        StateMutex = Offset(nameof(StateMutex));
        StateSchedule = Offset(nameof(StateSchedule));
        StateParser = Offset(nameof(StateParser));
        DseScheduleClear = Offset(nameof(DseScheduleClear));
        DseParserReset = Offset(nameof(DseParserReset));
        DseParse = Offset(nameof(DseParse));
        DseScheduleEnd = Offset(nameof(DseScheduleEnd));
        DseScheduleRow = Offset(nameof(DseScheduleRow));
        DseDictionary = Offset(nameof(DseDictionary));
        DsePhoneName = Offset(nameof(DsePhoneName));
        DseVelocityCoefficient = Offset(nameof(DseVelocityCoefficient));
        RowDictionary = Offset(nameof(RowDictionary));
        RowSinger = Offset(nameof(RowSinger));
        RowSustainSplit = Offset(nameof(RowSustainSplit));
        DictionaryPhones = Offset(nameof(DictionaryPhones));
        ParserSlot = Offset(nameof(ParserSlot));
        ParserNotes = Offset(nameof(ParserNotes));
        NoteStride = Offset(nameof(NoteStride));
        NoteCoefficient = Offset(nameof(NoteCoefficient));
        NotePhoneCount = Offset(nameof(NotePhoneCount));
        NotePhones = Offset(nameof(NotePhones));
        VsmEncodeNote = Offset(nameof(VsmEncodeNote));
        VsmEncodeSinger = Offset(nameof(VsmEncodeSinger));
        VsmMapScore = Offset(nameof(VsmMapScore));
        PartMappingFlag = Offset(nameof(PartMappingFlag));
        NotePositionsBegin = Offset(nameof(NotePositionsBegin));
        NotePositionsEnd = Offset(nameof(NotePositionsEnd));
        DseMapper = Offset(nameof(DseMapper));
        DseStationary = Offset(nameof(DseStationary));
        DseAllocate = Offset(nameof(DseAllocate));
        DsePaddingCaller = Offset(nameof(DsePaddingCaller));
        VsmPrepare = Offset(nameof(VsmPrepare));
        VsmCacheHash = Offset(nameof(VsmCacheHash));
        DseDurationScore = Offset(nameof(DseDurationScore));
        DseDurationNote = Offset(nameof(DseDurationNote));
        DseDurationWork = Offset(nameof(DseDurationWork));
        DseDurationLookup = Offset(nameof(DseDurationLookup));
        DseDurationSelect = Offset(nameof(DseDurationSelect));
        DurationWorkBytes = Offset(nameof(DurationWorkBytes));
        DurationWorkHalves = Offset(nameof(DurationWorkHalves));
        DurationWorkKind = Offset(nameof(DurationWorkKind));
        DurationWorkRightName = Offset(nameof(DurationWorkRightName));
        DseDurationRandom = Offset(nameof(DseDurationRandom));
        DurationCaller0 = Offset(nameof(DurationCaller0));
        DurationCaller1 = Offset(nameof(DurationCaller1));
        DurationCaller2 = Offset(nameof(DurationCaller2));
        DurationCaller3 = Offset(nameof(DurationCaller3));
        DurationCaller4 = Offset(nameof(DurationCaller4));
        DurationCaller5 = Offset(nameof(DurationCaller5));
        DurationCaller6 = Offset(nameof(DurationCaller6));
        DurationCaller7 = Offset(nameof(DurationCaller7));
        DurationCaller8 = Offset(nameof(DurationCaller8));
    }
    private static TimingNativeProfile ReadEmbedded(string resource)
    {
        using var source = typeof(TimingNativeProfile).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Missing timing compatibility profile.");
        using var buffer = new MemoryStream(); source.CopyTo(buffer);
        return new(buffer.ToArray());
    }
    internal bool Matches(string dse, string vsm) =>
        string.Equals(DseSha256, dse, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(VsmSha256, vsm, StringComparison.OrdinalIgnoreCase);
    private int Offset(string name) => int.Parse(_values[name].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    internal readonly int EngineSlot;
    internal readonly int EngineConfigureSlot;
    internal readonly int EngineStartSlot;
    internal readonly int EngineStopSlot;
    internal readonly int EngineDestroySlot;
    internal readonly int ScheduleCapacity;
    internal readonly int ScheduleMappedCount;
    internal readonly int RowBegin;
    internal readonly int RowEnd;
    internal readonly int RowSplit;
    internal readonly int RowFrom;
    internal readonly int RowTo;
    internal readonly int RowLeftFlag;
    internal readonly int RowRightFlag;
    internal readonly int ScoreStride;
    internal readonly int ScoreLeftDistance;
    internal readonly int ScoreRightDistance;
    internal readonly int ScoreLeftTransition;
    internal readonly int ScoreRightTransition;
    internal readonly int ScoreFromFlag;
    internal readonly int ScoreToFlag;
    internal readonly int ScoreFromPhone;
    internal readonly int ScoreToPhone;
    internal readonly int RendererLivePart;
    internal readonly int RendererSnapshotPart;
    internal readonly int DurationWorkLeftName;
    internal readonly int DseCreateEngine;
    internal readonly int DseEngineVtable;
    internal readonly int DseStates;
    internal readonly int DseStartMutex;
    internal readonly int StateCurrentFrame;
    internal readonly int StateMutex;
    internal readonly int StateSchedule;
    internal readonly int StateParser;
    internal readonly int DseScheduleClear;
    internal readonly int DseParserReset;
    internal readonly int DseParse;
    internal readonly int DseScheduleEnd;
    internal readonly int DseScheduleRow;
    internal readonly int DseDictionary;
    internal readonly int DsePhoneName;
    internal readonly int DseVelocityCoefficient;
    internal readonly int RowDictionary;
    internal readonly int RowSinger;
    internal readonly int RowSustainSplit;
    internal readonly int DictionaryPhones;
    internal readonly int ParserSlot;
    internal readonly int ParserNotes;
    internal readonly int NoteStride;
    internal readonly int NoteCoefficient;
    internal readonly int NotePhoneCount;
    internal readonly int NotePhones;
    internal readonly int VsmEncodeNote;
    internal readonly int VsmEncodeSinger;
    internal readonly int VsmMapScore;
    internal readonly int PartMappingFlag;
    internal readonly int NotePositionsBegin;
    internal readonly int NotePositionsEnd;
    internal readonly int DseMapper;
    internal readonly int DseStationary;
    internal readonly int DseAllocate;
    internal readonly int DsePaddingCaller;
    internal readonly int VsmPrepare;
    internal readonly int VsmCacheHash;
    internal readonly int DseDurationScore;
    internal readonly int DseDurationNote;
    internal readonly int DseDurationWork;
    internal readonly int DseDurationLookup;
    internal readonly int DseDurationSelect;
    internal readonly int DurationWorkBytes;
    internal readonly int DurationWorkHalves;
    internal readonly int DurationWorkKind;
    internal readonly int DurationWorkRightName;
    internal readonly int DseDurationRandom;
    internal readonly int DurationCaller0;
    internal readonly int DurationCaller1;
    internal readonly int DurationCaller2;
    internal readonly int DurationCaller3;
    internal readonly int DurationCaller4;
    internal readonly int DurationCaller5;
    internal readonly int DurationCaller6;
    internal readonly int DurationCaller7;
    internal readonly int DurationCaller8;
}
