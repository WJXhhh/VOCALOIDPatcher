using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VOCALOIDPatcher.McpBridge;

// Concrete DTOs are intentional: unconstrained JsonElement inputs are exposed
// as strings by some MCP clients and cannot describe the timing vocabulary.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TimingEdit
{
    [JsonPropertyName("op"), Description("boundary.set/move/reset, note.reset, coefficient.set/reset, phoneme_gain.set/reset, transition.set/reset/reset_expression, transitions.reset, comp.set/reset")]
    public required string Op { get; init; }
    [JsonPropertyName("note_index")] public int? NoteIndex { get; init; }
    [JsonPropertyName("boundary_index")] public int? BoundaryIndex { get; init; }
    [JsonPropertyName("phoneme_index")] public int? PhonemeIndex { get; init; }
    [JsonPropertyName("offset_frames"), Description("Absolute offset from native baseline; one frame is 256/44100 seconds.")]
    public int? OffsetFrames { get; init; }
    [JsonPropertyName("delta_frames"), Description("Relative movement from the currently applied boundary; positive moves right.")]
    public int? DeltaFrames { get; init; }
    [JsonPropertyName("coefficient")] public float? Coefficient { get; init; }
    [JsonPropertyName("decibels")] public float? Decibels { get; init; }
    [JsonPropertyName("layout_fingerprint")] public string? LayoutFingerprint { get; init; }
    [JsonPropertyName("transition_id")] public string? TransitionId { get; init; }
    [JsonPropertyName("begin_frame"), Description("Absolute frame in the queried Part's native timing coordinates.")]
    public int? BeginFrame { get; init; }
    [JsonPropertyName("end_frame")] public int? EndFrame { get; init; }
    [JsonPropertyName("phase"), Description("Source progress in [-1,1] within the current source range; preserves coverage unless explicitly changed.")]
    public float? Phase { get; init; }
    [JsonPropertyName("coverage"), Description("none, left, right, both, or auto (same enable policy as the editor).")]
    public string? Coverage { get; init; }
    [JsonPropertyName("onset_frames"), Description("Legacy incoming Sil source progress; nonpositive only.")]
    public int? OnsetFrames { get; init; }
    [JsonPropertyName("comp")] public TimingCompRecipe? Comp { get; init; }

    public static readonly string[] Operations = ["boundary.set", "boundary.move", "boundary.reset", "note.reset",
        "coefficient.set", "coefficient.reset", "phoneme_gain.set", "phoneme_gain.reset", "transition.set",
        "transition.reset", "transition.reset_expression", "transitions.reset", "comp.set", "comp.reset"];

    public void Validate()
    {
        if (!Operations.Contains(Op)) throw new ArgumentException("Unknown timing operation.");
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "op" };
        void Allow(params string[] fields) { foreach (string field in fields) allowed.Add(field); }
        void Need(bool condition, string field) { if (!condition) throw new ArgumentException($"{field} is required or invalid for {Op}."); }
        if (Op.StartsWith("transition", StringComparison.Ordinal))
        {
            Allow("layout_fingerprint"); Need(!string.IsNullOrWhiteSpace(LayoutFingerprint), "layout_fingerprint");
            if (Op != "transitions.reset") { Allow("transition_id"); Need(!string.IsNullOrWhiteSpace(TransitionId), "transition_id"); }
            if (Op == "transition.set")
            {
                Allow("begin_frame", "end_frame", "decibels", "phase", "coverage", "onset_frames");
                Need(BeginFrame != null || EndFrame != null || Decibels != null || Phase != null || Coverage != null || OnsetFrames != null, "transition controls");
            }
        }
        else
        {
            Allow("note_index"); Need(NoteIndex is >= 0, "note_index");
            if (Op.StartsWith("boundary.", StringComparison.Ordinal))
            {
                Allow("boundary_index"); Need(BoundaryIndex is >= 0 and <= 60, "boundary_index");
                if (Op == "boundary.set") { Allow("offset_frames"); Need(OffsetFrames != null, "offset_frames"); }
                if (Op == "boundary.move") { Allow("delta_frames"); Need(DeltaFrames != null, "delta_frames"); }
            }
            if (Op == "coefficient.set") { Allow("coefficient"); Need(Coefficient != null, "coefficient"); }
            if (Op.StartsWith("phoneme_gain.", StringComparison.Ordinal))
            {
                Allow("phoneme_index");
                if (Op == "phoneme_gain.set") { Allow("decibels"); Need(PhonemeIndex is >= 0 && Decibels != null, "phoneme_index/decibels"); }
                else Need(PhonemeIndex is null or >= 0, "phoneme_index");
            }
            if (Op == "comp.set") { Allow("comp"); Need(Comp != null, "comp"); }
        }
        foreach (JsonProperty property in JsonSerializer.SerializeToElement(this, BridgeProtocol.JsonOptions).EnumerateObject())
            if (!allowed.Contains(property.Name)) throw new ArgumentException($"{property.Name} is not applicable to {Op}.");
        if (OffsetFrames is < -1722 or > 1722 || DeltaFrames is < -1722 or > 1722)
            throw new ArgumentException("Boundary movement exceeds the supported ten second domain.");
        if (Coefficient is { } coefficient && (!float.IsFinite(coefficient) || coefficient is < 0 or > 1)) throw new ArgumentException("coefficient must be 0–1.");
        if (Decibels is { } gain && (!float.IsFinite(gain) || gain is < -60 or > 12)) throw new ArgumentException("decibels must be -60–12.");
        if (Phase is { } phase && (!float.IsFinite(phase) || phase is < -1 or > 1)) throw new ArgumentException("phase must be -1–1.");
        if (Coverage != null && Coverage is not ("none" or "left" or "right" or "both" or "auto")) throw new ArgumentException("Invalid coverage.");
        if (OnsetFrames is < -4096 or > 0) throw new ArgumentException("onset_frames must be -4096–0.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TimingCompRecipe(
    [property: JsonPropertyName("body")] TimingCompSource? Body = null,
    [property: JsonPropertyName("consonant")] TimingCompSource? Consonant = null,
    [property: JsonPropertyName("vowel")] TimingCompSource? Vowel = null,
    [property: JsonPropertyName("isolation")] TimingCompIsolation? Isolation = null,
    [property: JsonPropertyName("seam_ms")] double SeamMs = 5,
    [property: JsonPropertyName("bypass")] bool Bypass = false);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TimingCompSource(
    [property: JsonPropertyName("lyric")] string? Lyric = null,
    [property: JsonPropertyName("phonemes")] string? Phonemes = null,
    [property: JsonPropertyName("length_ms")] double LengthMs = 0,
    [property: JsonPropertyName("trim"), Description("keep_head, keep_tail, or middle")] string Trim = "keep_head",
    [property: JsonPropertyName("offset_ms")] double OffsetMs = 0,
    [property: JsonPropertyName("vowel_index")] int VowelIndex = -1,
    [property: JsonPropertyName("trim_begin_ms")] double TrimBeginMs = 0,
    [property: JsonPropertyName("trim_end_ms")] double TrimEndMs = 0,
    [property: JsonPropertyName("evec")] TimingCompEvec? Evec = null,
    [property: JsonPropertyName("context")] TimingCompContext? Context = null,
    [property: JsonPropertyName("expression")] TimingCompExpression? Expression = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TimingCompEvec(int Color, int Attack, int Release, int Extension);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TimingCompContext(string? Before = null, string? After = null, [property: JsonPropertyName("note_ms")] double NoteMs = 500);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TimingCompExpression(int Velocity, int Accent, int Decay, [property: JsonPropertyName("bend_depth")] int BendDepth,
    [property: JsonPropertyName("bend_length")] int BendLength, int Opening, [property: JsonPropertyName("rise_port")] bool RisePort,
    [property: JsonPropertyName("fall_port")] bool FallPort);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TimingCompIsolation(bool After = false, [property: JsonPropertyName("end_offset_ms")] double EndOffsetMs = 0,
    [property: JsonPropertyName("fade_ms")] double FadeMs = 5, [property: JsonPropertyName("source_length_ms")] double SourceLengthMs = 0);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TimingSettingsEdit
{
    [JsonPropertyName("phoneme_timing")] public bool? PhonemeTiming { get; init; }
    [JsonPropertyName("phoneme_volume")] public bool? PhonemeVolume { get; init; }
    [JsonPropertyName("transitions")] public bool? Transitions { get; init; }
    [JsonPropertyName("voice_comp")] public bool? VoiceComp { get; init; }
    [JsonPropertyName("smoothing")] public bool? Smoothing { get; init; }
    [JsonPropertyName("volume_threshold_db")] public double? VolumeThresholdDb { get; init; }
    [JsonPropertyName("crossfade_ms")] public double? CrossfadeMs { get; init; }
    [JsonPropertyName("loudness_window_ms")] public double? LoudnessWindowMs { get; init; }
    [JsonPropertyName("analysis_range_ms")] public double? AnalysisRangeMs { get; init; }
    [JsonPropertyName("phase_alignment")] public bool? PhaseAlignment { get; init; }
    [JsonPropertyName("timing_offset_ms")] public double? TimingOffsetMs { get; init; }

    public void Validate()
    {
        if (!JsonSerializer.SerializeToElement(this, BridgeProtocol.JsonOptions).EnumerateObject().Any()) throw new ArgumentException("A settings update must contain at least one field.");
        static void Range(double? value, double min, double max, string name)
        { if (value is { } v && (!double.IsFinite(v) || v < min || v > max)) throw new ArgumentException($"{name} must be {min}–{max}."); }
        Range(VolumeThresholdDb, .1, 30, "volume_threshold_db"); Range(CrossfadeMs, 1, 50, "crossfade_ms");
        Range(LoudnessWindowMs, 20, 300, "loudness_window_ms"); Range(AnalysisRangeMs, 20, 300, "analysis_range_ms");
        Range(TimingOffsetMs, -50, 50, "timing_offset_ms");
    }
}

public static class TimingContract
{
    public static T Parse<T>(JsonElement value)
    {
        static void Unique(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in node.EnumerateObject())
                { if (!seen.Add(p.Name)) throw new ArgumentException($"Duplicate timing field '{p.Name}'."); Unique(p.Value); }
            }
            else if (node.ValueKind == JsonValueKind.Array) foreach (var child in node.EnumerateArray()) Unique(child);
        }
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Timing input must be a structured object.");
        Unique(value);
        try { return value.Deserialize<T>(BridgeProtocol.JsonOptions) ?? throw new ArgumentException("Missing timing input."); }
        catch (JsonException e) { throw new ArgumentException(e.Message, e); }
    }
}
