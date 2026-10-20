using System.IO;
using System.Text.Json;
using VOCALOIDPatcher.ConsonantTiming.Smoothing;

internal static partial class Program
{
    private static int ProbeTransitionSmootherArtifacts(string directory)
    {
        Directory.CreateDirectory(directory);
        const int rate = 44100, junction = rate / 2;
        var report = new List<object>();
        foreach (int frequency in new[] { 120, 220, 440, 1000 })
        foreach (bool phaseStep in new[] { false, true })
        foreach (bool gainStep in new[] { false, true })
        foreach (bool align in new[] { false, true })
        foreach (var mode in new[] { TransitionSmoothingMode.Disabled, TransitionSmoothingMode.AutoOnVolumeDifference, TransitionSmoothingMode.Always })
        {
            var original = Enumerable.Range(0, rate).Select(i => (float)((gainStep && i < junction ? .25 : .5) * Math.Sin(
                2 * Math.PI * frequency * i / rate + (phaseStep && i >= junction ? Math.PI / 2 : 0)))).ToArray();
            var audio = (float[])original.Clone();
            var result = TransitionSmoother.SmoothJunction(audio, junction, rate,
                new() { Mode = mode, EnablePhaseAlignment = align }, 0, gainStep ? 20 * Math.Log10(2) : 0);
            int fadeLength = Math.Max(4, (int)Math.Round(rate * result.CrossfadeDurationMs / 1000));
            int fadeStart = junction + result.SpliceOffsetSamples - fadeLength / 2, fadeEnd = fadeStart + fadeLength;
            double Jump(float[] a, int at) => Math.Abs(a[at] - a[at - 1]);
            double MaxJump(float[] a) => Enumerable.Range(1, a.Length - 1).Max(i => Jump(a, i));
            string name = $"sine{frequency}-{(phaseStep ? "phase-step" : "continuous")}-{(gainStep ? "gain-step" : "equal-gain")}-align{align}-{mode}";
            report.Add(new { name, frequency, phaseStep, gainStep, align, mode = mode.ToString(), result,
                changed = audio.Where((s, i) => s != original[i]).Count(),
                originalMaxJump = MaxJump(original), outputMaxJump = MaxJump(audio),
                leftEdgeBefore = Jump(original, fadeStart), leftEdgeAfter = Jump(audio, fadeStart),
                rightEdgeBefore = Jump(original, fadeEnd), rightEdgeAfter = Jump(audio, fadeEnd),
                rmsError = Math.Sqrt(audio.Select((s, i) => Math.Pow(s - original[i], 2)).Average()) });
            // Store only a short synthetic diagnostic window, not bank audio.
            File.WriteAllLines(Path.Combine(directory, name + ".csv"),
                new[] { "sample,original,processed" }.Concat(Enumerable.Range(junction - 1500, 3000)
                    .Select(i => FormattableString.Invariant($"{i},{original[i]:R},{audio[i]:R}"))));
        }
        File.WriteAllText(Path.Combine(directory, "smoother-artifacts.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"smoother.cases={report.Count}; listeningValidation=False");
        return 0;
    }
}
