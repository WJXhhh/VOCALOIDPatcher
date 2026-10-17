"""Load installed V4X VQMp through DSE and compare offline Growl output."""

from __future__ import annotations

import argparse
from contextlib import contextmanager
import ctypes as c
import hashlib
import json
from pathlib import Path
import subprocess
import struct
import sys

from audit_frm2_numeric import F, I, P, Q, SHA256, f32
from audit_v4x_vqm_fields import feature_scalars
import probe_frm2
from probe_vqm_loaded_aux_mix import VDM_SHA256, VQM_SHA256
from verify_reference_ddi import pointer
from verify_region_v4x_index_frame_reads import verify
from verify_vqm_loaded_aux_pcm import loaded_auxiliary


MUTATIONS = ('frequency', 'amplitude', 'phase',
             'feature4', 'feature6', 'feature14', 'feature15',
             'feature16', 'feature17', 'feature18', 'outer18', 'outer19')
SCALAR_OFFSETS = {**{bit: 4 + bit * 4 for bit in range(10)},
                  14: 0x20c, 15: 0x214, 16: 0x218, 17: 0x21c,
                  18: 0x220, 19: 0x224}


def prepared_pcm(samples):
    """Model 0x15da20: normalize, fade 64 endpoints, mirror-pad 32768 each side."""
    count = len(samples)
    assert count >= 128
    values = [f32(value / 32768.) for value in samples]
    for index in range(64):
        scale = f32(index / 64.)
        values[index] = f32(scale * values[index])
        values[count - index - 1] = f32(scale * values[count - index - 1])
    repeats, remainder = divmod(32768, count)
    prefix = []
    suffix = []
    for index in range(repeats + 1):
        size = remainder if index == 0 else count
        prefix.extend(reversed(values[:size]) if (repeats - index) % 2 == 0
                      else values[count - size:] if size else [])
        size = remainder if index == repeats else count
        suffix.extend(reversed(values[count - size:]) if index % 2 == 0 and size
                      else values[:size])
    assert len(prefix) == len(suffix) == 32768
    return struct.pack(f'<{count + 65536}f', *(prefix + values + suffix))


