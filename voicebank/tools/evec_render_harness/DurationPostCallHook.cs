using System.Runtime.InteropServices;

internal static partial class Program
{
    // Research only, serialized DSE worker. Calls the native body with the exact
    // original stack, then preserves its GP/SSE registers and MXCSR across
    // managed observation. Internal LTCG calls may have a narrower
    // clobber contract than an exported Win64 function.
    private sealed unsafe class DurationPostCallHook : IDisposable
    {
        private readonly ResearchDurationDetour _detour;
        private readonly nint _memory;
        internal nint Context => _memory+0xc00;
        internal nint NativeBody => _detour.Trampoline;
        private readonly List<byte> _code=new();
        private int _offset;
        internal DurationPostCallHook(int rva,byte[] prefix,nint callback,nint before=0)
        {
            _memory=VirtualAlloc(0,4096,0x3000,0x40);
            if(_memory==0) throw new InvalidOperationException("Cannot allocate register-preserving bridge.");
            new Span<byte>((void*)_memory,4096).Clear();
            // Entry: save the two scratch registers before capturing the native
            // input arguments and replacing only the return address.
            Rip([0x48,0x89,0x05],_memory+0xd00);
            Rip([0x4c,0x89,0x15],_memory+0xd08);
            Emit(0x48,0xb8);Q(Context);
            Emit(0x48,0x89,0x08);                 // context[0] = RCX
            Emit(0x48,0x89,0x50,0x08);            // context[1] = RDX
            Emit(0x4c,0x89,0x40,0x10);            // context[2] = R8
            Emit(0x4c,0x89,0x48,0x18);            // context[3] = R9
            for(int index=4;index<14;index++)
            {
                Emit(0x4c,0x8b,0x94,0x24);D(8*(index+1));
                Emit(0x4c,0x89,0x90);D(index*8);
            }
            Emit(0x4c,0x8b,0x14,0x24);            // native caller return
            Emit(0x4c,0x89,0x90);D(0x80);
            Emit(0x49,0xba);Q(_memory+0x400);
            Emit(0x4c,0x89,0x14,0x24);
            Rip([0x4c,0x8b,0x15],_memory+0xd08);
            Rip([0x48,0x8b,0x05],_memory+0xd00);
            if(before!=0)
            {
                InvokeManaged(before,0x138);
                Rip([0x48,0x89,0x05],_memory+0xd00);
                Emit(0x48,0xb8);Q(Context);
                Emit(0x48,0x8b,0x08,0x48,0x8b,0x50,0x08,0x4c,0x8b,0x40,0x10,0x4c,0x8b,0x48,0x18);
                Rip([0x48,0x8b,0x05],_memory+0xd00);
            }
            Rip([0xff,0x25],_memory+0xd10);        // tail-enter original body
            if(_code.Count>0x400)throw new InvalidOperationException("Entry bridge overlaps post-call code.");
            Commit();

            _offset=0x400;_code.Clear();
            Rip([0x48,0x89,0x05],Context+0x88);    // native return, replaceable by observer
            InvokeManaged(callback,0x130);
            Rip([0x48,0x8b,0x05],Context+0x88);
            Rip([0xff,0x25],Context+0x80);
            Commit();
            FlushInstructionCache(GetCurrentProcess(),_memory,4096);
            _detour=new(rva,prefix,_memory);
            Marshal.WriteIntPtr(_memory+0xd10,_detour.Trampoline);
        }
        private void InvokeManaged(nint callback,int allocation)
        {
            Emit(0x9c,0x50,0x51,0x52,0x41,0x50,0x41,0x51,0x41,0x52,0x41,0x53);
            Emit(0x48,0x81,0xec);D(allocation);    // shadow + XMM0..15 + MXCSR
            for(int register=0;register<16;register++) Simd(register,false);
            Emit(0x0f,0xae,0x9c,0x24);D(0x120);  // STMXCSR
            Emit(0x48,0xb9);Q(Context);
            Emit(0x48,0xb8);Q(callback);
            Emit(0xff,0xd0);
            Emit(0x0f,0xae,0x94,0x24);D(0x120);  // LDMXCSR
            for(int register=0;register<16;register++) Simd(register,true);
            Emit(0x48,0x81,0xc4);D(allocation);
            Emit(0x41,0x5b,0x41,0x5a,0x41,0x59,0x41,0x58,0x5a,0x59,0x58,0x9d);
        }
        private void Emit(params byte[] bytes)=>_code.AddRange(bytes);
        private void D(int value)=>_code.AddRange(BitConverter.GetBytes(value));
        private void Q(nint value)=>_code.AddRange(BitConverter.GetBytes((long)value));
        private void Rip(byte[] instruction,nint address)
        {
            Emit(instruction);D(checked((int)(address-(_memory+_offset+_code.Count+4))));
        }
        private void Simd(int register,bool load)
        {
            Emit(0xf3);if(register>=8)Emit(0x44);
            Emit(0x0f,load?(byte)0x6f:(byte)0x7f,(byte)(0x84+((register&7)<<3)),0x24);
            D(0x20+16*register);
        }
        private void Commit()
        {
            if(_offset+_code.Count>0xc00) throw new InvalidOperationException("Bridge code exceeds bound.");
            Marshal.Copy(_code.ToArray(),0,_memory+_offset,_code.Count);
        }
        public void Dispose(){_detour.Dispose();VirtualFree(_memory,0,0x8000);}
        [DllImport("kernel32")] private static extern nint VirtualAlloc(nint address,nuint size,uint allocation,uint protection);
        [DllImport("kernel32")] private static extern bool VirtualFree(nint address,nuint size,uint kind);
        [DllImport("kernel32")] private static extern bool FlushInstructionCache(nint process,nint address,nuint size);
        [DllImport("kernel32")] private static extern nint GetCurrentProcess();
    }
}
