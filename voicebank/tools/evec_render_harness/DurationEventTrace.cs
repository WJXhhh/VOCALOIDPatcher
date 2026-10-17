using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VOCALOIDPatcher.ConsonantTiming;

internal static partial class Program
{
    private sealed unsafe class DurationEventTrace:IDisposable
    {
        private static DurationEventTrace? _active;
        private readonly nint _table=LocalTimingNative.Dse+0x5066e8;
        private readonly nint _original;
        private Exception? _error;
        internal List<object> Events {get;}=new();
        internal DurationEventTrace()
        {
            if(_active!=null)throw new InvalidOperationException("Event observer already active.");
            _original=Marshal.ReadIntPtr(_table,8*8);_active=this;
            Set((nint)(delegate* unmanaged<nint,nint,int>)&Add);
        }
        internal void Reset(){Events.Clear();_error=null;}
        internal void Check(){if(_error!=null)throw new InvalidOperationException("Event trace failed.",_error);}
        [UnmanagedCallersOnly]
        private static int Add(nint engine,nint input)
        {
            var owner=_active!;
            try
            {
                int count=Marshal.ReadInt32(input,16);
                if(count is <0 or >100000 || owner.Events.Count>256)throw new InvalidOperationException("Event observer bound exceeded.");
                string Values(int offset)
                {
                    nint data=Marshal.ReadIntPtr(input,offset);
                    return data==0?"null":Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>((void*)data,count*4)));
                }
                owner.Events.Add(new{kind=Marshal.ReadInt32(input),begin=Marshal.ReadInt32(input,4),
                    end=Marshal.ReadInt32(input,8),flags=Marshal.ReadInt32(input,12),count,primary=Values(24),secondary=Values(32)});
            }
            catch(Exception error){owner._error??=error;}
            return ((delegate* unmanaged<nint,nint,int>)owner._original)(engine,input);
        }
        private void Set(nint value)
        {
            nint slot=_table+8*8;
            if(!VirtualProtect(slot,8,0x40,out uint protection))throw new InvalidOperationException("Cannot observe native events.");
            Marshal.WriteIntPtr(slot,value);VirtualProtect(slot,8,protection,out _);
        }
        public void Dispose(){Set(_original);_active=null;}
        [DllImport("kernel32")]private static extern bool VirtualProtect(nint address,nuint size,uint protection,out uint previous);
    }
}
