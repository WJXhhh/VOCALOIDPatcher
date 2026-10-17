using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VOCALOIDPatcher.ConsonantTiming;

internal static partial class Program
{
    private sealed unsafe class DurationAcousticTrace:IDisposable
    {
        internal sealed record Row(int Frame,int Source,int Index,string Hash,int Phase=0,
            string SpectrumHash="",ulong Flags=0,int Bins=0,string SourceSpectrum="",string Score="",int[]? Effect=null,string InputPcm="");
        private static DurationAcousticTrace? _active;
        internal static nint CurrentState;
        private readonly DurationPostCallHook _hook;
        private readonly DurationPostCallHook _layer;
        private readonly DurationPostCallHook _processed;
        private readonly DurationPostCallHook _interpolated;
        private readonly DurationPostCallHook _pitch;
        private readonly DurationPostCallHook _spectrum;
        private readonly DurationPostCallHook _unvoiced;
        private readonly DurationPostCallHook _noiseFilter;
        private readonly Dictionary<nint,int> _ids;
        private Exception? _error;
        internal List<Row> Rows {get;}=new();
        internal DurationAcousticTrace(Dictionary<nint,int> ids)
        {
            _ids=ids;_active=this;
            _hook=new(0x175dd0,[0x48,0x89,0x5c,0x24,0x18,0x55,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57],
                (nint)(delegate* unmanaged<nint,void>)&Loaded);
            _layer=new(0x175730,[0x48,0x89,0x5c,0x24,0x08,0x57,0x48,0x83,0xec,0x20,0x8b,0x82,0xb0,0x01,0,0],
                (nint)(delegate* unmanaged<nint,void>)&LayerLoaded);
            _processed=new(0x1757a0,[0x40,0x53,0x55,0x57,0x41,0x57,0x48,0x81,0xec,0xa8,0,0,0,0x49,0x8b,0xd9],
                (nint)(delegate* unmanaged<nint,void>)&Processed);
            _interpolated=new(0x16a870,[0x48,0x89,0x5c,0x24,0x18,0x55,0x56,0x57,0x41,0x54,0x41,0x57,0x48,0x83,0xec,0x60],
                (nint)(delegate* unmanaged<nint,void>)&Interpolated);
            _pitch=new(0x16fcb0,[0x48,0x8b,0xc4,0x48,0x89,0x58,0x08,0x4c,0x89,0x48,0x20,0x55,0x56,0x57,0x41,0x54],
                (nint)(delegate* unmanaged<nint,void>)&Pitch);
            _spectrum=new(0x16ce20,[0x48,0x8b,0xc4,0x55,0x53,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57],
                (nint)(delegate* unmanaged<nint,void>)&Spectrum);
            _unvoiced=new(0x16e830,[0x48,0x8b,0xc4,0x55,0x53,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57],
                (nint)(delegate* unmanaged<nint,void>)&Unvoiced);
            _noiseFilter=new(0x166d60,[0x48,0x89,0x5c,0x24,0x08,0x48,0x89,0x74,0x24,0x10,0x57,0x48,0x83,0xec,0x20],
                (nint)(delegate* unmanaged<nint,void>)&NoiseFilter);
        }
        internal void Reset(){Rows.Clear();_error=null;}
        internal void Check(){if(_error!=null)throw new InvalidOperationException("Source acoustic trace failed.",_error);}
        [UnmanagedCallersOnly]
        private static void Loaded(nint context)
            =>Trace(context,false);
        [UnmanagedCallersOnly]
        private static void LayerLoaded(nint context)
            =>Trace(context,true);
        [UnmanagedCallersOnly]
        private static void Processed(nint context)
            =>Trace(context,false,2);
        [UnmanagedCallersOnly] private static void Interpolated(nint context)=>Trace(context,false,3);
        [UnmanagedCallersOnly] private static void Pitch(nint context)=>Trace(context,false,4);
        [UnmanagedCallersOnly] private static void Spectrum(nint context)=>Trace(context,false,5);
        [UnmanagedCallersOnly] private static void Unvoiced(nint context)=>Trace(context,false,6);
        [UnmanagedCallersOnly] private static void NoiseFilter(nint context)=>Trace(context,false,7);
        private static void Trace(nint context,bool layer,int phase=0)
        {
            var owner=_active!;
            try
            {
                if(owner.Rows.Count>20000)throw new InvalidOperationException("Acoustic trace exceeds bound.");
                nint state=Marshal.ReadIntPtr(context),work=Marshal.ReadIntPtr(context,8),frame=Marshal.ReadIntPtr(context,phase>=5?8:phase>=3?16:layer?32:24);
                CurrentState=state;
                nint material=phase>=3?0:layer?work:Marshal.ReadIntPtr(work,0x20);
                if(!owner._ids.TryGetValue(material,out int id))owner._ids.Add(material,id=owner._ids.Count);
                using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach(int offset in new[]{0x148,0x150,0x1d0,0x1d8,0x270})
                    hash.AppendData(new ReadOnlySpan<byte>((void*)(frame+offset),8));
                int count=Marshal.ReadInt32(frame,0x158);
                if(count is <0 or >10000)throw new InvalidOperationException("Harmonic trace exceeds bound.");
                hash.AppendData(BitConverter.GetBytes(count));
                foreach(int offset in new[]{0x160,0x168,0x170})
                {
                    nint data=Marshal.ReadIntPtr(frame,offset);
                    if(data!=0 && count>0)hash.AppendData(new ReadOnlySpan<byte>((void*)data,count*4));
                }
                using var spectrumHash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int bins=Marshal.ReadInt32(frame,0x1a8);
                if(bins is <0 or >16384)throw new InvalidOperationException("Spectrum trace exceeds bound.");
                spectrumHash.AppendData(BitConverter.GetBytes(bins));
                foreach(int offset in new[]{0x1b0,0x1b8})
                {
                    nint data=Marshal.ReadIntPtr(frame,offset);
                    if(data!=0 && bins>0)spectrumHash.AppendData(new ReadOnlySpan<byte>((void*)data,bins*4));
                }
                nint piece=Marshal.ReadIntPtr(frame,0x208);
                if(piece!=0)hash.AppendData(new ReadOnlySpan<byte>((void*)(piece+0xc),4));
                foreach(int offset in new[]{0x218,0x288})
                {
                    nint env=Marshal.ReadIntPtr(frame,offset);
                    if(env==0)continue;
                    int points=Marshal.ReadInt32(env,0x15c);
                    if(points is <0 or >10000)throw new InvalidOperationException("Envelope trace exceeds bound.");
                    hash.AppendData(BitConverter.GetBytes(points));
                    nint data=Marshal.ReadIntPtr(env,0x178);
                    for(int i=0;data!=0&&i<points;i++)
                    {
                        hash.AppendData(new ReadOnlySpan<byte>((void*)(data+i*16),8));
                        hash.AppendData(new ReadOnlySpan<byte>((void*)(data+i*16+8),4));
                    }
                }
                owner.Rows.Add(new(Marshal.ReadInt32(state,0x24158),id,phase>=3?0:Marshal.ReadInt32(context,16),Convert.ToHexString(hash.GetHashAndReset()),layer?1:phase,
                    Convert.ToHexString(spectrumHash.GetHashAndReset()),(ulong)Marshal.ReadInt64(frame,0x150),bins,
                    phase>=5?SpectrumOf(Marshal.ReadIntPtr(state,0x247b8)):"",
                    phase is 5 or 6 ? Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>((void*)Marshal.ReadIntPtr(context,24),44))):"",
                    phase is 5 or 6?EffectOf(state,Marshal.ReadIntPtr(context,16)):null,
                    !layer && phase is 0 or 2 && Marshal.ReadInt32(context,40)!=0 ?
                        Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>((void*)Marshal.ReadIntPtr(state,0x29510),
                            checked((Marshal.ReadInt32(Marshal.ReadIntPtr(state,0x1fc8),0x8c)+1)*2)))) : ""));
            }
            catch(Exception error){owner._error??=error;}
        }
        private static int[] EffectOf(nint state,nint position)
        {
            nint row=Marshal.ReadIntPtr(position);
            int bank=Marshal.ReadInt32(row,0x11c),unit=Marshal.ReadInt32(row,0x120);
            if(bank<0||unit<0)return [];
            nint effect=((delegate* unmanaged<nint,int,int,nint>)(LocalTimingNative.Dse+0x182220))(Marshal.ReadIntPtr(state),bank,unit);
            if(effect==0)return [];
            return Enumerable.Range(0,12).Select(i=>Marshal.ReadInt32(effect,i*4)).Append(Marshal.ReadInt32(effect,0x3c)).ToArray();
        }
        private static string SpectrumOf(nint frame)
        {
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int bins=Marshal.ReadInt32(frame,0x1a8);
            if(bins is <0 or >16384)throw new InvalidOperationException("Source spectrum bound.");
            foreach(int offset in new[]{0x1b0,0x1b8})
            {
                nint pointer=Marshal.ReadIntPtr(frame,offset);
                if(pointer!=0&&bins>0)hash.AppendData(new ReadOnlySpan<byte>((void*)pointer,bins*4));
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        public void Dispose(){_noiseFilter.Dispose();_unvoiced.Dispose();_spectrum.Dispose();_pitch.Dispose();_interpolated.Dispose();_processed.Dispose();_layer.Dispose();_hook.Dispose();_active=null;}
    }
}
