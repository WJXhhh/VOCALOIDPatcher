using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VOCALOIDPatcher.ConsonantTiming;

internal static partial class Program
{
    // Research contract for native allocation, junction shaping and pre-roll
    // insertion, before sample/frame mapping. Selection keeps its material
    // descriptors; real half-segment allocations and gaps use the time plan.
    private sealed unsafe class DurationTimingContract : IDisposable
    {
        internal sealed record Row(string Key, int[] Halves, int Kind, int Gap, string Description);
        private static DurationTimingContract? _active;
        private readonly DurationPostCallHook _shape;
        private readonly DurationPostCallHook _allocate;
        private readonly DurationPostCallHook _silence;
        private Exception? _error;
        private bool _capture = true;
        private int _cursor;
        internal List<Row> Plan { get; } = new();
        internal int Changed { get; private set; }
        internal DurationTimingContract()
        {
            _active = this;
            _shape = new(0x1b3d30, [0x48,0x89,0x5c,0x24,0x18,0x55,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57],
                (nint)(delegate* unmanaged<nint, void>)&Shape);
            _allocate = new(0x19d650, [0x48,0x8b,0xc4,0x4c,0x89,0x48,0x20,0x44,0x89,0x40,0x18,0x48,0x89,0x50,0x10],
                (nint)(delegate* unmanaged<nint, void>)&Allocate);
            _silence = new(0x1a7310, [0x48,0x83,0xec,0x48,0x85,0xd2,0x0f,0x8e,0x7b,0x01,0,0,0x48,0x8b,0x09],
                (nint)(delegate* unmanaged<nint, void>)&SilenceAfter, (nint)(delegate* unmanaged<nint, void>)&Silence);
        }
        internal void Capture() { _capture = true; Plan.Clear(); Reset(); }
        internal void Enforce() { _capture = false; Reset(); }
        private void Reset() { _error = null; _cursor = Changed = 0; }
        internal void Check()
        {
            if (_error != null) throw new InvalidOperationException("Real timing contract failed.", _error);
            if (!_capture && _cursor != Plan.Count) throw new InvalidOperationException("Native shaping topology changed.");
        }
        [UnmanagedCallersOnly]
        private static void Shape(nint context) => Observe(context, 0);
        [UnmanagedCallersOnly] private static void SilenceAfter(nint context) { }
        [UnmanagedCallersOnly]
        private static void Silence(nint context)
        {
            var owner = _active!;
            long caller = Marshal.ReadIntPtr(context, 0x80) - LocalTimingNative.Dse;
            if (caller is not (0x1a45c1 or 0x1a4cb0 or 0x1a546a or 0x1a5b17 or 0x1a72a1)) return;
            try
            {
                string key = $"sil:{caller:X}";
                int duration = Marshal.ReadInt32(context, 8);
                if (owner._cursor >= 512) throw new InvalidOperationException("Silence contract exceeds bound.");
                if (owner._capture) owner.Plan.Add(new(key, [], 2, duration, key));
                else
                {
                    if (owner._cursor >= owner.Plan.Count || owner.Plan[owner._cursor].Key != key)
                        throw new InvalidOperationException($"Native silence sequence changed at {owner._cursor}.");
                    int target = owner.Plan[owner._cursor].Gap;
                    if (target != duration) { Marshal.WriteInt32(context, 8, target); owner.Changed++; }
                }
                owner._cursor++;
            }
            catch (Exception error) { owner._error ??= error; }
        }
        [UnmanagedCallersOnly]
        private static void Allocate(nint context)
        {
            long caller = Marshal.ReadIntPtr(context, 0x80) - LocalTimingNative.Dse;
            if (caller is 0x1a47e8 or 0x1a4a1f or 0x1a5ddb or 0x1a5eb6) Observe(context, 1);
        }
        private static void Observe(nint context, int kind)
        {
            var owner = _active!;
            try
            {
                nint work = Marshal.ReadIntPtr(context, kind == 0 ? 8 : 24);
                nint output = kind == 0 ? 0 : Marshal.ReadIntPtr(context, 8);
                int count = Marshal.ReadInt32(work, 12);
                if (count is < 0 or > 32 || owner._cursor >= 512) throw new InvalidOperationException("Shaping contract exceeds bound.");
                using var key = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                string description = $"{kind}:{Marshal.ReadInt32(work,4)}:{Marshal.ReadByte(work,8)}:{count}:{Marshal.ReadByte(work,0x1b15)}";
                key.AppendData(BitConverter.GetBytes(kind));
                key.AppendData(new ReadOnlySpan<byte>((void*)(work + 4), 5));
                key.AppendData(BitConverter.GetBytes(count));
                for (int i = 0; i < count; i++)
                    foreach (int names in new[] { 0x10, 0x210 })
                    {
                        var name = new ReadOnlySpan<byte>((void*)(work + names + i * 16), 16);
                        int end = name.IndexOf((byte)0);
                        if (end < 0) throw new InvalidOperationException("Unterminated shaping phone.");
                        key.AppendData(name[..(end + 1)]);
                        description += ":" + System.Text.Encoding.ASCII.GetString(name[..end]);
                    }
                key.AppendData(new ReadOnlySpan<byte>((void*)(work + 0x1b15), 1));
                string identity = Convert.ToHexString(key.GetHashAndReset());
                var halves = new Span<int>((void*)(work + 0x1510), count * 2);
                if (owner._capture) owner.Plan.Add(new(identity, halves.ToArray(), kind, output == 0 ? 0 : Marshal.ReadInt32(output), description));
                else
                {
                    if (owner._cursor >= owner.Plan.Count || owner.Plan[owner._cursor].Key != identity)
                        throw new InvalidOperationException($"Native shaping identity changed at {owner._cursor}: {description}; expected {owner.Plan.ElementAtOrDefault(owner._cursor)?.Description}.");
                    int[] target = owner.Plan[owner._cursor].Halves;
                    bool changed = !halves.SequenceEqual(target);
                    if (changed) target.CopyTo(halves);
                    if (output != 0)
                    {
                        int gap = owner.Plan[owner._cursor].Gap;
                        changed |= Marshal.ReadInt32(output) != gap;
                        Marshal.WriteInt32(output, gap);
                    }
                    if (changed) owner.Changed++;
                }
                owner._cursor++;
            }
            catch (Exception error) { owner._error ??= error; }
        }
        public void Dispose() { _silence.Dispose(); _allocate.Dispose(); _shape.Dispose(); _active = null; }
    }
}
