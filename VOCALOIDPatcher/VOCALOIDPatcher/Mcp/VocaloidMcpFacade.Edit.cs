using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using VOCALOIDPatcher.McpBridge;
using VOCALOIDPatcher.Patch.Patches;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Mcp;

internal static partial class VocaloidMcpFacade
{
    private static object EditStructure(BridgeClientInfo client, JsonElement arguments)
    {
        (_, long previousRevision) = ValidateProject(arguments);
        JsonElement operations = Operations(arguments);
        bool dryRun = Bool(arguments, "dry_run");
        bool dangerous = operations.EnumerateArray().Any(operation =>
            String(operation, "op") is "delete_track" or "delete_part");
        Authorize(client, $"Edit project structure ({operations.GetArrayLength()} operations)", dangerous, dryRun);
        (_, WIVSMSequence vsm) = Context();

        if (!dryRun)
        {
            using var transaction = new Transaction(vsm) { Result = false };
            foreach (JsonElement operation in operations.EnumerateArray())
                ApplyStructureOperation(vsm, operation, true);
            transaction.Result = true;
        }
        else
        {
            foreach (JsonElement operation in operations.EnumerateArray())
                ApplyStructureOperation(vsm, operation, false);
        }

        long revision = dryRun ? previousRevision : McpRevisionTracker.Current().Revision;
        if (!dryRun)
            RefreshEditor();
        return MutationResult(dryRun, operations.GetArrayLength(), revision, dangerous);
    }

