using System.Runtime.InteropServices;
using VOCALOIDPatcher.ConsonantTiming;

internal static partial class Program
{
    private sealed record DurationFramePosition(int Frame,int Source,int SourcePositionBits,byte Flag0,byte Flag1);
    private static unsafe IEnumerable<DurationFramePosition> ReadDurationFramePositions(nint state,
        Dictionary<nint,int> ids,int first,int last)
    {
        nint schedule=Marshal.ReadIntPtr(state,0x1d568);
        int end=((delegate* unmanaged<nint,int>)(LocalTimingNative.Dse+0x18aba0))(schedule);
        if(end is <0 or >7183 || last-first is <0 or >4096) throw new InvalidOperationException("Unbounded source-frame trace.");
        var result=new List<DurationFramePosition>();
        for(int frame=Math.Max(3087,first);frame<Math.Min(end,last);frame++)
        {
            nint entry=((delegate* unmanaged<nint,int,nint>)(LocalTimingNative.Dse+0x18b260))(schedule,frame);
            nint row=Marshal.ReadIntPtr(entry);
            if(row==0 || Marshal.ReadInt32(row,0x11c)<0) continue;
            nint material=Marshal.ReadIntPtr(row,0x20);
            if(!ids.TryGetValue(material,out int id)) ids.Add(material,id=ids.Count);
            result.Add(new(frame,id,Marshal.ReadInt32(entry,8),Marshal.ReadByte(entry,12),Marshal.ReadByte(entry,13)));
        }
        return result;
    }
}
