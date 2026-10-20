using System.Numerics;
using VOCALOIDPatcher.AceParameters;

int failures = 0;
void Check(string name, bool pass, string detail = "")
{
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")} {name} {detail}");
    if (!pass) failures++;
}
AceAcousticCurve Constant(double value) => new([new(0, value)]);
AceAcousticControls Controls(double energy = 0, double air = 0, double tension = 0, double falsetto = 0)
    => new(Constant(energy), Constant(air), Constant(tension), Constant(falsetto));

if (args.Length == 3 && args[0] == "--process")
{
    string destination = Path.GetFullPath(args[2]);
    Directory.CreateDirectory(destination);
    foreach (var item in new[]
    {
        ("neutral", Controls()), ("energy", Controls(energy: .5)), ("air", Controls(air: .8)),
        ("air-low", Controls(air: .2)), ("air-medium", Controls(air: .5)), ("air-max", Controls(air: 1)),
        ("tension-pressed", Controls(tension: .8)), ("tension-relaxed", Controls(tension: -.8)),
        ("falsetto", Controls(falsetto: .8)),
    })
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        AceAcousticWave.Create(args[1], Path.Combine(destination, item.Item1 + ".wav"), item.Item2, compatiblePcm16: true);
        Console.WriteLine($"{item.Item1}: {watch.ElapsedMilliseconds} ms");
    }
    return 0;
}
double Rms(float[] x, int first = 4096) => Math.Sqrt(x.Skip(first).Take(x.Length - 2 * first).Average(v => (double)v * v));
double Harmonic(float[] x, double f, int rate = 44100)
{
    int start = 8192, length = 22050;
    Complex sum = Complex.Zero;
    for (int i = 0; i < length; i++) sum += x[start + i] * Complex.FromPolarCoordinates(1, -2 * Math.PI * f * i / rate);
    return sum.Magnitude / length;
}
double Tilt(float[] x, int a, int b) => 20 * Math.Log10(Harmonic(x, 200 * a) / Harmonic(x, 200 * b));
float[] Fixture(int rate = 44100)
{
    var model = new LfGlottalModel(1.5);
    var harmonics = Enumerable.Range(1, 60).Select(h => model.Spectrum(h)).ToArray();
    var x = new float[rate * 2];
    for (int i = 0; i < x.Length; i++)
    {
        double value = 0;
        for (int h = 1; h <= harmonics.Length && h * 200 < rate / 2; h++)
        {
            // Independent two-formant transfer function, unchanged in all runs.
            double f = h * 200;
            double tract = 1 + 2 * Math.Exp(-Math.Pow((f - 700) / 180, 2)) + 1.5 * Math.Exp(-Math.Pow((f - 1800) / 280, 2));
            value += harmonics[h - 1].Magnitude * tract * Math.Cos(2 * Math.PI * f * i / rate + harmonics[h - 1].Phase);
        }
        x[i] = (float)(value * .15 * Math.Min(1, Math.Min(i, x.Length - i - 1) / 1000d));
    }
    return x;
}

foreach (double rd in new[] { .5, 1.0, 1.5, 2.0, 2.7 })
{
    var lf = new LfGlottalModel(rd);
    int n = 100000;
    double area = 0;
    Complex numeric = Complex.Zero;
    for (int i = 0; i < n; i++)
    {
        double t = (i + .5) / n, v = lf.Derivative(t);
        area += v / n;
        numeric += v * Complex.FromPolarCoordinates(1d / n, -2 * Math.PI * 7 * t);
    }
    Check($"LF area {rd}", Math.Abs(area) < 1e-6, area.ToString("G5"));
    Check($"LF analytic spectrum {rd}", (numeric - lf.Spectrum(7)).Magnitude < 1e-7);
}