    private static void ApplyStructureOperation(WIVSMSequence vsm, JsonElement operation, bool execute)
    {
        string op = String(operation, "op") ?? throw Fault("invalid_request", "Each structure operation requires op.");
        switch (op)
        {
            case "add_track":
            {
                string typeName = String(operation, "type") ?? "midi_ai";
                VSMTrackType type = typeName.ToLowerInvariant() switch
                {
                    "midi" or "standard" => VSMTrackType.Midi,
                    "midi_ai" or "ai" => VSMTrackType.MidiAi,
                    "audio" => VSMTrackType.Audio,
                    _ => throw Fault("invalid_request", $"Unknown track type '{typeName}'."),
                };
                int index = Math.Clamp(Int(operation, "index", vsm.Tracks.Count), 0, vsm.Tracks.Count);
                string name = String(operation, "name") ?? "MCP Track";
                if (execute)
                {
                    WIVSMTrack track = vsm.InsertTrackEx((ulong)index, type, name)
                        ?? throw Fault("operation_failed", "VOCALOID could not insert the track.");
                    track.Color = type switch
                    {
                        VSMTrackType.Midi => UserSettings.Instance.VocaloidTrackColor,
                        VSMTrackType.MidiAi => UserSettings.Instance.VocaloidAiTrackColor,
                        VSMTrackType.Audio => UserSettings.Instance.AudioTrackColor,
                        _ => track.Color,
                    };
                    if (track is WIVSMMidiTrack midiTrack)
                        midiTrack.IsEnabledMidiRecording = false;
                    if (App.EffectEngine?.AddEffectBlockIfNoExist(track) == null)
                        throw Fault("operation_failed", "VOCALOID could not initialize the track effect block.");
                }
                break;
            }
            case "rename_track":
            {
                WIVSMTrack track = Track(vsm, Int(operation, "track_index", -1));
                string name = String(operation, "name") ?? throw Fault("invalid_request", "name is required.");
                if (execute)
                    track.Name = name;
                break;
            }
            case "move_track":
            {
                int from = Int(operation, "track_index", -1);
                Track(vsm, from);
                int to = Int(operation, "to_index", -1);
                if (to < 0 || to >= vsm.Tracks.Count)
                    throw Fault("invalid_reference", "to_index is out of range.");
                if (execute && !vsm.MoveTrack((ulong)from, (ulong)to))
                    throw Fault("operation_failed", "VOCALOID could not move the track.");
                break;
            }
            case "delete_track":
            {
                WIVSMTrack track = Track(vsm, Int(operation, "track_index", -1));
                if (execute && !vsm.RemoveTrack(track))
                    throw Fault("operation_failed", "VOCALOID could not delete the track.");
                break;
            }
            case "set_track_state":
            {
                WIVSMTrack track = Track(vsm, Int(operation, "track_index", -1));
                if (execute)
                {
                    if (Element(operation, "mute") != null)
                        track.SetMute(Bool(operation, "mute"));
                    if (Element(operation, "solo") != null)
                        track.SetSolo(Bool(operation, "solo"));
                }
                break;
            }
            case "add_part":
            {
                WIVSMTrack track = Track(vsm, Int(operation, "track_index", -1));
                long position = ResolveAbsoluteTick(vsm, operation);
                long duration = Long(operation, "duration_tick") ?? 1920;
                if (position < 0 || duration <= 0)
                    throw Fault("invalid_request", "Part position and duration are invalid.");
                string name = String(operation, "name") ?? "MCP Part";
                if (track is WIVSMMidiTrack midiTrack)
                {
                    if (execute)
                    {
                        WIVSMMidiPart midiPart = midiTrack.InsertPart(
                            new VSMAbsTick(position),
                            new VSMRelTick(duration),
                            name) ?? throw Fault("operation_failed", "VOCALOID could not insert the MIDI part.");
                        InitializeMidiPartDefaults(midiPart, String(operation, "voicebank_id"));
                    }
                }
                else if (track is WIVSMAudioTrack audioTrack)
                {
                    string audioPath = String(operation, "audio_path")
                                       ?? throw Fault("invalid_request", "audio_path is required when inserting an audio part.");
                    if (!McpAccessController.TryResolvePath(audioPath, out string fullPath, out BridgeError? pathError))
                        throw Fault(pathError!);
                    if (!File.Exists(fullPath))
                        throw Fault("invalid_reference", "The audio file does not exist.");
                    if (execute)
                    {
                        WIVSMAudioPart audioPart = audioTrack.InsertPart(new VSMAbsTick(position), name)
                                                   ?? throw Fault("operation_failed", "VOCALOID could not insert the audio part.");
                        audioPart.Region = new VSMAudioPartRegion(0, checked((int)duration));
                        if (!audioPart.SetOriginalWaveFile(fullPath))
                            throw Fault("operation_failed", "VOCALOID could not attach the audio file.");
                    }
                }
                else
                {
                    throw Fault("unsupported", "The installed editor does not support this track type.");
                }
                break;
            }
            case "rename_part":
            case "set_part":
            {
                WIVSMPart part = Part(vsm, Int(operation, "track_index", -1), Int(operation, "part_index", -1));
                if (!execute)
                    break;
                string? name = String(operation, "name");
                if (name != null && !part.SetName(name))
                    throw Fault("operation_failed", "VOCALOID could not rename the part.");
                if (part is WIVSMMidiPart midi)
                {
                    string? voicebank = String(operation, "voicebank_id");
                    if (voicebank != null && !(midi.IsAi ? midi.SetAiVoiceBankID(voicebank) : midi.SetVoiceBankID(voicebank)))
                        throw Fault("operation_failed", "VOCALOID rejected the voicebank.");
                    string? style = String(operation, "style_name");
                    if (style != null && !midi.SetStyleName(style))
                        throw Fault("operation_failed", "VOCALOID rejected the style.");
                    if (voicebank != null && style == null && string.IsNullOrEmpty(midi.StyleName))
                        ApplyVoiceDefaultStyle(midi);
                }
                break;
            }
            case "set_breath_effect":
            {
                WIVSMMidiPart part = MidiPart(
                    vsm,
                    Int(operation, "track_index", -1),
                    Int(operation, "part_index", -1));
                WIVSMBreathEffect effect = part.EffectManager?.BreathEffect
                    ?? throw Fault("unsupported", "The MIDI part does not expose a breath effect.");

                VSMBreathMode? mode = null;
                if (String(operation, "mode") is { } modeName)
                {
                    if (!Enum.TryParse(modeName, true, out VSMBreathMode parsedMode))
                        throw Fault("invalid_request", $"Unknown breath mode '{modeName}'.");
                    mode = parsedMode;
                }

                VSMBreathType? type = null;
                if (String(operation, "type") is { } typeName)
                {
                    if (!Enum.TryParse(typeName, true, out VSMBreathType parsedType))
                        throw Fault("invalid_request", $"Unknown breath type '{typeName}'.");
                    type = parsedType;
                }

                int? exhalation = Element(operation, "exhalation") != null
                    ? Int(operation, "exhalation")
                    : null;
                if (exhalation is < 0 or > 127)
                    throw Fault("invalid_request", "exhalation must be between 0 and 127.");

                if (execute)
                {
                    if (Element(operation, "bypass") != null && !effect.SetBypass(Bool(operation, "bypass")))
                        throw Fault("operation_failed", "VOCALOID rejected the breath bypass setting.");
                    if (mode != null && !effect.SetBreathMode(mode.Value))
                        throw Fault("operation_failed", "VOCALOID rejected the breath mode.");
                    if (type != null && !effect.SetBreathType(type.Value))
                        throw Fault("operation_failed", "VOCALOID rejected the breath type.");
                    if (exhalation != null && !effect.SetExhalation(exhalation.Value))
                        throw Fault("operation_failed", "VOCALOID rejected the exhalation setting.");
                }
                break;
            }
            case "move_part":
            {
                int sourceTrackIndex = Int(operation, "track_index", -1);
                WIVSMTrack sourceTrack = Track(vsm, sourceTrackIndex);
                WIVSMPart part = Part(vsm, sourceTrackIndex, Int(operation, "part_index", -1));
                WIVSMTrack targetTrack = Track(vsm, Int(operation, "to_track_index", sourceTrackIndex));
                long position = ResolveAbsoluteTick(vsm, operation, part.AbsPosTick.Value);
                if (position < 0)
                    throw Fault("invalid_request", "absolute_tick cannot be negative.");
                if (execute && !sourceTrack.MovePart(new VSMAbsTick(position), targetTrack, part))
                    throw Fault("operation_failed", "VOCALOID could not move the part.");
                break;
            }
            case "resize_part":
            {
                WIVSMPart part = Part(vsm, Int(operation, "track_index", -1), Int(operation, "part_index", -1));
                long? position = Element(operation, "absolute_tick") != null || Element(operation, "position") != null
                    ? ResolveAbsoluteTick(vsm, operation)
                    : null;
                long? duration = Long(operation, "duration_tick");
                if (position is < 0 || duration is <= 0)
                    throw Fault("invalid_request", "Part position or duration is invalid.");
                if (!execute)
                    break;
                if (part is WIVSMMidiPart midi)
                {
                    if (position != null && !midi.ResizeLeft(new VSMAbsTick(position.Value)))
                        throw Fault("operation_failed", "VOCALOID could not resize the left edge.");
                    if (duration != null && !midi.SetDuration(new VSMRelTick(duration.Value)))
                        throw Fault("operation_failed", "VOCALOID could not resize the part.");
                }
                else if (part is WIVSMAudioPart audio)
                {
                    WIVSMTrack track = Track(vsm, Int(operation, "track_index", -1));
                    if (position != null && !track.MovePart(new VSMAbsTick(position.Value), track, audio))
                        throw Fault("operation_failed", "VOCALOID could not move the audio part.");
                    if (duration != null)
                    {
                        VSMAudioPartRegion region = audio.Region;
                        region.TickEnd = checked(region.TickBegin + (int)duration.Value);
                        if (!audio.SetRegion(region))
                            throw Fault("operation_failed", "VOCALOID could not resize the audio part.");
                    }
                }
                else
                {
                    throw Fault("unsupported", "The installed editor does not support this part type.");
                }
                break;
            }
            case "duplicate_part":
            {
                WIVSMPart part = Part(vsm, Int(operation, "track_index", -1), Int(operation, "part_index", -1));
                WIVSMTrack target = Track(vsm, Int(operation, "to_track_index", Int(operation, "track_index", -1)));
                long position = ResolveAbsoluteTick(vsm, operation, part.AbsPosTick.Value);
                if (execute && target.DuplicatePart(new VSMAbsTick(position), part) == null)
                    throw Fault("operation_failed", "VOCALOID could not duplicate the part.");
                break;
            }
            case "delete_part":
            {
                WIVSMTrack track = Track(vsm, Int(operation, "track_index", -1));
                WIVSMPart part = Part(vsm, Int(operation, "track_index", -1), Int(operation, "part_index", -1));
                if (execute && !track.RemovePart(part))
                    throw Fault("operation_failed", "VOCALOID could not delete the part.");
                break;
            }
            case "add_tempo":
            {
                long tick = ResolveAbsoluteTick(vsm, operation);
                int value = TempoValue(operation);
                if (execute && vsm.InsertTempo(new VSMRelTick(tick), value) == null)
                    throw Fault("operation_failed", "VOCALOID could not insert the tempo.");
                break;
            }
            case "update_tempo":
            {
                int index = Int(operation, "item_index", -1);
                if (index < 0 || index >= vsm.Tempos.Count)
                    throw Fault("invalid_reference", "Tempo index is out of range.");
                if (execute)
                {
                    WIVSMTempo tempo = vsm.Tempos[index];
                    if (Element(operation, "absolute_tick") != null
                        && !vsm.MoveTempo(new VSMRelTick(ResolveAbsoluteTick(vsm, operation)), tempo))
                        throw Fault("operation_failed", "VOCALOID could not move the tempo.");
                    if (Element(operation, "value") != null || Element(operation, "bpm") != null)
                        tempo.Value = TempoValue(operation);
                }
                break;
            }
            case "delete_tempo":
            {
                int index = Int(operation, "item_index", -1);
                if (index <= 0 || index >= vsm.Tempos.Count)
                    throw Fault("invalid_reference", "Tempo index is out of range or identifies the required initial tempo.");
                if (execute && !vsm.RemoveTempo(vsm.Tempos[index]))
                    throw Fault("operation_failed", "VOCALOID could not delete the tempo.");
                break;
            }
            case "add_time_signature":
            {
                int bar = Int(operation, "bar", 1) - 1;
                int numerator = Int(operation, "numerator", 4);
                int denominator = Int(operation, "denominator", 4);
                if (bar < 0 || numerator <= 0 || denominator <= 0)
                    throw Fault("invalid_request", "Time signature values are invalid.");
                if (execute && vsm.InsertTimeSig(bar, new VSMTimeSigEvent(numerator, denominator)) == null)
                    throw Fault("operation_failed", "VOCALOID could not insert the time signature.");
                break;
            }
            case "update_time_signature":
            {
                int index = Int(operation, "item_index", -1);
                if (index < 0 || index >= vsm.TimeSigs.Count)
                    throw Fault("invalid_reference", "Time signature index is out of range.");
                if (execute)
                {
                    WIVSMTimeSig timeSig = vsm.TimeSigs[index];
                    if (Element(operation, "bar") != null && !vsm.MoveTimeSig(Int(operation, "bar", 1) - 1, timeSig))
                        throw Fault("operation_failed", "VOCALOID could not move the time signature.");
                    if ((Element(operation, "numerator") != null || Element(operation, "denominator") != null)
                        && !timeSig.SetTimeSig(
                            Int(operation, "numerator", timeSig.Numer),
                            Int(operation, "denominator", timeSig.Denom)))
                        throw Fault("operation_failed", "VOCALOID rejected the time signature.");
                }
                break;
            }
            case "delete_time_signature":
            {
                int index = Int(operation, "item_index", -1);
                if (index <= 0 || index >= vsm.TimeSigs.Count)
                    throw Fault("invalid_reference", "Time signature index is out of range or identifies the required initial signature.");
                if (execute && !vsm.RemoveTimeSig(vsm.TimeSigs[index]))
                    throw Fault("operation_failed", "VOCALOID could not delete the time signature.");
                break;
            }
            default:
                throw Fault("unsupported", $"Unsupported structure operation '{op}'.");
        }
    }

