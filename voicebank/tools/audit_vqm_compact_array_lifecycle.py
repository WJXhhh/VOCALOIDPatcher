"""Identify source-array changes in the two compact audio lifecycle exceptions.

All bank arrays and PCM stay in memory. The established native audio protocol
uses fresh owned roots and genuine CRT seeding; installed resources are read only.
"""
from __future__ import annotations

import argparse
from collections import Counter
import ctypes as c
import hashlib
import json
from pathlib import Path
import struct

from audit_frm2_numeric import I, Native, P, Q, SHA256
from audit_vqm_legacy_live_features import RESOURCE_SHA256, catalog, children, name
from verify_vqm_compact_amplitude_split import compact_audio, frames
from verify_vqm_legacy_bit17_consumption import audio, load_configure
from verify_vqm_legacy_seeded_consumption import random_reset


def capture(frame):
    n, m = I.from_address(frame+0x58).value, I.from_address(frame+0xa8).value
    assert 1 <= n <= 10000 and 0 <= m <= 100000
    feature = P.from_address(frame+0x128).value
    result = dict(mask=Q.from_address(frame+0x50).value, n=n, m=m,
        pitch=c.string_at(frame+0xf0, 4), feature=c.string_at(feature, 0x228))
    for field, offset, count in (('frequency',0x60,n),('amplitude',0x68,n),('phase',0x70,n),
                              ('first',0xb0,m),('second',0xb8,m)):
        result[field] = c.string_at(P.from_address(frame+offset).value, count*4) if count else b''
    return result


def run(dll, directory):
    assert hashlib.sha256((directory/'dvqm.dat').read_bytes()).hexdigest() == RESOURCE_SHA256
    native = Native(dll)
    reset, random_identity = random_reset(dll, 428)
    encoded = str(directory.resolve()).encode('utf-8')
    catalog_root = native.fn(0xfea0, P, c.c_char_p)(encoded)
    assert catalog_root
    cases = []
    try:
        entries = catalog(native, catalog_root)
        for index in (3, 10):
            side, descriptor, label, _ = entries[index]
            owned_root = native.fn(0xfea0, P, c.c_char_p)(encoded)
            assert owned_root
            control = None
            try:
                node = native.fn(0x23f00, P, P, I, P)(owned_root, side, descriptor)
                sample = {name(item):item for item in children(node)}[label]
                control, _ = load_configure(native, owned_root, (side,descriptor,label,sample), 'baseline')
                before = [capture(frame) for frame in frames(control)]
                result = compact_audio(audio(native, control, 64, reset))
                after = [capture(frame) for frame in frames(control)]
                assert len(before) == len(after)
                counts, masks = Counter(), Counter()
                for old, new in zip(before, after):
                    counts['frames'] += 1
                    counts['bit17_unchanged'] += old['feature'][0x21c:0x220] == new['feature'][0x21c:0x220]
                    counts['feature_arrays_unchanged'] += old['feature'][0x2c:0x20c] == new['feature'][0x2c:0x20c]
                    masked_old, masked_new = bytearray(old['feature']), bytearray(new['feature'])
                    for offset in (0x20,0x220):
                        masked_old[offset:offset+4] = bytes(4)
                        masked_new[offset:offset+4] = bytes(4)
                    counts['feature_outside_bit7_bit18_unchanged'] += masked_old == masked_new
                    counts['original_component_arrays_unchanged'] += all(old[key] == new[key] for key in ('frequency','amplitude','phase'))
                    counts['pitch_unchanged'] += old['pitch'] == new['pitch']
                    if old == new:
                        continue
                    counts['changed_frames'] += 1
                    masks[f'{hex(old["mask"])}->{hex(new["mask"])}'] += 1
                    counts['previously_no_optional_arrays'] += old['m'] == 0
                    counts['new_optional_arrays'] += old['m'] == 0 and new['m'] > 0
                    if new['m']:
                        n, m = new['n'], new['m']
                        for k in range(1, min(n,m)):
                            amplitude = struct.unpack_from('<f', new['amplitude'], k*4)[0]
                            a, b = (struct.unpack_from('<f', new[key], k*4)[0] for key in ('first','second'))
                            counts['changed_frame_amplitude_sum_comparisons'] += 1
                            counts['changed_frame_amplitude_sums_exact'] += amplitude == a+b
                        counts['changed_frame_bit18_matches_first_zero'] += new['feature'][0x220:0x224] == new['first'][:4]
                        counts['changed_frame_bit7_matches_second_zero'] += new['feature'][0x20:0x24] == new['second'][:4]
                cases.append(dict(sample_index=index, counts=dict(counts), mask_changes=dict(masks), audio=result))
            finally:
                if control:
                    native.fn(0xb480, None, P)(control)
                assert native.fn(0x10080, Q, P)(owned_root) == 0
    finally:
        assert native.fn(0x10080, Q, P)(catalog_root) == 0
    return dict(schema=1, dse_sha256=SHA256, resource_sha256=RESOURCE_SHA256,
        random_identity=random_identity, cases=cases,
        scope='Two known source lifecycle exceptions only, unchanged configured baseline and64 audio '
            'blocks. Installed data never modified; payloads and PCM in memory only. '
            'Changes are runtime source adaptation, not authoring proof or V4X Growl behavior.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll','directory','output'):
        parser.add_argument('--'+name,type=Path,required=True)
    args = parser.parse_args()
    result = run(args.dll,args.directory)
    sources = ('audit_vqm_compact_array_lifecycle.py','verify_vqm_compact_amplitude_split.py',
        'audit_frm2_numeric.py','audit_vqm_legacy_live_features.py','audit_vqm_legacy_phase_relations.py',
        'audit_vqm_compact_load_identity.py','verify_vqm_legacy_bit17_consumption.py',
        'verify_vqm_legacy_seeded_consumption.py','audit_vqm_v4_module_leads.py','probe_frm2.py',
        'verify_region_frame_blend.py')
    result['source_sha256'] = {name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources}
    args.output.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(result['cases']))


if __name__ == '__main__':
    main()
