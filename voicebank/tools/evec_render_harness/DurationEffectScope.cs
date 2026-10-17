using System.Runtime.InteropServices;

internal static partial class Program
{
    // The native database owns both immutable timbre tables and five mutable
    // last-voiced fields. Only the latter belong to an individual render.
    // This serialized-process probe redirects the three reviewed lookup callers
    // to a 64-byte header with shared, read-only table references. No native
    // bank, PCM cache, source material or previously rendered audio is replaced.
    private sealed unsafe class DurationEffectScope : IDisposable
    {
        private static DurationEffectScope? _active;
        private readonly DurationPostCallHook _lookup;
        private readonly Dictionary<nint, nint> _headers = new();
        private readonly Dictionary<nint, byte[]> _originals = new();
        private Exception? _error;
        internal int Copies => _headers.Count;
        internal DurationEffectScope()
        {
            _active = this;
            _lookup = new(0x182220, [0x48,0x89,0x5c,0x24,0x08,0x48,0x89,0x74,0x24,0x10,0x48,0x89,0x7c,0x24,0x18],
                (nint)(delegate* unmanaged<nint, void>)&Lookup);
        }
        internal void Reset()
        {
            foreach (nint header in _headers.Values) Marshal.FreeHGlobal(header);
            _headers.Clear(); _originals.Clear(); _error = null;
        }
        internal void Check()
        {
            if (_error != null) throw new InvalidOperationException("Render timbre scope failed.", _error);
            foreach (var item in _originals)
                if (!new ReadOnlySpan<byte>((void*)item.Key, 0x14).SequenceEqual(item.Value))
                    throw new InvalidOperationException("Render modified shared timbre history.");
        }
        [UnmanagedCallersOnly]
        private static void Lookup(nint context)
        {
            var owner = _active!;
            try
            {
                nint original = Marshal.ReadIntPtr(context, 0x88);
                if (original == 0) return;
                if (!owner._headers.TryGetValue(original, out nint header))
                {
                    if (owner._headers.Count >= 64) throw new InvalidOperationException("Timbre scope exceeds bound.");
                    header = Marshal.AllocHGlobal(0x40);
                    new ReadOnlySpan<byte>((void*)original, 0x40).CopyTo(new Span<byte>((void*)header, 0x40));
                    owner._originals.Add(original, new ReadOnlySpan<byte>((void*)original, 0x14).ToArray());
                    // Exact constructor 0x1C4240 values, not the last render's
                    // header. +0x14 onward contains bank configuration.
                    new Span<byte>((void*)header, 0x14).Clear();
                    Marshal.WriteInt32(header, 8, unchecked((int)0xff7fffff));
                    Marshal.WriteInt32(header, 12, 0x461c4000);
                    owner._headers.Add(original, header);
                }
                Marshal.WriteIntPtr(context, 0x88, header);
            }
            catch (Exception error) { owner._error ??= error; }
        }
        public void Dispose() { _lookup.Dispose(); Reset(); _active = null; }
    }
}
