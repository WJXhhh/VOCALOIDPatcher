"""Observe actual secondary-layer frame reads for owned region boundaries."""
import argparse
import ctypes as c
import json
from pathlib import Path

from audit_frm2_numeric import Native, P, I, Q, F, D, B, SHA256, f32
from probe_frm2 import UnvoicedFrame, UNVOICED_MASK, serialize_frame


def mapped(source, target, position):
    outer0, outer1, inner0, inner1 = source
    dest0, dest1, anchor0, anchor1 = target
    inner1 -= 1
    outer1 -= 1
    anchor1 -= 1
    dest1 -= 1
    anchor0 = min(anchor0, anchor1)
    if position < inner0:
        a, b, x, y = outer0, inner0, dest0, anchor0
    elif position > inner1:
        a, b, x, y = inner1, outer1, anchor1, dest1
    else:
        a, b, x, y = inner0, inner1, anchor0, anchor1
    return float(x) if a == b else f32(f32(f32((position-a)/(b-a)) * (y-x)) + x)


def run(dll, work):
    work.mkdir(parents=True, exist_ok=True)
    n = Native(dll)
    evaluate = n.fn(0x1722c0, None, P, P, I, I, P, P, I, I, B, P)
    cross_evaluate = n.fn(0x172e30, None, P, P, I, I, P, I, P, F, P)
    init = n.fn(0x12a670, P, P)
    frames = 96
    blob = bytearray(b'\0' * 8192)
    offsets = []
    for index in range(frames):
        offsets.append(len(blob))
        # An index-valued time makes native reads directly observable.
        blob.extend(serialize_frame(UnvoicedFrame(0, float(index), UNVOICED_MASK, 0.0)))
    path = work / 'owned_mapping_frames.bin'
    with path.open('xb') as file:
        file.write(blob)
    crt = c.CDLL('ucrtbase.dll')
    crt._wfopen.argtypes, crt._wfopen.restype = [c.c_wchar_p, c.c_wchar_p], P
    crt.fclose.argtypes, crt.fclose.restype = [P], I
    handle = crt._wfopen(str(path.resolve()), 'rb')
    if not handle:
        raise OSError('Cannot open owned mapping frames')
    context, controls, config, registry, voice, stream = (n.alloc(s) for s in
        [0x2b000, 0x5ab00, 0x100, 0x20, 0x280, 0x80])
    P.from_address(context).value = config
    P.from_address(config + 0x10).value = registry
    I.from_address(registry + 8).value = 1
    P.from_address(registry + 0x10).value = n.array(P, [voice])
    P.from_address(registry + 0x18).value = n.array(P, [n.alloc(8)])
    P.from_address(voice + 0x1a0).value = stream
    P.from_address(stream + 8).value = handle
    I.from_address(stream + 0x2c).value = 0x32534d53
    P.from_address(context + 0x1d568).value = controls
    P.from_address(controls).value = n.alloc(0x18)
    I.from_address(controls + 8).value = 1
    score = n.alloc(0x58)
    F.from_address(score).value = 200.0
    F.from_address(score + 0x10).value = 400.0
    F.from_address(score + 0x2c + 0x10).value = 400.0
    P.from_address(controls + 0x10).value = score
    I.from_address(controls + 0x18).value = 2
    analysis = n.alloc(0x100)
    I.from_address(analysis + 0x8c).value = 16
    P.from_address(context + 0x1fc8).value = analysis
    F.from_address(context + 0x1d550).value = 8
    P.from_address(context + 0x24118).value = n.alloc(34)
    for scratch in [0x29860, 0x29b88, 0x29eb0]:
        init(context + scratch)
    parent = n.alloc(0x180)
    callback = c.CFUNCTYPE(P, P)(lambda obj: parent)
    n.keep.append(callback)
    vtable = n.alloc(0x68)
    P.from_address(vtable + 0x60).value = c.cast(callback, P).value
    source, target = n.alloc(0x280), n.alloc(0x280)
    for part, pitch in [(source, 0.0), (target, 400.0)]:
        P.from_address(part).value = vtable
        F.from_address(part + 0x160).value = pitch
        F.from_address(part + 0x16c).value = .6
        B.from_address(part + 0x180).value = 1
        P.from_address(part + 0x188).value = n.array(F, [0.0] * 4)
        P.from_address(part + 0x1a8).value = n.array(Q, offsets)
        I.from_address(part + 0x1b0).value = frames
        Q.from_address(part + 0x1b8).value = 128
        Q.from_address(part + 0x1c0).value = 256
        I.from_address(part + 0x1d0).value = 1024
    P.from_address(parent + 0x148).value = n.array(P, [source, target])
    I.from_address(parent + 0x150).value = 2
    item, current_frame, gain = n.alloc(0x140), n.alloc(0x328), n.alloc(4)
    P.from_address(item + 0x20).value = source
    I.from_address(item + 0x14).value = 1
    init(current_frame)
    # Second voice uses a real directory lookup with matched/missing/empty triples.
    def node(name, children=()):
        result = n.alloc(0x280)
        raw = name.encode('ascii') + b'\0'
        c.memmove(result + 0x18, raw, len(raw))
        P.from_address(result + 0x148).value = n.array(P, children)
        I.from_address(result + 0x150).value = len(children)
        return result

    pair = node('b', [target])
    I.from_address(pair + 8).value = 0x75545241
    I.from_address(pair + 0x160).value = 1
    triple = node('c', [target])
    middle = node('^b', [triple])
    first = node('a', [pair, middle])
    other_voice = node('root', [node('voice', [node('articulation', [first])])])
    P.from_address(other_voice + 0x1a0).value = stream
    table = n.alloc(0x180)
    tokens = []
    path_names = n.alloc(96)
    for index, token in enumerate([b'a', b'b', b'c']):
        pointer = n.alloc(2)
        c.memmove(pointer, token + b'\0', 2)
        tokens.append(pointer)
        c.memmove(path_names + 32 * index, token + b'\0', 2)
        Q.from_address(path_names + 32 * index + 16).value = 1
        Q.from_address(path_names + 32 * index + 24).value = 15
    I.from_address(table + 0x144).value = 3
    P.from_address(table + 0x148).value = n.array(P, tokens)
    P.from_address(voice + 0x170).value = table
    P.from_address(other_voice + 0x170).value = table
    P.from_address(item + 0x70).value = path_names
    I.from_address(registry + 8).value = 2
    P.from_address(registry + 0x10).value = n.array(P, [voice, other_voice])
    other_id = n.array(I, [1, 0])
    P.from_address(registry + 0x18).value = n.array(P, [n.alloc(8), other_id])
    I.from_address(context + 0x29530).value = 1
    rows = []
    layouts = [(0, 12, 3, 9), (0, 12, 6, 7), (0, 12, 0, 12),
               (0, 1, 0, 1), (0, 8, 0, 1), (0, 8, 7, 8)]
    try:
        for src in layouts:
            for dst in layouts:
                ratio = (src[1] - src[0]) / (dst[1] - dst[0])
                if not 1/3 <= ratio <= 3:
                    continue
                for region in range(4):
                    source_groups = [(20*i+a, 20*i+b, 20*i+x, 20*i+y)
                                     for i in range(4) for a, b, x, y in [src]]
                    target_groups = [(22*i+a, 22*i+b, 22*i+x, 22*i+y)
                                     for i in range(4) for a, b, x, y in [dst]]
                    for part, groups in [(source, source_groups), (target, target_groups)]:
                        align = n.array(I, [v for group in groups for v in group])
                        P.from_address(part + 0x1d8).value = align
                        P.from_address(part + 0x1e0).value = align + 64
                    sg, tg = source_groups[region], target_groups[region]
                    for position in range(sg[0], sg[1]):
                        expected = mapped(sg, tg, position)
                        low = int(expected)
                        fraction = f32(expected - low)
                        nearest = low + (fraction > .5)
                        # Reset timestamps only; retain initialized native objects.
                        D.from_address(context + 0x29b88 + 0x148).value = -1
                        D.from_address(context + 0x29eb0 + 0x148).value = -1
                        evaluate(context, item, region, position, current_frame, 0, 0, 0, 0, gain)
                        actual = D.from_address(context + 0x29b88 + 0x148).value
                        next_read = D.from_address(context + 0x29eb0 + 0x148).value
                        assert actual == nearest, (sg, tg, position, expected, actual)
                        # Native scratch reset clears time to zero when no second read occurs.
                        assert next_read == (low + 1 if fraction > 0 else 0), (expected, next_read)
                        rows.append(dict(path='same_parent', source=sg, target=tg, region=region, position=position,
                                         expected_position=expected, observed_nearest=actual,
                                         observed_second=next_read))
                    for variant in ['cross_matched', 'cross_missing_triple', 'cross_empty_triple']:
                        I.from_address(first + 0x150).value = 1 if variant == 'cross_missing_triple' else 2
                        I.from_address(triple + 0x150).value = 0 if variant == 'cross_empty_triple' else 1
                        target_count = 4 if variant == 'cross_matched' else 2
                        target_region = region if target_count == 4 else region % 2
                        P.from_address(target + 0x1e0).value = P.from_address(target + 0x1d8).value + target_count * 16
                        tg = target_groups[target_region]
                        for position in range(sg[0], sg[1]):
                            expected = mapped(sg, tg, position)
                            low = int(expected)
                            fraction = f32(expected - low)
                            cross_evaluate(context, item, region, position, current_frame, 0, 0, .5, gain)
                            actual = D.from_address(context + 0x29b88 + 0x148).value
                            next_read = D.from_address(context + 0x29eb0 + 0x148).value
                            assert actual == low + (fraction > .5), (variant, sg, tg, position, expected, actual)
                            assert next_read == (low + 1 if fraction > 0 else 0), (variant, expected, next_read)
                            rows.append(dict(path=variant, source=sg, target=tg, region=region,
                                             target_region=target_region, position=position,
                                             expected_position=expected, observed_nearest=actual,
                                             observed_second=next_read))
    finally:
        crt.fclose(handle)
    return dict(dll_sha256=SHA256, cases=len(rows), mismatches=0,
                rows=rows, scope='Full same-parent and other-voice secondary-layer functions with owned frames; '
                'observes integer frame reads and half-frame selection, not acoustic interpolation weights; '
                'parent virtual accessor supplied by fixture, intensity cache preinitialized')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--work-dir', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = run(args.dll, args.work_dir)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({k: result[k] for k in ['cases', 'mismatches']}))
