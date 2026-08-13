using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VOCALOIDPatcher.Formats.LibreSvip;
using VOCALOIDPatcher.Formats.LibreSvip.Framework;
using VOCALOIDPatcher.Jobs;
using VOCALOIDPatcher.Mcp.Core;
using VOCALOIDPatcher.Mcp.Domains.NativeSemantics;
using VOCALOIDPatcher.McpBridge;
using VOCALOIDPatcher.Translation;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VAE;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Mcp;

internal static partial class VocaloidMcpFacade
{
    private static object SelectView(BridgeClientInfo client, JsonElement arguments)
    {
        ValidateProject(arguments);
        JsonElement request = Element(arguments, "request")
                              ?? throw Fault("invalid_request", "request is required.");
        (Yamaha.VOCALOID.Sequence sequence, WIVSMSequence vsm) = Context();
        string mode = (String(request, "mode") ?? (Bool(request, "clear", true) ? "replace" : "add")).ToLowerInvariant();
        if (mode is not "replace" and not "add" and not "toggle")
            throw Fault("invalid_request", "Selection mode must be replace, add, or toggle.");

        if (mode == "replace")
        {
            sequence.SelectAllNotesInActivePart(false);
            sequence.SelectAllParts(false);
            sequence.SelectAllTracks(false);
        }

        int? trackIndex = Long(request, "track_index") is { } track ? checked((int)track) : null;
        int? partIndex = Long(request, "part_index") is { } part ? checked((int)part) : null;
        if (trackIndex != null && partIndex != null)
        {
            WIVSMPart selectedPart = Part(vsm, trackIndex.Value, partIndex.Value);
            sequence.SelectPart(selectedPart, mode == "toggle" ? !selectedPart.IsSelected : true);
            bool alreadyActive = selectedPart.Equals(sequence.ActivePart);
            if (SelectionActivation.ShouldActivate(alreadyActive))
                sequence.SetActivePartAndTrack(selectedPart);
            if (!SelectionActivation.Succeeded(selectedPart.Equals(sequence.ActivePart)))
                throw Fault("operation_failed", "VOCALOID could not activate the requested part.");
        }
        else if (trackIndex != null)
        {
            WIVSMTrack selectedTrack = Track(vsm, trackIndex.Value);
            sequence.SelectTrack(selectedTrack, mode == "toggle" ? !selectedTrack.IsSelected : true);
        }

        if (Element(request, "note_indices") is { ValueKind: JsonValueKind.Array } noteIndices)
        {
            if (trackIndex == null || partIndex == null)
                throw Fault("invalid_request", "track_index and part_index are required when selecting notes.");
            WIVSMMidiPart midiPart = MidiPart(vsm, trackIndex.Value, partIndex.Value);
            foreach (JsonElement value in noteIndices.EnumerateArray())
            {
                if (!value.TryGetInt32(out int noteIndex) || noteIndex < 0 || noteIndex >= midiPart.Notes.Count)
                    throw Fault("invalid_reference", "A note index is out of range.");
                WIVSMNote note = midiPart.Notes[noteIndex];
                sequence.SelectNote(note, mode == "toggle" ? !note.IsSelected : true);
            }
        }

        if (Element(request, "note_range") is { ValueKind: JsonValueKind.Object } range)
        {
            if (trackIndex == null || partIndex == null)
                throw Fault("invalid_request", "track_index and part_index are required for note_range.");
            WIVSMMidiPart midiPart = MidiPart(vsm, trackIndex.Value, partIndex.Value);
            long begin = Math.Max(0, Long(range, "absolute_tick_begin") ?? 0);
            long end = Long(range, "absolute_tick_end") ?? long.MaxValue;
            int pitchMin = Int(range, "pitch_min", 0);
            int pitchMax = Int(range, "pitch_max", 127);
            if (end <= begin || pitchMin < 0 || pitchMax > 127 || pitchMax < pitchMin)
                throw Fault("invalid_request", "note_range bounds are invalid.");
            foreach (WIVSMNote note in midiPart.Notes.Where(note => note.AbsPosTick.Value < end && note.AbsEndTick.Value > begin && note.NoteNumber >= pitchMin && note.NoteNumber <= pitchMax))
                sequence.SelectNote(note, mode == "toggle" ? !note.IsSelected : true);
        }

        if (Element(request, "absolute_tick") != null || Element(request, "position") != null)
            ApplicationMainViewModel()?.SetCurrentPosition(new VSMAbsTick(Math.Max(0, ResolveAbsoluteTick(vsm, request))));

        ApplyViewRequest(vsm, request);
        (string selectionProjectId, long selectionRevision) = McpRevisionTracker.Current();
        McpEventHub.Publish("selection_changed", selectionProjectId, selectionRevision);
        McpEventHub.Publish("active_part_changed", selectionProjectId, selectionRevision);
        McpEventHub.Publish("view_changed", selectionProjectId, selectionRevision,
            new { revision_unchanged = true });
        return new { selection = QuerySelection(), view = ViewState(vsm), revision_unchanged = selectionRevision };
    }

