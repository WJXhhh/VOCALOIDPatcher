using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using VOCALOIDPatcher.McpBridge;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.TrackEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Mcp.Domains.NativeSemantics;

internal static class NativeSemanticJobs
{
    private static readonly Version MinimumVersion = new(6, 13, 0);
    private const long MinimumSplitTicks = 30;

    public static IReadOnlyList<CapabilityStatus> Capabilities()
    {
        Version? version = typeof(App).Assembly.GetName().Version;
        bool versionOk = version == null || version >= MinimumVersion;
        return new[]
        {
            Status("job.native_semantics.transpose_note", versionOk && Method<MusicalEditorViewModel>("TransposeNote", typeof(int))),
            Status("job.native_semantics.staccato_note", versionOk && Method<MusicalEditorViewModel>("StaccatoNote", typeof(StaccatoInfo.Type))),
            Status("job.native_semantics.join_notes", versionOk && Method<MusicalEditorViewModel>("JointNote")),
            Status("job.native_semantics.insert_rest", versionOk && Method<MusicalEditorViewModel>("InsertRest", typeof(VSMAbsTick), typeof(VSMRelTick))),
            Status("job.native_semantics.lyric_shift", versionOk && Method<MusicalEditorViewModel>("LyricMoveLeft") && Method<MusicalEditorViewModel>("LyricMoveRight")),
            Status("job.native_semantics.reset_lyrics", versionOk && Method<MusicalEditorViewModel>("ResetLyrics")),
            Status("job.native_semantics.phonetic_protect", versionOk && Method<MusicalEditorViewModel>("ChangePhoneticSymbolProtect")),
            Status("job.native_semantics.split_note", versionOk && Method<MusicalEditor>("SplitNote", typeof(VSMRelTick), typeof(SplitNoteInfo.NoteSplittingBasePosition), typeof(SplitNoteInfo.PhoneticSymbolsSplittingStrategy), typeof(string), typeof(bool), typeof(int))),
            Status("job.native_semantics.join_parts", versionOk && Method<TrackEditorViewModel>("JoinMidiParts")),
            Status("job.native_semantics.duplicate_track", versionOk && Method<TrackEditorViewModel>("ExecuteDuplicateTrack")),
            Status("job.native_semantics.quantize_position", versionOk && Property<MusicalEditorViewModel>("FullQuantizeNotesCommand") && Property<MusicalEditorViewModel>("HalfQuantizeNotesCommand")),
            Status("job.native_semantics.half_tempo", versionOk && Method<TrackEditorViewModel>("PartDoubleHalfTempo", typeof(double))),
            Status("job.native_semantics.double_tempo", versionOk && Method<TrackEditorViewModel>("PartDoubleHalfTempo", typeof(double))),
            Status("job.native_semantics.parameter_selection_reset", versionOk && Method<MusicalEditorViewModel>("ResetControlParametersToDefault")),
            Status("job.native_semantics.parameter_range_delete", versionOk && Method<MusicalEditorViewModel>("RemoveControlParameter", typeof(WIVSMMidiPart), typeof(VSMRelTick), typeof(VSMRelTick), typeof(bool))),
            Status("job.native_semantics.insert_lyrics_batch", versionOk && Method<WIVSMNote>("SetLyricsAndResetPhonemes", typeof(string))),
            Unsupported("job.native_semantics.normalize_note", "V6 requires overlap/removal lists computed by its dialog command; no non-dialog native planning entry is verified."),
            Unsupported("job.native_semantics.quantize_duration", "V6 6.13 exposes native Full/Half Quantize only for note start positions; no native duration-quantize business entry is present."),
            Unsupported("job.native_semantics.parameter_range_transform", "V6 6.13 exposes native selected-parameter reset and range deletion, but no non-gesture business entry for translate, scale, or clamp."),
            Unsupported("job.native_semantics.phonetic_conversion", "V6 6.13 exposes G2PA as part of native lyric mutation, but no independent selected-note phonetic-conversion command."),
        };
    }