@contextmanager
def growl_setup(native, voice, model, ddi, vdm, explib, control, loaded,
                mutation, no_material, ddb_path):
    with loaded_auxiliary(native, vdm, explib, "off") as prepare:
        engine = P.from_address(native.lib._handle + 0xaa69d0).value
        assert engine and not P.from_address(voice + 0x2f8).value
        if no_material:
            def control_only(ctx, ring):
                prepare(ctx, ring)
                F.from_address(ring + 0x5aad8).value = control

            yield control_only
            return
        root = P.from_address(P.from_address(voice + 0x148).value).value
        assert root
        sta, art = [P.from_address(P.from_address(root + 0x148).value + i * 8).value
                    for i in range(2)]
        vqm, growl = native.alloc(0x180), native.alloc(0x180)
        for node, name in ((vqm, b'vqm\0'), (growl, b'GROWL\0')):
            native.fn(0x1067f0, P, P, I)(node, 0)
            c.memmove(node + 0x18, name, len(name))
        P.from_address(root + 0x148).value = native.array(P, [sta, art, vqm])
        I.from_address(root + 0x150).value = 3
        P.from_address(vqm + 0x148).value = native.array(P, [growl])
        I.from_address(vqm + 0x150).value = 1
        parent = {root: voice, vqm: root, growl: vqm}
        callback = c.CFUNCTYPE(P, P)(lambda node: parent.get(node, 0))
        native.keep.append(callback)
        original = P.from_address(root).value
        vtable = native.alloc(0x100)
        c.memmove(vtable, original, 0x100)
        P.from_address(vtable + 0x60).value = c.cast(callback, P).value
        for node in (root, vqm, growl):
            P.from_address(node).value = vtable
        samples = list((model.vqm_data or {}).values())
        assert len(samples) == 3
        parts = []
        pcm_references = []
        frame_references = []
        for sample in samples:
            snd, count = pointer(sample['snd'], ddi)
            offsets = [pointer(frame, ddi)[0] for frame in sample['epr']]
            assert count == len(offsets) * 256 and 0 < count < 1_000_000
            with ddb_path.open('rb') as stream:
                stream.seek(snd)
                header = stream.read(18)
                assert len(header) == 18 and header[:4] == b'SND '
                size, rate, channels, stored_count = struct.unpack_from('<IIHI', header, 4)
                assert (size, rate, channels, stored_count) == (18 + count * 2, 44100, 1, count)
                payload = stream.read(count * 2)
                assert len(payload) == count * 2
                references = []
                for offset in offsets:
                    stream.seek(offset)
                    frame_header = stream.read(8)
                    assert len(frame_header) == 8 and frame_header[:4] == b'FRM2'
                    frame_size = struct.unpack_from('<I', frame_header, 4)[0]
                    assert 28 <= frame_size <= 65536
                    frame = probe_frm2.parse_frame(frame_header + stream.read(frame_size - 8))
                    assert isinstance(frame, probe_frm2.VqmFrame)
                    references.append(frame)
            pcm_references.append(prepared_pcm(struct.unpack(f'<{count}h', payload)))
            frame_references.append(references)
            part = native.alloc(0x280)
            F.from_address(part + 0x160).value = sample['pitch1']
            I.from_address(part + 0x1b0).value = len(offsets)
            P.from_address(part + 0x1a8).value = native.array(Q, offsets)
            Q.from_address(part + 0x1c0).value = snd + 18
            I.from_address(part + 0x1d0).value = count
            parts.append(part)
        P.from_address(growl + 0x148).value = native.array(P, parts)
        I.from_address(growl + 0x150).value = len(parts)
        find = native.fn(0x1076b0, P, P, c.c_char_p)
        assert find(find(find(voice, b'voice'), b'vqm'), b'GROWL') == growl
        kernel = c.WinDLL('kernel32', use_last_error=True)
        kernel.AddVectoredExceptionHandler.argtypes = [I, P]
        kernel.AddVectoredExceptionHandler.restype = P
        kernel.RemoveVectoredExceptionHandler.argtypes = [P]
        handler_type = c.WINFUNCTYPE(c.c_long, P)

        def identify_native_fault(pointers):
            record = P.from_address(pointers).value
            if I.from_address(record).value == -1073741819:
                address = P.from_address(record + 0x10).value
                context = P.from_address(pointers + 8).value
                stack = P.from_address(context + 0x98).value
                caller = P.from_address(stack + 0x38).value
                print(f'DSE access violation at RVA 0x{address - native.lib._handle:x}; '
                      f'caller RVA 0x{caller - native.lib._handle:x}',
                      file=sys.stderr, flush=True)
            return 0

        handler = handler_type(identify_native_fault)
        token = kernel.AddVectoredExceptionHandler(1, c.cast(handler, P))
        assert token
        try:
            native.fn(0x15dfb0, None, P, P)(voice + 0x2f8, growl)
        finally:
            kernel.RemoveVectoredExceptionHandler(token)
        begin, end = (P.from_address(voice + offset).value for offset in (0x2f8, 0x300))
        assert begin and end - begin == 0x50
        record = begin
        sample_begin = P.from_address(record + 0x28).value
        sample_end = P.from_address(record + 0x30).value
        assert sample_begin and sample_end - sample_begin == 3 * 0x850
        decoded = []
        changed_values = 0
        feature_checks = 0
        for index, sample in enumerate(samples):
            pcm = P.from_address(sample_begin + index * 0x850 + 0x808).value
            assert pcm and c.string_at(pcm, len(pcm_references[index])) == pcm_references[index]
            region = P.from_address(sample_begin + index * 0x850 + 0x800).value
            assert region
            frame_count = I.from_address(region + 0x18).value
            assert frame_count == len(sample['epr'])
            decoded.append(frame_count)
            frames = P.from_address(region + 0x20).value
            assert frames
            for frame_index, expected in enumerate(frame_references[index]):
                source = P.from_address(frames + frame_index * 8).value
                feature = P.from_address(source + 0x208).value if source else 0
                assert feature and I.from_address(feature).value == expected.feature_block.flags
                for bit, value in feature_scalars(expected).items():
                    assert F.from_address(feature + SCALAR_OFFSETS[bit]).value == value
                    feature_checks += 1
                assert c.c_ubyte.from_address(source + 0x258).value == expected.bit18_value
                assert c.c_uint32.from_address(source + 0x25c).value == expected.bit19_value
            if mutation:
                for frame_index in range(frame_count):
                    source = P.from_address(frames + frame_index * 8).value
                    assert source
                    if mutation in ('frequency', 'amplitude', 'phase'):
                        count = I.from_address(source + 0x158).value
                        array = P.from_address(source + {
                            'frequency': 0x160, 'amplitude': 0x168,
                            'phase': 0x170}[mutation]).value
                        assert 0 < count < 1000 and array
                        increment = {'frequency': 5., 'amplitude': 3.,
                                     'phase': .1}[mutation]
                        for component in range(count):
                            F.from_address(array + component * 4).value += increment
                    elif mutation.startswith('feature'):
                        feature = P.from_address(source + 0x208).value
                        assert feature
                        offset = {'feature4': 0x14, 'feature6': 0x1c,
                                  'feature14': 0x20c, 'feature15': 0x214,
                                  'feature16': 0x218, 'feature17': 0x21c,
                                  'feature18': 0x220}[mutation]
                        F.from_address(feature + offset).value += .75
                    else:
                        offset = 0x258 if mutation == 'outer18' else 0x25c
                        ty = c.c_ubyte if mutation == 'outer18' else c.c_uint32
                        ty.from_address(source + offset).value = (
                            255 if mutation == 'outer18' else 4097)
                    changed_values += 1
        assert not mutation or changed_values == sum(decoded)
        native.fn(0x171860, None, P, P)(engine, voice + 0x2f8)
        begin = P.from_address(engine + 0x2af30).value
        obj = P.from_address(begin).value
        assert P.from_address(obj + 8).value == record
        assert c.c_ubyte.from_address(obj + 0x118).value == 1
        loaded.update(sample_frames=decoded, bound=True,
                      sample_pitches=[sample['pitch1'] for sample in samples],
                      pcm_float32_exact=[True] * len(samples),
                      pcm_padding_each_side=32768, pcm_fade_each_edge=64,
                      feature_scalar_exact_checks=feature_checks,
                      fixture_version=2)
        if mutation:
            loaded.update(mutation=mutation, mutated_frames=changed_values)

        def growl_prepare(ctx, ring):
            prepare(ctx, ring)
            F.from_address(ring + 0x5aad8).value = control
            actual = native.fn(0x18adc0, F, P, I, I)(ring, 0x13, 0)
            assert abs(actual - control) < 1e-7

        yield growl_prepare


