"""Connect owned on-disk F0 frames to the full region position consumer."""
import argparse
import ctypes as c
import itertools
import json
import math
import struct
from pathlib import Path

from audit_frm2_numeric import Native, P, I, Q, F, D, B, SHA256, f32
from probe_frm2 import UnvoicedFrame, UNVOICED_MASK, serialize_frame
from verify_region_frame_blend import EMPTY, cents, mix


def run(dll, work, boundaries=False, lifecycle=False, postprocess=False, special_entry=False,
        wrapper=False, transition=False):
    work.mkdir(parents=True, exist_ok=True)
    n = Native(dll)
    init = n.fn(0x12a670, P, P)
    consume = n.fn(0x16a870, Q, P, I, P, P, B, B)
    clear = n.fn(0x12cf40, Q, P, Q, I, I)
    post = n.fn(0x16fcb0, Q, P, I, P, P, P, F, B, B)
    boundary = n.fn(0x16a0b0, Q, P, I, P, P, P)
    threshold = D.from_address(n.lib._handle + 0x5bf090).value
    assert threshold == 1e-8
    pitches = [220.0, 440.0, 0.0, 0.0, 330.0, 660.0]
    blob = bytearray(8192)
    if wrapper:
        # Nonzero owned PCM ensures the enabled analysis path does real work.
        blob[:] = struct.pack('<4096h', *[(j * 193 % 60001) - 30000 for j in range(4096)])
    offsets = []
    for index, pitch in enumerate(pitches):
        offsets.append(len(blob))
        blob.extend(serialize_frame(UnvoicedFrame(0, float(index + 10), UNVOICED_MASK, pitch)))
    path = work / 'owned_position_frames.bin'
    with path.open('xb') as file:
        file.write(blob)
    crt = c.CDLL('ucrtbase.dll')
    crt._wfopen.argtypes, crt._wfopen.restype = [c.c_wchar_p, c.c_wchar_p], P
    crt.fclose.argtypes, crt.fclose.restype = [P], I
    handle = crt._wfopen(str(path.resolve()), 'rb')
    if not handle:
        raise OSError('Cannot open owned frames')
    try:
        context, controls, config, registry, voice, stream = [n.alloc(s) for s in
            [0x2b200, 0x5ab00, 0x100, 0x20, 0x280, 0x80]]
        F.from_address(context + 0x2208).value = 440.
        F.from_address(context + 0x1d564).value = 22050.
        D.from_address(context + 0x1d558).value = .005
        post_score = n.alloc(0x58)
        F.from_address(post_score + 20).value = EMPTY
        P.from_address(context).value = config
        P.from_address(config + 0x10).value = registry
        I.from_address(registry + 8).value = 1
        P.from_address(registry + 0x10).value = n.array(P, [voice])
        P.from_address(registry + 0x18).value = n.array(P, [n.alloc(8)])
        P.from_address(voice + 0x1a0).value = stream
        P.from_address(stream + 8).value = handle
        I.from_address(stream + 0x2c).value = 0x32534d53
        P.from_address(context + 0x1d568).value = controls
        # Disable layer blending; no cache exists, so the loader uses the file.
        B.from_address(controls + 0x78).value = 1
        P.from_address(controls).value = n.alloc(0x18)
        I.from_address(controls + 8).value = 1
        score = n.alloc(0x58)
        F.from_address(score).value = 200.0
        P.from_address(controls + 0x10).value = score
        I.from_address(controls + 0x18).value = 2
        analysis = n.alloc(0x330 if wrapper else 0x100)
        if wrapper:
            # Real analysis constructor owns window storage and MKL descriptors.
            n.fn(0x147d10, P, P, I, B, F, I, I, F, I)(analysis, 8, 0, 2., 50, 0, 0., 1)
            assert P.from_address(analysis + 8).value
            assert P.from_address(analysis + 0x98).value
            I.from_address(context + 0x24120).value = 1
        I.from_address(analysis + 0x8c).value = 16
        P.from_address(context + 0x1fc8).value = analysis
        F.from_address(context + 0x1d550).value = 8
        for field in [0x24118, 0x29510]:
            P.from_address(context + field).value = n.alloc(34)
        I.from_address(context + 0x24124).value = 16
        table, classes = n.alloc(0x180), n.alloc(0x180)
        P.from_address(voice + 0x170).value = table
        P.from_address(table + 0x158).value = classes
        names = []
        for name in [b'a\0', b'b\0']:
            token = n.alloc(2)
            c.memmove(token, name, 2)
            names.append(token)
        I.from_address(table + 0x144).value = 2
        P.from_address(table + 0x148).value = n.array(P, names)
        parent, part = n.alloc(0x180), n.alloc(0x280)
        callback = c.CFUNCTYPE(P, P)(lambda obj: parent)
        n.keep.append(callback)
        vtable = n.alloc(0x68)
        P.from_address(vtable + 0x60).value = c.cast(callback, P).value
        P.from_address(part).value = vtable
        P.from_address(part + 0x1a8).value = n.array(Q, offsets)
        I.from_address(part + 0x1b0).value = len(pitches)
        Q.from_address(part + 0x1b8).value = 128
        Q.from_address(part + 0x1c0).value = 256
        I.from_address(part + 0x1d0).value = 1024
        align = n.array(I, [0, 3, 0, 3, 3, 6, 3, 6])
        P.from_address(part + 0x1d8).value = align
        P.from_address(part + 0x1e0).value = align + 32
        item, record = n.alloc(0x140), n.alloc(0x18)
        P.from_address(item + 0x20).value = part
        I.from_address(item + 0x14).value = 1
        P.from_address(record).value = item
        first, second, output = [n.alloc(0x328) for _ in range(3)]
        for frame in [first, second, output]:
            init(frame)
        n.fn(0x12ba70, Q, P, I, Q, I)(first, 16, 0x30, 0)
        for offset in [0x29860, 0x29b88, 0x29eb0]:
            init(context + offset)
        rows = []
        positions = [f32(i + t) for i in range(5) for t in [0, .125, .499, .5, .501, .875]] + [5.0, 5.5, 8.0]
        if boundaries:
            bits = struct.unpack('<I', struct.pack('<f', threshold))[0]
            adjacent = [struct.unpack('<f', struct.pack('<I', bits + delta))[0] for delta in [-1, 0, 1]]
            positions += [EMPTY, -2., -1., -.875, -.5, -.125, -1e-8, -0., *adjacent,
                          f32(2**-149), f32(1. - 2**-24), f32(1. + 2**-23)]
        reused=n.alloc(0x328)
        init(reused)
        Q.from_address(reused+0x150).value=0x80000600
        F.from_address(reused+0x1d0).value=123.
        F.from_address(reused+0x1d8).value=77.
        D.from_address(reused+0x148).value=99.
        states=['constructed','prefilled']+(['main_reset'] if lifecycle else [])
        for initial_state, reverse, position, record_flag in itertools.product(states, [False, True],
                positions, [0, 1] if postprocess else [0]):
            B.from_address(item + 0x12c).value = reverse
            F.from_address(record + 8).value = position
            P.from_address(context + 0x247b8).value = first
            P.from_address(context + 0x247c0).value = second
            output = reused if initial_state=='main_reset' else n.alloc(0x328)
            if initial_state!='main_reset':init(output)
            for frame in [first, second] + ([output] if initial_state == 'prefilled' else []):
                D.from_address(frame + 0x148).value = 99.0
                F.from_address(frame + 0x1d0).value = 123.0
                Q.from_address(frame + 0x150).value = 0x200
            reset_before=None
            if initial_state=='main_reset':
                reset_before=dict(mask=hex(Q.from_address(output+0x150).value),
                                  pitch=F.from_address(output+0x1d0).value,
                                  time=D.from_address(output+0x148).value)
                clear(output,0x6f0fffffff,1,0)
                assert Q.from_address(output+0x150).value==0x80000000
                if int(reset_before['mask'],16)&0x200:assert F.from_address(output+0x1d0).value==0.
                assert F.from_address(output+0x1d8).value==0.
                assert D.from_address(output+0x148).value==reset_before['time']
            initial_pitch = F.from_address(output + 0x1d0).value
            initial_time = D.from_address(output + 0x148).value
            initial_mask = Q.from_address(output + 0x150).value
            initial_bytes = c.string_at(output, 0x328)
            second_bytes = c.string_at(second, 0x328)
            consume(context, 0, output, record, 0, 0)
            raw = min(f32(position), 5.0) if reverse else f32(position)
            index = math.ceil(raw) if reverse else int(raw)
            fraction = 0.0 if raw < 0 else f32(index - raw if reverse else raw - index)
            a = max(0, min(5, index))
            b = max(0, min(5, index + (-1 if reverse else 1)))
            pa, pb = pitches[a], pitches[b]
            selected = b if fraction > .5 else a
            if index < 0:
                expected_pitch, expected_time, remaining = EMPTY, 99.0, 0.0
            elif fraction <= threshold:
                expected_pitch, expected_time = cents(pa) if pa > 0 else EMPTY, a + 10.0
                remaining = fraction
            elif pa > 0 and pb > 0:
                expected_pitch = mix(cents(pa), cents(pb), fraction)
                expected_time = a + 10.0 + (b - a) * fraction
                remaining = f32(1 - fraction) if fraction > .5 else fraction
            elif pitches[selected] > 0:
                expected_pitch, expected_time = cents(pitches[selected]), selected + 10.0
                remaining = 0.0
            else:
                expected_pitch, expected_time, remaining = initial_pitch, initial_time, 0.0
                assert c.string_at(output, 0x328) == initial_bytes
            if fraction <= threshold:
                assert c.string_at(second, 0x328) == second_bytes
            output_mask = Q.from_address(output + 0x150).value
            untouched = fraction > threshold and pitches[selected] == 0
            assert output_mask == (initial_mask if index < 0 or untouched else 0x80000200), (initial_state, reverse, position, hex(output_mask), index, fraction)
            actual_pitch = F.from_address(output + 0x1d0).value
            actual_time = D.from_address(output + 0x148).value
            assert math.isclose(actual_pitch, expected_pitch, rel_tol=3e-7, abs_tol=2e-4), (reverse, position, actual_pitch, expected_pitch)
            assert actual_time == expected_time, (reverse, position, actual_time, expected_time)
            assert F.from_address(context + 0x2415c).value == remaining
            nearest = P.from_address(context + 0x247b8).value
            assert D.from_address(nearest + 0x148).value == (99.0 if index < 0 else selected + 10.0)
            post_result = None
            if postprocess:
                B.from_address(record + 0xd).value = record_flag
                post(context, 0, output, record, post_score, 0., 0, 0 if special_entry else 1)
                final_pitch = F.from_address(output + 0x1d0).value
                final_mask = Q.from_address(output + 0x150).value
                valid = index >= 0 and pitches[selected] > 0 and not record_flag
                if valid:
                    assert final_mask & 0x80000200 == 0x80000200
                    assert math.isclose(final_pitch, 0., abs_tol=3e-4), (position, final_pitch)
                    assert math.isclose(F.from_address(post_score + 32).value, 0., abs_tol=3e-4)
                else:
                    assert final_mask & 0xe00200 == 0, (position, hex(final_mask))
                    assert final_pitch == (0. if output_mask & 0x200 and not record_flag else EMPTY)
                    assert F.from_address(post_score + 32).value == EMPTY
                assert D.from_address(output + 0x148).value == actual_time
                post_result = dict(record_flag=record_flag, valid=valid, pitch=final_pitch,
                                   special_entry=special_entry,
                                   mask=hex(final_mask), time=actual_time)
                if wrapper:
                    # Independently construct the same F0 state. Never restore a
                    # raw object snapshot after native code may resize its arrays.
                    wa, wb, wo = [n.alloc(0x328) for _ in range(3)]
                    for frame in [wa, wb, wo]:
                        init(frame)
                    n.fn(0x12ba70, Q, P, I, Q, I)(wa, 16, 0x30, 0)
                    for frame in [wa, wb]:
                        D.from_address(frame + 0x148).value = 99.
                        F.from_address(frame + 0x1d0).value = 123.
                        Q.from_address(frame + 0x150).value = 0x200
                    D.from_address(wo + 0x148).value = initial_time
                    F.from_address(wo + 0x1d0).value = initial_pitch
                    Q.from_address(wo + 0x150).value = initial_mask
                    P.from_address(context + 0x247b8).value = wa
                    P.from_address(context + 0x247c0).value = wb
                    boundary(context, 0, wo, record, post_score)
                    wrapper_pitch = F.from_address(wo + 0x1d0).value
                    wrapper_mask = Q.from_address(wo + 0x150).value
                    assert wrapper_pitch == final_pitch, (position, wrapper_pitch, final_pitch)
                    assert wrapper_mask == final_mask, (position, hex(wrapper_mask), hex(final_mask))
                    assert D.from_address(wo + 0x148).value == actual_time
                    post_result['wrapper_verified'] = True
                    if index >= 0:
                        fft = P.from_address(context + 0x247c8).value
                        assert fft
                        fft_values = list((F * 16).from_address(fft))
                        assert all(math.isfinite(value) for value in fft_values)
                        assert any(value != 0. for value in fft_values)
                        post_result['pcm_fft_nonzero'] = True
                    else:
                        post_result['pcm_fft_nonzero'] = None
            rows.append(dict(initial_state=initial_state, reverse=reverse, position=position, first_index=a,
                             requested_index=index if raw != EMPTY else 'INT_MIN',
                             second_index=b if fraction > threshold else None, fraction=fraction,
                             pitch=actual_pitch, time=actual_time, remaining_fraction=remaining,
                             output_mask=hex(output_mask), unchanged=untouched,reset_before=reset_before,
                             postprocess=post_result))
        transition_result = None
        if transition:
            from verify_region_transition_rebuild import run_fixture
            transition_result = run_fixture(n, context, controls, part, pitches)
        return dict(dse_sha256=SHA256, cases=len(rows), rows=rows, interpolation_threshold=threshold,
                    transition=transition_result,
                    scope='Full position consumer, owned F0-only disk frames, file fallback with layer blending disabled; fresh/prefilled objects and optional native main-loop clear on a continuously reused object; optional common postprocess with empty special/pitch-record vectors; --special-entry uses eighth argument=0 and native special-entry guard context+0x1fc0=0; --wrapper uses actual boundary wrapper with independent frames, native FFT constructor and nonzero owned PCM; not a full host render')
    finally:
        crt.fclose(handle)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--work-dir', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--boundaries', action='store_true')
    parser.add_argument('--lifecycle', action='store_true')
    parser.add_argument('--postprocess', action='store_true')
    parser.add_argument('--special-entry', action='store_true')
    parser.add_argument('--wrapper', action='store_true')
    parser.add_argument('--transition', action='store_true')
    args = parser.parse_args()
    if args.special_entry and not args.postprocess:
        parser.error('--special-entry requires --postprocess')
    if args.wrapper and not args.special_entry:
        parser.error('--wrapper requires --special-entry')
    if args.transition and not args.wrapper:
        parser.error('--transition requires --wrapper')
    result = run(args.dll, args.work_dir, args.boundaries,args.lifecycle,args.postprocess,args.special_entry,args.wrapper,args.transition)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(cases=result['cases'], status='passed')))
