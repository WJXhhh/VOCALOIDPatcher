using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using VOCALOIDPatcher.McpBridge;
using VOCALOIDPatcher.VoiceComp;

namespace VOCALOIDPatcher.ConsonantTiming;

// Pure adapter shared by MCP planning and offline contract tests. Kept outside
// the reusable timing models so native-only harnesses need no Comp dependency.
internal static class TimingMcpEdits
{
    internal static SourceRecipe CompSource(VoiceCompPlan plan, string role) => (role switch
    { "body" => plan.Consonant == null ? plan.Body : null, "consonant" => plan.Consonant, "vowel" => plan.Vowel, _ => null })
        ?? throw new ArgumentException("This source role is not present in the target recipe.");

    internal static VoiceCompPlan WithCompSource(VoiceCompPlan plan, string role, SourceRecipe source)
    {
        _ = CompSource(plan, role);
        return role switch { "body" => plan with { Body = source }, "consonant" => plan with { Consonant = source }, _ => plan with { Vowel = source } };
    }

    internal static VoiceCompPlan? EditComp(VoiceCompEdit edit, VoiceCompPlan? before, Func<SourceRecipe, SourceRecipe>? snapshot = null)
    {
        edit.Validate();
        var plan = before ?? new();
        switch (edit.Op)
        {
            case "reset": return null;
            case "replace": plan = Comp(edit.Recipe!); break;
            case "bypass": return before == null ? null : before with { Bypass = edit.Bypass!.Value };
            case "long_slice":
                plan = plan.WithAction(CompAction.LongSlice);
                string role = plan.Consonant != null ? "vowel" : "body";
                var source = CompSource(plan, role);
                plan = WithCompSource(plan, role, source with { LengthMs = edit.LengthMs ?? source.LengthMs,
                    OffsetMs = edit.OffsetMs ?? source.OffsetMs, Trim = edit.Trim switch { "keep_head" => CompTrim.KeepHead,
                        "keep_tail" => CompTrim.KeepTail, "middle" => CompTrim.Middle, _ => source.Trim } }); break;
            case "phoneme_splice":
                plan = plan.WithAction(CompAction.PhonemeSplice) with { SeamMs = edit.SeamMs ?? plan.SeamMs }; break;
            case "independent_sustain":
                plan = plan.WithAction(CompAction.IndependentSustain);
                plan = plan with { Isolation = plan.Isolation! with { SourceLengthMs = edit.LengthMs ?? plan.Isolation.SourceLengthMs,
                    EndOffsetMs = edit.EndOffsetMs ?? plan.Isolation.EndOffsetMs, FadeMs = edit.FadeMs ?? plan.Isolation.FadeMs } }; break;
            case "replace_source":
                var replacement = Comp(new TimingCompRecipe(Body: edit.Source)).Body!;
                plan = WithCompSource(plan, edit.SourceRole!, replacement); break;
            case "copy_source":
                var original = CompSource(plan, edit.SourceRole!);
                plan = WithCompSource(plan, edit.SourceRole!, (snapshot ?? throw new ArgumentException("Missing source snapshot."))(original)); break;
        }
        plan.Validate(); return plan.IsEmpty ? null : plan;
    }
    internal const double FrameSeconds = 256.0 / 44100;
    internal static string Id(TransitionLayout layout, TransitionIdentity edge)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(layout.Fingerprint + ":" + edge)));
    internal static TransitionEdge Edge(TransitionLayout layout, string? fingerprint, string? id)
    {
        if (layout.Fingerprint != fingerprint) throw new InvalidOperationException("Transition layout is stale; query it again.");
        return layout.Edges.SingleOrDefault(e => Id(layout, e.Identity) == id)
            ?? throw new ArgumentException("transition_id does not belong to this layout.");
    }
    internal static TransitionControlPlan Transition(TimingEdit edit, TransitionLayout layout, TransitionControlPlan controls, TransitionExecutionPlan execution)
    {
        edit.Validate();
        if (layout.Fingerprint != edit.LayoutFingerprint) throw new InvalidOperationException("Transition layout is stale; query it again.");
        if (edit.Op == "transitions.reset") return new(layout.Fingerprint);
        var edge = Edge(layout, edit.LayoutFingerprint, edit.TransitionId);
        var identity = edge.Identity;
        if (edit.Op == "transition.reset_expression") return TransitionExpressionControl.Reset(controls, identity);
        if (edit.Op == "transition.reset")
        {
            var frames = new[] { new TransitionEndpointRef(identity, TransitionEndpoint.Begin), new(identity, TransitionEndpoint.End) }
                .Select(e => layout.Junction(e).OriginalFrame).ToHashSet();
            return new(layout.Fingerprint, controls.Junctions.Where(j => !frames.Contains(layout.Junction(j.Endpoint).OriginalFrame)),
                controls.Gains.Where(g => g.Edge != identity), controls.Onsets.Where(o => o.Edge != identity),
                controls.Phases.Where(p => p.Edge != identity), controls.Coverages.Where(c => c.Edge != identity));
        }
        if (edit.Op != "transition.set") throw new ArgumentException("Expected a transition operation.");
        // Both endpoints are compiled together. Intermediate geometry need not
        // be valid, and aliases are replaced by their one physical junction.
        if (edit.BeginFrame is { } begin) controls = controls.WithJunction(layout, new(identity, TransitionEndpoint.Begin), begin);
        if (edit.EndFrame is { } end) controls = controls.WithJunction(layout, new(identity, TransitionEndpoint.End), end);
        if (edit.Decibels is { } db) controls = new(layout.Fingerprint, controls.Junctions,
            controls.Gains.Where(g => g.Edge != identity).Concat(db == 0 ? [] : new[] { new TransitionGainEdit(identity, db) }),
            controls.Onsets, controls.Phases, controls.Coverages);
        var row = execution.Segments.Where(s => s.To != null).ElementAt(layout.Edges.IndexOf(edge));
        if (edit.Coverage is { } coverage) controls = coverage == "auto" ? TransitionExpressionControl.Source(controls, row, identity, true) :
            controls.WithCoverage(identity, coverage switch { "left" => TransitionSourceSides.Left, "right" => TransitionSourceSides.Right,
                "both" => TransitionSourceSides.Both, _ => TransitionSourceSides.None });
        if (edit.Phase is { } phase) controls = TransitionExpressionControl.Progress(controls, row, identity, phase);
        if (edit.OnsetFrames is { } onset) controls = controls.WithOnset(identity, onset);
        _ = controls.Compile(layout);
        return controls;
    }
    internal static VoiceCompPlan Comp(TimingCompRecipe recipe)
    {
        static SourceRecipe? Source(TimingCompSource? s) => s == null ? null : new(s.Lyric, s.Phonemes, s.LengthMs,
            s.Trim switch { "keep_head" => CompTrim.KeepHead, "keep_tail" => CompTrim.KeepTail, "middle" => CompTrim.Middle,
                _ => throw new ArgumentException("Comp trim must be keep_head, keep_tail, or middle.") }, s.OffsetMs, s.VowelIndex, s.TrimBeginMs, s.TrimEndMs,
            s.Evec is { } e ? new(e.Color, e.Attack, e.Release, e.Extension) : null,
            s.Context is { } c ? new(c.Before, c.After, c.NoteMs) : null,
            s.Expression is { } x ? new(x.Velocity, x.Accent, x.Decay, x.BendDepth, x.BendLength, x.Opening, x.RisePort, x.FallPort) : null);
        var plan = new VoiceCompPlan(Source(recipe.Body), Source(recipe.Consonant), Source(recipe.Vowel),
            recipe.Isolation is { } i ? new(i.After, i.EndOffsetMs, i.FadeMs, i.SourceLengthMs) : null, recipe.SeamMs, recipe.Bypass);
        plan.Validate(); return plan;
    }
    internal static TimingCompRecipe? Comp(VoiceCompPlan? p)
    {
        static TimingCompSource? Source(SourceRecipe? s) => s == null ? null : new(s.Lyric, s.Phonemes, s.LengthMs,
            s.Trim switch { CompTrim.KeepHead => "keep_head", CompTrim.KeepTail => "keep_tail", _ => "middle" }, s.OffsetMs, s.VowelIndex,
            s.TrimBeginMs, s.TrimEndMs, s.Evec is { } e ? new(e.Color, e.Attack, e.Release, e.Extension) : null,
            s.Context is { } c ? new(c.Before, c.After, c.NoteMs) : null,
            s.Expression is { } x ? new(x.Velocity, x.Accent, x.Decay, x.BendDepth, x.BendLength, x.Opening, x.RisePort, x.FallPort) : null);
        return p == null ? null : new(Source(p.Body), Source(p.Consonant), Source(p.Vowel),
            p.Isolation is { } i ? new(i.After, i.EndOffsetMs, i.FadeMs, i.SourceLengthMs) : null, p.SeamMs, p.Bypass);
    }
}
