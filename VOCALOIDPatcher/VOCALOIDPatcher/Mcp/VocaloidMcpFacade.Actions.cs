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
using VOCALOIDPatcher.McpBridge;
using VOCALOIDPatcher.Translation;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VAE;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Mcp;

internal static partial class VocaloidMcpFacade
{
    private static object SelectView(BridgeClientInfo client, JsonElement arguments)
    {
        ValidateProject(arguments);
        if (!McpAccessController.AuthorizeWrite(client, "Change the active selection and editor view", false, out BridgeError? error))
            throw Fault(error!);
        JsonElement request = Element(arguments, "request")
                              ?? throw Fault("invalid_request", "request is required.");
        (Yamaha.VOCALOID.Sequence sequence, WIVSMSequence vsm) = Context();

        if (Bool(request, "clear", true))
        {
            sequence.SelectAllNotesInActivePart(false);
            sequence.SelectAllParts(false);
        }

        int? trackIndex = Long(request, "track_index") is { } track ? checked((int)track) : null;
        int? partIndex = Long(request, "part_index") is { } part ? checked((int)part) : null;
        if (trackIndex != null && partIndex != null)
        {
            WIVSMPart selectedPart = Part(vsm, trackIndex.Value, partIndex.Value);
            sequence.SelectPart(selectedPart, true);
            if (!sequence.SetActivePartAndTrack(selectedPart))
                throw Fault("operation_failed", "VOCALOID could not activate the requested part.");
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
                sequence.SelectNote(midiPart.Notes[noteIndex], true);
            }
        }

        if (Element(request, "absolute_tick") != null || Element(request, "position") != null)
            ApplicationMainViewModel()?.SetCurrentPosition(new VSMAbsTick(Math.Max(0, ResolveAbsoluteTick(vsm, request))));

        return QuerySelection();
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
            case "stop":
                main.StopPlay(null, false);
                break;
            case "seek":
                main.SetCurrentPosition(new VSMAbsTick(Math.Max(0, ResolveAbsoluteTick(vsm, arguments))));
                break;
            case "set_loop":
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
            case "status":
                break;
            default:
                throw Fault("invalid_request", "Transport action must be play, stop, seek, set_loop, set_loop_enabled, or status.");
        }

        VSMTickRange loop = vsm.LoopRange;
        return new
        {
            is_playing = App.AudioPlayer?.IsPlaying ?? false,
            position = Position(vsm, vsm.CurrentPosTick.Value),
            loop_enabled = vsm.IsLoopOn,
            loop_begin_tick = loop.Begin,
            loop_end_tick = loop.End,
        };
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
                throw Fault("unsupported", $"Unsupported creative job '{kind}'.");
        }

        long revision = dryRun ? previousRevision : McpRevisionTracker.Current().Revision;
        return new
        {
            dry_run = dryRun,
            kind,
            project = new ProjectContext(McpBridgeService.InstanceId ?? string.Empty, McpRevisionTracker.Current().ProjectId, revision),
        };
    }

    private static object ProjectFile(BridgeClientInfo client, JsonElement arguments)
    {
        string action = (String(arguments, "action") ?? throw Fault("invalid_request", "action is required.")).ToLowerInvariant();
        string? path = String(arguments, "path");
        bool dryRun = Bool(arguments, "dry_run");
        if (action is not "new" && action is not "open" || String(arguments, "project_id") != null)
            ValidateProject(arguments);

        string? fullPath = null;
        if (action is "open" or "save_as" or "export_midi")
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

        bool dangerous = action is "new" or "open" || fullPath != null && File.Exists(fullPath);
        if (!dryRun && !McpAccessController.AuthorizeWrite(client, $"Project file operation: {action}", dangerous, out BridgeError? error))
            throw Fault(error!);
        if (dryRun)
            return new { dry_run = true, valid = true, action, path = fullPath, confirmation_required = dangerous };

        JobInfo job = StartJob(action, client, async (cancellationToken, progress) =>
        {
            progress(0.1);
            object result = await OnUiAsync(() => ExecuteProjectFile(action, fullPath, cancellationToken));
            progress(1.0);
            return result;
        });
        return job;
    }

    private static object ExecuteProjectFile(string action, string? path, CancellationToken cancellationToken)
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
            default:
                throw Fault("invalid_request", "Project action must be new, open, save, save_as, or export_midi.");
        }
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
