using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static partial class Program
{
    // Frequency-domain contribution injection in a disposable DSE, before its
    // original IFFT/window/OLA/gain. Never writes a completed Part WAV.
    private sealed unsafe class BorrowSynthesisExperiment
    {
        internal sealed record Packet(int Frame, float[] Real, float[] Imag, float Gain);
        private readonly string _directory;
        private readonly Dictionary<string, Packet[]> _packets;
        private readonly Dictionary<string, int> _applied = new();
        internal string[] Modes => _packets.Keys.ToArray();
        internal int Applied(string mode) => _applied.GetValueOrDefault(mode);
        internal BorrowSynthesisExperiment(string directory)
        {
            _directory = directory;
            string? file = Environment.GetEnvironmentVariable("VOCALOID_BORROW_GRAIN_PACKETS");
            _packets = string.IsNullOrEmpty(file) ? new() : JsonSerializer.Deserialize<Dictionary<string, Packet[]>>(File.ReadAllText(file))!;
            Require(_packets.Count <= 24, "Too many research grain modes.");
            foreach (var pair in _packets)
                Require(pair.Key.StartsWith("7-grain-", StringComparison.Ordinal) || pair.Key.StartsWith("a-grain-", StringComparison.Ordinal), "Unreviewed grain mode name.");
        }
        internal void Visit(string mode, nint synth, nint frame, int time, float gain)
        {
            int bins = Marshal.ReadInt32(frame, 0x1a8), fft = Marshal.ReadInt32(synth, 0x64);
            Require(bins == 1025 && fft == 2048 && Marshal.ReadInt32(synth, 0x60) == 256, "Unreviewed contribution geometry.");
            nint real = Marshal.ReadIntPtr(frame, 0x1b0), imag = Marshal.ReadIntPtr(frame, 0x1b8);
            if (mode is "baseline" or "7-mid" or "a-mid")
            {
                var captured = new Packet(time, new ReadOnlySpan<float>((void*)real, bins).ToArray(),
                    new ReadOnlySpan<float>((void*)imag, bins).ToArray(), gain);
                File.WriteAllText(Path.Combine(_directory, mode + "-synth-" + time + ".json"), JsonSerializer.Serialize(captured));
            }
            string key = mode == "7-grain-identity-repeat" ? "7-grain-identity" : mode;
            if (_packets.TryGetValue(key, out var packets))
            {
                var packet = packets.Single(p => p.Frame == time);
                Require(packet.Real.Length == bins && packet.Imag.Length == bins && packet.Real.All(float.IsFinite) &&
                    packet.Imag.All(float.IsFinite) && BitConverter.SingleToInt32Bits(packet.Gain) == BitConverter.SingleToInt32Bits(gain), "Invalid grain packet/gain.");
                Marshal.Copy(packet.Real, 0, real, bins); Marshal.Copy(packet.Imag, 0, imag, bins);
                _applied[mode] = Applied(mode) + 1;
            }
            if (time == 3341)
            {
                nint dft = NativeLibrary.Load(Path.Combine(EditorDirectory, "DSE_DFT.dll"));
                var get = (delegate* unmanaged[Cdecl]<nint, int, float*, int>)NativeLibrary.GetExport(dft, "DftiGetValue");
                float scale = float.NaN;
                Require(get(Marshal.ReadIntPtr(synth, 0xa0), 5, &scale) == 0 && scale > 0 && float.IsFinite(scale), "Cannot verify native inverse scaling.");
                File.WriteAllText(Path.Combine(_directory, "inverse-scaling.json"), JsonSerializer.Serialize(new { scale }));
                NativeLibrary.Free(dft);
            }
        }
    }
}