    private static void ApplyViewRequest(WIVSMSequence vsm, JsonElement request)
    {
        MusicalEditorViewModel? editor = ApplicationMainViewModel()?.MusicalEditorVM;
        if (editor == null) return;
        MainWindow? mainWindow = Application.Current?.MainWindow as MainWindow;
        string? tool = String(request, "edit_tool");
        if (tool != null)
        {
            EditModeME mode = tool switch
            {
                "arrow" => EditModeME.Arrow, "pencil" => EditModeME.Pencil, "line" => EditModeME.Line,
                "scissors" => EditModeME.Scissors, "pitch" => EditModeME.Pitch,
                "vibrato" => EditModeME.Vibrato, "expression" => EditModeME.Amplitude,
                "timing" => EditModeME.PhonemeTiming,
                _ => throw Fault("unsupported", $"Edit tool '{tool}' is not available through the confirmed V6 6.13 semantic mode API."),
            };
            editor.EditorMode.ChangeMode(mode);
        }
        if (Long(request, "viewport_absolute_tick") is { } tick && editor.PianorollViewer != null)
            editor.PianorollViewer.ScrollToHorizontalOffset(Math.Max(0, tick) * editor.WidthPerTick);
        if (Double(request, "horizontal_zoom", double.NaN) is { } horizontal && !double.IsNaN(horizontal))
            editor.SliderHorizontalZoom = Math.Clamp(horizontal, 0, 1);
        if (Double(request, "vertical_zoom", double.NaN) is { } vertical && !double.IsNaN(vertical))
            editor.SliderVerticalZoom = Math.Clamp(vertical, 0, 1);
        if (String(request, "parameter_type") is { } parameterType)
        {
            if (!Enum.TryParse(parameterType, true, out ControlParameterTypeEnum parsedType))
                throw Fault("unsupported", $"Parameter panel type '{parameterType}' is not exposed by V6 6.13.");
            editor.ControlParameterType = parsedType;
        }
        if (Element(request, "parameter_panel_visible") != null)
            editor.ControlParameterAreaHeight = Bool(request, "parameter_panel_visible")
                ? MusicalEditorViewModel.DefaultControlParameterAreaHeight : MusicalEditorViewModel.CloseControlParameterAreaHeight;
        if (String(request, "lower_zone") is { } lowerZone)
        {
            if (mainWindow == null)
                throw Fault("v6_unavailable", "The main editor window is unavailable.", true);
            switch (lowerZone.ToLowerInvariant())
            {
                case "hidden":
                    mainWindow.HideLowerZone(false);
                    break;
                case "musical":
                    mainWindow.ShowLowerZone(LowerZoneKindEnum.Musical, false);
                    break;
                case "wave":
                    mainWindow.ShowLowerZone(LowerZoneKindEnum.Wave, false);
                    break;
                case "mixer":
                    mainWindow.ShowLowerZone(LowerZoneKindEnum.Mixer, false);
                    break;
                case "empty":
                    mainWindow.ShowLowerZone(LowerZoneKindEnum.Empty, false);
                    break;
                default:
                    throw Fault("invalid_request", "lower_zone must be hidden, musical, wave, mixer, or empty.");
            }
        }
        if (String(request, "right_zone") is { } rightZone)
        {
            if (mainWindow == null)
                throw Fault("v6_unavailable", "The main editor window is unavailable.", true);
            switch (rightZone.ToLowerInvariant())
            {
                case "hidden":
                    mainWindow.HideRightZone();
                    break;
                case "inspector":
                    mainWindow.ShowInspector();
                    break;
                case "media_browser":
                    mainWindow.ShowMediaBrowser();
                    break;
                default:
                    throw Fault("invalid_request", "right_zone must be hidden, inspector, or media_browser.");
            }
        }
    }

    private static object ViewState(WIVSMSequence vsm)
    {
        MusicalEditorViewModel? editor = ApplicationMainViewModel()?.MusicalEditorVM;
        MainWindow? mainWindow = Application.Current?.MainWindow as MainWindow;
        double offset = editor?.PianorollViewer?.HorizontalOffset ?? 0;
        double width = editor?.PianorollViewer?.ViewportWidth ?? 0;
        double perTick = editor?.WidthPerTick ?? 0;
        return new
        {
            available = editor != null,
            edit_tool = editor?.EditorMode.Mode.ToString().ToLowerInvariant(),
            parameter_type = editor?.ControlParameterType.ToString(),
            parameter_panel_visible = editor != null && editor.ControlParameterAreaHeight.Value > MusicalEditorViewModel.CloseControlParameterAreaHeight.Value,
            lower_zone = mainWindow == null || mainWindow.IsLowerZoneHidden ? "hidden" : mainWindow.LowerZoneKind.ToString().ToLowerInvariant(),
            lower_zone_visible = mainWindow != null && !mainWindow.IsLowerZoneHidden,
            mixer_visible = mainWindow?.IsMixerBrowserShown ?? false,
            right_zone = mainWindow == null || mainWindow.IsRightZoneHidden
                ? "hidden"
                : mainWindow.IsMediaBrowserShown ? "media_browser" : "inspector",
            inspector_visible = mainWindow?.IsInspectorShown ?? false,
            media_browser_visible = mainWindow?.IsMediaBrowserShown ?? false,
            horizontal_zoom = editor?.SliderHorizontalZoom,
            vertical_zoom = editor?.SliderVerticalZoom,
            viewport = new { absolute_tick_begin = perTick > 0 ? (long)(offset / perTick) : 0, absolute_tick_end = perTick > 0 ? (long)((offset + width) / perTick) : 0 },
        };
    }

