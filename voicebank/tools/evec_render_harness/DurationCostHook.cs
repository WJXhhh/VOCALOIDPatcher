using System.Runtime.InteropServices;
using VOCALOIDPatcher.ConsonantTiming;

internal static partial class Program
{
    // Replace only the stretch/compression cost consumed by the joint selector.
    // Real allocation, acoustic score frame addresses, gaps and NOTE times stay
    // native. A copied work object is used to calculate the virtual-time cost.
    private sealed unsafe class DurationCostHook : IDisposable
    {
        internal sealed record Call(int Start, int End, int First, int Last, int[] Sources,
            int[] Stages, float AcousticCost, float DurationCost);
        private static DurationCostHook? _active;
        private readonly DurationPostCallHook _cost;
        private const int WorkSize = 0x1c50;
        private readonly nint _scratch = Marshal.AllocHGlobal(WorkSize);
        private readonly nint _saved = Marshal.AllocHGlobal(WorkSize);
        private readonly Dictionary<nint, int> _ids;
        private Exception? _error;
        private double? _scale;
        internal float Weight { get; } = float.TryParse(Environment.GetEnvironmentVariable("VOCALOID_DURATION_WEIGHT"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float weight) && weight is >= 1 and <= 128 ? weight : 1;
        internal List<Call> Calls { get; } = new();
        internal int Allocations { get; private set; }
        internal int ReplacedCosts { get; private set; }

        internal DurationCostHook(Dictionary<nint, int> ids)
        {
            _ids = ids;
            if (_active != null) throw new InvalidOperationException("Joint duration probe already active.");
            _active = this;
            _cost = new(0x19e730, [0x48, 0x8b, 0xc4, 0x44, 0x89, 0x48, 0x20, 0x44, 0x89, 0x40, 0x18, 0x48, 0x89, 0x50, 0x10],
                (nint)(delegate* unmanaged<nint,void>)&Cost);
        }
        internal void Reset(double? scale)
        {
            _scale = scale; Calls.Clear(); Allocations = ReplacedCosts = 0; _error = null;
        }
        internal void Check() { if (_error != null) throw new InvalidOperationException("Joint selection duration callback failed.", _error); }
        private int Id(nint value)
        {
            if (value == 0) return -1;
            if (!_ids.TryGetValue(value, out int id)) _ids.Add(value, id = _ids.Count);
            return id;
        }

        [UnmanagedCallersOnly]
        private static void Cost(nint context)
        {
            var owner = _active!;
            try
            {
                nint state=Marshal.ReadIntPtr(context),output=Marshal.ReadIntPtr(context,8),work=Marshal.ReadIntPtr(context,32);
                int start=Marshal.ReadInt32(context,16),end=Marshal.ReadInt32(context,24),
                    first=Marshal.ReadInt32(context,56),last=Marshal.ReadInt32(context,64),threshold=Marshal.ReadInt32(context,88);
                byte flag=Marshal.ReadByte(context,72),capped=Marshal.ReadByte(context,80);
                int count = Marshal.ReadInt32(work, 12);
                if (count is < 0 or > 32 || owner.Calls.Count >= 4096) throw new InvalidOperationException("Joint candidate trace exceeds bound.");
                if (owner._scale.HasValue && end > start && first < last && first >= 0 && last <= 2 * count &&
                    *(float*)output < float.MaxValue && *(float*)(output + 4) < float.MaxValue)
                {
                    int duration = end - start;
                    int virtualFrames = Math.Clamp((int)Math.Round(duration * owner._scale.Value), 1, 4096);
                    if (virtualFrames != duration)
                    {
                        new ReadOnlySpan<byte>((void*)work, WorkSize).CopyTo(new Span<byte>((void*)owner._saved, WorkSize));
                        float actualCost = owner.AllocateCopy(state, duration, first, last, threshold, flag, capped);
                        float virtualCost = owner.AllocateCopy(state, virtualFrames, first, last, threshold, flag, capped);
                        if (!new ReadOnlySpan<byte>((void*)work, WorkSize).SequenceEqual(new ReadOnlySpan<byte>((void*)owner._saved, WorkSize)))
                            throw new InvalidOperationException("Virtual scoring modified real timing work.");
                        float score = *(float*)(output + 4) - actualCost + virtualCost * owner.Weight;
                        if (!float.IsFinite(score)) throw new InvalidOperationException("Non-finite virtual score.");
                        *(float*)(output + 4) = score;
                        owner.ReplacedCosts++;
                    }
                }
                var sources = new int[count]; var stages = new int[count];
                for (int i = 0; i < count; i++)
                {
                    sources[i] = owner.Id(Marshal.ReadIntPtr(work, 0x810 + i * 0x68));
                    stages[i] = Marshal.ReadInt32(work, 0x818 + i * 0x68);
                }
                owner.Calls.Add(new(start, end, first, last, sources, stages, *(float*)output, *(float*)(output + 4)));
            }
            catch (Exception error) { owner._error ??= error; }
        }

        private float AllocateCopy(nint state, int duration, int first, int last,
            int threshold, byte flag, byte capped)
        {
            var allocate = (delegate* unmanaged<nint, nint, int, nint, int, int, int, byte, byte, nint>)(LocalTimingNative.Dse + 0x19d650);
            new ReadOnlySpan<byte>((void*)_saved, WorkSize).CopyTo(new Span<byte>((void*)_scratch, WorkSize));
            int* temporary = stackalloc int[2]; temporary[0] = temporary[1] = 0;
            allocate(state, (nint)temporary, duration, _scratch, first, last, threshold, flag, capped);
            Allocations++;
            float cost = *(float*)(temporary + 1);
            if (!float.IsFinite(cost) || cost < 0) throw new InvalidOperationException("Invalid virtual duration cost.");
            return cost;
        }

        public void Dispose()
        {
            _cost.Dispose(); _active = null;
            Marshal.FreeHGlobal(_scratch); Marshal.FreeHGlobal(_saved);
        }
    }

