using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal static partial class Program
{
    private sealed unsafe class DurationFftTrace : IDisposable
    {
        internal sealed record Row(int Frame, int Count, int Mode, int Window, int Scale, int Kind, string Input, string Weights, string Output);
        private static DurationFftTrace? _active;
        private readonly DurationPostCallHook _hook;
        private Exception? _error;
        internal List<Row> Rows { get; } = new();
        internal DurationFftTrace()
        {
            _active = this;
            _hook = new(0x1492b0, [0x48,0x8b,0xc4,0x44,0x89,0x40,0x18,0x48,0x89,0x50,0x10,0x53,0x55,0x56,0x57],
                (nint)(delegate* unmanaged<nint, void>)&After);
        }
        internal void Reset() { Rows.Clear(); _error = null; }
        internal void Check() { if (_error != null) throw new InvalidOperationException("FFT trace failed.", _error); }
        private static string Hash(nint pointer, int count) => pointer == 0 ? "" : Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>((void*)pointer, count)));
        [UnmanagedCallersOnly]
        private static void After(nint context)
        {
            var owner = _active!;
            try
            {
                nint state = DurationAcousticTrace.CurrentState;
                if (state == 0) return;
                nint analysis = Marshal.ReadIntPtr(context);
                if (analysis != Marshal.ReadIntPtr(state, 0x1fc8)) return;
                int count = Marshal.ReadInt32(context, 16), bins = Marshal.ReadInt32(Marshal.ReadIntPtr(context, 56));
                if (count is < 0 or > 16384 || bins is < 0 or > 16384 || owner.Rows.Count >= 10000)
                    throw new InvalidOperationException("FFT trace exceeds bound.");
                nint output = Marshal.ReadIntPtr(context, 72);
                owner.Rows.Add(new(Marshal.ReadInt32(state, 0x24158), count, Marshal.ReadByte(context, 24),
                    Marshal.ReadInt32(context, 88), Marshal.ReadInt32(context, 80), Marshal.ReadByte(context, 64),
                    Hash(Marshal.ReadIntPtr(context, 8), count * 2), Hash(Marshal.ReadIntPtr(analysis, 8), count * 4),
                    output == 0 ? "" : Hash(Marshal.ReadIntPtr(output), (bins * 2 + 2) * 4)));
            }
            catch (Exception error) { owner._error ??= error; }
        }
        public void Dispose() { _hook.Dispose(); _active = null; }
    }
}