var source = Fixture();
Check("neutral PCM is exact", source.SequenceEqual(AceAcousticProcessor.Process(source, 44100, Controls())));
var silent = new float[16000];
Check("no noise in silence", AceAcousticProcessor.Process(silent, 16000, Controls(1, 1, 1, 1)).All(v => v == 0));
var boosted = AceAcousticProcessor.Process(source, 44100, Controls(energy: .5));
double boostDb = 20 * Math.Log10(Rms(boosted) / Rms(source));
Check("Energy is +9 dB", Math.Abs(boostDb - 9) < .001, boostDb.ToString("F4"));
var falsetto = AceAcousticProcessor.Process(source, 44100, Controls(falsetto: .8));
double thinning = Tilt(falsetto, 2, 4) - Tilt(source, 2, 4);
Check("Falsetto changes H2-H4", thinning > 3, thinning.ToString("F3"));
var relaxed = AceAcousticProcessor.Process(source, 44100, Controls(tension: -1));
var pressed = AceAcousticProcessor.Process(source, 44100, Controls(tension: 1));
double tightTilt = Tilt(pressed, 1, 4), looseTilt = Tilt(relaxed, 1, 4);
Check("Tension changes source closure spectrum", looseTilt - tightTilt > 2, $"pressed={tightTilt:F2}, relaxed={looseTilt:F2}");
Check("source shape preserves RMS", Math.Abs(20 * Math.Log10(Rms(falsetto) / Rms(source))) < 1);
double[] frame = falsetto.Skip(10000).Take(2048).Select(v => (double)v).ToArray();
double pitch = AceAcousticProcessor.EstimatePitch(frame, 44100, out _);
Check("no register pitch transposition", Math.Abs(pitch - 200) < 2, pitch.ToString("F3"));
var airy = AceAcousticProcessor.Process(source, 44100, Controls(air: 1));
double Noise(float[] x)
{
    var spectrum = new Complex[8192];
    for (int i = 0; i < spectrum.Length; i++) spectrum[i] = x[i + 8192] * (.5 - .5 * Math.Cos(2 * Math.PI * i / spectrum.Length));
    AceAcousticProcessor.Transform(spectrum, false);
    double power = 0;
    for (int k = 1; k < spectrum.Length / 2; k++)
    {
        double f = k * 44100d / spectrum.Length;
        if (f is > 2400 and < 5200 && Math.Abs(f - Math.Round(f / 200) * 200) > 45)
            power += spectrum[k].Magnitude * spectrum[k].Magnitude;
    }
    return power;
}
Check("Air adds inharmonic band energy", Noise(airy) > Noise(source) * 10, $"{Noise(source):G4}->{Noise(airy):G4}");
Check("Air is deterministic", airy.SequenceEqual(AceAcousticProcessor.Process(source, 44100, Controls(air: 1))));

// A dark vowel can have almost no power in the old analysis band. Aspiration
// must still be audible relative to the whole voice, rather than multiplying
// an effectively empty high-frequency band.
var dark = Enumerable.Range(0, 88200).Select(i => (float)(.08 *
    (Math.Sin(2 * Math.PI * 200 * i / 44100d) + .2 * Math.Sin(2 * Math.PI * 400 * i / 44100d)))).ToArray();
double ResidualRatio(float[] input, float[] processed)
{
    int first = 8192, length = 22050;
    double xx = 0, xy = 0;
    for (int i = first; i < first + length; i++) { xx += input[i] * input[i]; xy += input[i] * processed[i]; }
    double gain = xy / xx, residual = 0;
    for (int i = first; i < first + length; i++) residual += Math.Pow(processed[i] - gain * input[i], 2);
    return Math.Sqrt(residual / xx);
}
double previousAir = 0;
foreach (double amount in new[] { .2, .5, 1.0 })
{
    var darkAir = AceAcousticProcessor.Process(dark, 44100, Controls(air: amount));
    double ratio = ResidualRatio(dark, darkAir);
    Check($"Air grows on dark vowel at {amount}", ratio > previousAir + .02, ratio.ToString("F4"));
    Check($"Air retains level on dark vowel at {amount}", Math.Abs(20 * Math.Log10(Rms(darkAir) / Rms(dark))) < 1);
    previousAir = ratio;
}
Check("Air has useful full-scale aspiration", previousAir > .25, previousAir.ToString("F4"));
var random = new Random(42);
var unvoiced = Enumerable.Range(0, 22050).Select(_ => (float)((random.NextDouble() - .5) * .1)).ToArray();
Check("unvoiced consonant noise remains exact", unvoiced.SequenceEqual(
    AceAcousticProcessor.Process(unvoiced, 44100, Controls(air: 1, tension: 1, falsetto: 1))));
foreach (int rate in new[] { 16000, 48000 })
{
    var atRate = Fixture(rate);
    var processed = AceAcousticProcessor.Process(atRate, rate, Controls(tension: -.8, falsetto: .8));
    double estimate = AceAcousticProcessor.EstimatePitch(processed.Skip(rate / 2).Take(2048).Select(v => (double)v).ToArray(), rate, out _);
    Check($"pitch preserved at {rate} Hz", Math.Abs(estimate - 200) < 2 && processed.All(float.IsFinite));
}