    // Reviewed instruction prefixes only, with explicit branch relocation for
    // the silence wrapper. Used exclusively by the disposable harness, never
    // injected into the Editor.
    private sealed unsafe class ResearchDurationDetour : IDisposable
    {
        private readonly nint _entry;
        private readonly byte[] _original;
        internal nint Trampoline { get; private set; }
        internal ResearchDurationDetour(int rva, byte[] original, nint callback)
        {
            LocalTimingNative.VerifyImages(); _entry = LocalTimingNative.Dse + rva; _original = original;
            if ((original.Length is not (15 or 16 or 21) && !(rva == 0x141f50 && original.Length == 17)) ||
                !new ReadOnlySpan<byte>((void*)_entry, original.Length).SequenceEqual(original))
                throw new NotSupportedException($"Unreviewed research entry: {rva:X}.");
            Trampoline = VirtualAlloc(0, 64, 0x3000, 0x40);
            if (Trampoline == 0) throw new InvalidOperationException("Research trampoline allocation failed.");
            if (rva == 0x1a7310)
            {
                // Reviewed SUB/TEST/JLE/MOV entry. Expand JLE into inverse JG
                // over an absolute jump; a copied rel32 would target the wrong
                // address when VirtualAlloc is outside the image's 2 GiB range.
                original.AsSpan(0, 6).CopyTo(new Span<byte>((void*)Trampoline, 6));
                Marshal.WriteByte(Trampoline, 6, 0x7f); Marshal.WriteByte(Trampoline, 7, 14);
                Jump(Trampoline + 8, _entry + 0x187, 14);
                original.AsSpan(12, 3).CopyTo(new Span<byte>((void*)(Trampoline + 22), 3));
                Jump(Trampoline + 25, _entry + original.Length, 14);
            }
            else
            {
                original.CopyTo(new Span<byte>((void*)Trampoline, original.Length));
                Jump(Trampoline + original.Length, _entry + original.Length, 14);
            }
            FlushInstructionCache(GetCurrentProcess(), Trampoline, 64);
            if (!VirtualProtect(_entry, (nuint)original.Length, 0x40, out uint protection)) throw new InvalidOperationException("Research detour protection failed.");
            Jump(_entry, callback, original.Length);
            if (!VirtualProtect(_entry, (nuint)original.Length, protection, out _)) throw new InvalidOperationException("Research protection restore failed.");
            FlushInstructionCache(GetCurrentProcess(), _entry, (nuint)original.Length);
        }
        private static void Jump(nint location, nint target, int length)
        {
            var bytes = new Span<byte>((void*)location, length); bytes.Fill(0x90);
            bytes[0] = 0xff; bytes[1] = 0x25; bytes.Slice(2, 4).Clear();
            BitConverter.TryWriteBytes(bytes.Slice(6, 8), (long)target);
        }
        public void Dispose()
        {
            if (Trampoline == 0) return;
            if (!VirtualProtect(_entry, (nuint)_original.Length, 0x40, out uint protection)) throw new InvalidOperationException("Research detach protection failed.");
            Marshal.Copy(_original, 0, _entry, _original.Length);
            if (!VirtualProtect(_entry, (nuint)_original.Length, protection, out _)) throw new InvalidOperationException("Research detach restore failed.");
            FlushInstructionCache(GetCurrentProcess(), _entry, (nuint)_original.Length);
            VirtualFree(Trampoline, 0, 0x8000); Trampoline = 0;
        }
        [DllImport("kernel32")] private static extern nint VirtualAlloc(nint address, nuint size, uint allocation, uint protection);
        [DllImport("kernel32")] private static extern bool VirtualFree(nint address, nuint size, uint kind);
        [DllImport("kernel32")] private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint previous);
        [DllImport("kernel32")] private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
        [DllImport("kernel32")] private static extern nint GetCurrentProcess();
    }
}
