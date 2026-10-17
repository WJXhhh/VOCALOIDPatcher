"""Native transition preparation and bounded common-postprocessor contracts.

Owned objects only; no Editor, reference PCM, or Windows temporary files.
Native allocations are retained until this disposable process exits.
"""
import argparse
import ctypes as c
import itertools
import json
from pathlib import Path

from audit_frm2_numeric import Native, P, I, Q, F, D, B, SHA256
from verify_region_frame_blend import EMPTY


def run(dll):
    n = Native(dll)
    init = n.fn(0x12a670, P, P)
    prepare = n.fn(0x1751f0, None, P, I, I, B, B, I, I, I, I, F)
    invalidate = n.fn(0x1cc0a0, None, P, B)
    clear = n.fn(0x12cf40, Q, P, Q, I, I)
    harmonics = n.fn(0x12ba10, Q, P, I, Q, I)
    post = n.fn(0x16fcb0, Q, P, I, P, P, P, F, B, B)
    dispatch = n.fn(0x16af70, None, P, P)
    cache = n.alloc(0x80)
    curve = n.array(F, [-9.] * 500)
    P.from_address(cache + 0x58).value = curve
    prepare(cache, 7, 11, 0, 1, 13, 17, 3, 5, 8.)
    frames = [P.from_address(cache + offset).value for offset in [8, 16]]
    assert all(frames) and frames[0] != frames[1]
    for j, frame in enumerate(frames):
        Q.from_address(frame + 0x150).value = 0x80000600
        F.from_address(frame + 0x1d0).value = 123. + j
        F.from_address(frame + 0x1d8).value = 77. + j
        D.from_address(frame + 0x148).value = 99. + j
    snapshots = [c.string_at(frame, 0x328) for frame in frames]
    preparation = []
    for left, right, a, b, scale in itertools.product([0, 7], [-1, 11], [0, 1], [0, 1], [.5, 8.]):
        before = c.string_at(cache, 0x80)
        invalidate(cache, 0)
        expected = bytearray(before)
        expected[0x44] = 0
        assert c.string_at(cache, 0x80) == expected
        c.memset(curve, 0, 2000)
        prepare(cache, left, right, a, b, 13, 17, 3, 5, scale)
        assert B.from_address(cache + 0x44).value == 1
        assert [P.from_address(cache + off).value for off in [8, 16]] == frames
        assert [c.string_at(frame, 0x328) for frame in frames] == snapshots
        assert list((F * 500).from_address(curve)) == [100.] * 500
        assert [I.from_address(cache + off).value for off in [0x48, 0x4c]] == [left, right]
        assert [B.from_address(cache + off).value for off in [0x50, 0x51]] == [a, b]
        assert [I.from_address(cache + off).value for off in [0x68, 0x6c, 0x70, 0x74]] == [13, 17, 3, 5]
        assert F.from_address(cache).value == scale
        assert F.from_address(cache + 4).value == scale / 2
        assert all(I.from_address(cache + off).value == 0 for off in [0x54, 0x60, 0x64])
        preparation.append(dict(left=left, right=right, stationary=[a, b], scale=scale,
                                frames_preserved=True, curve_reset=True))

    context, controls, item, record, score, part = [n.alloc(size) for size in
        [0x2b200, 0x5ab00, 0x140, 0x18, 0x58, 0x280]]
    P.from_address(context + 0x1d568).value = controls
    F.from_address(context + 0x2208).value = 440.
    F.from_address(context + 0x1d564).value = 22050.
    D.from_address(context + 0x1d558).value = .005
    P.from_address(record).value = item
    I.from_address(item + 0x14).value = -1
    first, output = n.alloc(0x328), n.alloc(0x328)
    init(first)
    init(output)
    P.from_address(context + 0x247b8).value = first
    P.from_address(context + 0x247c0).value = first
    ring = n.alloc(4 * 0x140)
    P.from_address(controls + 0x20).value = ring
    I.from_address(controls + 0x2c).value = 4
    P.from_address(controls + 0x50).value = ring
    I.from_address(controls + 0x58).value = 4
    I.from_address(controls + 0x5c).value = 3
    gates = []
    for start, distance, flags in itertools.product(range(4), [1, 2, 3], [(0, 0), (1, 0), (1, 1)]):
        for slot in range(4):
            B.from_address(ring + slot * 0x140 + 0xe4).value = flags[0]
            B.from_address(ring + slot * 0x140 + 0x98).value = flags[1]
        P.from_address(record).value = ring + start * 0x140
        I.from_address(controls + 0x60).value = (start + distance) & 3
        before = c.string_at(ring, 4 * 0x140)
        dispatch(context, record)
        assert c.string_at(ring, 4 * 0x140) == before
        gates.append(dict(start=start, end=(start + distance) & 3, prepared=flags[0],
                          enabled=flags[1], unchanged=True))
    P.from_address(record).value = item
    cleanup_mask = Q.from_address(n.lib._handle + 0xa12588).value
    assert cleanup_mask == 0xe00000, hex(cleanup_mask)
    cases = []
    causes = ['no_part', 'empty_source', 'empty_candidate', 'empty_target', 'ratio_too_low', 'ratio_too_high', 'record_flag']
    for cause, pitch_bit, encoding, scalar, count in itertools.product(causes, [0, 0x200], [0, 0x80000000], [0, 0x400], [0, 3]):
        clear(output, 0x6f0fffffff, 0, 0)
        if count:
            harmonics(output, count, 7, 1)
            for off in [0x160, 0x168, 0x170]:
                pointer = P.from_address(output + off).value
                assert pointer
                for j in range(count):
                    F.from_address(pointer + 4*j).value = float(j + 1)
        Q.from_address(output + 0x150).value = (7 if count else 0) | pitch_bit | encoding | scalar | cleanup_mask
        # List counts can be cleared without destroying entries (param4=0).
        I.from_address(output + 0x260).value = 3
        I.from_address(output + 0x27c).value = 2
        F.from_address(output + 0x1d0).value = 123.
        F.from_address(output + 0x1d8).value = 77.
        D.from_address(output + 0x148).value = 99.
        P.from_address(item + 0x20).value = None if cause == 'no_part' else part
        F.from_address(part + 0x164).value = {'empty_source': EMPTY, 'ratio_too_high': -1200.}.get(cause, 0.)
        F.from_address(first + 0x1d0).value = EMPTY if cause == 'empty_candidate' else 0.
        B.from_address(record + 0xd).value = cause == 'record_flag'
        F.from_address(score).value = {'empty_target': EMPTY, 'ratio_too_low': -24000.,
                                       'ratio_too_high': 12000.}.get(cause, 0.)
        F.from_address(score + 20).value = EMPTY
        F.from_address(score + 32).value = 123.
        F.from_address(score + 36).value = 123.
        # Explicitly select the branch without special-object generation.
        assert post(context, 0, output, record, score, 0., 0, 1) == 0
        mask = Q.from_address(output + 0x150).value
        pitch = F.from_address(output + 0x1d0).value
        expected_pitch = 0. if pitch_bit and cause != 'record_flag' else EMPTY
        assert mask == encoding | scalar | (7 if count else 0), (cause, hex(mask))
        assert pitch == expected_pitch, (cause, pitch_bit, pitch, expected_pitch)
        assert I.from_address(output + 0x158).value == count
        for off in [0x160, 0x168, 0x170]:
            pointer = P.from_address(output + off).value
            if count:
                assert list((F * count).from_address(pointer)) == [float(j + 1) for j in range(count)]
            else:
                assert pointer is None
        assert I.from_address(output + 0x260).value == 0
        assert I.from_address(output + 0x27c).value == 0
        assert F.from_address(output + 0x1d8).value == 77.
        assert D.from_address(output + 0x148).value == 99.
        assert F.from_address(score + 32).value == EMPTY
        assert F.from_address(score + 36).value == EMPTY
        cases.append(dict(cause=cause, input_pitch_valid=bool(pitch_bit), encoding=bool(encoding),
                          scalar_valid=bool(scalar), harmonics=count, mask=hex(mask), pitch=pitch))
    return dict(dse_sha256=SHA256, preparation_cases=len(preparation), postprocess_cases=len(cases),
                gate_cases=len(gates), gates=gates, cleanup_mask=hex(cleanup_mask),
                preparation=preparation, postprocess=cases,
                scope='Native preparation/invalidate reuse and invalid-voicing common postprocess; '
                      'owned frames, empty special/pitch-record vectors, eighth argument=1; '
                      'not full dispatcher regeneration, host render, or all acoustic mixing')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = run(args.dll)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({key: value for key, value in result.items() if key not in ['preparation', 'postprocess', 'gates']}))
