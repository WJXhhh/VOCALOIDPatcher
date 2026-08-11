using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using VOCALOIDPatcher.Formats.LibreSvip.Framework;
using VOCALOIDPatcher.McpBridge;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Mcp;

internal static partial class VocaloidMcpFacade
{
    private sealed record PageCursor(string ProjectId, long Revision, string Kind, int Offset);

    private static object GetState()
    {
        (Yamaha.VOCALOID.Sequence sequence, WIVSMSequence vsm) = Context();
        (string projectId, long revision) = McpRevisionTracker.Current();
        int activeTrack = IndexOf(vsm.Tracks, sequence.ActiveTrack);
        int activePart = activeTrack < 0 ? -1 : IndexOf(vsm.Tracks[activeTrack].Parts, sequence.ActivePart);
        VSMTickRange loop = vsm.LoopRange;

        return new
        {
            instance_id = McpBridgeService.InstanceId,
            editor_version = typeof(App).Assembly.GetName().Version?.ToString(),
            project = new ProjectContext(McpBridgeService.InstanceId ?? string.Empty, projectId, revision),
            project_name = App.Shared?.Document?.FileName ?? vsm.Title,
            project_path = App.Shared?.Document?.DocumentUri?.LocalPath,
            is_new = App.Shared?.Document?.IsNew ?? false,
            is_dirty = vsm.IsDirty,
            playback = new
            {
                is_playing = App.AudioPlayer?.IsPlaying ?? false,
                position = Position(vsm, vsm.CurrentPosTick.Value),
                loop_enabled = vsm.IsLoopOn,
                loop_begin_tick = loop.Begin,
                loop_end_tick = loop.End,
            },
            rendering = new
            {
                is_rendering = !vsm.IsFinishedRendering,
                mixdown_mode = App.AudioPlayer?.MixdownMode.ToString(),
            },
            active = new
            {
                track = activeTrack < 0 ? null : new EntityRef(projectId, revision, "track", activeTrack),
                part = activePart < 0 ? null : new EntityRef(projectId, revision, "part", activeTrack, activePart),
            },
            access = McpAccessController.GetStatus(),
            capabilities = Capabilities(),
        };
    }

    private static CapabilityManifest Capabilities()
    {
        Version? version = typeof(App).Assembly.GetName().Version;
        var reasons = new List<string>();
        bool hasTransactions = typeof(WIVSMSequence).GetMethod("Commit") != null
                               && typeof(WIVSMSequence).GetMethod("Rollback") != null;
        bool hasMixdown = typeof(AudioPlayer).GetMethod("ExecuteAudioMixdownMaster") != null;
        if (!hasTransactions)
            reasons.Add("The installed editor does not expose the required transaction methods.");
        if (!hasMixdown)
            reasons.Add("The installed editor does not expose master mixdown.");
        if (version != null && version < new Version(6, 13))
            reasons.Add("This editor is older than the full-capability 6.13 baseline; individual calls may return unsupported.");

        return new CapabilityManifest(
            ReadProject: true,
            EditStructure: hasTransactions,
            EditNotes: hasTransactions,
            G2pa: hasTransactions,
            EditParameters: hasTransactions,
            Selection: true,
            Transport: true,
            History: typeof(WIVSMSequence).GetMethod("Undo") != null,
            ProjectFiles: true,
            Conversion: SvipFormatRegistry.All.Count > 0,
            Mixdown: hasMixdown,
            UnsupportedReasons: reasons);
    }

    private static object GetCatalog()
    {
        var voicebanks = new List<object>();
        DatabaseManager? database = App.DatabaseManager;
        if (database != null)
        {
            AddVoicebanks(database, VDMVoiceBankType.Dse, database.NumVoiceBanks, false, voicebanks);
            AddVoicebanks(database, VDMVoiceBankType.Dnn, database.NumAiVoiceBanks, true, voicebanks);
        }