    private static int TempoValue(JsonElement operation)
    {
        if (Long(operation, "value") is { } raw)
            return checked((int)raw);
        double bpm = Double(operation, "bpm", 120.0);
        if (bpm is < 20 or > 300)
            throw Fault("invalid_request", "bpm must be between 20 and 300.");
        return checked((int)Math.Round(bpm * 100));
    }

    private static object EditNotes(BridgeClientInfo client, JsonElement arguments)
    {
        (_, long previousRevision) = ValidateProject(arguments);
        JsonElement operations = Operations(arguments);
        bool dryRun = Bool(arguments, "dry_run");
        int deletionCount = operations.EnumerateArray().Count(operation => String(operation, "op") == "delete");
        bool dangerous = deletionCount > 32;
        Authorize(client, $"Edit notes ({operations.GetArrayLength()} operations)", dangerous, dryRun);
        (_, WIVSMSequence vsm) = Context();

        if (!dryRun)
        {
            using var transaction = new Transaction(vsm) { Result = false };
            foreach (JsonElement operation in operations.EnumerateArray())
                ApplyNoteOperation(vsm, operation, true);
            transaction.Result = true;
        }
        else
        {
            foreach (JsonElement operation in operations.EnumerateArray())
                ApplyNoteOperation(vsm, operation, false);
        }

