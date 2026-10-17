"""Resolve installed DVQM properties and read segment durations in place."""
import argparse
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
from verify_region_frame_blend import EMPTY
from audit_frm2_numeric import Native, P, I, Q, F, D, SHA256, f32


def run(dll, directory, load_samples=False, configure=False, audio=False, audio_limit=1, audio_blocks=4, audio_start=0, audio_harmonics=0, audio_no_gate=False, audio_prepass=False, audio_native_scalars=False, audio_native_factor=1., audio_pointer_initial=1.):
    assert 0 <= audio_start and 1 <= audio_limit <= 119 and 1 <= audio_blocks <= 1024
    assert math.isfinite(audio_native_factor) and 0. <= audio_native_factor <= 2.
    assert math.isfinite(audio_pointer_initial)
    n = Native(dll)
    root = n.fn(0xfea0, P, c.c_char_p)(str(directory.resolve()).encode())
    assert root
    resolve = n.fn(0x23f00, P, P, I, P)
    segments = n.fn(0x11180, Q, P, I, P, c.c_char_p, P)
    load_sample = n.fn(0x100a0, Q, P, I, P, c.c_char_p, P)
    destroy = n.fn(0xb480, None, P)
    sample_info = n.fn(0xb5b0, Q, P, P)
    loaded_samples = 0
    configured_samples = 0
    audio_rows = []

    def name(node):
        return c.string_at(P.from_address(node + 0x18).value).decode('ascii')

    def children(node):
        count = I.from_address(node + 0x50).value
        assert 0 <= count < 1000
        return list((P * count).from_address(P.from_address(node + 0x48).value)) if count else []

    rows = []
    try:
        for side, branch in enumerate(children(root)):
            assert name(branch) == ['attack', 'release'][side]

            def visit(node, path):
                nonlocal loaded_samples, configured_samples
                assert len(path) <= 4
                entries = {name(child): child for child in children(node)}
                if 'config' not in entries:
                    for label, child in entries.items():
                        assert label not in ['keywords', 'phmap']
                        visit(child, path + [label])
                    return
                descriptor = n.alloc(0x108)
                I.from_address(descriptor).value = len(path)
                for index, label in enumerate(path):
                    encoded = label.encode('ascii')
                    assert len(encoded) < 65
                    c.memmove(descriptor + 4 + index * 65, encoded, len(encoded))
                assert resolve(root, side, descriptor) == node
                samples = {label: child for label, child in entries.items()
                           if label not in ['config', 'keywords', 'phmap']}
                assert samples
                for phoneme, sample in samples.items():
                    metadata = P.from_address(sample + 0x68).value
                    assert metadata
                    points = P.from_address(metadata + 0x40).value
                    assert points
                    boundaries = list((F * 4).from_address(points))
                    expected = [f32(boundaries[j+1] - boundaries[j]) for j in range(3)]
                    output = n.alloc(44)
                    c.memset(output, 0xA5, 44)
                    status = segments(root, side, descriptor, phoneme.encode(), output + 16) & 0xffffffff
                    assert status == 0, (side, path, phoneme, status)
                    actual = list((F * 3).from_address(output + 16))
                    assert actual == expected and all(math.isfinite(x) for x in actual)
                    assert c.string_at(output, 16) == b'\xA5' * 16
                    assert c.string_at(output + 28, 16) == b'\xA5' * 16
                    configuration_state = None
                    if load_samples:
                        # Use the actual constructor: the control includes a
                        # sample-rate field at +a8 beyond its parameter fields.
                        control = n.fn(0xb400, P)()
                        assert control
                        try:
                            assert load_sample(root, side, descriptor, phoneme.encode(), control) == 0
                            internal = P.from_address(control).value
                            assert internal and P.from_address(internal).value == sample
                            info = n.alloc(0x64)
                            c.memset(info, 0xA5, 0x64)
                            assert sample_info(info + 16, control) == 0
                            duration = F.from_address(info + 20).value
                            assert math.isfinite(duration) and duration > 0
                            assert c.string_at(info, 16) == b'\xA5' * 16
                            assert c.string_at(info + 0x54, 16) == b'\xA5' * 16
                            sample_scalars = [F.from_address(info + 16 + off).value
                                              for off in (0x34, 0x38, 0x3c, 0x40)]
                            assert all(math.isfinite(value) for value in sample_scalars)
                            sample_flags = [c.c_uint8.from_address(info + 16 + off).value
                                            for off in range(4)]
                            sample_feature = I.from_address(info + 16 + 0x10).value
                            loaded_samples += 1
                            if configure:
                                count = 101
                                times = n.array(F, [j * .01 for j in range(count)])
                                pitch = n.array(F, [0.] * count)
                                overrides = n.array(F, [EMPTY] * count)
                                dynamics = n.array(F, [.6] * count)
                                other = n.array(F, [.5] * count)
                                config = n.fn(0xb6b0, Q, F, F, F, F, F, F, I, P, P, P, P, P, P)
                                # 179410 stores the four getSampleInf floats in a reordered
                                # object, then passes them to setconfig in their original order.
                                scale = [min(1., max(0., audio_native_factor * sample_scalars[j]))
                                         for j in (0, 1, 2, 3)] if audio_native_scalars else [1., 1., 1., 1.]
                                status = config(.5, *scale, 1., count,
                                                times, pitch, overrides, dynamics, other, control)
                                assert status == 0, (side, path, phoneme, 'config', status)
                                out_count, out_times, out_pitch = n.alloc(4), n.alloc(8), n.alloc(8)
                                assert n.fn(0xd140, Q, P, P, P, P)(out_count, out_times, out_pitch, control) == 0
                                assert I.from_address(out_count).value == count
                                for pointer in [P.from_address(out_times).value, P.from_address(out_pitch).value]:
                                    assert pointer and all(math.isfinite(v) for v in (F * count).from_address(pointer))
                                curve = P.from_address(internal + 0xa0).value
                                curve_count = I.from_address(curve + 0x5c).value
                                curve_nodes = P.from_address(curve + 0x78).value
                                configuration_state = dict(
                                    sample_rate=F.from_address(control + 0xa8).value,
                                    block_size=I.from_address(control + 0xa4).value,
                                    time_fields={hex(off): F.from_address(internal + off).value
                                                 for off in [0x38, 0x3c, 0x48, 0xb0, 0xb4]},
                                    time_map_count=curve_count,
                                    time_map=[(D.from_address(curve_nodes + j*16).value,
                                               F.from_address(curve_nodes + j*16 + 8).value)
                                              for j in range(curve_count)],
                                    synf0_range=[min((F * count).from_address(P.from_address(out_pitch).value)),
                                                 max((F * count).from_address(P.from_address(out_pitch).value))])
                                configured_samples += 1
                                if audio and configured_samples > audio_start and len(audio_rows) < audio_limit:
                                    print("audio sample", side, path, phoneme, flush=True)
                                    from region_dvqm_audio import consume
                                    audio_rows.append(dict(side=side, property=path, phoneme=phoneme,
                                                           **consume(n, control, audio_blocks, audio_harmonics,
                                                                     not audio_no_gate, audio_prepass,
                                                                     audio_pointer_initial)))
                        finally:
                            destroy(control)
                    rows.append(dict(side=side, property=path, phoneme=phoneme,
                                     durations=actual, configuration_state=configuration_state,
                                     sample_scalars=sample_scalars if load_samples else None,
                                     sample_flags=sample_flags if load_samples else None,
                                     sample_feature=sample_feature if load_samples else None))
            for node in children(branch):
                visit(node, [name(node)])
    finally:
        n.fn(0x10080, Q, P)(root)
    with (directory / 'dvqm.dat').open('rb') as source:
        digest = hashlib.file_digest(source, 'sha256').hexdigest()
    return dict(dse_sha256=SHA256, resource_sha256=digest, cases=len(rows),
                audio_contract=dict(enabled=audio, start=audio_start, limit=audio_limit,
                                    max_blocks=audio_blocks, harmonics=audio_harmonics,
                                    gate_pointer=not audio_no_gate, prepass=audio_prepass,
                                    native_config_scalars=audio_native_scalars,
                                    native_factor=audio_native_factor,
                                    pointer_initial=audio_pointer_initial),
                properties=len({(r['side'], tuple(r['property'])) for r in rows}),
                mismatches=0, rows=rows, loaded_samples=loaded_samples, configured_samples=configured_samples, audio=audio_rows,
                scope='Native property resolution and segmentation matched in-memory metadata; '
                      'optional native sample load/configuration/synf0/info/destruction; '
                      'configuration checks return status, count and finiteness only; '
                      'no PCM/frame export or synthesis accuracy claim')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--directory', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--load-samples', action='store_true')
    parser.add_argument('--configure', action='store_true')
    parser.add_argument('--audio', action='store_true')
    parser.add_argument('--audio-limit', type=int, default=1)
    parser.add_argument('--audio-blocks', type=int, default=4)
    parser.add_argument('--audio-start', type=int, default=0)
    parser.add_argument('--audio-harmonics', type=int, default=0)
    parser.add_argument('--audio-no-gate', action='store_true')
    parser.add_argument('--audio-prepass', action='store_true')
    parser.add_argument('--audio-native-scalars', action='store_true')
    parser.add_argument('--audio-native-factor', type=float, default=1.)
    parser.add_argument('--audio-pointer-initial', type=float, default=1.)
    args = parser.parse_args()
    result = run(dll=args.dll, directory=args.directory,
                 load_samples=args.load_samples or args.configure or args.audio,
                 configure=args.configure or args.audio, audio=args.audio,
                 audio_limit=args.audio_limit, audio_blocks=args.audio_blocks,
                 audio_start=args.audio_start, audio_harmonics=args.audio_harmonics,
                 audio_no_gate=args.audio_no_gate, audio_prepass=args.audio_prepass,
                 audio_native_scalars=args.audio_native_scalars,
                 audio_native_factor=args.audio_native_factor,
                 audio_pointer_initial=args.audio_pointer_initial)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({k: v for k, v in result.items() if k not in ['rows', 'audio']}))
