using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using VOCALOIDPatcher.Formats.LibreSvip.Framework;
using VOCALOIDPatcher.Mcp.Core;
using VOCALOIDPatcher.Mcp.Domains.NativeSemantics;
using VOCALOIDPatcher.Mcp.Domains.ExtensionParameters;
using VOCALOIDPatcher.McpBridge;
using VOCALOIDPatcher.Mcp.Domains.AudioParts;
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
        MainViewModel? main = ApplicationMainViewModel();

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
                start_mode = main?.StartMode == StartModeEnum.BeginLoop ? "begin_loop" : "song_position",
                playback_rate = 1.0,
                playback_rate_editable = false,
            },
            rendering = new
            {
                is_rendering = !vsm.IsFinishedRendering,
                mixdown_mode = App.AudioPlayer?.MixdownMode.ToString(),
            },
            active = new
            {
                track = activeTrack < 0 ? null : Ref(projectId, revision, "track", vsm.Tracks[activeTrack], activeTrack),
                part = activePart < 0 ? null : Ref(projectId, revision, "part", vsm.Tracks[activeTrack].Parts[activePart], activeTrack, activePart),
            },
            access = McpAccessController.GetStatus(),
            capabilities = Capabilities(),
            capability_status = CapabilityStatuses(),
            latest_event_id = McpEventHub.LatestEventId,
            view = ViewState(vsm),
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

    private static IReadOnlyList<CapabilityStatus> CapabilityStatuses()
    {
        Version? version = typeof(App).Assembly.GetName().Version;
        return McpContractCatalog.BaselineCapabilities.Concat(McpDomainRegistry.Capabilities).Concat(NativeSemanticJobs.Capabilities()).Select(capability =>
        {
            bool versionSupported = version == null || version >= new Version(6, 13);
            return capability with
            {
                Implemented = capability.Implemented && versionSupported,
                Availability = versionSupported ? capability.Availability : "unsupported",
                UnavailableReason = versionSupported ? capability.UnavailableReason : "The installed editor is older than 6.13.0.",
            };
        }).Concat(ExtensionParameterRegistry.Capabilities()).Concat(McpContractCatalog.StageSevenCapabilities).ToArray();
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
            capability_status = CapabilityStatuses(),
            operations = McpContractCatalog.Operations.Concat(McpDomainRegistry.Operations).ToArray(),
            domains = McpContractCatalog.Domains.Concat(McpDomainRegistry.Contracts).ToArray(),
            extension_parameters = ExtensionParameterRegistry.Schema,
            error_codes = McpErrorCodes.Catalog,
            native_semantic_jobs = NativeSemanticJobCatalog.Jobs,
            query = new
            {
                kinds = new[] { "summary", "tracks", "parts", "audio_parts", "notes", "tempos", "time_signatures", "parameters", "extension_parameters", "selection" }.Concat(McpDomainRegistry.Contracts.SelectMany(item => item.QueryKinds)).Distinct().ToArray(),
                default_page_size = BridgeProtocol.DefaultPageSize,
                maximum_page_size = BridgeProtocol.MaxPageSize,
                maximum_scanned_items = QueryBudget.DefaultMaxScannedItems,
                maximum_response_bytes = QueryBudget.DefaultMaxResponseBytes,
                dispatcher_budget_ms = QueryBudget.DefaultMaxDispatcherMilliseconds,
                parameter_modes = new[] { "raw", "summary", "buckets" },
            },
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
        if (Long(arguments, "changed_since_revision") is { } changedSince && kind is not "summary" and not "selection")
        {
            (string currentProjectId, long currentRevision) = McpRevisionTracker.Current();
            if (changedSince >= currentRevision)
                return new
                {
                    project = new ProjectContext(McpBridgeService.InstanceId ?? string.Empty, currentProjectId, currentRevision),
                    items = Array.Empty<object>(),
                    total = 0,
                    next_page_token = (string?)null,
                    change_filter = "no_changes",
                };
        }
        string normalizedKind = kind.ToLowerInvariant();
        (_, WIVSMSequence registeredSequence) = Context();
        (string registeredProjectId, long registeredRevision) = McpRevisionTracker.Current();
        if (normalizedKind == "audio_parts")
            return QueryAudioParts(offset, pageSize);
        if (McpDomainRegistry.TryQuery(normalizedKind, registeredSequence, registeredProjectId, registeredRevision, arguments, out object? registeredResult))
            return registeredResult!;
        return normalizedKind switch
        {
            "summary" => QuerySummary(),
            "tracks" => QueryTracks(offset, pageSize),
            "parts" => QueryParts(offset, pageSize),
            "notes" or "lyrics" or "phonemes" => QueryNotes(offset, pageSize, Element(arguments, "filter"), Element(arguments, "projection"), QueryBudgetFrom(arguments)),
            "tempo" or "tempos" => QueryTempos(offset, pageSize),
            "time_signature" or "time_signatures" => QueryTimeSignatures(offset, pageSize),
            "parameters" => QueryParameters(offset, pageSize, Element(arguments, "filter"), arguments, QueryBudgetFrom(arguments)),
            "extension_parameters" => QueryExtensionParameters(arguments),
            "selection" => QuerySelection(),
            _ => throw Fault("invalid_request", $"Unsupported project query kind '{kind}'."),
        };
    }

    private static object QueryExtensionParameters(JsonElement arguments)
    {
        (_, WIVSMSequence vsm) = Context();
        (string projectId, long revision) = McpRevisionTracker.Current();
        return ExtensionParameterRegistry.Query(vsm, projectId, revision, arguments);
    }

    private static QueryBudget QueryBudgetFrom(JsonElement arguments)
        => new(
            Int(arguments, "max_scanned_items", QueryBudget.DefaultMaxScannedItems),
            Int(arguments, "max_response_bytes", QueryBudget.DefaultMaxResponseBytes),
            Int(arguments, "dispatcher_budget_ms", QueryBudget.DefaultMaxDispatcherMilliseconds));

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
            reference = Ref(projectId, revision, "track", track, index),
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
                    reference = Ref(projectId, revision, "part", part, trackIndex, partIndex),
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

    private static object QueryAudioParts(int offset, int pageSize)
    {
        (_, WIVSMSequence vsm) = Context();
        (string projectId, long revision) = McpRevisionTracker.Current();
        return Page(AudioPartDomain.Query(vsm, projectId, revision), "audio_parts", offset, pageSize, projectId, revision);
    }

    private static object QueryNotes(int offset, int pageSize, JsonElement? filter, JsonElement? projection, QueryBudget budget)
    {
        (_, WIVSMSequence vsm) = Context();
        (string projectId, long revision) = McpRevisionTracker.Current();
        int? onlyTrack = filter is { } f ? Long(f, "track_index") is { } t ? checked((int)t) : null : null;
        int? onlyPart = filter is { } f2 ? Long(f2, "part_index") is { } p ? checked((int)p) : null : null;
        long? absoluteBegin = filter is { } f3 ? Long(f3, "absolute_tick_begin") : null;
        long? absoluteEnd = filter is { } f4 ? Long(f4, "absolute_tick_end") : null;
        long? relativeBegin = filter is { } f5 ? Long(f5, "part_relative_tick_begin") : null;
        long? relativeEnd = filter is { } f6 ? Long(f6, "part_relative_tick_end") : null;
        int? pitchMin = filter is { } f7 ? Long(f7, "pitch_min") is { } pitchMinValue ? checked((int)pitchMinValue) : null : null;
        int? pitchMax = filter is { } f8 ? Long(f8, "pitch_max") is { } pitchMaxValue ? checked((int)pitchMaxValue) : null : null;
        int? language = filter is { } f9 ? Long(f9, "language_id") is { } languageValue ? checked((int)languageValue) : null : null;
        bool? selected = filter is { } f10 && Element(f10, "selected") != null ? Bool(f10, "selected") : null;
        string? voicebank = filter is { } f11 ? String(f11, "voicebank_id") : null;
        string? search = filter is { } f12 ? String(f12, "text") : null;
        HashSet<string>? fields = projection is { ValueKind: JsonValueKind.Array }
            ? projection.Value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var items = new List<object>(pageSize);
        int matched = 0;
        int scanOrdinal = 0;
        bool truncated = false;
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
                string partVoicebank = part.IsAi ? part.AiVoiceBankID : part.VoiceBankID;
                if (voicebank != null && !string.Equals(voicebank, partVoicebank, StringComparison.OrdinalIgnoreCase))
                    continue;
                IReadOnlyList<WIVSMNote> notes = part.Notes;
                for (int noteIndex = 0; noteIndex < notes.Count; noteIndex++)
                {
                    if (items.Count >= pageSize)
                    {
                        truncated = true;
                        goto Complete;
                    }
                    if (scanOrdinal++ < offset)
                        continue;
                    if (!budget.TryScan())
                    {
                        truncated = true;
                        goto Complete;
                    }
                    WIVSMNote note = notes[noteIndex];
                    if (absoluteBegin != null && note.AbsPosTick.Value < absoluteBegin
                        || absoluteEnd != null && note.AbsPosTick.Value >= absoluteEnd
                        || relativeBegin != null && note.RelPosTick.Value < relativeBegin
                        || relativeEnd != null && note.RelPosTick.Value >= relativeEnd
                        || pitchMin != null && note.NoteNumber < pitchMin
                        || pitchMax != null && note.NoteNumber > pitchMax
                        || language != null && note.LangID != language
                        || selected != null && note.IsSelected != selected
                        || search != null && !note.Lyric.Contains(search, StringComparison.OrdinalIgnoreCase) && !note.Phonemes.Contains(search, StringComparison.OrdinalIgnoreCase))
                        continue;
                    matched++;
                    var item = new Dictionary<string, object?>
                    {
                        ["reference"] = Ref(projectId, revision, "note", note, trackIndex, partIndex, noteIndex),
                    };
                    void Add(string name, object? value) { if (fields == null || fields.Contains(name)) item[name] = value; }
                    if (fields == null || fields.Contains("position"))
                        item["position"] = Position(vsm, note.AbsPosTick.Value, note.RelPosTick.Value);
                    Add("duration_tick", note.DurationTick.Value);
                    Add("note_number", note.NoteNumber);
                    Add("lyric", note.Lyric);
                    Add("phonemes", note.Phonemes);
                    Add("language_id", note.LangID);
                    Add("selected", note.IsSelected);
                    Add("is_ai", note.IsAi);
                    Add("voicebank_id", partVoicebank);
                    if (fields == null || fields.Contains("expression"))
                    {
                        VSMNoteExpression expression = note.GetNoteExpression();
                        item["expression"] = new
                        {
                            accent = expression.Accent,
                            decay = expression.Decay,
                            bend_depth = expression.BendDepth,
                            bend_length = expression.BendLength,
                            opening = expression.Opening,
                            rise_port = expression.RisePort,
                            fall_port = expression.FallPort,
                        };
                    }
                    if (fields == null || fields.Contains("ai_expression"))
                    {
                        VSMAiNoteExpression aiExpression = note.GetAiNoteExpression();
                        item["ai_expression"] = new
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
                        };
                    }
                    if (fields == null || fields.Contains("vibrato"))
                        item["vibrato"] = new
                        {
                            type = note.VibratoType.ToString(),
                            duration_tick = note.VibratoDurationTick.Value,
                            enabled = note.IsAiVibratoEnabled,
                            depth = note.VibratoDepth,
                            rate = note.VibratoRate,
                        };
                    if (fields == null || fields.Contains("direct_pitch"))
                        item["direct_pitch"] = note.DirectPitches.Select(point => new
                        {
                            note_relative_tick = point.Tick,
                            value = point.Value,
                        }).ToArray();
                    items.Add(item);
                }
            }
        }
    Complete:
        int responseBytes = JsonSerializer.SerializeToUtf8Bytes(items, BridgeProtocol.JsonOptions).Length;
        if (responseBytes > budget.MaxResponseBytes)
            throw Fault(McpErrorCodes.QueryTooLarge, "The projected note page exceeds the response byte budget; reduce page_size or projection.", true,
                new { max_response_bytes = budget.MaxResponseBytes, page_size = items.Count, projection_required = true });
        int next = scanOrdinal;
        return new
        {
            project = new ProjectContext(McpBridgeService.InstanceId ?? string.Empty, projectId, revision),
            items,
            total = truncated ? (int?)null : matched,
            scanned_items = budget.ScannedItems,
            response_bytes = responseBytes,
            dispatcher_ms = budget.ElapsedMilliseconds,
            next_page_token = truncated ? EncodeCursor(new PageCursor(projectId, revision, "notes", next)) : null,
        };
    }

    private static object QueryTempos(int offset, int pageSize)
    {
        (_, WIVSMSequence vsm) = Context();
        (string projectId, long revision) = McpRevisionTracker.Current();
        object[] all = vsm.Tempos.Select((tempo, index) => (object)new
        {
            reference = Ref(projectId, revision, "tempo", tempo, itemIndex: index),
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
            reference = Ref(projectId, revision, "time_signature", timeSig, itemIndex: index),
            bar = timeSig.PosBar + 1,
            numerator = timeSig.Numer,
            denominator = timeSig.Denom,
            absolute_tick = vsm.GetTickFromBar(timeSig.PosBar).Value,
        }).ToArray();
        return Page(all, "time_signatures", offset, pageSize, projectId, revision);
    }

    private static object QueryParameters(int offset, int pageSize, JsonElement? filter, JsonElement arguments, QueryBudget budget)
    {
        (_, WIVSMSequence vsm) = Context();
        (string projectId, long revision) = McpRevisionTracker.Current();
        int? onlyTrack = filter is { } f ? Long(f, "track_index") is { } t ? checked((int)t) : null : null;
        int? onlyPart = filter is { } f2 ? Long(f2, "part_index") is { } p ? checked((int)p) : null : null;
        string? requestedType = filter is { } f3 ? String(f3, "parameter_type") : null;
        long? tickBegin = filter is { } f4 ? Long(f4, "absolute_tick_begin") : null;
        long? tickEnd = filter is { } f5 ? Long(f5, "absolute_tick_end") : null;
        double? valueMin = filter is { } f6 && Element(f6, "value_min") != null ? Double(f6, "value_min") : null;
        double? valueMax = filter is { } f7 && Element(f7, "value_max") != null ? Double(f7, "value_max") : null;
        string mode = (String(arguments, "parameter_mode") ?? "raw").ToLowerInvariant();
        if (mode is not "raw" and not "summary" and not "buckets")
            throw Fault(McpErrorCodes.InvalidRequest, "parameter_mode must be raw, summary, or buckets.");
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

        var all = new List<object>(mode == "raw" ? pageSize : Math.Min(budget.MaxScannedItems, 4096));
        int matched = 0;
        int scanOrdinal = 0;
        bool truncated = false;
        bool Consider(object candidate)
        {
            if (mode == "raw" && all.Count >= pageSize)
            {
                truncated = true;
                return false;
            }
            if (mode == "raw" && scanOrdinal++ < offset)
                return true;
            if (!budget.TryScan())
            {
                truncated = true;
                return false;
            }
            JsonElement json = JsonSerializer.SerializeToElement(candidate, BridgeProtocol.JsonOptions);
            long tick = json.TryGetProperty("absolute_tick", out JsonElement tickElement) ? tickElement.GetInt64() : 0;
            double value = json.TryGetProperty("value", out JsonElement valueElement) ? valueElement.GetDouble() : 0;
            if (tickBegin != null && tick < tickBegin || tickEnd != null && tick >= tickEnd
                || valueMin != null && value < valueMin || valueMax != null && value > valueMax)
                return true;
            matched++;
            all.Add(candidate);
            return true;
        }
        for (int trackIndex = 0; trackIndex < vsm.Tracks.Count; trackIndex++)
        {
            if (onlyTrack != null && onlyTrack != trackIndex)
                continue;
            WIVSMTrack track = vsm.Tracks[trackIndex];
            if (onlyPart == null && includeTrackVolume)
                for (int index = 0; index < track.Volumes.Count; index++)
                {
                    WIVSMTrackVolume point = track.Volumes[index];
                    if (!Consider(new
                    {
                        reference = Ref(projectId, revision, "track_volume", point, trackIndex, itemIndex: index),
                        parameter_type = "track_volume",
                        absolute_tick = point.RelPosTick.Value,
                        value = point.Value,
                    })) goto ParametersComplete;
                }
            if (onlyPart == null && includeTrackPan)
                for (int index = 0; index < track.Panpots.Count; index++)
                {
                    WIVSMPanpot point = track.Panpots[index];
                    if (!Consider(new
                    {
                        reference = Ref(projectId, revision, "track_pan", point, trackIndex, itemIndex: index),
                        parameter_type = "track_pan",
                        absolute_tick = point.RelPosTick.Value,
                        value = point.Value,
                    })) goto ParametersComplete;
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
                        if (!Consider(new
                        {
                            reference = Ref(projectId, revision, "parameter", point, trackIndex, partIndex, checked((int)itemIndex)),
                            parameter_type = type.ToString(),
                            part_relative_tick = point.RelPosTick.Value,
                            absolute_tick = part.AbsPosTick.Value + point.RelPosTick.Value,
                            value = point.Value,
                        })) goto ParametersComplete;
                    }
                }
                if (includeDirectPitch)
                {
                    IReadOnlyList<WIVSMNote> notes = part.Notes;
                    for (int noteIndex = 0; noteIndex < notes.Count; noteIndex++)
                    {
                        WIVSMNote note = notes[noteIndex];
                        int pointIndex = 0;
                        foreach (VSMDirectPitchData point in note.DirectPitches)
                        {
                            if (!Consider(new
                            {
                                reference = Ref(projectId, revision, "direct_pitch", note, trackIndex, partIndex, noteIndex),
                                parameter_type = "direct_pitch",
                                note_index = noteIndex,
                                point_index = pointIndex++,
                                note_relative_tick = point.Tick,
                                absolute_tick = note.AbsPosTick.Value + point.Tick,
                                value = point.Value,
                            })) goto ParametersComplete;
                        }
                    }
                }
            }
        }
        if (onlyTrack == null && onlyPart == null && includeMasterVolume)
            for (int index = 0; index < vsm.MasterVolumes.Count; index++)
            {
                WIVSMMasterVolume point = vsm.MasterVolumes[index];
                if (!Consider(new
                {
                    reference = Ref(projectId, revision, "master_volume", point, itemIndex: index),
                    parameter_type = "master_volume",
                    absolute_tick = point.RelPosTick.Value,
                    value = point.Value,
                })) goto ParametersComplete;
            }
    ParametersComplete:
        if (string.Equals(mode, "summary", StringComparison.OrdinalIgnoreCase))
        {
            var values = all.Select(item => JsonSerializer.SerializeToElement(item, BridgeProtocol.JsonOptions))
                .Where(item => item.TryGetProperty("value", out _)).Select(item => item.GetProperty("value").GetDouble()).ToArray();
            return new
            {
                project = new ProjectContext(McpBridgeService.InstanceId ?? string.Empty, projectId, revision),
                count = values.Length,
                minimum = values.Length == 0 ? (double?)null : values.Min(),
                maximum = values.Length == 0 ? (double?)null : values.Max(),
                average = values.Length == 0 ? (double?)null : values.Average(),
                scanned_items = budget.ScannedItems,
                complete = !truncated,
            };
        }
        if (string.Equals(mode, "buckets", StringComparison.OrdinalIgnoreCase))
        {
            int bucketTicks = Math.Clamp(Int(arguments, "bucket_ticks", 480), 1, int.MaxValue);
            var buckets = all.Select(item => JsonSerializer.SerializeToElement(item, BridgeProtocol.JsonOptions))
                .Where(item => item.TryGetProperty("absolute_tick", out _) && item.TryGetProperty("value", out _))
                .GroupBy(item => item.GetProperty("absolute_tick").GetInt64() / bucketTicks)
                .Select(group => new
                {
                    absolute_tick_begin = group.Key * bucketTicks,
                    absolute_tick_end = (group.Key + 1) * bucketTicks,
                    count = group.Count(),
                    minimum = group.Min(item => item.GetProperty("value").GetDouble()),
                    maximum = group.Max(item => item.GetProperty("value").GetDouble()),
                    average = group.Average(item => item.GetProperty("value").GetDouble()),
                }).Cast<object>().ToArray();
            return Page(buckets, "parameters", offset, pageSize, projectId, revision);
        }
        return new
        {
            project = new ProjectContext(McpBridgeService.InstanceId ?? string.Empty, projectId, revision),
            items = all,
            total = truncated ? (int?)null : matched,
            scanned_items = budget.ScannedItems,
            dispatcher_ms = budget.ElapsedMilliseconds,
            next_page_token = truncated ? EncodeCursor(new PageCursor(projectId, revision, "parameters", scanOrdinal)) : null,
        };
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
                    parts.Add(Ref(projectId, revision, "part", part, trackIndex, partIndex));
                if (part is not WIVSMMidiPart midi)
                    continue;
                for (int noteIndex = 0; noteIndex < midi.Notes.Count; noteIndex++)
                    if (midi.Notes[noteIndex].IsSelected)
                        notes.Add(Ref(projectId, revision, "note", midi.Notes[noteIndex], trackIndex, partIndex, noteIndex));
            }
        }

        int activeTrack = IndexOf(vsm.Tracks, sequence.ActiveTrack);
        int activePart = activeTrack < 0 ? -1 : IndexOf(vsm.Tracks[activeTrack].Parts, sequence.ActivePart);
        return new
        {
            active_track = activeTrack < 0 ? null : Ref(projectId, revision, "track", vsm.Tracks[activeTrack], activeTrack),
            active_part = activePart < 0 ? null : Ref(projectId, revision, "part", vsm.Tracks[activeTrack].Parts[activePart], activeTrack, activePart),
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