        long revision = dryRun ? previousRevision : McpRevisionTracker.Current().Revision;
        if (!dryRun)
            RefreshEditor();
        return MutationResult(dryRun, operations.GetArrayLength(), revision, dangerous);
    }

    private static void ApplyNoteOperation(WIVSMSequence vsm, JsonElement operation, bool execute)
    {
        string op = String(operation, "op") ?? throw Fault("invalid_request", "Each note operation requires op.");
        int trackIndex = Int(operation, "track_index", -1);
        int partIndex = Int(operation, "part_index", -1);
        WIVSMMidiPart part = MidiPart(vsm, trackIndex, partIndex);
        switch (op)
        {
            case "add":
            {
                long tick = ResolvePartRelativeTick(vsm, part, operation);
                int duration = Int(operation, "duration_tick", 480);
                int number = Int(operation, "note_number", 60);
                int velocity = Int(operation, "velocity", 64);
                int language = Int(operation, "language_id", part.LangID);
                if (tick < 0 || duration <= 0 || number is < 0 or > 127 || velocity is < 0 or > 127)
                    throw Fault("invalid_request", "Note position, duration, number, or velocity is invalid.");
                if (execute)
                {
                    string lyric = String(operation, "lyric") ?? "あ";
                    string phonemes = String(operation, "phonemes") ?? string.Empty;
                    bool validPhonemes = !string.IsNullOrWhiteSpace(phonemes);
                    if (part.InsertNote(
                            new VSMRelTick(tick),
                            new VSMNoteEvent(duration, number, velocity),
                            part.GetDefaultNoteExpression(),
                            part.GetDefaultAiNoteExpression(),
                            lyric,
                            phonemes,
                            validPhonemes,
                            language) == null)
                        throw Fault("operation_failed", "VOCALOID could not insert the note.");
                }
                break;
            }
            case "move":
            {
                WIVSMNote note = Note(vsm, trackIndex, partIndex, Int(operation, "note_index", -1));
                long tick = ResolvePartRelativeTick(vsm, part, operation, note.RelPosTick.Value);
                int number = Int(operation, "note_number", note.NoteNumber);
                if (tick < 0 || number is < 0 or > 127)
                    throw Fault("invalid_request", "Note position or number is invalid.");
                if (execute && (!part.MoveNote(new VSMRelTick(tick), note) || !note.SetNoteNumber(number)))
                    throw Fault("operation_failed", "VOCALOID could not move the note.");
                break;
            }
            case "resize":
            {
                WIVSMNote note = Note(vsm, trackIndex, partIndex, Int(operation, "note_index", -1));
                long duration = Long(operation, "duration_tick") ?? note.DurationTick.Value;
                if (duration <= 0)
                    throw Fault("invalid_request", "duration_tick must be positive.");
                if (execute && !note.SetDuration(new VSMRelTick(duration)))
                    throw Fault("operation_failed", "VOCALOID could not resize the note.");
                break;
            }
            case "update":
            {
                WIVSMNote note = Note(vsm, trackIndex, partIndex, Int(operation, "note_index", -1));
                if (!execute)
                    break;
                if (String(operation, "lyric") is { } lyric)
                    note.Lyric = lyric;
                if (Element(operation, "note_number") != null && !note.SetNoteNumber(Int(operation, "note_number")))
                    throw Fault("operation_failed", "VOCALOID rejected the note number.");
                if (Element(operation, "language_id") != null && !note.SetLangID(Int(operation, "language_id")))
                    throw Fault("operation_failed", "VOCALOID rejected the language.");
                if (String(operation, "phonemes") is { } phonemes
                    && !note.SetPhonemes(phonemes, true, Int(operation, "language_id", note.LangID)))
                    throw Fault("operation_failed", "VOCALOID rejected the phonemes.");
                if (Element(operation, "vibrato_enabled") != null && !note.SetAiVibratoEnabled(Bool(operation, "vibrato_enabled")))
                    throw Fault("operation_failed", "VOCALOID rejected the vibrato setting.");
                if (Element(operation, "vibrato_duration_tick") != null
                    && !note.SetVibratoDuration(new VSMRelTick(Long(operation, "vibrato_duration_tick")!.Value)))
                    throw Fault("operation_failed", "VOCALOID rejected the vibrato duration.");
                if (String(operation, "vibrato_type") is { } vibratoName)
                {
                    if (!Enum.TryParse(vibratoName, true, out VSMVibratoType vibratoType))
                        throw Fault("invalid_request", $"Unknown vibrato type '{vibratoName}'.");
                    note.VibratoType = vibratoType;
                }
                if (Element(operation, "vibrato_depth") != null)
                {
                    note.RemoveAllVibratoEvents(VSMVibratoEventType.Depth);
                    if (note.InsertVibratoEvent(VSMRelTick.Zero, VSMVibratoEventType.Depth, Int(operation, "vibrato_depth")) == null)
                        throw Fault("operation_failed", "VOCALOID rejected the vibrato depth.");
                }
                if (Element(operation, "vibrato_rate") != null)
                {
                    note.RemoveAllVibratoEvents(VSMVibratoEventType.Rate);
                    if (note.InsertVibratoEvent(VSMRelTick.Zero, VSMVibratoEventType.Rate, Int(operation, "vibrato_rate")) == null)
                        throw Fault("operation_failed", "VOCALOID rejected the vibrato rate.");
                }
                ApplyNoteExpressions(note, operation);
                break;
            }
            case "duplicate":
            case "copy":
            {
                WIVSMNote note = Note(vsm, trackIndex, partIndex, Int(operation, "note_index", -1));
                WIVSMMidiPart target = MidiPart(vsm, Int(operation, "to_track_index", trackIndex), Int(operation, "to_part_index", partIndex));
                long tick = ResolvePartRelativeTick(vsm, target, operation, note.RelPosTick.Value + note.DurationTick.Value);
                if (execute && target.DuplicateNote(new VSMRelTick(tick), note) == null)
                    throw Fault("operation_failed", "VOCALOID could not duplicate the note.");
                break;
            }
            case "delete":
            {
                WIVSMNote note = Note(vsm, trackIndex, partIndex, Int(operation, "note_index", -1));
                if (execute && !part.RemoveNote(note))
                    throw Fault("operation_failed", "VOCALOID could not delete the note.");
                break;
            }
            default:
                throw Fault("unsupported", $"Unsupported note operation '{op}'.");
        }
    }