    private static object Transport(JsonElement arguments)
    {
        (_, WIVSMSequence vsm) = Context();
        string action = (String(arguments, "action") ?? throw Fault("invalid_request", "action is required.")).ToLowerInvariant();
        MainViewModel? main = ApplicationMainViewModel();
        if (main == null)
            throw Fault("v6_unavailable", "The main editor view is unavailable.", true);

        switch (action.ToLowerInvariant())
        {
            case "play":
                main.StartPlay(null);
                break;
            case "resume":
                if (App.AudioPlayer?.IsPlaying != true) main.StartPlay(null);
                break;
            case "pause":
                if (App.AudioPlayer?.IsPlaying == true) main.StopPlay(null, false);
                break;
            case "stop":
                main.StopPlay(null, false);
                break;
            case "seek":
                main.SetCurrentPosition(new VSMAbsTick(Math.Max(0, ResolveAbsoluteTick(vsm, arguments))));
                break;
            case "grid_previous":
            case "grid_next":
            {
                int grid = Math.Max(1, main.MusicalEditorVM?.TickPerQuantize ?? Yamaha.VOCALOID.Design.Sequence.resolution);
                long current = vsm.CurrentPosTick.Value;
                long target = action == "grid_next" ? (current / grid + 1) * grid : Math.Max(0, (current - 1) / grid * grid);
                main.SetCurrentPosition(new VSMAbsTick(target));
                break;
            }
            case "set_loop":
            case "set_markers":
            {
                int begin = checked((int)(Long(arguments, "loop_begin_tick") ?? vsm.LoopRange.Begin));
                int end = checked((int)(Long(arguments, "loop_end_tick") ?? vsm.LoopRange.End));
                if (begin < 0 || end <= begin)
                    throw Fault("invalid_request", "The loop range is invalid.");
                if (!vsm.SetLoopRange(new VSMTickRange(begin, end)))
                    throw Fault("operation_failed", "VOCALOID rejected the loop range.");
                if (Element(arguments, "loop_enabled") != null)
                    vsm.IsLoopOn = Bool(arguments, "loop_enabled");
                break;
            }
            case "set_loop_enabled":
                vsm.IsLoopOn = Bool(arguments, "loop_enabled");
                break;
            case "set_start_mode":
                main.StartMode = (String(arguments, "start_mode") ?? throw Fault("invalid_request", "start_mode is required.")).ToLowerInvariant() switch
                {
                    "song_position" => StartModeEnum.SongPosition,
                    "begin_loop" => StartModeEnum.BeginLoop,
                    _ => throw Fault("invalid_request", "start_mode must be song_position or begin_loop."),
                };
                break;
            case "status":
                break;
            default:
                throw Fault("invalid_request", "Transport action must be play, pause, resume, stop, seek, grid_previous, grid_next, set_markers, set_loop, set_loop_enabled, set_start_mode, or status.");
        }

        VSMTickRange loop = vsm.LoopRange;
        object result = new
        {
            is_playing = App.AudioPlayer?.IsPlaying ?? false,
            position = Position(vsm, vsm.CurrentPosTick.Value),
            loop_enabled = vsm.IsLoopOn,
            loop_begin_tick = loop.Begin,
            loop_end_tick = loop.End,
            start_mode = main.StartMode == StartModeEnum.BeginLoop ? "begin_loop" : "song_position",
            playback_rate = 1.0,
            playback_rate_editable = false,
        };
        (string transportProjectId, long transportRevision) = McpRevisionTracker.Current();
        if (action != "status")
            McpEventHub.Publish("transport_changed", transportProjectId, transportRevision, new { action, is_playing = App.AudioPlayer?.IsPlaying ?? false });
        return result;
    }

    private static object History(BridgeClientInfo client, JsonElement arguments)
    {
        (_, WIVSMSequence vsm) = Context();
        string action = String(arguments, "action") ?? "status";
        if (action.Equals("status", StringComparison.OrdinalIgnoreCase))
            return HistoryStatus(vsm);