        return new
        {
            voicebanks,
            languages = Enum.GetValues<VSMLanguageID>().Select(value => new { id = (int)value, name = value.ToString() }).ToArray(),
            parameter_types = Enum.GetNames<VSMControllerType>(),
            conversion_formats = SvipFormatRegistry.All.Select(info => new
            {
                id = info.Id,
                display_name = info.DisplayName,
                extensions = info.AllExtensions.ToArray(),
                can_import = info.CanImport,
                can_export = info.CanExport,
                multiple_file = info.MultipleFile,
            }).ToArray(),
            capabilities = Capabilities(),
        };
    }

    private static void AddVoicebanks(
        DatabaseManager database,
        VDMVoiceBankType type,
        ulong count,
        bool ai,
        ICollection<object> target)
    {
        for (ulong index = 0; index < count; index++)
        {
            VoiceBank? bank = database.GetVoiceBankByIndex(index, type);
            if (bank == null)
                continue;
            target.Add(new
            {
                comp_id = bank.CompID,
                name = bank.Name,
                is_ai = ai,
                native_language_id = bank.NativeLangID,
                language_ids = bank.LangIDs,
                available = bank.IsAvailableInSequence,
            });
        }
    }

    private static object QueryProject(JsonElement arguments)
    {
        string kind = String(arguments, "kind") ?? "summary";
        int pageSize = Math.Clamp(Int(arguments, "page_size", BridgeProtocol.DefaultPageSize), 1, BridgeProtocol.MaxPageSize);
        int offset = DecodeCursor(String(arguments, "page_token"), kind);
        return kind.ToLowerInvariant() switch
        {
            "summary" => QuerySummary(),
            "tracks" => QueryTracks(offset, pageSize),
            "parts" => QueryParts(offset, pageSize),
            "notes" or "lyrics" or "phonemes" => QueryNotes(offset, pageSize, Element(arguments, "filter")),
            "tempo" or "tempos" => QueryTempos(offset, pageSize),
            "time_signature" or "time_signatures" => QueryTimeSignatures(offset, pageSize),
            "parameters" => QueryParameters(offset, pageSize, Element(arguments, "filter")),
            "selection" => QuerySelection(),
            _ => throw Fault("invalid_request", $"Unsupported project query kind '{kind}'."),
        };
    }

    private static object QuerySummary()
    {
        (Yamaha.VOCALOID.Sequence sequence, WIVSMSequence vsm) = Context();
        (string projectId, long revision) = McpRevisionTracker.Current();
        int midiParts = vsm.Tracks.Sum(track => track.Parts.Count(part => part is WIVSMMidiPart));
        int audioParts = vsm.Tracks.Sum(track => track.Parts.Count(part => part is WIVSMAudioPart));
        int notes = vsm.MidiTracks.Sum(track => track.Notes.Count);
        return new
        {
            project = new ProjectContext(McpBridgeService.InstanceId ?? string.Empty, projectId, revision),
            title = vsm.Title,
            file_name = App.Shared?.Document?.FileName,
            is_dirty = vsm.IsDirty,
            track_count = vsm.Tracks.Count,
            midi_part_count = midiParts,
            audio_part_count = audioParts,
            note_count = notes,
            tempo_count = vsm.Tempos.Count,
            time_signature_count = vsm.TimeSigs.Count,
            selection = SelectionCore(sequence, vsm, projectId, revision),
        };
    }

    private static object QueryTracks(int offset, int pageSize)
    {
        (_, WIVSMSequence vsm) = Context();
        (string projectId, long revision) = McpRevisionTracker.Current();
        object[] all = vsm.Tracks.Select((track, index) => (object)new
        {
            reference = new EntityRef(projectId, revision, "track", index),
            name = track.Name,
            type = track.Type.ToString(),
            selected = track.IsSelected,
            mute = track.IsMute,
            solo = track.IsSolo,
            part_count = track.Parts.Count,
        }).ToArray();
        return Page(all, "tracks", offset, pageSize, projectId, revision);
    }