    private static void ApplyNoteExpressions(WIVSMNote note, JsonElement operation)
    {
        if (Element(operation, "expression") is { ValueKind: JsonValueKind.Object } source)
        {
            VSMNoteExpression expression = note.GetNoteExpression();
            expression.Accent = Int(source, "accent", expression.Accent);
            expression.Decay = Int(source, "decay", expression.Decay);
            expression.BendDepth = Int(source, "bend_depth", expression.BendDepth);
            expression.BendLength = Int(source, "bend_length", expression.BendLength);
            expression.Opening = Int(source, "opening", expression.Opening);
            expression.RisePort = Bool(source, "rise_port", expression.RisePort);
            expression.FallPort = Bool(source, "fall_port", expression.FallPort);
            if (!note.SetNoteExpression(expression))
                throw Fault("operation_failed", "VOCALOID rejected the note expression.");
        }

        if (Element(operation, "ai_expression") is { ValueKind: JsonValueKind.Object } aiSource)
        {
            VSMAiNoteExpression expression = note.GetAiNoteExpression();
            expression.PitchFine = (float)Double(aiSource, "pitch_fine", expression.PitchFine);
            expression.PitchDriftStart = (float)Double(aiSource, "pitch_drift_start", expression.PitchDriftStart);
            expression.PitchDriftEnd = (float)Double(aiSource, "pitch_drift_end", expression.PitchDriftEnd);
            expression.PitchScalingCenter = (float)Double(aiSource, "pitch_scaling_center", expression.PitchScalingCenter);
            expression.PitchScalingOrigin = (float)Double(aiSource, "pitch_scaling_origin", expression.PitchScalingOrigin);
            expression.PitchTransitionStart = (float)Double(aiSource, "pitch_transition_start", expression.PitchTransitionStart);
            expression.PitchTransitionEnd = (float)Double(aiSource, "pitch_transition_end", expression.PitchTransitionEnd);
            expression.AmplitudeWhole = (float)Double(aiSource, "amplitude_whole", expression.AmplitudeWhole);
            expression.AmplitudeStart = (float)Double(aiSource, "amplitude_start", expression.AmplitudeStart);
            expression.AmplitudeEnd = (float)Double(aiSource, "amplitude_end", expression.AmplitudeEnd);
            expression.VibratoLeadingDepth = (float)Double(aiSource, "vibrato_leading_depth", expression.VibratoLeadingDepth);
            expression.VibratoFollowingDepth = (float)Double(aiSource, "vibrato_following_depth", expression.VibratoFollowingDepth);
            if (!note.SetAiNoteExpression(expression))
                throw Fault("operation_failed", "VOCALOID rejected the AI note expression.");
        }
    }