def single(args):
    assert hashlib.sha256(args.vdm.read_bytes()).hexdigest() == VDM_SHA256
    assert hashlib.sha256((args.explib / 'VQM.vdb').read_bytes()).hexdigest() == VQM_SHA256
    loaded = {}
    blocks = []
    paths = list(args.voice_root.rglob(args.bank + '.ddi'))
    assert len(paths) == 1
    ddb_path = paths[0].with_suffix('.ddb')

    def output(time, block):
        assert len(block) == 512 and time == len(blocks)
        blocks.append(hashlib.sha256(block).hexdigest())

    result = verify(
        args.dse, args.bank, args.first, args.second, args.manifest, args.voice_root,
        args.ddb_tools, full_loop_limit=1, full_loop_min_target_time=150,
        third=args.third,
        full_loop_engine_setup=lambda n, voice, model, ddi: growl_setup(
            n, voice, model, ddi, args.vdm, args.explib, args.control, loaded,
            args.mutation, args.no_material, ddb_path),
        full_loop_output_observer=output)
    loop = result['full_loop']
    assert loop['events'] == 1 and len(blocks) == loop['blocks']
    assert loop['pcm_observer_checks'] == loop['pcm_observer_matches']
    assert loop.get('sta_pcm_checks', 0) == loop.get('sta_pcm_matches', 0)
    return dict(bank=args.bank, control=args.control, loaded=loaded,
                blocks=loop['blocks'], nonzero_blocks=loop['nonzero_blocks'],
                output_sha256=loop['rows'][0]['stream_sha256'], hashes=blocks)