        ValidateProject(arguments);
        if (!McpAccessController.AuthorizeWrite(client, action, false, out BridgeError? error))
            throw Fault(error!);
        switch (action.ToLowerInvariant())
        {
            case "undo":
                if (!vsm.CanUndo())
                    throw Fault("operation_failed", "There is no operation to undo.");
                vsm.Undo();
                break;
            case "redo":
                if (!vsm.CanRedo())
                    throw Fault("operation_failed", "There is no operation to redo.");
                vsm.Redo();
                break;
            default:
                throw Fault("invalid_request", "History action must be status, undo, or redo.");
        }
        long revision = McpRevisionTracker.Current().Revision;
        RefreshEditor();
        return new { revision, history = HistoryStatus(vsm) };
    }

    private static object HistoryStatus(WIVSMSequence vsm)
        => new { can_undo = vsm.CanUndo(), can_redo = vsm.CanRedo() };

    private static object RunCreativeJob(BridgeClientInfo client, JsonElement arguments)
    {
        (_, long previousRevision) = ValidateProject(arguments);
        string kind = String(arguments, "kind") ?? throw Fault("invalid_request", "kind is required.");
        bool dryRun = Bool(arguments, "dry_run");
        JsonElement options = Element(arguments, "options") ?? JsonSerializer.SerializeToElement(new { });
        if (!dryRun && !McpAccessController.AuthorizeWrite(client, $"Run creative job: {kind}", false, out BridgeError? error))
            throw Fault(error!);

        object? impact = null;
        switch (kind.ToLowerInvariant())
        {
            case "lyric":
                if (string.IsNullOrEmpty(String(options, "lyric")))
                    throw Fault("invalid_request", "options.lyric is required.");
                if (!dryRun) JobTools.ApplyLyric(String(options, "lyric")!);
                break;
            case "quantize_length":
                if (Int(options, "grid_ticks", 0) < 1)
                    throw Fault("invalid_request", "options.grid_ticks must be positive.");
                if (!dryRun) JobTools.ApplyQuantizeLength(Int(options, "grid_ticks"), Double(options, "strength", 1.0));
                break;
            case "swing":
                if (!dryRun) JobTools.ApplySwing(Int(options, "subdivision", 8), Double(options, "ratio", 60.0));
                break;
            case "harmony":
            {
                var intervals = new List<JobTools.HarmonyInterval>();
                if (Element(options, "intervals") is not { ValueKind: JsonValueKind.Array } values)
                    throw Fault("invalid_request", "options.intervals is required.");
                foreach (JsonElement value in values.EnumerateArray())
                    if (value.ValueKind != JsonValueKind.String
                        || !Enum.TryParse(value.GetString(), true, out JobTools.HarmonyInterval interval))
                        throw Fault("invalid_request", "An unknown harmony interval was supplied.");
                    else
                        intervals.Add(interval);
                if (!dryRun) JobTools.ApplyHarmony(Int(options, "root_id"), intervals, Bool(options, "force_new_track"));
                break;
            }
            default:
                try
                {
                    impact = NativeSemanticJobs.PlanAndRun(kind.ToLowerInvariant(), options, ApplicationMainViewModel() ?? throw new InvalidOperationException("The V6 main view model is unavailable."), !dryRun);
                }
                catch (NotSupportedException exception)
                {
                    throw Fault(McpErrorCodes.Unsupported, exception.Message);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
                {
                    throw Fault(McpErrorCodes.InvalidRequest, exception.Message);
                }
                break;
        }

        if (!dryRun && impact != null)
            RefreshEditor();
        long revision = dryRun ? previousRevision : McpRevisionTracker.Current().Revision;
        return new
        {
            dry_run = dryRun,
            kind,
            valid = true,
            impact,
            project = new ProjectContext(McpBridgeService.InstanceId ?? string.Empty, McpRevisionTracker.Current().ProjectId, revision),
        };
    }

    private static object ProjectFile(BridgeClientInfo client, JsonElement arguments)
    {
        string action = (String(arguments, "action") ?? throw Fault("invalid_request", "action is required.")).ToLowerInvariant();
        string? path = String(arguments, "path");
        bool dryRun = Bool(arguments, "dry_run");
        JsonElement options = Element(arguments, "options") ?? JsonSerializer.SerializeToElement(new { });
        if (action == "recent")
            return RecentProjects();
        if (action is not "new" && action is not "open" || String(arguments, "project_id") != null)
            ValidateProject(arguments);

        string? fullPath = null;
        if (action is "open" or "save_as" or "export_midi" or "import_project" or "import_midi" or "import_tempo_time_signature")
        {
            if (path == null)
                throw Fault("invalid_request", "path is required.");
            if (!McpAccessController.TryResolvePath(path, out fullPath, out BridgeError? pathError))
                throw Fault(pathError!);
        }
        else if (action == "save" && path != null)
        {
            if (!McpAccessController.TryResolvePath(path, out fullPath, out BridgeError? pathError))
                throw Fault(pathError!);
        }
        else if (action == "save")
        {
            string? currentPath = App.Shared?.Document?.DocumentUri?.LocalPath;
            if (!string.IsNullOrWhiteSpace(currentPath))
            {
                if (!McpAccessController.TryResolvePath(currentPath, out fullPath, out BridgeError? pathError))
                    throw Fault(pathError!);
            }
        }