    private static object EditParameters(BridgeClientInfo client, JsonElement arguments)
    {
        (_, long previousRevision) = ValidateProject(arguments);
        JsonElement operations = Operations(arguments);
        bool dryRun = Bool(arguments, "dry_run");
        Authorize(client, $"Edit parameters ({operations.GetArrayLength()} operations)", false, dryRun);
        (_, WIVSMSequence vsm) = Context();

        if (!dryRun)
        {
            using var transaction = new Transaction(vsm) { Result = false };
            foreach (JsonElement operation in operations.EnumerateArray())
                ApplyParameterOperation(vsm, operation, true);
            transaction.Result = true;
        }
        else
        {
            foreach (JsonElement operation in operations.EnumerateArray())
                ApplyParameterOperation(vsm, operation, false);
        }

        long revision = dryRun ? previousRevision : McpRevisionTracker.Current().Revision;
        if (!dryRun)
            RefreshEditor();
        return MutationResult(dryRun, operations.GetArrayLength(), revision, false);
    }

    private static void ApplyParameterOperation(WIVSMSequence vsm, JsonElement operation, bool execute)
    {
        string op = String(operation, "op") ?? throw Fault("invalid_request", "Each parameter operation requires op.");
        int trackIndex = Int(operation, "track_index", -1);
        switch (op)
        {
            case "add_controller":
            case "set_controller":
            {
                WIVSMMidiPart part = MidiPart(vsm, trackIndex, Int(operation, "part_index", -1));
                VSMControllerType type = ControllerType(operation);
                long tick = ResolvePartRelativeTick(vsm, part, operation);
                int value = Int(operation, "value");
                if (execute && part.InsertController(new VSMRelTick(tick), type, value) == null)
                    throw Fault("operation_failed", "VOCALOID could not insert the controller point.");
                break;
            }
            case "update_controller":
            {
                WIVSMMidiPart part = MidiPart(vsm, trackIndex, Int(operation, "part_index", -1));
                VSMControllerType type = ControllerType(operation);
                int itemIndex = Int(operation, "item_index", -1);
                if (itemIndex < 0 || (ulong)itemIndex >= part.GetNumController(type))
                    throw Fault("invalid_reference", "Controller point index is out of range.");
                if (execute)
                {
                    WIVSMMidiController point = part.GetController(type, (ulong)itemIndex)!;
                    if (Element(operation, "part_relative_tick") != null
                        && !part.MoveController(new VSMRelTick(Long(operation, "part_relative_tick")!.Value), point))
                        throw Fault("operation_failed", "VOCALOID could not move the controller point.");
                    if (Element(operation, "value") != null)
                        point.Value = Int(operation, "value");
                }
                break;
            }
            case "delete_controller":
            {
                WIVSMMidiPart part = MidiPart(vsm, trackIndex, Int(operation, "part_index", -1));
                VSMControllerType type = ControllerType(operation);
                int itemIndex = Int(operation, "item_index", -1);
                if (itemIndex < 0 || (ulong)itemIndex >= part.GetNumController(type))
                    throw Fault("invalid_reference", "Controller point index is out of range.");
                if (execute && !part.RemoveController(part.GetController(type, (ulong)itemIndex)!))
                    throw Fault("operation_failed", "VOCALOID could not delete the controller point.");
                break;
            }
            case "set_direct_pitch":
            {
                WIVSMNote note = Note(vsm, trackIndex, Int(operation, "part_index", -1), Int(operation, "note_index", -1));
                long tick = Long(operation, "note_relative_tick") ?? 0;
                float value = (float)Double(operation, "value");
                if (execute && !note.SetDirectPitch(new VSMRelTick(tick), value))
                    throw Fault("operation_failed", "VOCALOID could not set direct pitch.");
                break;
            }
            case "clear_direct_pitch":
            {
                WIVSMNote note = Note(vsm, trackIndex, Int(operation, "part_index", -1), Int(operation, "note_index", -1));
                long begin = Long(operation, "begin_tick") ?? int.MinValue;
                long end = Long(operation, "end_tick") ?? int.MaxValue;
                if (execute && !note.ClearDirectPitches(new VSMRelTick(begin), new VSMRelTick(end)))
                    throw Fault("operation_failed", "VOCALOID could not clear direct pitch.");
                break;
            }
            case "track_volume":
            case "track_pan":
            {
                WIVSMTrack track = Track(vsm, trackIndex);
                long tick = ResolveAbsoluteTick(vsm, operation);
                int value = Int(operation, "value");
                if (execute)
                {
                    object? point = op == "track_volume"
                        ? track.InsertVolume(new VSMRelTick(tick), value)
                        : track.InsertPanpot(new VSMRelTick(tick), value);
                    if (point == null)
                        throw Fault("operation_failed", "VOCALOID could not insert the track automation point.");
                }
                break;
            }
            case "update_track_volume":
            case "delete_track_volume":
            case "update_track_pan":
            case "delete_track_pan":
            {
                WIVSMTrack track = Track(vsm, trackIndex);
                int itemIndex = Int(operation, "item_index", -1);
                bool volume = op.EndsWith("volume", StringComparison.Ordinal);
                int count = checked((int)(volume ? track.NumVolumes : track.NumPanpots));
                if (itemIndex < 0 || itemIndex >= count || itemIndex == 0 && op.StartsWith("delete", StringComparison.Ordinal))
                    throw Fault("invalid_reference", "Automation index is invalid or identifies the required initial point.");
                if (!execute)
                    break;
                if (volume)
                {
                    WIVSMTrackVolume point = track.GetVolume((ulong)itemIndex)!;
                    if (op.StartsWith("delete", StringComparison.Ordinal))
                    {
                        if (!track.RemoveVolume(point)) throw Fault("operation_failed", "VOCALOID could not delete the volume point.");
                    }
                    else
                    {
                        if (Element(operation, "absolute_tick") != null || Element(operation, "position") != null)
                            if (!track.MoveVolume(new VSMRelTick(ResolveAbsoluteTick(vsm, operation)), point))
                                throw Fault("operation_failed", "VOCALOID could not move the volume point.");
                        if (Element(operation, "value") != null) point.Value = Int(operation, "value");
                    }
                }
                else
                {
                    WIVSMPanpot point = track.GetPanpot((ulong)itemIndex)!;
                    if (op.StartsWith("delete", StringComparison.Ordinal))
                    {
                        if (!track.RemovePanpot(point)) throw Fault("operation_failed", "VOCALOID could not delete the pan point.");
                    }
                    else
                    {
                        if (Element(operation, "absolute_tick") != null || Element(operation, "position") != null)
                            if (!track.MovePanpot(new VSMRelTick(ResolveAbsoluteTick(vsm, operation)), point))
                                throw Fault("operation_failed", "VOCALOID could not move the pan point.");
                        if (Element(operation, "value") != null) point.Value = Int(operation, "value");
                    }
                }
                break;
            }
            case "master_volume":
            {
                long tick = ResolveAbsoluteTick(vsm, operation);
                int value = Int(operation, "value");
                if (execute && vsm.InsertMasterVolume(new VSMRelTick(tick), value) == null)
                    throw Fault("operation_failed", "VOCALOID could not insert the master volume point.");
                break;
            }
            case "update_master_volume":
            case "delete_master_volume":
            {
                int itemIndex = Int(operation, "item_index", -1);
                if (itemIndex < 0 || itemIndex >= vsm.MasterVolumes.Count
                    || itemIndex == 0 && op == "delete_master_volume")
                    throw Fault("invalid_reference", "Master volume index is invalid or identifies the required initial point.");
                if (!execute)
                    break;
                WIVSMMasterVolume point = vsm.MasterVolumes[itemIndex];
                if (op == "delete_master_volume")
                {
                    if (!vsm.RemoveMasterVolume(point)) throw Fault("operation_failed", "VOCALOID could not delete the master volume point.");
                }
                else
                {
                    if (Element(operation, "absolute_tick") != null || Element(operation, "position") != null)
                        if (!vsm.MoveMasterVolume(new VSMRelTick(ResolveAbsoluteTick(vsm, operation)), point))
                            throw Fault("operation_failed", "VOCALOID could not move the master volume point.");
                    if (Element(operation, "value") != null) point.Value = Int(operation, "value");
                }
                break;
            }
            default:
                throw Fault("unsupported", $"Unsupported parameter operation '{op}'.");
        }
    }