// Test reduction on a voiced input with reproducible broadband aspiration.
var noisy = source.Select(v => v + (float)((random.NextDouble() - .5) * .002)).ToArray();
var dry = AceAcousticProcessor.Process(noisy, 44100, Controls(air: -1));
Check("Air reduction lowers inharmonic energy", Noise(dry) < Noise(noisy) * .8, $"{Noise(noisy):G4}->{Noise(dry):G4}");

var local = new AceAcousticCurve([new(0, 0), new(.2, 0), new(.3, 1), new(.6, 1), new(.7, 0)]);
var localized = AceAcousticProcessor.Process(source, 44100, new(Constant(0), Constant(0), local, Constant(0)));
Check("no effect on later notes", localized.Skip(44100).SequenceEqual(source.Skip(44100)));
Check("neutral boundary has no STFT spill", localized.Skip(30870).SequenceEqual(source.Skip(30870)));
Check("local source change is audible", !localized.Take(30000).SequenceEqual(source.Take(30000)));
var localAir = AceAcousticProcessor.Process(source, 44100, new(Constant(0), local, Constant(0), Constant(0)));
Check("aspiration stops at neutral boundary", localAir.Skip(30870).SequenceEqual(source.Skip(30870)));
var combined = AceAcousticProcessor.Process(source, 44100, Controls(air: .8, tension: .6, falsetto: .6));
Check("combined source controls retain finite level", combined.All(float.IsFinite) &&
    Math.Abs(20 * Math.Log10(Rms(combined) / Rms(source))) < 1);
var cancelled = new CancellationToken(true);
try { AceAcousticProcessor.Process(source, 44100, Controls(tension: 1), cancelled); Check("cancellation", false); }
catch (OperationCanceledException) { Check("cancellation", true); }
try { AceAcousticProcessor.Process([float.NaN], 44100, Controls()); Check("reject NaN", false); }
catch (ArgumentException) { Check("reject NaN", true); }
try { _ = new AceAcousticCurve([new(1, 0), new(0, 1)]); Check("reject unsorted curves", false); }
catch (ArgumentException) { Check("reject unsorted curves", true); }

string directory = Path.Combine(Path.GetTempPath(), "VOCALOIDPatcher", "AceAcousticTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
string input = Path.Combine(directory, "input.wav"), output = Path.Combine(directory, "output.wav");
using (var writer = new BinaryWriter(File.Create(input)))
{
    writer.Write(0x46464952); writer.Write(36 + source.Length * 4); writer.Write(0x45564157);
    writer.Write(0x20746d66); writer.Write(16); writer.Write((short)3); writer.Write((short)1);
    writer.Write(44100); writer.Write(44100 * 4); writer.Write((short)4); writer.Write((short)32);
    writer.Write(0x61746164); writer.Write(source.Length * 4);
    foreach (float sample in source) writer.Write(sample);
}
byte[] original = File.ReadAllBytes(input);
AceAcousticWave.Create(input, output, Controls());
Check("neutral WAVE is byte exact", original.SequenceEqual(File.ReadAllBytes(output)));
File.Delete(output);
AceAcousticWave.Create(input, output, Controls(tension: -.5, falsetto: .5));
Check("WAVE processing preserves length", new FileInfo(input).Length == new FileInfo(output).Length);
string hostOutput = Path.Combine(directory, "host.wav");
AceAcousticWave.Create(input, hostOutput, Controls(energy: 1), compatiblePcm16: true);
using (var reader = new BinaryReader(File.OpenRead(hostOutput)))
{
    reader.BaseStream.Position = 20;
    Check("host WAVE uses PCM16", reader.ReadUInt16() == 1 && new FileInfo(hostOutput).Length == 44 + source.Length * 2);
}
Check("source file remains unchanged", original.SequenceEqual(File.ReadAllBytes(input)));
try { AceAcousticWave.Create(input, input, Controls(energy: 1)); Check("source overwrite rejected", false); }
catch (ArgumentException) { Check("source overwrite rejected", true); }
string bad = Path.Combine(directory, "bad.wav");
File.WriteAllBytes(bad, original[..100]);
try { AceAcousticWave.Create(bad, Path.Combine(directory, "bad-out.wav"), Controls(energy: 1)); Check("truncated RIFF rejected", false); }
catch (InvalidDataException) { Check("truncated RIFF rejected", true); }
Console.WriteLine($"Artifacts: {directory}");
Console.WriteLine($"Failures: {failures}");
return failures == 0 ? 0 : 1;