        string[] audioPaths = action == "import_audio" ? ResolveAudioImportPaths(path, options) : Array.Empty<string>();

        if (action is "open" or "import_project" or "import_midi" or "import_tempo_time_signature")
        {
            if (fullPath == null || !File.Exists(fullPath))
                throw Fault("invalid_reference", "The native input file does not exist.");
        }
        if (action == "import_project" && Path.GetExtension(fullPath!).ToLowerInvariant() is not (".vpr" or ".vsqx" or ".ppsf"))
            throw Fault("unsupported", "Native project import supports VPR, VSQX, and PPSF only.");
        if (action is "import_midi" or "import_tempo_time_signature"
            && Path.GetExtension(fullPath!).ToLowerInvariant() is not (".mid" or ".midi"))
            throw Fault("unsupported", "Native MIDI import requires a .mid or .midi file.");

        if (action == "revert")
        {
            string dirtyAction = (String(options, "dirty_action") ?? "cancel").ToLowerInvariant();
            if (dirtyAction is not "save" and not "discard" and not "cancel")
                throw Fault("invalid_request", "options.dirty_action must be save, discard, or cancel.");
            string? current = App.Shared?.Document?.DocumentUri?.LocalPath;
            if (string.IsNullOrWhiteSpace(current) || !File.Exists(current))
                throw Fault("invalid_reference", "The current project has no existing saved file to revert to.");
            if (dirtyAction == "cancel")
            {
                (string projectId, long revision) = McpRevisionTracker.Current();
                return new
                {
                    reverted = false,
                    outcome = "cancel",
                    project = new ProjectContext(McpBridgeService.InstanceId ?? string.Empty, projectId, revision),
                };
            }
        }

        bool dangerous = action is "new" or "open" or "revert" || fullPath != null && File.Exists(fullPath);
        if (!dryRun && !McpAccessController.AuthorizeWrite(client, $"Project file operation: {action}", dangerous, out BridgeError? error))
            throw Fault(error!);
        if (dryRun)
            return new { dry_run = true, valid = true, action, path = fullPath, audio_file_count = audioPaths.Length, confirmation_required = dangerous };

        JobInfo job = StartJob(action, client, async (cancellationToken, progress) =>
        {
            progress(0.1);
            object result = await OnUiAsync(() => ExecuteProjectFile(action, fullPath, audioPaths, options, cancellationToken));
            progress(1.0);
            return result;
        });
        return job;
    }

