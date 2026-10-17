using VOCALOIDPatcher.Evec;

internal static partial class Program
{
    private static void VerifyRenderTimingPlans()
    {
        var state = State(EvecConstants.VoiceColorSoft, EvecConstants.AttackMild,
            EvecConstants.ReleaseBreathShort, 2);
        foreach (double duration in new[] { 1.0, 89.999, 90, 104.999, 105, 149.999, 150, 1000 })
        foreach (double? gap in new double?[] { null, -20, 0, 99.999, 100, 100.001, 200 })
        foreach (bool sameLanguage in new[] { false, true })
        {
            string original = state.ToString();
            if (!EvecRenderTimingPlan.TryCreate("k a", state, duration, gap, sameLanguage, out var plan))
                throw new InvalidOperationException("Expected a valid Japanese CV plan.");
            bool color = duration >= 90;
            bool release = duration >= 150 && !(sameLanguage && gap.HasValue && gap <= 100);
            if (plan.Count != 1 + (color ? 1 : 0) + (release ? 1 : 0) ||
                plan[0].BeginMs != 0 || plan[^1].EndMs != duration || state.ToString() != original)
                throw new InvalidOperationException("Split budget or state preservation failed.");
            if (plan[0].Phonemes != "k k k k#2 a")
                throw new InvalidOperationException("CTop must stay in the base segment.");
            if (color && (plan[0].EndMs != 45 || plan[1].Phonemes != "a#2"))
                throw new InvalidOperationException("Color must be anchored to logical note start.");
            for (int index = 0; index < plan.Count; index++)
            {
                if (plan[index].EndMs <= plan[index].BeginMs ||
                    (index > 0 && plan[index - 1].EndMs != plan[index].BeginMs))
                    throw new InvalidOperationException("Render segments must partition the source note.");
            }
            if (release && (plan[^1].BeginMs != duration - 60 || plan[^1].Phonemes != "*#1"))
                throw new InvalidOperationException("Release must occupy the final 60 ms.");
        }
        foreach (int releaseId in new[] { EvecConstants.ReleaseBreathShort, EvecConstants.ReleaseBreathLong })
        {
            var releaseOnly = State(release: releaseId);
            foreach (double duration in new[] { 104.999, 105.0, 1000 })
            {
                EvecRenderTimingPlan.TryCreate("k a", releaseOnly, duration, null, false, out var plan);
                if (plan.Count != (duration >= 105 ? 2 : 1) ||
                    (duration >= 105 && plan[^1].Phonemes != releaseOnly.ReleasePhoneme))
                    throw new InvalidOperationException("Release-only threshold or recording ID changed.");
            }
        }
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, 0, -1 })
        {
            if (EvecRenderTimingPlan.TryCreate("k a", state, invalid, null, false, out _))
                throw new InvalidOperationException("Invalid duration was accepted.");
        }
        if (EvecRenderTimingPlan.TryCreate("k a t", state, 1000, null, false, out _) ||
            EvecRenderTimingPlan.TryCreate("k a", state, 1000, double.NaN, true, out _))
            throw new InvalidOperationException("Unsupported coda or invalid neighbor was accepted.");
        if (!EvecRenderTimingPlan.TryCreate("k a", state, 1000, 100.00000000000009, true, out var edge) ||
            edge.Count != 2)
            throw new InvalidOperationException("Tempo-map floating-point noise reenabled a connected release.");
        foreach (double threshold in new[] { 90.0, 105.0, 150.0 })
        {
            var thresholdState = threshold == 105 ? State(release: EvecConstants.ReleaseBreathShort) : state;
            if (!EvecRenderTimingPlan.TryCreate("k a", thresholdState, threshold - 1e-12, null, false, out var near) ||
                near.Count != (threshold == 150 ? 3 : 2) || near[^1].EndMs != threshold - 1e-12)
                throw new InvalidOperationException("Tempo-map rounding dropped a threshold articulation.");
        }
        Console.WriteLine("render-timing-plan=passed (112 joint budgets, release IDs, invalid inputs)");
    }
}
