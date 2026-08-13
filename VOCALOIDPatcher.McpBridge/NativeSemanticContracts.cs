namespace VOCALOIDPatcher.McpBridge;

public sealed record NativeSemanticJobContract(
    string Id,
    string[] RequiredOptions,
    bool Implemented,
    string? UnavailableReason = null,
    string MinimumEditorVersion = "6.13.0");

public static class NativeSemanticJobCatalog
{
    public static IReadOnlyList<NativeSemanticJobContract> Jobs { get; } = new[]
    {
        Available("transpose_note", "semitones"),
        Available("staccato_note", "strength"),
        Available("join_notes"),
        Available("insert_rest", "absolute_tick", "length_tick"),
        Available("lyric_shift_left"),
        Available("lyric_shift_right"),
        Available("reset_lyrics"),
        Available("toggle_phonetic_protect"),
        Available("split_note", "length_tick", "base_position", "phoneme_strategy"),
        Available("join_parts"),
        Available("duplicate_track"),
        Available("quantize_position", "strength"),
        Available("half_tempo"),
        Available("double_tempo"),
        Available("parameter_selection_reset"),
        Available("parameter_range_delete", "start_tick", "end_tick"),
        Available("insert_lyrics_batch", "lyrics"),
        Unavailable("normalize_note", "V6 requires overlap/removal lists computed by its dialog command; no non-dialog native planning entry is verified."),
        Unavailable("quantize_duration", "V6 6.13 exposes native Full/Half Quantize only for note start positions; no native duration-quantize business entry is present."),
        Unavailable("parameter_range_transform", "V6 6.13 exposes native selected-parameter reset and range deletion, but no non-gesture business entry for translate, scale, or clamp."),
        Unavailable("phonetic_conversion", "V6 6.13 exposes G2PA as part of native lyric mutation, but no independent selected-note phonetic-conversion command."),
    };

    private static NativeSemanticJobContract Available(string id, params string[] required)
        => new(id, required, true);

    private static NativeSemanticJobContract Unavailable(string id, string reason)
        => new(id, Array.Empty<string>(), false, reason);
}
