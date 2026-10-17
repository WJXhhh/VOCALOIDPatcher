"""Check the temporal lowpass preceding legacy synthesis bit7/18 adaptation.

Owned scalar sequences only. Genuine DSE FFT plans are initialized by its
reviewed entry; the enclosing audio synthesis context is not reconstructed.
No installed waveform, frame payload or instruction bytes are serialized.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import random
import struct

from audit_frm2_numeric import F, I, Native, P, SHA256, f32
from audit_vqm_v4_module_leads import PE
from verify_frm2_analysis_windows import fft
from verify_vqm_spectrum_stage_precision import fft_library_identity


def mirrored(sequence, length):
    assert 1<=len(sequence)<=length//2+1
    result=[0.]*(length+2)
    result[:len(sequence)]=sequence
    for k,value in enumerate(sequence):
        result[length-k]=value
    for k in range(len(sequence),length-len(sequence)+1):
        result[k]=sequence[-1]
    return result


def weights(length, start, width):
    result=[]
    for k in range(length//2):
        distance=f32(k-start)
        if distance<0:
            result.append(1.)
        elif width<=distance:
            result.append(0.)
        else:
            result.append(f32(1.-f32(distance/width)))
    return result


def independent(sequence,length,start,width):
    transformed=fft(mirrored(sequence,length)[:length])
    for k,weight in enumerate(weights(length,start,width)):
        z=transformed[k]
        transformed[k]=complex(f32(f32(z.real)*weight),f32(f32(z.imag)*weight))
    transformed[0]=complex(transformed[0].real,0.)
    # The actual loop stops BEFORE Nyquist. Do not apply its cutoff to bin N/2.
    for k in range(1,length//2):
        transformed[length-k]=transformed[k].conjugate()
    return [f32(z.real/length) for z in fft([z.conjugate() for z in transformed])]


def comparison(actual,expected):
    assert len(actual)==len(expected)
    assert all(math.isfinite(x) for x in actual+expected)
    return dict(values=len(actual),exact=sum(struct.pack('<f',a)==struct.pack('<f',b)
        for a,b in zip(actual,expected)),maximum=max(abs(a-b) for a,b in zip(actual,expected)))


class Harness:
    def __init__(self,dll):
        self.native=Native(dll)
        self.fft_identity=fft_library_identity()
        native=self.native
        self.bank_base=native.alloc(0x400+64)
        c.memset(self.bank_base,0xa5,32)
        self.bank=self.bank_base+32
        c.memset(self.bank+0x400,0x5a,32)
        native.fn(0x580b0,None,P)(self.bank)
        self.forward=[P.from_address(self.bank+0x290+8*k).value for k in range(18)]
        self.backward=[P.from_address(self.bank+0x320+8*k).value for k in range(18)]
        assert self.forward[0] is None and self.backward[0] is None
        assert all(self.forward[1:]) and all(self.backward[1:])
        self.bank_before=c.string_at(self.bank_base,0x400+64)
        assert self.bank_before[:32]==bytes([0xa5])*32 and self.bank_before[-32:]==bytes([0x5a])*32
        self.context_base=native.alloc(0x2de8+64)
        c.memset(self.context_base,0xa5,32)
        self.context=self.context_base+32
        c.memset(self.context+0x2de8,0x5a,32)
        P.from_address(self.context+0x2de0).value=self.bank
        self.context_before=c.string_at(self.context_base,0x2de8+64)
        self.invoke=native.fn(0x46f20,None,P,P,I,I,F,F)
        self.compute_forward=c.CFUNCTYPE(I,P,P)(P.from_address(native.lib._handle+0x224058).value)
        self.compute_backward=c.CFUNCTYPE(I,P,P)(P.from_address(native.lib._handle+0x224060).value)

    def buffer(self,values):
        base=self.native.alloc(len(values)*4+64)
        c.memset(base,0xa5,32)
        c.memset(base+32+len(values)*4,0x5a,32)
        pointer=base+32
        c.memmove(pointer,struct.pack('<'+'f'*len(values),*values),len(values)*4)
        return base,pointer,c.string_at(base,len(values)*4+64)

    def call(self,sequence,length,start,width,missing=None):
        index=int(math.log2(length)) if length&(length-1)==0 else None
        payload=sequence+[0.]*(length+2-len(sequence))
        base,pointer,before=self.buffer(payload)
        original_bank=c.string_at(self.bank,0x400)
        if missing is not None:
            assert index is not None
            P.from_address(self.bank+(0x290 if missing=='forward' else 0x320)+8*index).value=None
        try:
            self.invoke(self.context,pointer,len(sequence),length,start,width)
        finally:
            c.memmove(self.bank,original_bank,len(original_bank))
        actual=[F.from_address(pointer+4*k).value for k in range(length)]
        after=c.string_at(base,len(payload)*4+64)
        assert before[:32]==after[:32] and before[-32:]==after[-32:]
        assert c.string_at(self.context_base,0x2de8+64)==self.context_before
        assert c.string_at(self.bank_base,0x400+64)==self.bank_before
        if missing=='forward':
            # Forward fallback zeros N floats, but leaves the N/N+1 Nyquist
            # slots initialized by mirroring. A working inverse consumes them.
            expected=[f32(sequence[0]/length)*(-1)**k for k in range(length)]
            assert struct.pack('<'+'f'*length,*actual)==struct.pack('<'+'f'*length,*expected)
            return dict(length=length,count=len(sequence),missing=missing,
                stale_nyquist_alternation_exact=True,all_zero=False,
                context_bank_and_guards_preserved=True)
        if index is None or missing is not None:
            assert actual==[0.]*length
            return dict(length=length,count=len(sequence),missing=missing,all_zero=True,
                context_bank_and_guards_preserved=True)
        expected=independent(sequence,length,start,width)
        comp=comparison(actual,expected)
        assert comp['maximum']<=.001,comp
        # Separate exact comparison shares only the two native transforms.
        prepared=mirrored(sequence,length)
        _,shared,shared_before=self.buffer(prepared)
        assert self.compute_forward(self.forward[index],shared)==0
        for k,weight in enumerate(weights(length,start,width)):
            if weight==1.:
                continue
            for component in (0,1):
                address=shared+8*k+4*component
                F.from_address(address).value=f32(F.from_address(address).value*weight) if weight else 0.
        F.from_address(shared+4).value=0.
        assert self.compute_backward(self.backward[index],shared)==0
        expected_shared=[F.from_address(shared+4*k).value for k in range(length)]
        assert struct.pack('<'+'f'*length,*actual)==struct.pack('<'+'f'*length,*expected_shared)
        shared_after=c.string_at(shared-32,len(prepared)*4+64)
        assert shared_before[:32]==shared_after[:32] and shared_before[-32:]==shared_after[-32:]
        return dict(length=length,count=len(sequence),start=start,width=width,
            independent=comp,shared_transform_exact_values=length,
            context_bank_and_guards_preserved=True)


def caller_evidence(dll):
    # Shape checks for the pinned .pdata entry are independent of Ghidra's
    # containing-function display. No disassembly or proprietary bytes saved.
    pe=PE(dll.read_bytes())
    directory,size=struct.unpack_from('<II',pe.data,pe.directories+24)
    records=list(struct.iter_unpack('<III',pe.data[pe.offset(directory):pe.offset(directory)+size]))
    parent=next((lo,hi) for lo,hi,_ in records if lo==0x31830)
    assert parent==(0x31830,0x3a66b)
    exported=next(x for x in pe.exports() if x['name']=='wbhsm_getAudioChunk')
    assert exported['rva']=='0xeb00' and not exported['forwarded']
    verified_calls=[]
    for call,target in ((0xfb41,0x305f0),(0xfd2f,0x305f0),
                        (0x30c2d,0x31290),(0x30ccd,0x31290),(0x30d0b,0x31290),
                        (0x313ef,0x31830),(0x3292f,0x46f20),(0x32e54,0x46f20)):
        at=pe.offset(call)
        assert pe.data[at]==0xe8
        assert call+5+struct.unpack_from('<i',pe.data,at+1)[0]==target
        verified_calls.append(dict(call_rva=hex(call),target_rva=hex(target)))
    return dict(parent_runtime_range=[hex(x) for x in parent],
        export='wbhsm_getAudioChunk',export_rva='0xeb00',
        caller_chain=['0x305f0','0x31290','0x31830'],
        export_calls=['0xfb41','0xfd2f'],frame_block_call='0x313ef',
        f0_smoothing_call='0x3292f',amplitude_smoothing_call='0x32e54',
        f0_residual_write='0x32993',amplitude_residual_write='0x32ecd',
        adapted_bit18_write='0x32f9e',adapted_bit7_write='0x32fb4',
        direct_call_targets_verified=verified_calls,
        role='Read-only Ghidra xrefs plus aligned caller instructions: '
             'audio-output export, temporal F0/amplitude adaptation. Frame+b8 '
             'in this branch is source minus blended lowpass result, not an '
             'ordinary physical phase array. Full parent runtime unverified.')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    harness=Harness(args.dll)
    rng=random.Random(416)
    rows=[]
    for length in (8,16,32,64,128):
        for count in sorted(set((1,2,3,length//2-1,length//2,length//2+1))):
            signals=dict(constant=[440.]*count,ramp=[f32(200.+k*7.1) for k in range(count)],
                impulse=[100. if k==count//2 else 0. for k in range(count)],
                alternating=[25.*(-1)**k for k in range(count)],
                random=[f32(rng.uniform(-100,100)) for _ in range(count)])
            for shape,sequence in signals.items():
                for start,width in ((-1.,2.),(0.,1.),(1.,3.),(3.25,2.5),
                                    (float(length//2+1),1.),(float(length//4),0.)):
                    rows.append(dict(shape=shape,**harness.call(sequence,length,start,width)))
    fallback=[]
    for length in (8,16,32,64,128):
        for missing in ('forward','backward'):
            fallback.append(harness.call([1.,2.,3.],length,1.,2.,missing))
    for length in (6,12,24):
        fallback.append(harness.call([1.,2.,3.],length,1.,2.))
    sources=('verify_vqm_temporal_lowpass.py','audit_frm2_numeric.py','audit_vqm_v4_module_leads.py',
             'verify_frm2_analysis_windows.py','verify_vqm_spectrum_stage_precision.py')
    report=dict(dse_sha256=SHA256,fft_identity=harness.fft_identity,caller_review=caller_evidence(args.dll),
        entries=dict(plan_initializer='0x580b0',lowpass='0x46f20',size_index='0x57f90'),
        cases=rows,fallback_cases=fallback,
        source_sha256={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources},
        scope='Owned scalar sequences and genuinely initialized FFT descriptor bank. '
              'Parent is an explicit minimal pointer holder, not a constructed full '
              'wbhsm synthesizer. Independent FFT model compared with tolerance; '
              'separate exact check shares native forward/backward transforms. '
              'No claim of complete parent execution, original VQM maker smoothing '
              'parameters, saved bit7/18 reproduction or bit17 production.')
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(cases=len(rows),values=sum(x['independent']['values'] for x in rows),
        max_independent_error=max(x['independent']['maximum'] for x in rows),fallbacks=len(fallback))))


if __name__=='__main__':
    main()
