using System;
using System.Runtime.InteropServices;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

// Native VSM score records are 72 bytes, including two phoneme flags before
// the string pointers. The managed VSMScore shape omits those flags.
internal sealed unsafe class ConsonantTimingScoreAdapter : IDisposable
{
    private nint _records;
    private readonly int _count;
    private nint _object;
    private nint _table;
    private GCHandle _self;

    internal ConsonantTimingScoreAdapter(byte[] records)
    {
        if (records.Length == 0 || records.Length % LocalTimingNative.Profile.ScoreStride != 0 || records.Length > 4096 * LocalTimingNative.Profile.ScoreStride)
            throw new ArgumentException("Invalid bounded native score.");
        _count = records.Length / LocalTimingNative.Profile.ScoreStride;
        try
        {
            _records = Marshal.AllocHGlobal(records.Length);
            Marshal.Copy(records, 0, _records, records.Length);
            _self = GCHandle.Alloc(this);
            _table = Marshal.AllocHGlobal(16);
            Marshal.WriteIntPtr(_table, (nint)(delegate* unmanaged<nint, long>)&Count);
            Marshal.WriteIntPtr(_table, 8, (nint)(delegate* unmanaged<nint, long, nint>)&At);
            _object = Marshal.AllocHGlobal(16);
            Marshal.WriteIntPtr(_object, _table);
            Marshal.WriteIntPtr(_object, 8, GCHandle.ToIntPtr(_self));
        }
        catch { Dispose(); throw; }
    }

    internal void Map(WIVSMMidiPart privatePart, int[] tempoPairs)
    {
        ObjectDisposedException.ThrowIf(_object == 0, this);
        if (tempoPairs.Length == 0 || tempoPairs.Length % 2 != 0)
            throw new ArgumentException("Invalid tempo map.");
        fixed (int* tempo = tempoPairs)
        {
            nint* map = stackalloc nint[3];
            map[0] = (nint)tempo;
            map[1] = (nint)(tempo + tempoPairs.Length);
            map[2] = map[1];
            nint provider = _object;
            nint part = (nint)privatePart;
            byte previous = Marshal.ReadByte(part, LocalTimingNative.Profile.PartMappingFlag);
            try
            {
                Marshal.WriteByte(part, LocalTimingNative.Profile.PartMappingFlag, 1);
                ((delegate* unmanaged<nint, nint, nint, void>)(LocalTimingNative.Vsm + LocalTimingNative.Profile.VsmMapScore))(
                    part, (nint)(&provider), (nint)map);
            }
            finally { Marshal.WriteByte(part, LocalTimingNative.Profile.PartMappingFlag, previous); }
        }
    }

    [UnmanagedCallersOnly]
    private static long Count(nint self) =>
        ((ConsonantTimingScoreAdapter)GCHandle.FromIntPtr(Marshal.ReadIntPtr(self, 8)).Target!)._count;

    [UnmanagedCallersOnly]
    private static nint At(nint self, long index)
    {
        var adapter = (ConsonantTimingScoreAdapter)GCHandle.FromIntPtr(Marshal.ReadIntPtr(self, 8)).Target!;
        return index >= 0 && index < adapter._count ? adapter._records + (nint)(index * LocalTimingNative.Profile.ScoreStride) : 0;
    }

    public void Dispose()
    {
        Marshal.FreeHGlobal(_object);
        Marshal.FreeHGlobal(_table);
        if (_self.IsAllocated) _self.Free();
        Marshal.FreeHGlobal(_records);
        _object = _table = _records = 0;
    }
}