    private static object ExecuteProjectFile(string action, string? path, string[] audioPaths, JsonElement options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MainViewModel? main = ApplicationMainViewModel();
        Document? document = App.Shared?.Document;
        if (action is "new" or "open" && document == null)
        {
            McpInitialProjectSetup.SuppressNextAddTrackDialog();
            Application.Current?.Windows.OfType<HomeWindow>().FirstOrDefault()?.Close();
            main = ApplicationMainViewModel();
            document = App.Shared?.Document;
        }
        if (main == null || document == null)
            throw Fault("v6_unavailable", "The active VOCALOID document is unavailable.");
        if (App.AudioPlayer?.IsPlaying == true)
            main.StopPlay(null, false);

        switch (action)
        {
            case "new":
                ConfirmDirtyDocument(document, main);
                main.Close();
                App.Shared!.Document = new Document();
                if (!App.Shared.Document.New())
                    throw Fault("operation_failed", "VOCALOID could not create a new project.");
                McpInitialProjectSetup.SuppressNextAddTrackDialog();
                bool isNew = App.Shared.Document.IsNew;
                App.Shared.Document.IsNew = false;
                try
                {
                    main.Refresh();
                }
                finally
                {
                    App.Shared.Document.IsNew = isNew;
                }
                McpRevisionTracker.ProjectReplaced();
                return new { created = true };
            case "open":
                ConfirmDirtyDocument(document, main);
                main.Close();
                App.Shared!.Document = new Document();
                App.Shared.Document.Load(path!);
                if (!string.Equals(
                        Path.GetFullPath(App.Shared.Document.DocumentUri?.LocalPath ?? string.Empty),
                        Path.GetFullPath(path!),
                        StringComparison.OrdinalIgnoreCase))
                    throw Fault("operation_failed", "VOCALOID could not open the requested project.");
                main.Refresh();
                McpRevisionTracker.ProjectReplaced();
                return new { opened = true, path };
            case "save":
            case "save_as":
            {
                string destination = path ?? document.DocumentUri?.LocalPath
                    ?? throw Fault("invalid_request", "A path is required for an unsaved project.");
                string directory = Path.GetDirectoryName(destination)
                                   ?? throw Fault("invalid_request", "The destination directory is invalid.");
                string projectName = Path.GetFileNameWithoutExtension(destination);
                if (!document.Save(directory, projectName))
                    throw Fault("operation_failed", "VOCALOID could not save the project.");
                return new { saved = true, path = document.DocumentUri?.LocalPath };
            }
            case "export_midi":
                if (!document.SaveSMF(path!, VSMEncoding.Utf8))
                    throw Fault("operation_failed", "VOCALOID could not export MIDI.");
                return new { exported = true, path };
            case "revert":
                return ExecuteRevert(main, document, options);
            case "import_project":
            {
                string extension = Path.GetExtension(path!).ToLowerInvariant();
                if (extension is not ".vpr" and not ".vsqx" and not ".ppsf")
                    throw Fault("unsupported", "Native project import supports VPR, VSQX, and PPSF only.");
                long before = McpRevisionTracker.Current().Revision;
                main.ImportProjectFile(path!, Bool(options, "import_tempo_time_signature", true));
                return NativeImportResult("project", before, new { extension });
            }
            case "import_midi":
            {
                if (Path.GetExtension(path!).ToLowerInvariant() is not (".mid" or ".midi"))
                    throw Fault("unsupported", "Native MIDI import requires a .mid or .midi file.");
                long before = McpRevisionTracker.Current().Revision;
                var parameter = new MidiImportParam
                {
                    TrackType = string.Equals(String(options, "track_type"), "standard", StringComparison.OrdinalIgnoreCase)
                        ? MidiImportTrackType.VOCALOID : MidiImportTrackType.AI,
                    Track = null,
                    AbsTick = new VSMAbsTick(Math.Max(0, Long(options, "absolute_tick") ?? 0)),
                    NeedsImportTempoAndTimeSig = Bool(options, "import_tempo_time_signature", true),
                    CodePage = string.Equals(String(options, "encoding"), "utf8", StringComparison.OrdinalIgnoreCase) ? CodePage.Utf8 : CodePage.ShiftJis,
                };
                main.ImportMidiFile(path!, parameter);
                return NativeImportResult("midi", before, new { track_type = parameter.TrackType.ToString(), code_page = parameter.CodePage.ToString() });
            }
            case "import_tempo_time_signature":
            {
                if (Path.GetExtension(path!).ToLowerInvariant() is not (".mid" or ".midi"))
                    throw Fault("unsupported", "Tempo/time-signature import requires a native MIDI file.");
                long before = McpRevisionTracker.Current().Revision;
                main.ImportTempoAndTimeSig(path!);
                return NativeImportResult("tempo_time_signature", before, null);
            }
            case "import_audio":
            {
                long before = McpRevisionTracker.Current().Revision;
                long tick = Math.Max(0, Long(options, "absolute_tick") ?? Context().Vsm.CurrentPosTick.Value);
                main.SetCurrentPosition(new VSMAbsTick(tick));
                if (string.Equals(String(options, "placement"), "different_tracks", StringComparison.OrdinalIgnoreCase))
                    main.ImportAudioFilesOnDifferentTrack(audioPaths);
                else
                    main.ImportAudioFilesOnOneTrack(audioPaths);
                return NativeImportResult("audio", before, new { file_count = audioPaths.Length, absolute_tick = tick });
            }
            default:
                throw Fault("invalid_request", "Unknown project lifecycle action.");
        }
    }

    private static object ExecuteRevert(MainViewModel main, Document document, JsonElement options)
    {
        string path = document.DocumentUri?.LocalPath ?? throw Fault("invalid_request", "The current project has no saved file to revert to.");
        if (!File.Exists(path))
            throw Fault("invalid_reference", "The saved project file no longer exists.");
        string dirtyAction = (String(options, "dirty_action") ?? "cancel").ToLowerInvariant();
        if (dirtyAction is not "save" and not "discard" and not "cancel")
            throw Fault("invalid_request", "options.dirty_action must be save, discard, or cancel.");
        if (dirtyAction == "cancel")
            return new { reverted = false, outcome = "cancel" };
        if (document.Sequence?.VSMSequence?.IsDirty == true && dirtyAction == "save" && !main.Save())
            throw Fault("operation_failed", "The current project could not be saved before revert.");
        main.ClearTemporarySettings();
        main.Close();
        App.Shared!.Document = new Document();
        App.Shared.Document.Load(path);
        main.Refresh();
        McpRevisionTracker.ProjectReplaced();
        (string projectId, long revision) = McpRevisionTracker.Current();
        return new
        {
            reverted = true,
            outcome = dirtyAction == "save" ? "saved_then_reverted" : "discarded_then_reverted",
            project = new ProjectContext(McpBridgeService.InstanceId ?? string.Empty, projectId, revision),
        };
    }

    private static object NativeImportResult(string kind, long beforeRevision, object? details)
    {
        (string projectId, long revision) = McpRevisionTracker.Current();
        if (revision <= beforeRevision)
            throw Fault("operation_failed", $"VOCALOID completed the native {kind} import without committing a project change.");
        return new { imported = true, native = true, kind, details, project = new ProjectContext(McpBridgeService.InstanceId ?? string.Empty, projectId, revision) };
    }