    private static object QueryParts(int offset, int pageSize)
    {
        (_, WIVSMSequence vsm) = Context();
        (string projectId, long revision) = McpRevisionTracker.Current();
        var all = new List<object>();
        for (int trackIndex = 0; trackIndex < vsm.Tracks.Count; trackIndex++)
        {
            WIVSMTrack track = vsm.Tracks[trackIndex];
            for (int partIndex = 0; partIndex < track.Parts.Count; partIndex++)
            {
                WIVSMPart part = track.Parts[partIndex];
                all.Add(new
                {
                    reference = new EntityRef(projectId, revision, "part", trackIndex, partIndex),
                    name = part.Name,
                    type = part.Type.ToString(),
                    selected = part.IsSelected,
                    position = Position(vsm, part.AbsPosTick.Value),
                    duration_tick = part.DurationTick.Value,
                    note_count = part is WIVSMMidiPart midiPart ? midiPart.Notes.Count : 0,
                    language_id = part is WIVSMMidiPart midi ? midi.LangID : (int?)null,
                    voicebank_id = part is WIVSMMidiPart midi2 ? (midi2.IsAi ? midi2.AiVoiceBankID : midi2.VoiceBankID) : null,
                    style_name = part is WIVSMMidiPart midi3 ? midi3.StyleName : null,
                });
            }
        }
        return Page(all, "parts", offset, pageSize, projectId, revision);
    }

    private static object QueryNotes(int offset, int pageSize, JsonElement? filter)
    {
        (_, WIVSMSequence vsm) = Context();
        (string projectId, long revision) = McpRevisionTracker.Current();
        int? onlyTrack = filter is { } f ? Long(f, "track_index") is { } t ? checked((int)t) : null : null;
        int? onlyPart = filter is { } f2 ? Long(f2, "part_index") is { } p ? checked((int)p) : null : null;
        var all = new List<object>();
        for (int trackIndex = 0; trackIndex < vsm.Tracks.Count; trackIndex++)
        {
            if (onlyTrack != null && trackIndex != onlyTrack)
                continue;
            WIVSMTrack track = vsm.Tracks[trackIndex];
            for (int partIndex = 0; partIndex < track.Parts.Count; partIndex++)
            {
                if (onlyPart != null && partIndex != onlyPart)
                    continue;
                if (track.Parts[partIndex] is not WIVSMMidiPart part)
                    continue;
                for (int noteIndex = 0; noteIndex < part.Notes.Count; noteIndex++)
                {
                    WIVSMNote note = part.Notes[noteIndex];
                    VSMNoteExpression expression = note.GetNoteExpression();
                    VSMAiNoteExpression aiExpression = note.GetAiNoteExpression();
                    all.Add(new
                    {
                        reference = new EntityRef(projectId, revision, "note", trackIndex, partIndex, noteIndex),
                        position = Position(vsm, note.AbsPosTick.Value, note.RelPosTick.Value),
                        duration_tick = note.DurationTick.Value,
                        note_number = note.NoteNumber,
                        lyric = note.Lyric,
                        phonemes = note.Phonemes,
                        language_id = note.LangID,
                        selected = note.IsSelected,
                        is_ai = note.IsAi,
                        expression = new
                        {
                            accent = expression.Accent,
                            decay = expression.Decay,
                            bend_depth = expression.BendDepth,
                            bend_length = expression.BendLength,
                            opening = expression.Opening,
                            rise_port = expression.RisePort,
                            fall_port = expression.FallPort,
                        },
                        ai_expression = new
                        {
                            pitch_fine = aiExpression.PitchFine,
                            pitch_drift_start = aiExpression.PitchDriftStart,
                            pitch_drift_end = aiExpression.PitchDriftEnd,
                            pitch_scaling_center = aiExpression.PitchScalingCenter,
                            pitch_scaling_origin = aiExpression.PitchScalingOrigin,
                            pitch_transition_start = aiExpression.PitchTransitionStart,
                            pitch_transition_end = aiExpression.PitchTransitionEnd,
                            amplitude_whole = aiExpression.AmplitudeWhole,
                            amplitude_start = aiExpression.AmplitudeStart,
                            amplitude_end = aiExpression.AmplitudeEnd,
                            vibrato_leading_depth = aiExpression.VibratoLeadingDepth,
                            vibrato_following_depth = aiExpression.VibratoFollowingDepth,
                        },
                        vibrato = new
                        {
                            type = note.VibratoType.ToString(),
                            duration_tick = note.VibratoDurationTick.Value,
                            enabled = note.IsAiVibratoEnabled,
                            depth = note.VibratoDepth,
                            rate = note.VibratoRate,
                        },
                        direct_pitch = note.DirectPitches.Select(point => new
                        {
                            note_relative_tick = point.Tick,
                            value = point.Value,
                        }).ToArray(),
                    });
                }
            }
        }
        return Page(all, "notes", offset, pageSize, projectId, revision);
    }

