"""Check the reviewed mode-2 free-peak contract against saved VQM bit16.

Actual native FFT and peaks feed a persistent tracker. Independent mode-2
selection never reads saved bit16. Commercial PCM/arrays stay in memory; the
report only retains counts, file hashes, and error statistics.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
from pathlib import Path
import struct

from audit_frm2_numeric import B, D, F, I, Native, P, Q, SHA256, f32
from frm2_mode2_tracks import advance_zero
from verify_frm2_mode2_lifecycle import verify as verify_owned_mode2
from verify_vqm_bit16_peak_interpolation import auxiliary_sequences, metrics
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit16_tracking_selection import KEYS, check_guard, guarded
from verify_vqm_pcm_quantization_bounds import midpoint


def bits(value):
    return struct.pack('<f',value)


def case(native,sequence,score,mapping):
    config,analysis = native.alloc(0x35f0), native.alloc(0xce0)
    assert native.fn(0x90de0,P,P)(config) == config
    # Follow document65's explicit mode-2 fixed-center contract. No claim
    # that these settings belonged to the unavailable commercial maker.
    for key,value in ((0,2.),(8,float(score)),(9,0.)):
        for x in (0.,1.):
            native.add(config+0x5d0+key*0xa0,x,value)
    assert native.fn(0xcdde0,P,P,I,P)(analysis,44100,config) == analysis
    tracker = P.from_address(analysis+0xc98).value
    assert tracker and P.from_address(tracker+0x21be0).value and P.from_address(tracker+0x21be8).value
    I.from_address(tracker+0x18).value = 0
    get = native.fn(0x2b050,F,P,I,F)
    values = {key:get(config,key,0.) for key in set(KEYS) | {3}}
    assert values[0] == 2 and values[4] == 200 and values[8] == score and values[9] == 0
    fft_context = native.alloc(0x400)
    assert native.fn(0x49790,P,P,I,P,I)(fft_context,0,config,0) == fft_context
    fft = native.fn(0x4e8e0,Q,P,P,I,B,F,P,P,P,B,P,F,I,I,I,I)
    detect = native.fn(0x51e60,Q,P,P,I,P,P,F,I,F,D,F)
    advance = native.fn(0xef9f0,Q,P,P,D)
    link = guarded(native,0x30)
    queue = guarded(native,8)
    P.from_address(queue).value = link
    state = dict(initialized=False,tracks=[])
    pcm = sequence['pcm'] if mapping == 'raw' else midpoint(sequence['pcm'],-1)
    raw_hash = hashlib.sha256(struct.pack('<'+str(len(sequence['pcm']))+'h',*sequence['pcm'])).hexdigest()
    errors, strongest_errors = [], []
    missing_first = total_frames = output_values = state_values = sorted_values = 0
    first_differs_from_strongest = 0
    traces = dict(added=0,reused=0,retired=0,collisions=0)
    chosen = hashlib.sha256()
    for index in range(4,len(sequence['frames'])-4):
        time = index*256/44100.
        assert all(get(config,key,time) == value for key,value in values.items())
        source = guarded(native,2049*4)
        source_bytes = struct.pack('<2049f',*pcm[index*256-1024:index*256+1025])
        c.memmove(source,source_bytes,len(source_bytes))
        scalar,spectrum,half = P(),P(),I()
        assert fft(fft_context,source,2049,10,1.,c.byref(scalar),0,c.byref(half),2,
            c.byref(spectrum),2.,0,0,0,0) == 0 and half.value == 2048
        output = guarded(native,30000)
        pointer,count = P(output),I()
        threshold = native.fn(0x2af20,F,F)(values[3])
        assert detect(spectrum,scalar,half.value,c.byref(pointer),c.byref(count),44100.,0,threshold,0.,0.) == 0
        assert pointer.value == output and 0 < count.value <= 1500
        peaks = [[F.from_address(output+20*j+off).value for off in (0,4,8)]
            for j in range(count.value)]
        frame = guarded(native,4096)
        assert native.frame_ctor(frame) == frame
        F.from_address(frame+0xf0).value = f32(sequence['frames'][index]['f0'])
        D.from_address(frame+0x48).value = time
        P.from_address(link+0x28).value = frame
        P.from_address(link+0x20).value = output
        I.from_address(link+0x18).value = count.value
        strongest = max((p for p in peaks if values[0x27] <= p[0] <= values[2]),key=lambda p:p[1])
        expected,trace = advance_zero(state,peaks,values)
        assert advance(tracker,queue,time) == 0
        retained = sorted((p for p in peaks if p[1] > f32(-3e38)),key=lambda p:-p[1])[:201]
        assert I.from_address(link+0x18).value == len(retained)
        for j,p in enumerate(retained):
            actual = [F.from_address(output+20*j+off).value for off in (0,4,8)]
            assert [bits(v) for v in actual] == [bits(v) for v in p]
            sorted_values += 3
        assert I.from_address(tracker+0x1c).value == I.from_address(frame+0x58).value == len(expected)
        assert bool(I.from_address(tracker+0x21b30).value) == state['initialized']
        for slot,track in enumerate(state['tracks']):
            for name,at,ty in (('amplitude',0,F),('center',4,F),('previous',8,I),('current',12,I),
                ('rank',16,I),('retired',80,I),('candidates',20,I),('distance',32,F),
                ('metric',44,F),('cost',56,F),('order',68,I)):
                wanted = track[name] if isinstance(track[name],list) else [track[name]]
                actual = [ty.from_address(tracker+0x20+slot*0x54+at+4*j).value for j in range(len(wanted))]
                assert actual == wanted,(sequence['category'],score,mapping,index,slot,name)
                state_values += len(wanted)
            actual = [F.from_address(P.from_address(frame+at).value+4*slot).value for at in (0x60,0x68,0x70)]
            assert [bits(v) for v in actual] == [bits(v) for v in expected[slot]]
            output_values += 3
        for key,value in trace.items():
            traces[key] += value
        total_frames += 1
        available = bool(expected) and state['tracks'][0]['current'] >= 0
        chosen.update(struct.pack('<i',state['tracks'][0]['current'] if expected else -1))
        # Compare the actual first component only when that output is available;
        # missing-output defaults are not misrepresented as produced bit16.
        if available:
            prediction = f32(expected[0][1]/10.)
            errors.append(prediction-sequence['frames'][index]['bit16'])
            first_differs_from_strongest += bits(expected[0][1]) != bits(strongest[1])
        else:
            missing_first += 1
        strongest_errors.append(f32(strongest[1]/10.)-sequence['frames'][index]['bit16'])
        assert c.string_at(source,2049*4) == source_bytes
        for address,size in ((source,2049*4),(output,30000),(frame,4096),(link,0x30),(queue,8)):
            check_guard(address,size)
    assert raw_hash == hashlib.sha256(struct.pack('<'+str(len(sequence['pcm']))+'h',*sequence['pcm'])).hexdigest()
    return dict(case=sequence['category'],selector_score=score,pcm_mapping=mapping,frames=total_frames,
        config={hex(key):value for key,value in sorted(values.items())},
        available_first=len(errors),missing_first=missing_first,first_differs_from_strongest=first_differs_from_strongest,
        exact_track_fields=state_values,exact_frame_values=output_values,exact_sorted_peak_values=sorted_values,
        lifecycle_operations=traces,selected_sorted_indices_sha256=chosen.hexdigest(),
        source_pcm_sha256=raw_hash,source_and_guards_preserved=True,
        checks=dict(native_mode2_first=metrics(errors) if errors else None,
            strongest_in_frequency_bounds=metrics(strongest_errors)))


def run(args):
    parser_hash,ddi_hash,sequences = load_sequences(args.manifest,args.voice_root,args.ddb_tools)
    aux_hash = hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest()
    assert aux_hash == 'a629b36e0b4d571c37f947809d34dcfa283a5af42f12d635cd5f75143ee6203c'
    for i,seq in enumerate(sequences):
        seq['category'] = 'V4X_'+str(i)
    native = Native(args.dll)
    owned = verify_owned_mode2(native)
    assert owned['parameters']['max'] == 0 and owned['collisions'] > 0 and owned['reused'] > 0
    rows = []
    for seq in sequences+auxiliary_sequences(args.aux_vdb):
        if args.only and seq['category'] != args.only:
            continue
        auxiliary = seq['category'] in ('TENSE','BRWSP')
        for score in ((1,2) if auxiliary else (1,)):
            for mapping in (('raw','toward_zero_half_lsb') if auxiliary else ('raw',)):
                row = case(native,seq,score,mapping);rows.append(row)
                print(json.dumps({key:row[key] for key in ('case','selector_score','pcm_mapping',
                    'frames','available_first','missing_first','checks')}),flush=True)
    sources = ('verify_vqm_bit16_free_peak_tracking.py','audit_frm2_numeric.py','frm2_mode2_tracks.py',
        'frm2_peak_selection.py','verify_frm2_mode2_lifecycle.py','verify_vqm_bit16_peak_interpolation.py',
        'verify_vqm_bit16_spectral_power.py','verify_vqm_bit16_tracking_selection.py','verify_vqm_pcm_quantization_bounds.py')
    return dict(schema=1,dse_sha256=SHA256,parser_sha256=parser_hash,ddi_sha256=ddi_hash,
        auxiliary_sha256=aux_hash,source_sha256={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
            for name in sources},owned_mode2=owned,cases=rows,
        summary=dict(cases=len(rows),calls=sum(row['frames'] for row in rows),
            exact_track_fields=sum(row['exact_track_fields'] for row in rows),
            exact_frame_values=sum(row['exact_frame_values'] for row in rows),
            exact_sorted_peak_values=sum(row['exact_sorted_peak_values'] for row in rows)),
        limits=['Reviewed mode2 fixed-center contract, zero queue index and immediate retirement',
            'Mode2 growing-queue crossing, delayed retirement and original initial state are not tested',
            'Native FFT/peaks shared; selection and lifecycle independently predicted',
            'Neither configuration nor half-LSB input is identified as the original maker contract',
            'Strongest-peak control is selected without saved bit16; missing outputs excluded and counted',
            'No recovered PCM producer, bit17 generator, or full VQM/Growl completion claimed'],
        installed_files_unchanged=True,no_windows_temp=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll','manifest','voice-root','ddb-tools','aux-vdb','output'):
        parser.add_argument('--'+name,type=Path,required=True)
    parser.add_argument('--only',choices=('V4X_0','V4X_1','V4X_2','TENSE','BRWSP'))
    args = parser.parse_args()
    report = run(args)
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(report['summary']))


if __name__ == '__main__':
    main()