    private static VSMControllerType ControllerType(JsonElement operation)
    {
        string name = String(operation, "parameter_type") ?? throw Fault("invalid_request", "parameter_type is required.");
        if (!TryControllerType(name, out VSMControllerType type))
            throw Fault("invalid_request", $"Unknown parameter type '{name}'.");
        return type;
    }

    private static bool TryControllerType(string name, out VSMControllerType type)
    {
        string normalized = name.ToUpperInvariant() switch
        {
            "DYN" => nameof(VSMControllerType.Dynamics),
            "BRE" => nameof(VSMControllerType.Breathiness),
            "BRI" => nameof(VSMControllerType.Brightness),
            "CLE" => nameof(VSMControllerType.Clearness),
            "PIT" => nameof(VSMControllerType.PitchBend),
            "PBS" => nameof(VSMControllerType.PitchBendSens),
            "POR" => nameof(VSMControllerType.Portamento),
            _ => name,
        };
        return Enum.TryParse(normalized, true, out type);
    }

    private static object MutationResult(bool dryRun, int operationCount, long revision, bool confirmationRequired)
    {
        (string projectId, _) = McpRevisionTracker.Current();
        return new
        {
            dry_run = dryRun,
            valid = true,
            operation_count = operationCount,
            confirmation_required = confirmationRequired,
            project = new ProjectContext(McpBridgeService.InstanceId ?? string.Empty, projectId, revision),
        };
    }