    private static object QueryTempos(int offset, int pageSize)
    {
        (_, WIVSMSequence vsm) = Context();
        (string projectId, long revision) = McpRevisionTracker.Current();
        object[] all = vsm.Tempos.Select((tempo, index) => (object)new
        {
            reference = new EntityRef(projectId, revision, "tempo", ItemIndex: index),
            position = Position(vsm, tempo.RelPosTick.Value),
            value = tempo.Value,
            bpm = tempo.Value / 100.0,
        }).ToArray();
        return Page(all, "tempos", offset, pageSize, projectId, revision);
    }

    private static object QueryTimeSignatures(int offset, int pageSize)
    {
        (_, WIVSMSequence vsm) = Context();
        (string projectId, long revision) = McpRevisionTracker.Current();
        object[] all = vsm.TimeSigs.Select((timeSig, index) => (object)new
        {
            reference = new EntityRef(projectId, revision, "time_signature", ItemIndex: index),
            bar = timeSig.PosBar + 1,
            numerator = timeSig.Numer,
            denominator = timeSig.Denom,
            absolute_tick = vsm.GetTickFromBar(timeSig.PosBar).Value,
        }).ToArray();
        return Page(all, "time_signatures", offset, pageSize, projectId, revision);
    }

    private static object QueryParameters(int offset, int pageSize, JsonElement? filter)
    {
        (_, WIVSMSequence vsm) = Context();
        (string projectId, long revision) = McpRevisionTracker.Current();
        int? onlyTrack = filter is { } f ? Long(f, "track_index") is { } t ? checked((int)t) : null : null;
        int? onlyPart = filter is { } f2 ? Long(f2, "part_index") is { } p ? checked((int)p) : null : null;
        string? requestedType = filter is { } f3 ? String(f3, "parameter_type") : null;
        IEnumerable<VSMControllerType> types = Enum.GetValues<VSMControllerType>();
        bool includeDirectPitch = requestedType == null || requestedType.Equals("direct_pitch", StringComparison.OrdinalIgnoreCase);
        bool includeTrackVolume = requestedType == null || requestedType.Equals("track_volume", StringComparison.OrdinalIgnoreCase);
        bool includeTrackPan = requestedType == null || requestedType.Equals("track_pan", StringComparison.OrdinalIgnoreCase);
        bool includeMasterVolume = requestedType == null || requestedType.Equals("master_volume", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(requestedType))
        {
            if (TryControllerType(requestedType, out VSMControllerType parsed))
                types = new[] { parsed };
            else if (includeDirectPitch || includeTrackVolume || includeTrackPan || includeMasterVolume)
                types = Array.Empty<VSMControllerType>();
            else
                throw Fault("invalid_request", $"Unknown parameter type '{requestedType}'.");
        }