    public static object PlanAndRun(string kind, JsonElement options, MainViewModel main, bool execute)
    {
        Version? version = typeof(App).Assembly.GetName().Version;
        if (version != null && version < MinimumVersion)
            throw new NotSupportedException($"Native semantic jobs require V6 {MinimumVersion} or newer; installed version is {version}.");
        MusicalEditorViewModel? musical = main.MusicalEditorVM;
        TrackEditorViewModel? trackEditor = main.TrackEditorVM;
        (Yamaha.VOCALOID.Sequence projectSequence, WIVSMSequence sequence) = Context(main);
        return kind switch
        {
            "transpose_note" => Transpose(musical, options, execute),
            "staccato_note" => Staccato(musical, options, execute),
            "join_notes" => JoinNotes(musical, execute),
            "insert_rest" => InsertRest(musical, options, execute),
            "lyric_shift_left" => LyricShift(musical, true, execute),
            "lyric_shift_right" => LyricShift(musical, false, execute),
            "reset_lyrics" => ResetLyrics(musical, execute),
            "toggle_phonetic_protect" => ToggleProtect(musical, execute),
            "split_note" => SplitNotes(musical, options, execute),
            "join_parts" => JoinParts(trackEditor, sequence, execute),
            "duplicate_track" => DuplicateTracks(trackEditor, sequence, execute),
            "quantize_position" => QuantizePosition(musical, sequence, options, execute),
            "half_tempo" => ChangePartTempo(trackEditor, sequence, false, execute),
            "double_tempo" => ChangePartTempo(trackEditor, sequence, true, execute),
            "parameter_selection_reset" => ResetSelectedParameters(musical, execute),
            "parameter_range_delete" => DeleteParameterRange(musical, projectSequence, sequence, options, execute),
            "insert_lyrics_batch" => InsertLyricsBatch(musical, projectSequence, sequence, options, execute),
            "normalize_note" or "quantize_duration" or "parameter_range_transform" or "phonetic_conversion"
                => throw new NotSupportedException(Capabilities().First(item => item.Id.EndsWith(kind switch
                {
                    var value => value,
                }, StringComparison.Ordinal)).UnavailableReason),
            _ => throw new NotSupportedException($"Unknown native semantic job '{kind}'."),
        };
    }

    private static object Transpose(MusicalEditorViewModel? vm, JsonElement options, bool execute)
    {
        WIVSMMidiPart part = ActivePart(vm);
        int semitones = RequiredInt(options, "semitones");
        if (semitones is < -12 or > 12 || semitones == 0)
            throw new ArgumentOutOfRangeException("semitones", "semitones must be from -12 through 12 and non-zero.");
        List<WIVSMNote> notes = TargetNotes(part);
        int changed = notes.Count(note => Math.Clamp(note.NoteNumber + semitones, 0, 127) != note.NoteNumber);
        if (changed == 0)
            throw new ArgumentException("Transpose would not change any target note.");
        if (execute) vm!.TransposeNote(semitones);
        return Summary("transpose_note", notes.Count, new { semitones, changed_notes = changed, clamped_notes = notes.Count - changed });
    }

    private static object Staccato(MusicalEditorViewModel? vm, JsonElement options, bool execute)
    {
        WIVSMMidiPart part = ActivePart(vm);
        if (!Enum.TryParse(ReadString(options, "strength"), true, out StaccatoInfo.Type strength))
            throw new ArgumentException("options.strength must be weak, medium, or strong.");
        List<WIVSMNote> notes = TargetNotes(part);
        if (execute) vm!.StaccatoNote(strength);
        return Summary("staccato_note", notes.Count, new { strength = strength.ToString().ToLowerInvariant() });
    }

    private static object JoinNotes(MusicalEditorViewModel? vm, bool execute)
    {
        WIVSMMidiPart part = ActivePart(vm);
        List<WIVSMNote> notes = part.SelectedNotes;
        if (notes.Count < 2)
            throw new ArgumentException("join_notes requires at least two selected notes in the active MIDI part.");
        if (execute) vm!.JointNote();
        return Summary("join_notes", notes.Count, new { resulting_notes = 1 });
    }

