"""Test native harmonic tracking as the missing saved-VQM bit16 peak rule.

Commercial PCM and frames stay in memory. Native/default configuration is used;
saved bit16 is compared only after selection, never fed to the producer/model.
No installed data or binary is modified, and no Windows Temp output is used.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import statistics
import struct

from audit_frm2_numeric import B, D, F, I, Native, P, Q, SHA256, f32
from frm2_peak_selection import advance_outer
from verify_vqm_bit16_peak_interpolation import auxiliary_sequences, metrics
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_pcm_quantization_bounds import midpoint


KEYS = (0, 2, 4, 8, 9, 10, 0xd, 0x11, 0x12, 0x13,
        0x21, 0x22, 0x27, 0x28, 0x29, 0x33)
GUARD = b'\xa5' * 32


def guarded(native, size):
    base = native.alloc(size+64)
    c.memmove(base, GUARD, 32)
    c.memmove(base+size+32, GUARD, 32)
    return base+32


def check_guard(address, size):
    assert c.string_at(address-32, 32) == GUARD
    assert c.string_at(address+size, 32) == GUARD


def bits(value):
    return struct.pack('<f', value)


def case(native, sequence, mapping, score=1):
    config, analysis = native.alloc(0x35f0), native.alloc(0xce0)
    assert native.fn(0x90de0, P, P)(config) == config
    assert score in (1,2)
    if score == 2:
        # Reviewed selector branch: choose minimum negative amplitude among
        # its three candidate slots. This is an explicit hypothesis, not a
        # claim about the commercial maker's unknown configuration.
        for position in (0.,1.):
            native.add(config+0x5d0+8*0xa0,position,2.)
    assert native.fn(0xcdde0, P, P, I, P)(analysis, 44100, config) == analysis
    tracker = P.from_address(analysis+0xc98).value
    assert tracker
    I.from_address(tracker+0x18).value = 0
    get = native.fn(0x2b050, F, P, I, F)
    values = {key: get(config, key, 0.) for key in KEYS}
    assert values[0] == 1 and values[4] == 200
    assert values[8] == score and values[0x33] == 0
    assert I.from_address(config+0x520).value == 0
    assert I.from_address(config+0x588).value == 0
    assert get(config, 3, 0.) == -300.
    ratio_env = config+0x5d0+0x23*0xa0
    assert I.from_address(ratio_env+0x50).value != 0
    fft_context = native.alloc(0x400)
    assert native.fn(0x49790, P, P, I, P, I)(fft_context, 0, config, 0) == fft_context
    analyze = native.fn(0x4e8e0, Q, P, P, I, B, F, P, P, P, B, P, F, I, I, I, I)
    detect = native.fn(0x51e60, Q, P, P, I, P, P, F, I, F, D, F)
    advance = native.fn(0xef9f0, Q, P, P, D)
    output = guarded(native, 30000)
    link = guarded(native, 0x30)
    queue = native.array(P, [link])
    state = dict(tracks=[], history=0., initialized=False)
    pcm = sequence['pcm'] if mapping == 'raw' else midpoint(sequence['pcm'], -1)
    threshold = native.fn(0x2af20, F, F)(-300.)
    errors = {key: [] for key in ('tracked_first', 'nearest_f0', 'best_amplitude_diagnostic')}
    chosen = hashlib.sha256()
    first_missing = nearest_differs = first_output_checks = track_checks = 0
    frame_harmonic_values = 0
    selected_ranks = []
    selected_frequency_minus_f0 = []
    for index in range(4, len(sequence['frames'])-4):
        time = index*256/44100.
        assert all(get(config, key, time) == value for key, value in values.items())
        source = guarded(native, 2049*4)
        c.memmove(source, struct.pack('<2049f', *pcm[index*256-1024:index*256+1025]), 2049*4)
        original_pcm = c.string_at(source, 2049*4)
        scalar, packed, half = P(), P(), I()
        assert analyze(fft_context, source, 2049, 10, 1., c.byref(scalar), 0,
                       c.byref(half), 2, c.byref(packed), 2., 0, 0, 0, 0) == 0
        assert half.value == 2048
        pointer, count = P(output), I()
        assert detect(packed, scalar, half.value, c.byref(pointer), c.byref(count),
                      44100., 0, threshold, 0., 0.) == 0
        assert pointer.value == output and 0 < count.value <= 1500
        peaks = [tuple(F.from_address(output+20*j+off).value for off in (0,4,8))
                 for j in range(count.value)]
        assert all(all(math.isfinite(x) for x in p) for p in peaks)
        assert all(a[0] <= b[0] for a,b in zip(peaks,peaks[1:]))
        frame = guarded(native, 4096)
        assert native.frame_ctor(frame) == frame
        saved = sequence['frames'][index]
        F.from_address(frame+0xf0).value = f0 = f32(saved['f0'])
        D.from_address(frame+0x48).value = time
        P.from_address(link+0x28).value = frame
        P.from_address(link+0x20).value = output
        I.from_address(link+0x18).value = count.value
        for j in range(count.value):
            I.from_address(tracker+0x1ec50+4*j).value = 0
            I.from_address(tracker+0x203c0+4*j).value = 0
        model = advance_outer(state, peaks, f0, values, [0]*len(peaks), timestamp=time)
        assert advance(tracker, queue, time) == 0
        assert I.from_address(frame+0x58).value == len(model['output']) == 200
        assert bool(I.from_address(tracker+0x21b30).value) == state['initialized']
        assert F.from_address(tracker+0x21bd8).value == state['history']
        for slot, track in enumerate(state['tracks']):
            actual = I.from_address(tracker+0x2c+slot*0x54).value
            assert actual == track['current'], (sequence.get('category'), mapping,index,slot)
            track_checks += 1
            expected = model['output'][slot]
            if expected is not None:
                actual_output = [F.from_address(P.from_address(frame+off).value+4*slot).value
                                 for off in (0x60,0x68,0x70)]
                assert [bits(x) for x in actual_output] == [bits(x) for x in expected]
                frame_harmonic_values += 3
        selected = state['tracks'][0]['current']
        chosen.update(struct.pack('<i', selected))
        selected_ranks.append(selected)
        actual_amplitude = F.from_address(P.from_address(frame+0x68).value).value
        expected_first = model['output'][0]
        if expected_first is None:
            first_missing += 1
            prediction = -10.
        else:
            assert bits(actual_amplitude) == bits(expected_first[1])
            prediction = f32(actual_amplitude/10.)
            first_output_checks += 1
            selected_frequency_minus_f0.append(peaks[selected][0]-f0)
        nearest = min(range(len(peaks)), key=lambda j: abs(peaks[j][0]-f0))
        nearest_differs += selected != nearest
        actual_bit16 = saved['features'][16]
        errors['tracked_first'].append(prediction-actual_bit16)
        errors['nearest_f0'].append(f32(peaks[nearest][1]/10.)-actual_bit16)
        errors['best_amplitude_diagnostic'].append(min(
            (f32(p[1]/10.)-actual_bit16 for p in peaks), key=abs))
        assert c.string_at(source, 2049*4) == original_pcm
        for address,size in ((source,2049*4),(frame,4096),(output,30000),(link,0x30)):
            check_guard(address,size)
        # Harmonic scheduling may update the two peak metadata words; the three
        # acoustic values stay untouched and are rechecked separately.
        assert [tuple(F.from_address(output+20*j+off).value for off in (0,4,8))
                for j in range(count.value)] == peaks
    return dict(case=sequence.get('category', sequence.get('label','V4X')),
        pcm_mapping=mapping, selector_score=score, frames=len(errors['tracked_first']),
        supplied_f0='Saved Frame F0 converted to physical Hz',
        config={hex(k):v for k,v in values.items()}, harmonic_ratio=1.,
        tracking_calls=len(errors['tracked_first']), exact_track_assignments=track_checks,
        exact_harmonic_acoustic_values=frame_harmonic_values,
        available_first_output_checks=first_output_checks, missing_first=first_missing,
        tracking_differs_from_nearest=nearest_differs,
        selected_indices_sha256=chosen.hexdigest(),
        selected_index_range=[min(selected_ranks),max(selected_ranks)],
        selected_frequency_minus_f0_range=[min(selected_frequency_minus_f0),
                                          max(selected_frequency_minus_f0)],
        checks={key:{**metrics(err), 'mean_signed_error':statistics.fmean(err)}
                for key,err in errors.items()}, source_and_guards_preserved=True,
        scope='Native FFT/peaks -> mode1 harmonic scheduler; queue index zero, '
              'no temporal repair. Model shares the native peak/F0 inputs.')


def run(args):
    parser_hash, ddi_hash, sequences = load_sequences(args.manifest,args.voice_root,args.ddb_tools)
    aux_hash = hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest()
    assert aux_hash == 'a629b36e0b4d571c37f947809d34dcfa283a5af42f12d635cd5f75143ee6203c'
    auxiliary = auxiliary_sequences(args.aux_vdb)
    for number, seq in enumerate(sequences):
        seq['category'] = f'V4X_{number}'
    native = Native(args.dll)
    rows = []
    for score in args.scores:
        for seq in sequences+auxiliary:
            row = case(native,seq,'raw',score);rows.append(row)
            print(json.dumps({k:row[k] for k in ('case','pcm_mapping','selector_score','frames','missing_first','checks')}),flush=True)
        for seq in auxiliary:
            row = case(native,seq,'toward_zero_half_lsb',score);rows.append(row)
            print(json.dumps({k:row[k] for k in ('case','pcm_mapping','selector_score','frames','missing_first','checks')}),flush=True)
    dependencies = ('verify_vqm_bit16_tracking_selection.py','audit_frm2_numeric.py',
                    'frm2_peak_selection.py','verify_vqm_bit16_peak_interpolation.py',
                    'verify_vqm_bit16_spectral_power.py','verify_vqm_pcm_quantization_bounds.py')
    pins = {name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
            for name in dependencies}
    return dict(schema=1,dse_sha256=SHA256,parser_sha256=parser_hash,ddi_sha256=ddi_hash,
        auxiliary_sha256=aux_hash,source_sha256=pins,cases=rows,
        summary=dict(cases=len(rows),tracking_calls=sum(x['tracking_calls'] for x in rows),
            exact_track_assignments=sum(x['exact_track_assignments'] for x in rows),
            exact_harmonic_acoustic_values=sum(x['exact_harmonic_acoustic_values'] for x in rows)),
        limits=['Neither default score1 nor the reviewed amplitude score2 is known to be the original maker configuration',
                'First complete window initializes history; preceding four frames are not reconstructed',
                'No temporal queue repair or full DRS entry is claimed',
                'Amplitude-matched peak is an oracle diagnostic only, not a selection algorithm',
                'Half-LSB source is a fixed hypothesis, not the original unquantized PCM'],
        no_windows_temp=True,installed_files_unchanged=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ('dll','manifest','voice-root','ddb-tools','aux-vdb','output'):
        parser.add_argument('--'+option,type=Path,required=True)
    parser.add_argument('--scores',type=int,nargs='+',choices=(1,2),default=[1,2])
    args = parser.parse_args()
    if len(set(args.scores)) != len(args.scores):
        parser.error('duplicate scoring branches')
    report = run(args)
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(report['summary']))


if __name__ == '__main__':
    main()