    private static string[] ResolveAudioImportPaths(string? path, JsonElement options)
    {
        var supplied = new List<string>();
        if (!string.IsNullOrWhiteSpace(path))
            supplied.Add(path);
        if (Element(options, "paths") is { ValueKind: JsonValueKind.Array } paths)
            supplied.AddRange(paths.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!));
        if (supplied.Count is < 1 or > 64)
            throw Fault("invalid_request", "Audio import requires between 1 and 64 paths.");
        var resolved = new List<string>(supplied.Count);
        foreach (string item in supplied.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!McpAccessController.TryResolvePath(item, out string fullPath, out BridgeError? error))
                throw Fault(error!);
            if (!File.Exists(fullPath))
                throw Fault("invalid_reference", "An audio import file does not exist.");
            if (!string.Equals(Path.GetExtension(fullPath), ".wav", StringComparison.OrdinalIgnoreCase))
                throw Fault("unsupported", "Native audio import supports WAVE files only.");
            resolved.Add(fullPath);
        }
        return resolved.ToArray();
    }

    private static object RecentProjects()
    {
        string[] recent = (Application.Current?.MainWindow as MainWindow)?.RecentFilePaths?.ToArray()
                          ?? Array.Empty<string>();
        return new
        {
            items = recent.Take(30).Select(item =>
            {
                bool allowed = McpAccessController.TryResolvePath(item, out string safePath, out _);
                return new
                {
                    id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(item))).ToLowerInvariant()[..24],
                    name = Path.GetFileName(item),
                    path = allowed ? safePath : null,
                    exists = allowed && File.Exists(safePath),
                    accessible = allowed,
                };
            }).ToArray(),
        };
    }

    private static void ConfirmDirtyDocument(Document document, MainViewModel main)
    {
        if (document.Sequence?.VSMSequence?.IsDirty != true)
            return;
        MessageBoxResult choice = System.Windows.MessageBox.Show(
            TranslationManager.Tr("VOCALOIDPatcher_Mcp_SaveDirtyProject"),
            TranslationManager.Tr("VOCALOIDPatcher_Mcp_ConfirmationTitle"),
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);
        if (choice == MessageBoxResult.Cancel)
            throw Fault("confirmation_denied", "The user cancelled replacing the dirty project.");
        if (choice == MessageBoxResult.Yes && !main.Save())
            throw Fault("confirmation_denied", "The current project was not saved.");
    }

    private static object ConvertProject(BridgeClientInfo client, JsonElement arguments)
    {
        string action = (String(arguments, "action") ?? throw Fault("invalid_request", "action is required.")).ToLowerInvariant();
        string format = String(arguments, "format") ?? throw Fault("invalid_request", "format is required.");
        string path = String(arguments, "path") ?? throw Fault("invalid_request", "path is required.");
        bool dryRun = Bool(arguments, "dry_run");
        if (!SvipFormatRegistry.TryGet(format, out SvipFormatInfo info))
            throw Fault("unsupported", $"Unknown conversion format '{format}'.");
        if (action == "import" && !info.CanImport || action == "export" && !info.CanExport)
            throw Fault("unsupported", $"Format '{format}' does not support {action}.");
        if (action is not "import" and not "export")
            throw Fault("invalid_request", "Conversion action must be import or export.");
        if (action == "export" || String(arguments, "project_id") != null)
            ValidateProject(arguments);
        if (!McpAccessController.TryResolvePath(path, out string fullPath, out BridgeError? pathError))
            throw Fault(pathError!);
        bool dangerous = action == "import" || File.Exists(fullPath);
        if (!dryRun && !McpAccessController.AuthorizeWrite(client, $"{action} {format} project", dangerous, out BridgeError? error))
            throw Fault(error!);
        if (dryRun)
            return new { dry_run = true, valid = true, action, format, path = fullPath, confirmation_required = dangerous };

        return StartJob("convert_" + action, client, async (cancellationToken, progress) =>
        {
            if (action == "import")
            {
                progress(0.1);
                var project = await Task.Run(() => SvipProjectLoader.Load(info, new[] { fullPath }), cancellationToken);
                progress(0.6);
                await OnUiAsync(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    V6BridgeSvip.Import(project);
                    RefreshEditor();
                    return true;
                });
                progress(1.0);
                return new { imported = true, format, path = fullPath };
            }

            progress(0.1);
            var exportedProject = await OnUiAsync(() => V6BridgeSvip.Export(Bool(Element(arguments, "options") ?? default, "resolve_overlaps")));
            progress(0.5);
            byte[] content = await Task.Run(() => info.Converter.Dump(exportedProject), cancellationToken);
            await File.WriteAllBytesAsync(fullPath, content, cancellationToken);
            progress(1.0);
            return new { exported = true, format, path = fullPath, bytes = content.Length };
        });
    }

    private static object Mixdown(BridgeClientInfo client, JsonElement arguments)
    {
        ValidateProject(arguments);
        string target = (String(arguments, "target") ?? "master").ToLowerInvariant();
        string path = String(arguments, "path") ?? throw Fault("invalid_request", "path is required.");
        bool dryRun = Bool(arguments, "dry_run");
        if (!McpAccessController.TryResolvePath(path, out string fullPath, out BridgeError? pathError))
            throw Fault(pathError!);
        bool dangerous = File.Exists(fullPath);
        if (!dryRun && !McpAccessController.AuthorizeWrite(client, $"Mix down {target} audio", dangerous, out BridgeError? error))
            throw Fault(error!);
        if (dryRun)
            return new { dry_run = true, valid = true, target, path = fullPath, confirmation_required = dangerous };

        JsonElement options = Element(arguments, "options") ?? JsonSerializer.SerializeToElement(new { });
        int? trackIndex = Long(options, "track_index") is { } track ? checked((int)track) : null;
        int? partIndex = Long(options, "part_index") is { } part ? checked((int)part) : null;
        return StartJob("mixdown_" + target, client, async (cancellationToken, progress) =>
        {
            progress(0.05);
            using CancellationTokenRegistration registration = cancellationToken.Register(VEAudioEngine.CancelMixdown);
            bool succeeded = await OnUiAsync(() => ExecuteMixdown(target, fullPath, options, trackIndex, partIndex, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (!succeeded)
                throw Fault("job_failed", "VOCALOID could not complete the mixdown.", true);
            progress(1.0);
            return new { mixed_down = true, target, path = fullPath };
        });
    }

    private static bool ExecuteMixdown(
        string target,
        string path,
        JsonElement options,
        int? trackIndex,
        int? partIndex,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (_, WIVSMSequence vsm) = Context();
        if (!vsm.IsFinishedRendering || App.AudioPlayer == null || App.AudioPlayer.IsPlaying)
            throw Fault("busy", "VOCALOID is playing or rendering.", true);
        var mixdown = new MixdownOption
        {
            SampleRate = ParseSampleRate(Int(options, "sample_rate", 44100)),
            BitDepth = Int(options, "bit_depth", 24) == 16 ? MixdownBitDepth.BD16 : MixdownBitDepth.BD24,
            Channel = String(options, "channel")?.Equals("mono", StringComparison.OrdinalIgnoreCase) == true
                ? ChannelType.Mono
                : ChannelType.Stereo,
            IsAudioEffectBypass = Bool(options, "bypass_effects"),
            OpenExplorer = false,
        };
        return target.ToLowerInvariant() switch
        {
            "master" => App.AudioPlayer.ExecuteAudioMixdownMaster(path, mixdown),
            "part" when trackIndex != null && partIndex != null
                => App.AudioPlayer.ExecuteAudioMixdownPart(Part(vsm, trackIndex.Value, partIndex.Value), path, mixdown),
            "track" when trackIndex != null
                => App.AudioPlayer.ExecuteAudioMixdownTrackWithTrackInfo(
                    new List<AudioMixdownTrackInfo>
                    {
                        new(Track(vsm, trackIndex.Value), trackIndex.Value) { FullPath = path },
                    },
                    mixdown),
            _ => throw Fault("invalid_request", "Mixdown target must be master, track, or part; track/part indices belong in options."),
        };
    }

    private static MixdownSampleRate ParseSampleRate(int value) => value switch
    {
        44100 => MixdownSampleRate.SR44100,
        48000 => MixdownSampleRate.SR48000,
        96000 => MixdownSampleRate.SR96000,
        192000 => MixdownSampleRate.SR192000,
        _ => throw Fault("invalid_request", "sample_rate must be 44100, 48000, 96000, or 192000."),
    };

    private static object ManageJob(BridgeClientInfo client, JsonElement arguments)
    {
        string action = String(arguments, "action") ?? "list";
        string? id = String(arguments, "job_id");
        return action.ToLowerInvariant() switch
        {
            "list" => new { jobs = McpJobManager.List(client.Id) },
            "get" when id != null => McpJobManager.Get(id, client.Id) ?? throw Fault("invalid_reference", "The job does not exist."),
            "cancel" when id != null => new { cancelled = McpJobManager.Cancel(id, client.Id), job = McpJobManager.Get(id, client.Id) },
            _ => throw Fault("invalid_request", "Job action must be list, get, or cancel; get/cancel require job_id."),
        };
    }

    private static JobInfo StartJob(
        string kind,
        BridgeClientInfo client,
        Func<CancellationToken, Action<double>, Task<object?>> action)
    {
        if (!McpAccessController.BeginJob(client))
            throw Fault("write_lease_held", "The write lease expired before the job started.", true);
        return McpJobManager.Start(kind, client.Id, async (cancellationToken, progress) =>
        {
            try
            {
                return await action(cancellationToken, progress).ConfigureAwait(false);
            }
            finally
            {
                McpAccessController.EndJob(client);
            }
        });
    }

    private static Task<T> OnUiAsync<T>(Func<T> action)
    {
        var dispatcher = Application.Current?.Dispatcher
                         ?? throw Fault("v6_unavailable", "VOCALOID UI dispatcher is unavailable.", true);
        return dispatcher.CheckAccess()
            ? Task.FromResult(action())
            : dispatcher.InvokeAsync(action).Task;
    }
}