    private static object InsertRest(MusicalEditorViewModel? vm, JsonElement options, bool execute)
    {
        WIVSMMidiPart part = ActivePart(vm);
        long absoluteTick = RequiredLong(options, "absolute_tick");
        long length = RequiredLong(options, "length_tick");
        if (length <= 0 || length > 100_000)
            throw new ArgumentOutOfRangeException("length_tick", "length_tick must be from 1 through 100000.");
        if (absoluteTick < part.AbsPosTick.Value || absoluteTick > part.AbsEndTick.Value)
            throw new ArgumentOutOfRangeException("absolute_tick", "absolute_tick must be inside the active MIDI part.");
        int moved = part.Notes.Count(note => note.AbsPosTick.Value >= absoluteTick);
        if (moved == 0)
            throw new ArgumentException("No notes begin at or after absolute_tick in the active MIDI part.");
        if (execute) vm!.InsertRest(new VSMAbsTick(absoluteTick), new VSMRelTick(length));
        return Summary("insert_rest", moved, new { absolute_tick = absoluteTick, length_tick = length, moved_notes = moved, moves_controllers = true });
    }

    private static object LyricShift(MusicalEditorViewModel? vm, bool left, bool execute)
    {
        WIVSMMidiPart part = ActivePart(vm);
        List<WIVSMNote> notes = part.StraightSelectedNote;
        if (notes.Count == 0)
            throw new ArgumentException("Lyric shift requires a contiguous selected-note range in the active MIDI part.");
        if (left && notes[0].Prev == null)
            throw new ArgumentException("The selected lyric range cannot shift left beyond the first note.");
        if (!left && notes[^1].Next == null)
            throw new ArgumentException("The selected lyric range cannot shift right beyond the last note.");
        if (execute)
        {
            if (left) vm!.LyricMoveLeft(); else vm!.LyricMoveRight();
        }
        return Summary(left ? "lyric_shift_left" : "lyric_shift_right", notes.Count, new { contiguous = true, resets_phonemes = true });
    }

    private static object ResetLyrics(MusicalEditorViewModel? vm, bool execute)
    {
        WIVSMMidiTrack track = ActiveTrack(vm);
        int selected = track.MidiParts.Sum(part => part.SelectedNotes.Count);
        if (selected == 0)
            throw new ArgumentException("reset_lyrics requires selected notes on the active MIDI track.");
        if (execute) vm!.ResetLyrics();
        return Summary("reset_lyrics", selected, new { resets_phonemes = true, clears_protection = true });
    }

    private static object ToggleProtect(MusicalEditorViewModel? vm, bool execute)
    {
        WIVSMMidiTrack track = ActiveTrack(vm);
        WIVSMNote[] selected = track.MidiParts.SelectMany(part => part.SelectedNotes).ToArray();
        if (selected.Length == 0)
            throw new ArgumentException("toggle_phonetic_protect requires selected notes on the active MIDI track.");
        bool target = !vm!.GetPhoneticSymbolProtectState().GetValueOrDefault();
        if (execute) vm.ChangePhoneticSymbolProtect();
        return Summary("toggle_phonetic_protect", selected.Length, new { protected_state = target, resets_unprotected_phonemes = !target });
    }

    private static object SplitNotes(MusicalEditorViewModel? vm, JsonElement options, bool execute)
    {
        WIVSMMidiPart part = ActivePart(vm);
        long length = RequiredLong(options, "length_tick");
        if (length < MinimumSplitTicks)
            throw new ArgumentOutOfRangeException("length_tick", $"length_tick must be at least {MinimumSplitTicks}.");
        if (!Enum.TryParse(NormalizeEnum(ReadString(options, "base_position")), true, out SplitNoteInfo.NoteSplittingBasePosition basePosition))
            throw new ArgumentException("base_position must be note_on or note_off.");
        if (!Enum.TryParse(NormalizeEnum(ReadString(options, "phoneme_strategy")), true, out SplitNoteInfo.PhoneticSymbolsSplittingStrategy strategy))
            throw new ArgumentException("phoneme_strategy must be melisma, specific_phoneme, or vowel.");
        string symbol = ReadString(options, "phonetic_symbol") ?? string.Empty;
        if (strategy == SplitNoteInfo.PhoneticSymbolsSplittingStrategy.SpecificPhoneme && string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("phonetic_symbol is required for specific_phoneme.");
        int transpose = OptionalInt(options, "transpose", 0);
        if (transpose is < -12 or > 12)
            throw new ArgumentOutOfRangeException("transpose", "transpose must be from -12 through 12.");
        List<WIVSMNote> notes = TargetNotes(part);
        if (notes.Any(note => note.DurationTick.Value - length < MinimumSplitTicks))
            throw new ArgumentException("Every target note must leave at least 30 ticks on both sides of the split.");
        bool protect = OptionalBool(options, "protect_phonemes");
        if (execute)
            new MusicalEditor().SplitNote(new VSMRelTick(length), basePosition, strategy, symbol, protect, transpose);
        return Summary("split_note", notes.Count, new { length_tick = length, base_position = basePosition.ToString(), phoneme_strategy = strategy.ToString(), created_notes = notes.Count });
    }