        var all = new List<object>();
        for (int trackIndex = 0; trackIndex < vsm.Tracks.Count; trackIndex++)
        {
            if (onlyTrack != null && onlyTrack != trackIndex)
                continue;
            WIVSMTrack track = vsm.Tracks[trackIndex];
            if (onlyPart == null && includeTrackVolume)
                for (int index = 0; index < track.Volumes.Count; index++)
                {
                    WIVSMTrackVolume point = track.Volumes[index];
                    all.Add(new
                    {
                        reference = new EntityRef(projectId, revision, "track_volume", trackIndex, ItemIndex: index),
                        parameter_type = "track_volume",
                        absolute_tick = point.RelPosTick.Value,
                        value = point.Value,
                    });
                }
            if (onlyPart == null && includeTrackPan)
                for (int index = 0; index < track.Panpots.Count; index++)
                {
                    WIVSMPanpot point = track.Panpots[index];
                    all.Add(new
                    {
                        reference = new EntityRef(projectId, revision, "track_pan", trackIndex, ItemIndex: index),
                        parameter_type = "track_pan",
                        absolute_tick = point.RelPosTick.Value,
                        value = point.Value,
                    });
                }
            for (int partIndex = 0; partIndex < track.Parts.Count; partIndex++)
            {
                if (onlyPart != null && onlyPart != partIndex)
                    continue;
                if (track.Parts[partIndex] is not WIVSMMidiPart part)
                    continue;
                foreach (VSMControllerType type in types)
                {
                    ulong count;
                    try { count = part.GetNumController(type); }
                    catch { continue; }
                    for (ulong itemIndex = 0; itemIndex < count; itemIndex++)
                    {
                        WIVSMMidiController? point = part.GetController(type, itemIndex);
                        if (point == null)
                            continue;
                        all.Add(new
                        {
                            reference = new EntityRef(projectId, revision, "parameter", trackIndex, partIndex, checked((int)itemIndex)),
                            parameter_type = type.ToString(),
                            part_relative_tick = point.RelPosTick.Value,
                            absolute_tick = part.AbsPosTick.Value + point.RelPosTick.Value,
                            value = point.Value,
                        });
                    }
                }
                if (includeDirectPitch)
                {
                    for (int noteIndex = 0; noteIndex < part.Notes.Count; noteIndex++)
                    {
                        WIVSMNote note = part.Notes[noteIndex];
                        int pointIndex = 0;
                        foreach (VSMDirectPitchData point in note.DirectPitches)
                        {
                            all.Add(new
                            {
                                reference = new EntityRef(projectId, revision, "direct_pitch", trackIndex, partIndex, noteIndex),
                                parameter_type = "direct_pitch",
                                note_index = noteIndex,
                                point_index = pointIndex++,
                                note_relative_tick = point.Tick,
                                absolute_tick = note.AbsPosTick.Value + point.Tick,
                                value = point.Value,
                            });
                        }
                    }
                }
            }
        }
        if (onlyTrack == null && onlyPart == null && includeMasterVolume)
            for (int index = 0; index < vsm.MasterVolumes.Count; index++)
            {
                WIVSMMasterVolume point = vsm.MasterVolumes[index];
                all.Add(new
                {
                    reference = new EntityRef(projectId, revision, "master_volume", ItemIndex: index),
                    parameter_type = "master_volume",
                    absolute_tick = point.RelPosTick.Value,
                    value = point.Value,
                });
            }
        return Page(all, "parameters", offset, pageSize, projectId, revision);
    }

    private static object QuerySelection()
    {
        (Yamaha.VOCALOID.Sequence sequence, WIVSMSequence vsm) = Context();
        (string projectId, long revision) = McpRevisionTracker.Current();
        return SelectionCore(sequence, vsm, projectId, revision);
    }

