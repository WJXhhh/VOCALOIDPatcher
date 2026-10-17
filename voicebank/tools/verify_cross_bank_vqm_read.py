"""Read installed VQM chunks in place with the hash-pinned native DSE5 reader.

Checks saved bit13 fields, F0 and outer18/19 against the independent parser.
No proprietary payload is written; native allocations live until process exit.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
import math
import mmap
from pathlib import Path
import struct

from audit_frm2_numeric import Native, I, P, Q, SHA256
from audit_v5_cross_reference import feature_fields, sha
from probe_frm2 import parse_frame
from verify_reference_ddi import load_public_parser, pointer
from analyze_reference_layers import walk_art
from verify_vqm_feature_dse5_roundtrip import crt_functions


def run(dll, v5_root, v4_root, manifest_path, parser_root):
    manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
    assert sha(parser_root/'utils/ddi_utils.py') == manifest['parser_sha256']
    paths = [(p, 'V5') for p in sorted(v5_root.rglob('*.ddi'))]
    assert len(paths) == 7
    for pin in manifest['banks']:
        found = list(v4_root.rglob(pin['bank']+'.ddi'))
        assert len(found) == 1 and sha(found[0]) == pin['ddi_sha256']
        paths.append((found[0], 'V4X'))
    native = Native(dll)
    crt = crt_functions()
    crt._fseeki64.argtypes, crt._fseeki64.restype = [P,c.c_int64,I], I
    ctor = native.fn(0x12a670,P,P)
    read = native.fn(0x106e20,Q,P,P)
    model_type = load_public_parser(parser_root)
    rows, ordinary_rows = [], []
    offsets = {bit:4+bit*4 for bit in range(10)}
    offsets.update({10:0x2c,11:0xcc,12:0x16c,13:0x1bc,14:0x20c})
    offsets.update({bit:0x214+(bit-15)*4 for bit in range(15,20)})
    for path, family in paths:
        print(json.dumps(dict(bank=path.stem,stage='start')),flush=True)
        raw = path.read_bytes()
        model = model_type(raw)
        model.read()
        handle = crt._wfopen(str(path.with_suffix('.ddb').resolve()),'rb')
        assert handle
        frames = fields = values = nonzero17 = consumed = 0
        digest = hashlib.sha256()
        try:
            with path.with_suffix('.ddb').open('rb') as source, mmap.mmap(source.fileno(),0,access=mmap.ACCESS_READ) as data:
                def read_at(offset, size):
                    target = native.alloc(0x328)
                    assert ctor(target) == target
                    stream = native.alloc(0x80)
                    Q.from_address(stream).value = offset
                    P.from_address(stream+8).value = handle
                    I.from_address(stream+0x2c).value = 0x32534d53
                    assert crt._fseeki64(handle,offset,0) == 0
                    assert read(target,stream) == 0
                    assert Q.from_address(stream).value == offset+size
                    return target
                for sample in (model.vqm_data or {}).values():
                    for encoded in sample['epr']:
                        offset, _ = pointer(encoded,raw)
                        size = struct.unpack_from('<I',data,offset+4)[0]
                        expected = parse_frame(data[offset:offset+size])
                        target = read_at(offset,size)
                        assert Q.from_address(target+0x150).value == expected.mask | (1<<31)
                        normalized = math.log(expected.f0_hz/440.)*1200./math.log(2.)
                        assert c.string_at(target+0x1d0,4) == struct.pack('<f',normalized)
                        feature = P.from_address(target+0x208).value
                        assert feature and I.from_address(feature).value == expected.feature_block.flags
                        for bit, entries in feature_fields(expected.feature_block).items():
                            if not entries:
                                continue
                            packed = struct.pack('<'+'f'*len(entries),*entries)
                            observed = c.string_at(feature+offsets[bit],len(packed))
                            assert observed == packed, (path.stem,offset,bit)
                            digest.update(observed)
                            fields += 1
                            values += len(entries)
                        assert I.from_address(target+0x258).value == expected.bit18_value
                        assert c.c_uint32.from_address(target+0x25c).value == expected.bit19_value
                        nonzero17 += struct.unpack('<f',c.string_at(feature+0x21c,4))[0] != 0
                        frames += 1
                        consumed += size
                if family == 'V5':
                    from probe_frm2 import MainFrame, serialize_frame
                    from audit_v5_cross_reference import stats
                    groups = {'STA':[p for u in model.sta_data.values() for p in u['stap'].values()],
                              'ART':[p for root in model.art_data.values() for _, parts in walk_art(root) for p in parts]}
                    for category, parts in groups.items():
                        chosen = [parts[round(k*(len(parts)-1)/15)] for k in range(16)]
                        selected = sorted({pointer(part['epr'][index],raw)[0]
                            for part in chosen for index in (0,len(part['epr'])//2,len(part['epr'])-1)})
                        for offset in selected:
                            size = struct.unpack_from('<I',data,offset+4)[0]
                            payload = data[offset:offset+size]
                            expected = parse_frame(payload)
                            assert serialize_frame(expected) == payload
                            target = read_at(offset,size)
                            assert Q.from_address(target+0x150).value == expected.mask | (1<<31)
                            normalized = (math.log(expected.f0_hz/440.)*1200./math.log(2.)
                                          if expected.f0_hz>0 else -3.4028234663852886e38)
                            assert c.string_at(target+0x1d0,4) == struct.pack('<f',normalized)
                            row = dict(bank=path.stem,category=category,offset=hex(offset),
                                mask=hex(expected.mask),bytes=size,python_byte_roundtrip=True,
                                native_read_and_pitch_normalization_exact=True)
                            if isinstance(expected,MainFrame):
                                row.update(harmonics=len(expected.frequencies_hz),
                                    amplitudes=stats(expected.amplitudes),
                                    primary_resonances=len(expected.resonances),
                                    secondary_resonances=len(expected.secondary_resonances),
                                    secondary_header=list(expected.secondary_header))
                            ordinary_rows.append(row)
        finally:
            assert crt.fclose(handle) == 0
        rows.append(dict(bank=path.stem,family=family,ddi_sha256=sha(path),
            frames=frames,feature_fields=fields,feature_float_values=values,
            feature_values_sha256=digest.hexdigest(),nonzero_bit17_frames=nonzero17,
            native_consumed_bytes=consumed,all_field_comparisons_exact=True,
            frame_pitch_normalized_to_cent_exact=True))
    return dict(dse_sha256=SHA256,reader_rva='0x106e20',feature_reader_rva='0x135eb0',
        rows=rows,ordinary_v5_samples=ordinary_rows,
        ordinary_sample_policy='16 parts spread through each STA/ART part list per V5 bank; first/middle/last frames, deduplicated.',
        scope='Saved VQM and sampled ordinary-frame reading only. Exact feature/F0/control transport '
        'does not identify their original producer, verify spectra numerically, '
        'or establish audible influence or host dispatch.')


def main():
    p = argparse.ArgumentParser(description=__doc__)
    for name in ('dll','v5-root','v4-root','manifest','ddb-tools','output'):
        p.add_argument('--'+name,required=True,type=Path)
    a = p.parse_args()
    assert not a.output.exists()
    result = run(a.dll,a.v5_root,a.v4_root,a.manifest,a.ddb_tools)
    result['tool_sha256'] = sha(Path(__file__))
    a.output.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(banks=len(result['rows']),frames=sum(r['frames'] for r in result['rows']),exact=True)))


if __name__ == '__main__':
    main()
