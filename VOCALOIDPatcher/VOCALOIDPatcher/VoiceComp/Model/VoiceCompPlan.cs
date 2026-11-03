using System;
using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VOCALOIDPatcher.VoiceComp;

internal enum CompAction { LongSlice, PhonemeSplice, IndependentSustain }
internal enum CompTrim { KeepHead, KeepTail, Middle }
internal enum CompSourceRole { Connected, Body, Consonant, Vowel }
internal sealed record CompEvecState(int Color, int Attack, int Release, int Extension);
internal sealed record CompSourceExpression(int Velocity, int Accent, int Decay, int BendDepth, int BendLength,
    int Opening, bool RisePort, bool FallPort)
{
    internal void Validate()
    {
        if (Velocity is < 0 or > 127 || Accent is < 0 or > 127 || Decay is < 0 or > 127 || BendDepth is < 0 or > 127 ||
            BendLength is < 0 or > 127 || Opening is < 0 or > 127)
            throw new InvalidDataException("Invalid source expression snapshot.");
    }
}
internal sealed record CompSourceContext(string? Before = null, string? After = null, double NoteMs = 500)
{
    internal void Validate()
    {
        if (Before?.Length > 60 || After?.Length > 60 || Before != null && string.IsNullOrWhiteSpace(Before) ||
            After != null && string.IsNullOrWhiteSpace(After) || !double.IsFinite(NoteMs) || NoteMs is < 50 or > 5000)
            throw new InvalidDataException("Invalid source pronunciation context.");
    }
}

// Source time and song time are deliberately separate. Zero length means the
// documented automatic length, not a zero-duration note or a sampling bias.
internal sealed record SourceRecipe(string? Lyric = null, string? Phonemes = null,
    double LengthMs = 0, CompTrim Trim = CompTrim.KeepHead, double OffsetMs = 0,
    int VowelIndex = -1, double TrimBeginMs = 0, double TrimEndMs = 0, CompEvecState? Evec = null,
    CompSourceContext? Context = null, CompSourceExpression? Expression = null)
{
    internal void Validate()
    {
        Context?.Validate();
        Expression?.Validate();
        if (Lyric?.Length > 256 || Phonemes?.Length > 60 ||
            Phonemes != null && string.IsNullOrWhiteSpace(Phonemes) ||
            !double.IsFinite(LengthMs) || LengthMs < 0 || LengthMs > 60000 ||
            !double.IsFinite(OffsetMs) || Math.Abs(OffsetMs) > 60000 ||
            VowelIndex is < -1 or > 59 || !double.IsFinite(TrimBeginMs) || !double.IsFinite(TrimEndMs) ||
            TrimBeginMs is < 0 or > 60000 || TrimEndMs is < 0 or > 60000 || !Enum.IsDefined(Trim))
            throw new InvalidDataException("Invalid voice-comp source recipe.");
        if (Evec != null && (Evec.Color is < 0 or > 8192 || Evec.Attack is < 0 or > 8192 ||
            Evec.Release is < 0 or > 8192 || Evec.Extension is < 0 or > 128))
            throw new InvalidDataException("Invalid voice-comp EVEC state.");
    }
    internal double Duration(double targetMs) => LengthMs > 0 ? LengthMs : Math.Max(targetMs * 2, 1000);
}

internal sealed record ContextBreak(bool After = false, double EndOffsetMs = 0, double FadeMs = 5, double SourceLengthMs = 0)
{
    internal void Validate()
    {
        if (!double.IsFinite(EndOffsetMs) || Math.Abs(EndOffsetMs) > 1000 ||
            !double.IsFinite(FadeMs) || FadeMs is < 0 or > 30 ||
            !double.IsFinite(SourceLengthMs) || SourceLengthMs is < 0 or > 60000)
            throw new InvalidDataException("Invalid voice-comp context break.");
    }
    // Isolation removes the next note's context without stretching the current
    // pronunciation. Explicit source length and the audible cut remain separate.
    internal double Duration(double noteMs) => SourceLengthMs > 0 ? SourceLengthMs : noteMs + Math.Max(0, EndOffsetMs);
}

internal sealed record VoiceCompPlan(SourceRecipe? Body = null, SourceRecipe? Consonant = null,
    SourceRecipe? Vowel = null, ContextBreak? Isolation = null, double SeamMs = 5, bool Bypass = false)
{
    internal bool HasSources => Body != null || Consonant != null;
    internal bool IsEmpty => !HasSources && Isolation?.After != true;
    internal bool Active => !Bypass && !IsEmpty;
    internal string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this))));
    internal void Validate()
    {
        Body?.Validate(); Consonant?.Validate(); Vowel?.Validate(); Isolation?.Validate();
        if ((Consonant == null) != (Vowel == null) || !double.IsFinite(SeamMs) || SeamMs is < 0 or > 30)
            throw new InvalidDataException("Invalid voice-comp plan.");
    }
    internal VoiceCompPlan WithAction(CompAction action) => action switch
    {
        CompAction.LongSlice => Consonant != null ? this : this with { Body = Body ?? new() },
        CompAction.PhonemeSplice => this with { Consonant = Consonant ?? new(), Vowel = Vowel ?? Body ?? new() },
        CompAction.IndependentSustain => this with { Isolation = Isolation ?? new(true) },
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
}

internal sealed record SlicePlacement(string Path, long SourceBegin, long OutputBegin, long Length,
    int FadeIn = 0, int FadeOut = 0, CompSourceRole Role = CompSourceRole.Body)
{
    internal long OutputEnd => checked(OutputBegin + Length);
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Path) || SourceBegin < 0 || OutputBegin < 0 || Length <= 0 ||
            FadeIn < 0 || FadeOut < 0 || FadeIn > Length || FadeOut > Length)
            throw new InvalidDataException("Invalid voice-comp slice placement.");
        _ = checked(SourceBegin + Length); _ = OutputEnd;
    }
}

internal sealed record CompRenderPlan(int SampleRate, long Samples, ImmutableArray<SlicePlacement> Slices);

internal static class CompAlignment
{
    internal static long Crop(long begin, long end, long length, CompTrim trim, long offset = 0)
    {
        if (begin < 0 || end <= begin || length <= 0 || end - begin < length)
            throw new InvalidDataException("Source is shorter than the requested slice.");
        long origin = trim switch { CompTrim.KeepHead => begin, CompTrim.KeepTail => end - length,
            CompTrim.Middle => begin + (end - begin - length) / 2, _ => throw new ArgumentOutOfRangeException(nameof(trim)) };
        return Math.Clamp(checked(origin + offset), begin, end - length);
    }

    // Preserve the donor consonant's duration. Only the vowel entrance is locked.
    internal static (long ConsonantBegin, long VowelBegin) AtVowel(long donorBegin, long donorVowel, long targetVowel)
    {
        if (donorBegin < 0 || donorVowel <= donorBegin || targetVowel < donorVowel - donorBegin)
            throw new InvalidDataException("Consonant exceeds the available pre-roll.");
        return (targetVowel - (donorVowel - donorBegin), targetVowel);
    }
}