    private static object JoinParts(TrackEditorViewModel? vm, WIVSMSequence sequence, bool execute)
    {
        if (!sequence.IsFinishedRendering)
            throw new InvalidOperationException("join_parts is unavailable while V6 rendering is active.");
        var groups = sequence.MidiTracks.Select(track => new { track, parts = track.SelectedMidiParts }).Where(item => item.parts.Count >= 2).ToArray();
        if (groups.Length == 0)
            throw new ArgumentException("join_parts requires at least two selected MIDI parts on one track.");
        if (vm == null)
            throw new InvalidOperationException("The V6 Track Editor view model is unavailable.");
        if (execute) vm.JoinMidiParts();
        return Summary("join_parts", groups.Sum(item => item.parts.Count), new { affected_tracks = groups.Length, resulting_parts = groups.Length, resets_phonemes = true });
    }

    private static object DuplicateTracks(TrackEditorViewModel? vm, WIVSMSequence sequence, bool execute)
    {
        int selected = sequence.SelectedTracks.Count;
        if (selected == 0)
            throw new ArgumentException("duplicate_track requires at least one selected track.");
        if (sequence.NumTrack + (ulong)selected > sequence.MaxNumTrack)
            throw new InvalidOperationException("Duplicating the selected tracks would exceed V6's track limit.");
        if (vm == null)
            throw new InvalidOperationException("The V6 Track Editor view model is unavailable.");
        if (execute) vm.ExecuteDuplicateTrack();
        return Summary("duplicate_track", selected, new { created_tracks = selected });
    }

    private static object QuantizePosition(MusicalEditorViewModel? vm, WIVSMSequence sequence, JsonElement options, bool execute)
    {
        WIVSMMidiPart part = ActivePart(vm);
        if (vm!.Quantize == QuantizeTypeEnum.Off)
            throw new InvalidOperationException("quantize_position requires an active V6 quantize grid.");
        string strength = ReadString(options, "strength")?.ToLowerInvariant()
            ?? throw new ArgumentException("options.strength must be full or half.");
        float rate = strength switch
        {
            "full" => 1.0f,
            "half" => 0.5f,
            _ => throw new ArgumentException("options.strength must be full or half."),
        };
        List<WIVSMNote> notes = part.SelectedNotes;
        if (notes.Count == 0)
            throw new ArgumentException("quantize_position requires selected notes in the active MIDI part.");
        int moved = notes.Count(note =>
        {
            VSMAbsTick quantized = sequence.GetQuantizedTick(note.AbsPosTick, QuantizeStrategy.Nearest, vm.Quantize);
            return VSMAbsTickExtension.InterpolateLinear(note.AbsPosTick, quantized, rate) != note.AbsPosTick;
        });
        if (moved == 0)
            throw new ArgumentException("The selected notes are already aligned for the requested quantize strength.");
        if (execute)
        {
            DelegateCommand command = strength == "full"
                ? MusicalEditorViewModel.FullQuantizeNotesCommand
                : MusicalEditorViewModel.HalfQuantizeNotesCommand;
            if (!command.CanExecute(null))
                throw new InvalidOperationException("The native V6 quantize command is currently unavailable.");
            command.Execute(null);
        }
        return Summary("quantize_position", notes.Count, new { strength, moved_notes = moved, grid = vm.Quantize.ToString() });
    }

