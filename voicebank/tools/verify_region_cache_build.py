"""Exercise native ART/STA frame/PCM cache construction on owned synthetic files."""
import argparse
import ctypes as c
import json
from pathlib import Path
import struct

from audit_frm2_numeric import Native, P, I, Q, F, SHA256
from verify_region_frame_blend import cents
from probe_frm2 import UnvoicedFrame, UNVOICED_MASK, serialize_frame


def run(dll, work, stationary=False, lifecycle=False):
    work.mkdir(parents=True, exist_ok=True)
    n = Native(dll)
    build_art = n.fn(0x1b9bd0, None, P, c.c_char_p, c.c_char_p, P, P)
    build_sta = n.fn(0x1ba960, None, P, c.c_char_p, P, P)

    def build(outer, left, right, root, table):
        if stationary:
            build_sta(outer, right, root, table)
        else:
            build_art(outer, left, right, root, table)
    load = n.fn(0x175dd0, None, P, P, I, P, I, I, P)
    crt = c.CDLL('ucrtbase.dll')
    crt._wfopen.argtypes, crt._wfopen.restype = [c.c_wchar_p, c.c_wchar_p], P
    crt.fclose.argtypes, crt.fclose.restype = [P], I

    def node(name, children=()):
        obj = n.alloc(0x280)
        raw = name.encode('ascii') + b'\0'
        c.memmove(obj + 0x18, raw, len(raw))
        P.from_address(obj + 0x148).value = n.array(P, children)
        I.from_address(obj + 0x150).value = len(children)
        return obj

    rows = []
    lifecycle_rows = []
    retained_rows = []
    window_cases = 0
    frames = 40
    for window, hop in [(16, 8), (64, 8), (2048, 256)]:
        count = frames * hop + window
        prefix = 4096
        samples = [(i * 193 % 60001) - 30000 for i in range(prefix + count + 4096)]
        pcm_blob = struct.pack('<' + str(len(samples)) + 'h', *samples)
        offsets = []
        blob = bytearray(pcm_blob)
        for index in range(frames):
            offsets.append(len(blob))
            blob.extend(serialize_frame(UnvoicedFrame(0, index * hop / 44100,
                                                       UNVOICED_MASK, 220.0 + index * 3)))
        path = work / f'owned_cache_w{window}.bin'
        with path.open('xb') as file:
            file.write(blob)
        handle = crt._wfopen(str(path.resolve()), 'rb')
        if not handle:
            raise OSError('Cannot open owned cache fixture')
        try:
            for mode in [0, 1, 2]:
                for core in [0, 1, window // 2, window // 2 + 1, window]:
                    config, context, outer = (n.alloc(s) for s in [0x100, 0x2b000, 0x4300])
                    P.from_address(outer).value = config
                    P.from_address(outer + 8).value = context
                    I.from_address(config + 0x80).value = mode
                    c.c_double.from_address(context + 0x1d558).value = .01
                    F.from_address(context + 0x1d550).value = hop
                    I.from_address(context + 0x2af18).value = 3
                    analysis = n.alloc(0x100)
                    I.from_address(analysis + 0x8c).value = window
                    P.from_address(context + 0x1fc8).value = analysis
                    part = n.alloc(0x280)
                    P.from_address(part + 0x1a8).value = n.array(Q, offsets)
                    I.from_address(part + 0x1b0).value = frames
                    Q.from_address(part + 0x1b8).value = prefix * 2
                    Q.from_address(part + 0x1c0).value = (prefix + core) * 2
                    I.from_address(part + 0x1d0).value = count
                    align = n.array(I, [0, 20, 6, 14, 20, 40, 24, 31])
                    P.from_address(part + 0x1d8).value = align
                    P.from_address(part + 0x1e0).value = align + 32
                    # Deliberately different: first cache allocation must adopt config.
                    I.from_address(part + 0x200).value = 77
                    unit = node('b', [part])
                    I.from_address(unit + 8).value = 0x75415453 if stationary else 0x75545241
                    I.from_address(unit + 0x160).value = 0
                    branch = node('stationary', [node('normal', [unit])]) if stationary else node('articulation', [node('a', [unit])])
                    root = node('root', [node('voice', [branch])])
                    stream = n.alloc(0x80)
                    P.from_address(stream + 8).value = handle
                    I.from_address(stream + 0x2c).value = 0x32534d53
                    P.from_address(root + 0x1a0).value = stream
                    table = n.alloc(0x180)
                    token = n.alloc(2)
                    c.memmove(token, b'b\0', 2)
                    I.from_address(table + 0x144).value = 1
                    P.from_address(table + 0x148).value = n.array(P, [token])
                    build(outer, b'a', b'b', root, table)
                    actual_mode = I.from_address(part + 0x200).value
                    cache = P.from_address(part + 0x258).value
                    assert actual_mode == mode, (window, mode, actual_mode)
                    assert bool(cache) == (mode in [1, 2])
                    outside = 0
                    if cache:
                        actual = list((c.c_int16 * count).from_address(cache))
                        begin = prefix + core - window // 2
                        expected = samples[begin:begin + count]
                        assert actual == expected, (window, mode, core)
                        outside = sum(i < prefix or i >= prefix + count
                                      for i in range(begin, begin + count))
                        # Existing caches retain their mode on a subsequent request.
                        I.from_address(config + 0x80).value = (mode + 1) % 3
                        build(outer, b'a', b'b', root, table)
                        assert I.from_address(part + 0x200).value == mode
                        assert P.from_address(part + 0x258).value == cache
                    if stationary:
                        if mode < 2:
                            assert P.from_address(part + 0x248).value
                        else:
                            frame_cache = P.from_address(part + 0x250).value
                            assert frame_cache and all((P * frames).from_address(frame_cache))
                    elif mode < 2:
                        assert I.from_address(part + 0x204).value == (3 if mode == 0 else 15)
                        assert I.from_address(part + 0x208).value == (2 if mode == 0 else 10)
                    else:
                        frame_cache = P.from_address(part + 0x240).value
                        assert frame_cache and all((P * frames).from_address(frame_cache))
                    # Connect the real cache to the full frame loader, including
                    # its file fallback. The registry and control flag are owned.
                    registry = n.alloc(0x20)
                    I.from_address(registry + 8).value = 1
                    P.from_address(registry + 0x10).value = n.array(P, [root])
                    P.from_address(registry + 0x18).value = n.array(P, [n.alloc(8)])
                    P.from_address(config + 0x10).value = registry
                    P.from_address(context).value = config
                    controls = n.alloc(0x100)
                    P.from_address(context + 0x1d568).value = controls
                    c.c_double.from_address(context + 0x1d558).value = .01
                    F.from_address(context + 0x1d550).value = hop
                    frame_work, output_frame = n.alloc(0x140), n.alloc(0x328)
                    P.from_address(frame_work + 0x20).value = part
                    n.fn(0x12a670, P, P)(output_frame)
                    destination = n.alloc((window + 1) * 2 + 32)
                    P.from_address(context + 0x29510).value = destination + 16
                    loaded = n.alloc(4)
                    for active in [0, 1]:
                        c.c_ubyte.from_address(controls + 0x78).value = active
                        for requested in [-1, 0, 1, frames - 1, frames + 5]:
                            frame_index = max(0, min(frames - 1, requested))
                            c.memset(destination, 0xA5, (window + 1) * 2 + 32)
                            I.from_address(loaded).value = 0
                            load(context, frame_work, requested, output_frame, int(stationary), 1, loaded)
                            begin = prefix + core + frame_index * hop - window // 2
                            positions = range(begin, begin + window + 1)
                            expected = [samples[i] if active and cache or prefix <= i < prefix + count
                                        else 0 for i in positions]
                            actual = list((c.c_int16 * (window + 1)).from_address(destination + 16))
                            assert actual == expected, (window, mode, core, active, requested)
                            assert I.from_address(loaded).value == 1
                            assert c.c_double.from_address(output_frame + 0x148).value == frame_index * hop / 44100
                            assert abs(F.from_address(output_frame + 0x1d0).value - cents(220.0 + frame_index * 3)) < 1e-4
                            assert c.string_at(destination, 16) == b'\xA5' * 16
                            assert c.string_at(destination + 16 + (window + 1) * 2, 16) == b'\xA5' * 16
                            window_cases += 1
                    rows.append(dict(window=window, hop=hop, frames=frames, pcm_count=count,
                                     mode=mode, core_offset_samples=core, pcm_cache=bool(cache),
                                     samples_read_outside_snd=outside))
                    if lifecycle:
                        cleanup = n.fn(0x1b4df0, None, P)
                        cache_offsets = [0x210, 0x218, 0x220, 0x228, 0x230,
                                         0x238, 0x240, 0x248, 0x250, 0x258]
                        for requested_mode in [0, 1, 2, 0]:
                            previous_mode = I.from_address(part + 0x200).value
                            before = [P.from_address(part + off).value for off in cache_offsets]
                            I.from_address(config + 0x80).value = requested_mode
                            build(outer, b'a', b'b', root, table)
                            # STA's mode adoption guard excludes its first-frame
                            # pointer (+248), unlike the ART cache arrays.
                            expected_mode = requested_mode if stationary and previous_mode == 0 else previous_mode
                            assert I.from_address(part + 0x200).value == expected_mode
                            after = [P.from_address(part + off).value for off in cache_offsets]
                            assert all(not old or old == new for old, new in zip(before, after))
                            assert bool(P.from_address(part + 0x258).value) == (expected_mode in [1, 2])
                            retained_rows.append(dict(original_mode=mode, previous_mode=previous_mode,
                                                      requested_mode=requested_mode, actual_mode=expected_mode))
                        for next_mode in [0, 1, 2, 0]:
                            # Destroy only native-owned caches, through the actual
                            # builder-maintained tracking vector. Never restore
                            # pointers from snapshots after freeing them.
                            cleanup(outer)
                            assert all(not P.from_address(part + off).value
                                       for off in cache_offsets)
                            assert P.from_address(outer + 0x4208).value == P.from_address(outer + 0x4200).value
                            cleanup(outer)
                            I.from_address(config + 0x80).value = next_mode
                            build(outer, b'a', b'b', root, table)
                            assert I.from_address(part + 0x200).value == next_mode
                            assert (P.from_address(outer + 0x4208).value -
                                    P.from_address(outer + 0x4200).value) == 8
                            assert P.from_address(P.from_address(outer + 0x4200).value).value == part
                            new_cache = P.from_address(part + 0x258).value
                            assert bool(new_cache) == (next_mode in [1, 2])
                            for active in [0, 1]:
                                c.c_ubyte.from_address(controls + 0x78).value = active
                                for index in range(frames):
                                    c.memset(destination, 0xA5, (window + 1) * 2 + 32)
                                    load(context, frame_work, index, output_frame, int(stationary), 1, loaded)
                                    assert c.c_double.from_address(output_frame + 0x148).value == index * hop / 44100
                                    assert abs(F.from_address(output_frame + 0x1d0).value - cents(220. + index * 3)) < 1e-4
                                    begin = prefix + core + index * hop - window // 2
                                    expected = [samples[i] if active and new_cache or prefix <= i < prefix + count
                                                else 0 for i in range(begin, begin + window + 1)]
                                    assert list((c.c_int16 * (window + 1)).from_address(destination + 16)) == expected
                                    assert c.string_at(destination, 16) == b'\xA5' * 16
                                    assert c.string_at(destination + 16 + (window + 1) * 2, 16) == b'\xA5' * 16
                            lifecycle_rows.append(dict(window=window, core=core, original_mode=mode,
                                                       rebuilt_mode=next_mode, frame_reads=frames * 2))
                        cleanup(outer)
        finally:
            crt.fclose(handle)
    return dict(material='STA' if stationary else 'ART', dll_sha256=SHA256, cases=len(rows), window_cases=window_cases,
                mismatches=0, rows=rows, lifecycle=lifecycle_rows,
                rebuild_cases=len(lifecycle_rows),
                retained_mode_cases=len(retained_rows), retained_modes=retained_rows,
                rebuild_frame_reads=sum(row['frame_reads'] for row in lifecycle_rows),
                scope=('Full native STA' if stationary else 'Full native ART') +
                      ' cache builder with synthetic tree, distinct F0/time marker FRM2 and PCM; '
                      'connected to native frame loader with private registry; '
                      'no host mode or commercial-material invocation claim')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--stationary', action='store_true')
    parser.add_argument('--lifecycle', action='store_true')
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--work-dir', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = run(args.dll, args.work_dir, args.stationary, args.lifecycle)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({k: result[k] for k in ['cases', 'window_cases', 'rebuild_cases', 'rebuild_frame_reads', 'mismatches']}))
