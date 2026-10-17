"""Compare saved VQM bit16 after native growing-queue harmonic repair.

All commercial PCM, peaks and frame arrays remain in memory. Source files and
loaded commercial objects are never modified. Only counts, hashes and errors
are written. The supplied F0 and clipped first four frames remain limitations.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
from pathlib import Path
import struct

from audit_frm2_numeric import B, D, F, I, Native, P, Q, SHA256, f32
from frm2_peak_selection import advance_outer
from frm2_track_repair import repair
from verify_frm2_track_repair import verify as verify_owned_repair
from verify_vqm_bit16_peak_interpolation import auxiliary_sequences, metrics
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit16_tracking_selection import KEYS, check_guard, guarded


def packed_column(frame, column):
    return struct.pack('<'+str(len(frame))+'f', *(item[column] for item in frame))


def case(native, sequence, score, regime):
    config, analysis = native.alloc(0x35f0), native.alloc(0xce0)
    assert native.fn(0x90de0, P, P)(config) == config
    if score == 2:
        for x in (0., 1.):
            native.add(config+0x5d0+8*0xa0, x, 2.)
    assert regime in ('default', 'verified_control')
    if regime == 'verified_control':
        # One fixed positive control from the independently verified document60
        # matrix; it is not chosen or fitted using commercial feature errors.
        for key,value in ((6,.03001),(7,.04001)):
            for x in (0.,1.):
                native.add(config+0x5d0+key*0xa0,x,value)
    get = native.fn(0x2b050, F, P, I, F)
    values = {key:get(config, key, 0.) for key in set(KEYS) | {3,6,7}}
    assert values[0] == 1 and values[4] == 200 and values[8] == score
    default_rate = F.from_address(config+0x18).value
    # Match the explicit 256-sample source hop, without tuning to saved bit16.
    rate = f32(44100/256)
    F.from_address(config+0x18).value = rate
    assert native.fn(0xcdde0, P, P, I, P)(analysis, 44100, config) == analysis
    tracker = P.from_address(analysis+0xc98).value
    assert tracker and I.from_address(tracker+0x21b30).value == 0
    fft_context = native.alloc(0x400)
    assert native.fn(0x49790, P, P, I, P, I)(fft_context, 0, config, 0) == fft_context
    fft = native.fn(0x4e8e0, Q, P, P, I, B, F, P, P, P, B, P, F, I, I, I, I)
    detect = native.fn(0x51e60, Q, P, P, I, P, P, F, I, F, D, F)
    advance = native.fn(0xef9f0, Q, P, P, D)
    indices = range(4, len(sequence['frames'])-4)
    queue = guarded(native, 8*len(indices))
    state = dict(tracks=[], history=0., initialized=False)
    expected, addresses, original, saved_features = [], [], [], []
    removed = filled = historical_values = assignments = acoustic_values = 0
    historical_first_changed = current_first_changed = 0
    first_missing = first_inactive = 0
    selected = hashlib.sha256()
    source_hash = hashlib.sha256(struct.pack('<'+str(len(sequence['pcm']))+'h', *sequence['pcm'])).hexdigest()
    for step, index in enumerate(indices):
        time = index*256/44100.
        assert all(get(config,key,time) == value for key,value in values.items())
        signal = sequence['pcm'][index*256-1024:index*256+1025]
        source = guarded(native, 2049*4)
        source_bytes = struct.pack('<2049f', *signal)
        c.memmove(source, source_bytes, len(source_bytes))
        scalar, spectrum, half = P(), P(), I()
        assert fft(fft_context,source,2049,10,1.,c.byref(scalar),0,c.byref(half),2,
                   c.byref(spectrum),2.,0,0,0,0) == 0
        assert half.value == 2048
        output = guarded(native,30000)
        pointer, count = P(output), I()
        threshold = native.fn(0x2af20, F, F)(values[3])
        assert detect(spectrum,scalar,half.value,c.byref(pointer),c.byref(count),
                      44100.,0,threshold,0.,0.) == 0
        assert pointer.value == output and 0 < count.value <= 1500
        peaks = [tuple(F.from_address(output+20*j+off).value for off in (0,4,8))
                 for j in range(count.value)]
        frame, link = guarded(native,4096), guarded(native,0x30)
        assert native.frame_ctor(frame) == frame
        f0 = f32(sequence['frames'][index]['f0'])
        F.from_address(frame+0xf0).value = f0
        D.from_address(frame+0x48).value = time
        P.from_address(link+0x28).value = frame
        P.from_address(link+0x20).value = output
        I.from_address(link+0x18).value = count.value
        P.from_address(queue+8*step).value = link
        I.from_address(tracker+0x18).value = step
        for j in range(count.value):
            I.from_address(tracker+0x1ec50+4*j).value = 0
            I.from_address(tracker+0x203c0+4*j).value = 0
        result = advance_outer(state,peaks,f0,values,[0]*len(peaks),timestamp=time)
        fresh = [list(p) if p is not None else [0.,10000.,0.] for p in result['output']]
        assert len(fresh) == 200
        original.append(fresh[0][:])
        first_missing += result['output'][0] is None
        first_inactive += fresh[0][1] <= values[3] or fresh[0][1] == 10000.
        expected.append(fresh)
        addresses.append(frame)
        saved_features.append(sequence['frames'][index]['bit16'])
        trace = repair(expected,rate,values[3],values[6],values[7],first_time=4*256/44100.)
        removed += trace['removed']; filled += trace['filled']
        assert advance(tracker,queue,time) == 0
        assert F.from_address(tracker+0x21bd8).value == state['history']
        assert bool(I.from_address(tracker+0x21b30).value) == state['initialized']
        assert B.from_address(tracker+0x21b34).value == 1
        selected.update(struct.pack('<i',state['tracks'][0]['current']))
        for slot, track in enumerate(state['tracks']):
            assert I.from_address(tracker+0x2c+slot*0x54).value == track['current']
            assignments += 1
            acoustic_values += 3*(result['output'][slot] is not None)
        # Repair may rewrite old arrays; recheck the entire queue on every call.
        for old, wanted in zip(addresses,expected):
            assert I.from_address(old+0x58).value == len(wanted)
            for column, at in enumerate((0x60,0x68,0x70)):
                assert c.string_at(P.from_address(old+at).value,len(wanted)*4) == packed_column(wanted,column)
                historical_values += len(wanted)
            check_guard(old,4096)
        current_first_changed += expected[-1][0] != original[-1]
        assert c.string_at(source,2049*4) == source_bytes
        assert [tuple(F.from_address(output+20*j+off).value for off in (0,4,8))
                for j in range(count.value)] == peaks
        for address,size in ((source,2049*4),(output,30000),(link,0x30)):
            check_guard(address,size)
        check_guard(queue,8*len(indices))
        if step % 100 == 0:
            print(json.dumps(dict(case=sequence['category'],score=score,regime=regime,queue_frames=step+1)),flush=True)
    historical_first_changed = sum(a != b[0] for a,b in zip(original,expected))
    immediate = [f32(p[1]/10.)-saved for p,saved in zip(original,saved_features)]
    final = [f32(frame[0][1]/10.)-saved for frame,saved in zip(expected,saved_features)]
    assert source_hash == hashlib.sha256(struct.pack('<'+str(len(sequence['pcm']))+'h', *sequence['pcm'])).hexdigest()
    assert current_first_changed == historical_first_changed == 0
    assert first_inactive == first_missing == 0
    return dict(case=sequence['category'],selector_score=score,repair_regime=regime,frames=len(indices),
        default_config_rate=default_rate,supplied_queue_rate=rate,
        config={hex(k):v for k,v in sorted(values.items())},
        temporal_enabled=True,repair_removed_operations=removed,repair_filled_operations=filled,
        exact_historical_values=historical_values,exact_track_assignments=assignments,
        selected_acoustic_values=acoustic_values,missing_first=first_missing,
        first_inactive=first_inactive,
        first_slot_invariant='Every fresh first slot is active at the fixed threshold. '
            'There is no ended active run or bounded inactive gap in that slot. '
            'For any nonnegative static cfg6/cfg7 durations, document60 repair cannot rewrite it.',
        current_first_changed=current_first_changed,historical_first_changed=historical_first_changed,
        selected_indices_sha256=selected.hexdigest(),source_pcm_sha256=source_hash,
        source_and_guards_preserved=True,
        checks=dict(immediate_first=metrics(immediate),final_repaired_first=metrics(final)))


def run(args):
    parser_hash,ddi_hash,sequences = load_sequences(args.manifest,args.voice_root,args.ddb_tools)
    aux_hash = hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest()
    assert aux_hash == 'a629b36e0b4d571c37f947809d34dcfa283a5af42f12d635cd5f75143ee6203c'
    for i,sequence in enumerate(sequences):
        sequence['category'] = 'V4X_'+str(i)
    native = Native(args.dll)
    baseline_path = Path(__file__).resolve().parents[2]/'voicebank_doc/evidence/422_vqm_bit16_tracking_selection.json'
    baseline = json.loads(baseline_path.read_text(encoding='utf-8'))
    assert baseline['dse_sha256'] == SHA256
    for name,pin in baseline['source_sha256'].items():
        assert hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() == pin
    owned = verify_owned_repair(native)
    assert owned['parameters']['max'] == 0 and owned['removed'] > 0 and owned['filled'] > 0
    rows = []
    for seq in sequences+auxiliary_sequences(args.aux_vdb):
        if args.only and seq['category'] != args.only:
            continue
        for score in ((1,2) if seq['category'] in ('TENSE','BRWSP') else (1,)):
            for regime in ('default','verified_control'):
                row = case(native,seq,score,regime); rows.append(row)
                control = next(item for item in baseline['cases'] if item['case'] == seq['category']
                    and item['pcm_mapping'] == 'raw' and item['selector_score'] == score)
                assert row['selected_indices_sha256'] == control['selected_indices_sha256']
                assert row['checks']['immediate_first'] == {key:control['checks']['tracked_first'][key]
                    for key in row['checks']['immediate_first']}
                row['pinned422_zero_index_control_reproduced'] = True
                print(json.dumps({k:row[k] for k in ('case','selector_score','repair_regime','frames',
                    'repair_removed_operations','repair_filled_operations','historical_first_changed','checks')}),flush=True)
    sources = ('verify_vqm_bit16_queue_repair.py','audit_frm2_numeric.py','frm2_peak_selection.py',
        'frm2_track_repair.py','verify_frm2_track_repair.py','verify_vqm_bit16_peak_interpolation.py',
        'verify_vqm_bit16_spectral_power.py','verify_vqm_bit16_tracking_selection.py')
    return dict(schema=1,dse_sha256=SHA256,parser_sha256=parser_hash,ddi_sha256=ddi_hash,
        auxiliary_sha256=aux_hash,source_sha256={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                                               for name in sources},
        baseline422_sha256=hashlib.sha256(baseline_path.read_bytes()).hexdigest(),
        owned_repair={k:v for k,v in owned.items() if k != 'rows'},cases=rows,
        summary=dict(cases=len(rows),tracking_calls=sum(row['frames'] for row in rows),
            exact_historical_values=sum(row['exact_historical_values'] for row in rows),
            removed_operations=sum(row['repair_removed_operations'] for row in rows),
            filled_operations=sum(row['repair_filled_operations'] for row in rows)),
        limits=['Native FFT/peaks shared; independent tracking and growing-queue repair model',
            'Saved physical F0 supplied; first four clipped frames and original history unavailable',
            'Rate follows explicit 256-sample hop; original maker configuration remains unknown',
            'Immediate and final repaired first amplitudes compared separately; no claim about feature-write timing',
            'Known selector branches are hypotheses, not recovered original configuration',
            'No full DRS PCM producer, serialized output or bit17 rule established'],
        no_windows_temp=True,installed_files_unchanged=True)


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