    private static object SelectionCore(
        Yamaha.VOCALOID.Sequence sequence,
        WIVSMSequence vsm,
        string projectId,
        long revision)
    {
        var parts = new List<EntityRef>();
        var notes = new List<EntityRef>();
        for (int trackIndex = 0; trackIndex < vsm.Tracks.Count; trackIndex++)
        {
            WIVSMTrack track = vsm.Tracks[trackIndex];
            for (int partIndex = 0; partIndex < track.Parts.Count; partIndex++)
            {
                WIVSMPart part = track.Parts[partIndex];
                if (part.IsSelected)
                    parts.Add(new EntityRef(projectId, revision, "part", trackIndex, partIndex));
                if (part is not WIVSMMidiPart midi)
                    continue;
                for (int noteIndex = 0; noteIndex < midi.Notes.Count; noteIndex++)
                    if (midi.Notes[noteIndex].IsSelected)
                        notes.Add(new EntityRef(projectId, revision, "note", trackIndex, partIndex, noteIndex));
            }
        }

        int activeTrack = IndexOf(vsm.Tracks, sequence.ActiveTrack);
        int activePart = activeTrack < 0 ? -1 : IndexOf(vsm.Tracks[activeTrack].Parts, sequence.ActivePart);
        return new
        {
            active_track = activeTrack < 0 ? null : new EntityRef(projectId, revision, "track", activeTrack),
            active_part = activePart < 0 ? null : new EntityRef(projectId, revision, "part", activeTrack, activePart),
            selected_parts = parts,
            selected_notes = notes,
        };
    }

    private static MusicalPosition Position(WIVSMSequence vsm, long absoluteTick, long? partRelativeTick = null)
    {
        VSMBeatTime beat = vsm.GetBeatTimeFromTick(new VSMAbsTick(absoluteTick));
        return new MusicalPosition(
            absoluteTick,
            beat.Bar + 1,
            beat.Beat + 1,
            beat.Clock,
            vsm.GetTimeFromTick(new VSMAbsTick(absoluteTick)),
            partRelativeTick);
    }

    private static object Page(
        IReadOnlyList<object> all,
        string kind,
        int offset,
        int pageSize,
        string projectId,
        long revision)
    {
        if (offset < 0 || offset > all.Count)
            throw Fault("invalid_reference", "The page token offset is invalid.");
        object[] items = all.Skip(offset).Take(pageSize).ToArray();
        int next = offset + items.Length;
        return new
        {
            project = new ProjectContext(McpBridgeService.InstanceId ?? string.Empty, projectId, revision),
            items,
            total = all.Count,
            next_page_token = next < all.Count ? EncodeCursor(new PageCursor(projectId, revision, kind, next)) : null,
        };
    }

    private static string EncodeCursor(PageCursor cursor)
        => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor, BridgeProtocol.JsonOptions));

    private static int DecodeCursor(string? token, string kind)
    {
        if (string.IsNullOrWhiteSpace(token))
            return 0;
        try
        {
            PageCursor cursor = JsonSerializer.Deserialize<PageCursor>(Convert.FromBase64String(token), BridgeProtocol.JsonOptions)
                                ?? throw new InvalidOperationException();
            (string projectId, long revision) = McpRevisionTracker.Current();
            if (!string.Equals(cursor.ProjectId, projectId, StringComparison.Ordinal)
                || cursor.Revision != revision
                || !string.Equals(cursor.Kind, NormalizeKind(kind), StringComparison.OrdinalIgnoreCase))
                throw Fault("stale_project", "The pagination token belongs to an older project revision.");
            return cursor.Offset;
        }
        catch (McpFaultException)
        {
            throw;
        }
        catch
        {
            throw Fault("invalid_reference", "The pagination token is invalid.");
        }
    }

    private static string NormalizeKind(string kind) => kind.ToLowerInvariant() switch
    {
        "tempo" => "tempos",
        "time_signature" => "time_signatures",
        "lyrics" or "phonemes" => "notes",
        _ => kind.ToLowerInvariant(),
    };

    private static int IndexOf<T>(IReadOnlyList<T> items, T? value) where T : class
    {
        if (value == null)
            return -1;
        for (int i = 0; i < items.Count; i++)
            if (items[i].Equals(value))
                return i;
        return -1;
    }
}