def compare(args):
    rows = []
    for control, no_material in ((0., False), (1., False), (1., True)):
        cmd = [sys.executable, '-B', str(Path(__file__).resolve()),
               '--dse', str(args.dse), '--vdm', str(args.vdm),
               '--explib', str(args.explib), '--manifest', str(args.manifest),
               '--voice-root', str(args.voice_root), '--ddb-tools', str(args.ddb_tools),
               '--bank', args.bank, '--first', args.first, '--second', args.second,
               '--control', str(control)]
        if args.third:
            cmd += ['--third', args.third]
        if no_material:
            cmd += ['--no-material']
        child = subprocess.run(cmd, capture_output=True, text=True, timeout=180)
        if child.returncode:
            raise RuntimeError(f'Growl {control}: {child.stderr[-1800:] or child.returncode}')
        rows.append(json.loads(child.stdout))
    left, right, unbound = rows
    assert left['loaded'] == right['loaded']
    assert not unbound['loaded'] and unbound['hashes'] == left['hashes']
    report = dict(dse_sha256=SHA256, vdm_sha256=VDM_SHA256,
                  auxiliary_vqm_sha256=VQM_SHA256, bank=args.bank,
                  route=[args.first, args.second] + ([args.third] if args.third else []),
                  loaded=left['loaded'], blocks=left['blocks'],
                  zero_control_sha256=left['output_sha256'],
                  unbound_full_control_sha256=unbound['output_sha256'],
                  full_control_sha256=right['output_sha256'],
                  changed_pcm_blocks=sum(a != b for a, b in zip(
                      left['hashes'], right['hashes'])),
                  scope='Installed V4X DDI/DDB VQMp loaded by native DSE into an '
                        'independent offline note event. Output digests only; no Editor host.')
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dse', 'vdm', 'explib', 'manifest', 'voice-root', 'ddb-tools'):
        parser.add_argument('--' + name, required=True, type=Path)
    parser.add_argument('--bank')
    parser.add_argument('--first', default='a')
    parser.add_argument('--second', default='Sil')
    parser.add_argument('--third')
    parser.add_argument('--control', type=float, choices=(0., 1.))
    parser.add_argument('--mutation', choices=MUTATIONS)
    parser.add_argument('--mutation-matrix', action='store_true')
    parser.add_argument('--no-material', action='store_true')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    if args.control is not None:
        if not args.bank:
            parser.error('--control requires --bank')
        print(json.dumps(single(args)))
        return
    if args.mutation_matrix:
        if args.mutation:
            parser.error('--mutation-matrix cannot use --mutation')
        if not args.bank:
            manifest = json.loads(args.manifest.read_text(encoding='utf-8'))
            cases = []
            for entry in manifest['banks']:
                cmd = [sys.executable, '-B', str(Path(__file__).resolve()),
                       '--dse', str(args.dse), '--vdm', str(args.vdm),
                       '--explib', str(args.explib), '--manifest', str(args.manifest),
                       '--voice-root', str(args.voice_root),
                       '--ddb-tools', str(args.ddb_tools),
                       '--bank', entry['bank'], '--first', args.first,
                       '--second', args.second, '--mutation-matrix']
                if args.third:
                    cmd += ['--third', args.third]
                child = subprocess.run(cmd, capture_output=True, text=True, timeout=300)
                if child.returncode:
                    raise RuntimeError(f"{entry['bank']}: {child.stderr[-1800:] or child.returncode}")
                cases.append(json.loads(child.stdout))
                print(f"Checked {entry['bank']}", file=sys.stderr, flush=True)
            assert len(cases) == 8
            report = dict(dse_sha256=SHA256, bank_count=len(cases), cases=cases,
                          scope='All installed V4X VQMp Growl frames, native loaded, '
                                'mutated in memory, and compared in separate offline DSE events.')
            if args.output:
                args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
            print(json.dumps(report))
            return
        rows = []
        for mutation in (None, *MUTATIONS):
            cmd = [sys.executable, '-B', str(Path(__file__).resolve()),
                   '--dse', str(args.dse), '--vdm', str(args.vdm),
                   '--explib', str(args.explib), '--manifest', str(args.manifest),
                   '--voice-root', str(args.voice_root), '--ddb-tools', str(args.ddb_tools),
                   '--bank', args.bank, '--first', args.first,
                   '--second', args.second, '--control', '1.0']
            if args.third:
                cmd += ['--third', args.third]
            if mutation:
                cmd += ['--mutation', mutation]
            child = subprocess.run(cmd, capture_output=True, text=True, timeout=180)
            if child.returncode:
                raise RuntimeError(f'{mutation}: {child.stderr[-1800:] or child.returncode}')
            rows.append(json.loads(child.stdout))
        baseline = rows[0]
        report = dict(bank=args.bank, control=1.0,
                      loaded=baseline['loaded'],
                      source_frames=sum(baseline['loaded']['sample_frames']),
                      baseline_output_sha256=baseline['output_sha256'],
                      mutations=[dict(field=name,
                                      mutated_frames=row['loaded']['mutated_frames'],
                                      changed_pcm_blocks=sum(a != b for a, b in zip(
                                          baseline['hashes'], row['hashes'])),
                                      output_sha256=row['output_sha256'])
                                 for name, row in zip(MUTATIONS, rows[1:])],
                      scope='Native-loaded installed V4X Growl FRM2 fields changed '
                            'in memory in separate offline DSE processes; no bank files changed.')
        if args.output:
            args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
        print(json.dumps(report))
        return
    if args.bank:
        report = compare(args)
    else:
        manifest = json.loads(args.manifest.read_text(encoding='utf-8'))
        rows = []
        for entry in manifest['banks']:
            args.bank = entry['bank']
            rows.append(compare(args))
        assert len(rows) == 8
        report = dict(dse_sha256=SHA256, bank_count=len(rows), cases=rows,
                      scope='All installed V4X Growl VQMp materials, independently '
                            'loaded and compared at zero/full control in DSE offline events.')
    if args.output:
        args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report))


if __name__ == '__main__':
    main()
