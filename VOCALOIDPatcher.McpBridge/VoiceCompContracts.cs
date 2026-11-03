using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VOCALOIDPatcher.McpBridge;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VoiceCompDonor(
    [property: JsonPropertyName("track_index")] int TrackIndex,
    [property: JsonPropertyName("part_index")] int PartIndex,
    [property: JsonPropertyName("note_index")] int NoteIndex);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VoiceCompEdit
{
    [JsonPropertyName("op"), Description("long_slice, phoneme_splice, independent_sustain, replace, replace_source, copy_source, bypass, reset")]
    public required string Op { get; init; }
    [JsonPropertyName("note_indices"), Description("Explicit zero-based targets in one Part; 1–128 distinct notes; one undo step.")]
    public required int[] NoteIndices { get; init; }
    [JsonPropertyName("recipe")] public TimingCompRecipe? Recipe { get; init; }
    [JsonPropertyName("source")] public TimingCompSource? Source { get; init; }
    [JsonPropertyName("source_role"), Description("body, consonant, or vowel; must exist in the target recipe.")]
    public string? SourceRole { get; init; }
    [JsonPropertyName("donor")] public VoiceCompDonor? Donor { get; init; }
    [JsonPropertyName("bypass")] public bool? Bypass { get; init; }
    [JsonPropertyName("length_ms"), Description("Actual source duration; for independent_sustain this is isolation.source_length_ms. Zero selects automatic length.")]
    public double? LengthMs { get; init; }
    [JsonPropertyName("trim"), Description("keep_head (remove tail), keep_tail (remove head), middle")]
    public string? Trim { get; init; }
    [JsonPropertyName("offset_ms")] public double? OffsetMs { get; init; }
    [JsonPropertyName("seam_ms")] public double? SeamMs { get; init; }
    [JsonPropertyName("end_offset_ms")] public double? EndOffsetMs { get; init; }
    [JsonPropertyName("fade_ms")] public double? FadeMs { get; init; }

    public static readonly string[] Operations = ["long_slice", "phoneme_splice", "independent_sustain", "replace", "replace_source", "copy_source", "bypass", "reset"];
    public void Validate()
    {
        if (!Operations.Contains(Op)) throw new ArgumentException("Unknown voice-comp operation.");
        if (NoteIndices == null || NoteIndices.Length is < 1 or > 128 || NoteIndices.Any(i => i < 0) || NoteIndices.Distinct().Count() != NoteIndices.Length)
            throw new ArgumentException("note_indices requires 1–128 distinct nonnegative indices.");
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "op", "note_indices" };
        void Allow(params string[] fields) { foreach (string f in fields) allowed.Add(f); }
        switch (Op)
        {
            case "long_slice": Allow("length_ms", "trim", "offset_ms"); break;
            case "phoneme_splice": Allow("seam_ms"); break;
            case "independent_sustain": Allow("length_ms", "end_offset_ms", "fade_ms"); break;
            case "replace": Allow("recipe"); if (Recipe == null) throw new ArgumentException("recipe is required."); break;
            case "replace_source": Allow("source_role", "source"); if (Source == null) throw new ArgumentException("source is required."); break;
            case "copy_source": Allow("source_role", "donor"); if (Donor == null || Donor.TrackIndex < 0 || Donor.PartIndex < 0 || Donor.NoteIndex < 0) throw new ArgumentException("A valid donor is required."); break;
            case "bypass": Allow("bypass"); if (Bypass == null) throw new ArgumentException("bypass is required."); break;
        }
        foreach (var field in JsonSerializer.SerializeToElement(this, BridgeProtocol.JsonOptions).EnumerateObject())
            if (!allowed.Contains(field.Name)) throw new ArgumentException($"{field.Name} is not applicable to {Op}.");
        if (Op is "replace_source" or "copy_source" && SourceRole is not ("body" or "consonant" or "vowel"))
            throw new ArgumentException("source_role must be body, consonant, or vowel.");
        if (Trim != null && Trim is not ("keep_head" or "keep_tail" or "middle")) throw new ArgumentException("Invalid trim mode.");
        static void Range(double? value, double min, double max, string name)
        { if (value is { } v && (!double.IsFinite(v) || v < min || v > max)) throw new ArgumentException($"Invalid {name}."); }
        Range(LengthMs, 0, 60000, "length_ms"); Range(OffsetMs, -60000, 60000, "offset_ms");
        Range(SeamMs, 0, 30, "seam_ms"); Range(EndOffsetMs, -1000, 1000, "end_offset_ms"); Range(FadeMs, 0, 30, "fade_ms");
    }
}