    private static object ChangePartTempo(TrackEditorViewModel? vm, WIVSMSequence sequence, bool doubleTempo, bool execute)
    {
        if (vm == null)
            throw new InvalidOperationException("The V6 Track Editor view model is unavailable.");
        if (sequence.SelectedMidiParts.Count != 1)
            throw new ArgumentException("half_tempo and double_tempo require exactly one selected MIDI part.");
        WIVSMMidiPart part = sequence.SelectedMidiParts[0];
        double coefficient = doubleTempo ? TrackEditorViewModel.DoubleTempoCoef : TrackEditorViewModel.HalfTempoCoef;
        int controllers = Enum.GetValues(typeof(VSMControllerType)).Cast<VSMControllerType>()
            .Sum(type => checked((int)part.GetNumController(type)));
        long originalDuration = part.DurationTick.Value;
        long targetDuration = Math.Max((part.DurationTick * coefficient).Value, Yamaha.VOCALOID.Design.Sequence.minPartTick);
        int clampedNotes = part.Notes.Count(note => note.DurationTick * coefficient < (long)Yamaha.VOCALOID.Design.Sequence.minNoteTick);
        if (execute && !vm.PartDoubleHalfTempo(coefficient))
            throw new InvalidOperationException("V6 rejected the native Part tempo transformation; its transaction was rolled back.");
        return Summary(doubleTempo ? "double_tempo" : "half_tempo", 1, new
        {
            coefficient,
            notes = part.Notes.Count,
            controllers,
            original_duration_tick = originalDuration,
            target_duration_tick = targetDuration,
            notes_clamped_to_minimum_duration = clampedNotes,
            scales_direct_pitch_timing = true,
        });
    }

    private static object ResetSelectedParameters(MusicalEditorViewModel? vm, bool execute)
    {
        WIVSMMidiPart part = ActivePart(vm);
        int selected = 0;
        int removed = 0;
        int reset = 0;
        foreach (VSMControllerType type in Enum.GetValues(typeof(VSMControllerType)))
        {
            if (type is VSMControllerType.DynamicsHmm or VSMControllerType.PitchBendHmm or VSMControllerType.WeightHmm)
                continue;
            bool inRun = false;
            for (ulong index = 0; index < part.GetNumController(type); index++)
            {
                WIVSMMidiController? controller = part.GetController(type, index);
                if (controller?.Selected == true)
                {
                    selected++;
                    if (inRun) removed++; else reset++;
                    inRun = true;
                }
                else
                {
                    inRun = false;
                }
            }
        }
        if (selected == 0)
            throw new ArgumentException("parameter_selection_reset requires selected native breakpoints in the active MIDI part.");
        if (execute) vm!.ResetControlParametersToDefault();
        return Summary("parameter_selection_reset", selected, new { reset_to_default = reset, removed_points = removed, excludes_hmm_parameters = true });
    }

    private static object DeleteParameterRange(MusicalEditorViewModel? vm, Yamaha.VOCALOID.Sequence projectSequence, WIVSMSequence sequence, JsonElement options, bool execute)
    {
        WIVSMMidiPart part = ActivePart(vm);
        long start = RequiredLong(options, "start_tick");
        long end = RequiredLong(options, "end_tick");
        if (start < 0 || end < start || end > part.DurationTick.Value)
            throw new ArgumentOutOfRangeException("start_tick", "start_tick and end_tick must define an inclusive range inside the active MIDI part.");
        VSMControllerType type = MusicalEditorViewModel.CtrlParamTypeToVSMControllerType(vm!.ControlParameterType);
        if (!Enum.IsDefined(typeof(VSMControllerType), type))
            throw new InvalidOperationException("The active parameter type is note-based and has no native breakpoint range-delete entry.");
        int deleted = 0;
        for (ulong index = 0; index < part.GetNumController(type); index++)
        {
            WIVSMMidiController? controller = part.GetController(type, index);
            if (controller != null && start <= controller.RelPosTick.Value && controller.RelPosTick.Value <= end)
                deleted++;
        }
        if (deleted == 0)
            throw new ArgumentException("The requested range contains no breakpoints for the active parameter type.");
        if (execute)
        {
            using var selectionNotifier = new SelectionNotifier(projectSequence);
            using var transaction = new Transaction(sequence);
            transaction.Result = vm.RemoveControlParameter(part, new VSMRelTick(start), new VSMRelTick(end), true);
            if (!transaction.Result)
                throw new InvalidOperationException("V6 rejected the native parameter range deletion; its transaction was rolled back.");
        }
        return Summary("parameter_range_delete", deleted, new { start_tick = start, end_tick = end, parameter_type = vm.ControlParameterType.ToString() });
    }