    private static void InitializeMidiPartDefaults(WIVSMMidiPart part, string? requestedVoicebankId)
    {
        DatabaseManager database = App.DatabaseManager
            ?? throw Fault("v6_unavailable", "The VOCALOID voicebank database is not available.", true);

        string voicebankId = part.IsAi
            ? part.Prev?.VoiceBankID ?? database.DefaultVoiceBank?.CompID ?? string.Empty
            : requestedVoicebankId ?? part.Prev?.VoiceBankID ?? database.DefaultVoiceBank?.CompID ?? string.Empty;
        string aiVoicebankId = part.IsAi
            ? requestedVoicebankId ?? part.Prev?.AiVoiceBankID ?? database.DefaultAiVoiceBank?.CompID ?? string.Empty
            : part.Prev?.AiVoiceBankID ?? database.DefaultAiVoiceBank?.CompID ?? string.Empty;

        if (!part.SetVoiceBankID(voicebankId) || !part.SetAiVoiceBankID(aiVoicebankId))
            throw Fault("operation_failed", "VOCALOID could not initialize the MIDI part voicebanks.");

        ApplyVoiceDefaultStyle(part);
    }

    private static void ApplyVoiceDefaultStyle(WIVSMMidiPart part)
    {
        DatabaseManager database = App.DatabaseManager
            ?? throw Fault("v6_unavailable", "The VOCALOID voicebank database is not available.", true);
        VoiceBank? voicebank = part.IsAi
            ? database.GetVoiceBankByCompID(part.AiVoiceBankID, VDMVoiceBankType.Dnn)
            : database.GetVoiceBankByCompID(part.VoiceBankID);
        if (voicebank == null)
            throw Fault("invalid_reference", "The MIDI part voicebank is not installed.");

        WIVSMEffectManager effectManager = part.EffectManager
            ?? throw Fault("unsupported", "The MIDI part does not expose an effect manager.");
        Yamaha.VOCALOID.VSStyle.Style? style = effectManager.GetVoiceDefaultStyle(voicebank);
        if (style != null
            && (!part.SetStyleName(style.Name) || !part.SetStylePresetID(style.Id)))
            throw Fault("operation_failed", "VOCALOID could not apply the voice default style.");

        try
        {
            effectManager.InsertVoiceDefaultStyleEffects(voicebank);
        }
        catch
        {
            throw Fault("operation_failed", "VOCALOID could not initialize the voice default effects.");
        }
        if (App.EffectEngine?.AddEffectBlockIfNoExist(part) == null)
            throw Fault("operation_failed", "VOCALOID could not initialize the MIDI part effect block.");
    }

    private static void RefreshEditor()
    {
        try
        {
            // A committed native transaction already raises the VSM update observer events that
            // drive TrackEditor/MusicalEditor ModelChanged refreshes. MainViewModel.Refresh() is
            // only for replacing the active Sequence: on the same Sequence it re-subscribes every
            // editor and AudioPlayer renderer handler without removing the previous subscription.
            // Repeating it after MCP mutations therefore multiplies every render callback.
            ShowOtherTracksNotesPatch.RefreshPianoroll();
        }
        catch
        {
            // The transaction has already committed; UI refresh must not invalidate it.
        }
    }

    private static MainViewModel? ApplicationMainViewModel()
        => System.Windows.Application.Current?.MainWindow?.DataContext as MainViewModel;
}
