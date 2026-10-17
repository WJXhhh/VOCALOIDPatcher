using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal static partial class Program
{
    private sealed unsafe class DurationMixTrace : IDisposable
    {
        internal sealed record Voice(int State, string Header, string Pcm);
        internal sealed record Row(int Frame, string Analysis, string Mixed, Voice[] Voices);
        private static DurationMixTrace? _active;
        private readonly DurationPostCallHook _hook;
        private Exception? _error;
        internal List<Row> Rows { get; } = new();
        internal DurationMixTrace()
        {
            _active = this;
            _hook = new(0x171e10, [0x48,0x89,0x5c,0x24,0x10,0x48,0x89,0x74,0x24,0x18,0x57,0x48,0x83,0xec,0x50],
                (nint)(delegate* unmanaged<nint, void>)&Observe);
        }
        internal void Reset() { Rows.Clear(); _error = null; }
        internal void Check() { if (_error != null) throw new InvalidOperationException("Mix trace failed.", _error); }
        private static string Hash(nint pointer, int bytes) => Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>((void*)pointer, bytes)));
        [UnmanagedCallersOnly]
        private static void Observe(nint context)
        {
            var owner = _active!;
            try
            {
                nint state = Marshal.ReadIntPtr(context), output = Marshal.ReadIntPtr(context, 8);
                nint analysis = Marshal.ReadIntPtr(state, 0x1fc8);
                int count = Marshal.ReadInt32(analysis, 0x60);
                nint begin = Marshal.ReadIntPtr(state, 0x2b168), end = Marshal.ReadIntPtr(state, 0x2b170);
                int voices = checked((int)((end - begin) / 8));
                if (count is < 0 or > 8192 || voices is < 0 or > 128 || owner.Rows.Count > 10000)
                    throw new InvalidOperationException("Mix trace exceeds bound.");
                var items = new Voice[voices];
                for (int i = 0; i < voices; i++)
                {
                    nint item = Marshal.ReadIntPtr(begin, i * 8);
                    int stage = Marshal.ReadInt32(item, 0x54);
                    using var header = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    header.AppendData(new ReadOnlySpan<byte>((void*)(item + 0x18), 0x40));
                    header.AppendData(new ReadOnlySpan<byte>((void*)(item + 0x60), 0x40));
                    items[i] = new(stage, Convert.ToHexString(header.GetHashAndReset()), stage > 0 ? Hash(Marshal.ReadIntPtr(item, 0x58), count * 4) : "");
                }
                owner.Rows.Add(new(Marshal.ReadInt32(state, 0x24158), Hash(Marshal.ReadIntPtr(analysis, 0x3d0), count * 2), Hash(output, count * 2), items));
            }
            catch (Exception error) { owner._error ??= error; }
        }
        public void Dispose() { _hook.Dispose(); _active = null; }
    }
}