    private static object InsertLyricsBatch(MusicalEditorViewModel? vm, Yamaha.VOCALOID.Sequence projectSequence, WIVSMSequence sequence, JsonElement options, bool execute)
    {
        WIVSMMidiPart part = ActivePart(vm);
        if (projectSequence.IsNoteSelectedInMultipleParts())
            throw new ArgumentException("insert_lyrics_batch cannot span multiple MIDI parts.");
        List<WIVSMNote> notes = part.StraightSelectedNote;
        if (notes.Count == 0 || part.FirstSelectedNote == null)
            throw new ArgumentException("insert_lyrics_batch requires a contiguous selected-note range in the active MIDI part.");
        string lyrics = ReadString(options, "lyrics")?.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal).Trim()
            ?? throw new ArgumentException("options.lyrics must be a non-empty string.");
        if (lyrics.Length == 0)
            throw new ArgumentException("options.lyrics must be a non-empty string.");
        if (lyrics.Length > Yamaha.VOCALOID.Design.Sequence.maxInsertLyricsTextLength)
            throw new ArgumentOutOfRangeException("lyrics", $"lyrics must not exceed {Yamaha.VOCALOID.Design.Sequence.maxInsertLyricsTextLength} characters.");
        if (execute)
        {
            using var selectionNotifier = new SelectionNotifier(projectSequence);
            using var transaction = new Transaction(sequence);
            transaction.Result = part.FirstSelectedNote.SetLyricsAndResetPhonemes(lyrics);
            if (!transaction.Result)
                throw new InvalidOperationException("V6 rejected the native batch lyric insertion; its transaction was rolled back.");
            WIVSMNote? target = part.G2paManagerTargetNote;
            if (target != null)
                projectSequence.SelectNoteAndDeselectOtherNotes(part, target);
        }
        return Summary("insert_lyrics_batch", notes.Count, new { selected_notes = notes.Count, resets_phonemes = true, native_g2pa = true });
    }

    private static object Summary(string kind, int affected, object detail) => new { semantic_entry = kind, affected_items = affected, detail, native_transaction = true };

    private static (Yamaha.VOCALOID.Sequence Sequence, WIVSMSequence Vsm) Context(MainViewModel main)
        => main.Sequence is { VSMSequence: { } vsm } sequence ? (sequence, vsm) : throw new InvalidOperationException("No V6 project is open.");

    private static WIVSMMidiPart ActivePart(MusicalEditorViewModel? vm)
        => vm?.ActivePart ?? throw new InvalidOperationException("The active part must be a MIDI part in the Musical Editor.");

    private static WIVSMMidiTrack ActiveTrack(MusicalEditorViewModel? vm)
        => vm?.ActiveTrack ?? throw new InvalidOperationException("The active track must be a MIDI track in the Musical Editor.");

    private static List<WIVSMNote> TargetNotes(WIVSMMidiPart part)
    {
        List<WIVSMNote> notes = part.HasSelectedNote ? part.SelectedNotes : part.Notes;
        return notes.Count > 0 ? notes : throw new ArgumentException("The active MIDI part contains no target notes.");
    }

    private static CapabilityStatus Status(string id, bool available)
        => new(id, available, false, "6.13.0", available ? null : "The installed editor does not expose the verified V6 6.13 method signature.", available ? "host_validation_required" : "unsupported");

    private static CapabilityStatus Unsupported(string id, string reason) => new(id, false, false, "6.13.0", reason, "unsupported");

    private static bool Method<T>(string name, params Type[] arguments)
        => typeof(T).GetMethod(name, BindingFlags.Instance | BindingFlags.Public, null, arguments, null) != null;

    private static bool Property<T>(string name)
        => typeof(T).GetProperty(name, BindingFlags.Static | BindingFlags.Public) != null;

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long RequiredLong(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result) ? result : throw new ArgumentException($"options.{name} must be an integer.");

    private static int RequiredInt(JsonElement element, string name) => checked((int)RequiredLong(element, name));
    private static int OptionalInt(JsonElement element, string name, int fallback) => element.TryGetProperty(name, out _) ? RequiredInt(element, name) : fallback;
    private static bool OptionalBool(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();

    private static string NormalizeEnum(string? value) => value?.Replace("_", string.Empty, StringComparison.Ordinal) ?? string.Empty;
}
