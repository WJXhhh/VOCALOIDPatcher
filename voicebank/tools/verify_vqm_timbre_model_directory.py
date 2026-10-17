"""Verify native TimbreModel construction, sorting and owned Frame loading.

Run in a disposable process. Native objects survive until process exit; their
destructors must not free Python-owned fixture memory. Only metadata/statistics
are persisted. The temporary input contains independently generated data only.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
from pathlib import Path
import struct

from audit_frm2_numeric import F, I, Native, P, Q, SHA256
from audit_vqm_v4_module_leads import PE
from verify_vqm_feature_dse5_roundtrip import crt_functions, owned_frame


VTABLES = {
    'model': (0x226610, '.?AVCDBTimbreModel@DSE5@@'),
    'array': (0x226420, '.?AVCDBTimbreFrameArray@DSE5@@'),
    'leaf': (0x226250, '.?AVCDBTimbreFrame@DSE5@@'),
}
FIELDS = {'dynamics': (0x21c, True), 'opening': (0x218, True),
          'pitch': (0x1d0, False)}


def type_metadata(pe: PE) -> dict:
    result = {}
    for role, (vtable, expected) in VTABLES.items():
        locator = struct.unpack_from('<Q', pe.data, pe.offset(vtable - 8))[0]
        record = struct.unpack_from('<6I', pe.data, pe.offset(locator - pe.image_base))
        actual = pe.text(record[3] + 16)
        if record[0] != 1 or actual != expected:
            raise AssertionError('Timbre RTTI identity differs')
        result[role] = dict(vtable_rva=hex(vtable), type_name=actual)
    return result


def run(dll: Path, scratch: Path) -> dict:
    if not scratch.parent.is_dir() or scratch.exists():
        raise ValueError('Use a new scratch file in an existing research directory')
    native = Native(dll)
    identities = type_metadata(PE(dll.read_bytes()))
    root = native.alloc(0x168)
    if native.fn(0x10fb30, P, P)(root) != root:
        raise AssertionError('Model constructor returned another object')
    append = native.fn(0x10fd10, I, P, P, c.c_char_p)
    find = native.fn(0x1076b0, P, P, c.c_char_p)
    sort = native.fn(0x1103e0, None, P, c.c_char_p)
    query = native.fn(0x110020, Q, P, c.c_char_p, I, P)
    endpoints = native.fn(0x10ffb0, None, P, c.c_char_p, P, P)
    if (P.from_address(root).value != native.lib._handle + VTABLES['model'][0]
            or I.from_address(root + 0x150).value != 3):
        raise AssertionError('Native model did not construct three groups')

    def field(frame, name):
        offset, indirect = FIELDS[name]
        base = P.from_address(frame + 0x208).value if indirect else frame
        return F.from_address(base + offset)

    def leaves(group):
        count = I.from_address(group + 0x150).value
        if not 0 <= count <= 6:
            raise AssertionError('Unexpected fixture directory size')
        vector = P.from_address(group + 0x148).value
        return [P.from_address(vector + 8 * i).value for i in range(count)]

    query_count = 0

    def check_queries(name, group, expected_values):
        nonlocal query_count
        actual_values = []
        for index, leaf in enumerate(leaves(group)):
            frame = P.from_address(leaf + 0x160).value
            result = F(999.)
            status = query(root, name.encode(), index, c.byref(result))
            if status or bytes(result) != bytes(field(frame, name)):
                raise AssertionError('Directory query differs from its held Frame')
            actual_values.append(result.value)
            query_count += 1
        if actual_values != expected_values:
            raise AssertionError('Directory order differs from independent expectation')
        return actual_values

    groups = []
    for name in FIELDS:
        group = find(root, name.encode())
        if not group or P.from_address(group).value != native.lib._handle + VTABLES['array'][0]:
            raise AssertionError('Constructed group is not TimbreFrameArray')
        values = [1.25, -0.25, 3., 0.5, -0.25, 0.]
        created = []
        for index, value in enumerate(values):
            frame = native.alloc(0x328)
            native.fn(0x12a670, P, P)(frame)
            feature = native.alloc(0x228)
            P.from_address(frame + 0x208).value = feature
            Q.from_address(frame + 0x150).value |= 0x2000
            I.from_address(feature).value = 0x70000
            for control, control_value in (('pitch', 333. + index),
                    ('opening', 17. + index), ('dynamics', -8. + index)):
                field(frame, control).value = control_value
            field(frame, name).value = value
            leaf = native.alloc(0x170)
            native.fn(0x10ec80, P, P, P)(leaf, frame)
            if (P.from_address(leaf).value != native.lib._handle + VTABLES['leaf'][0]
                    or P.from_address(leaf + 0x160).value != frame
                    or append(root, leaf, name.encode()) != 0):
                raise AssertionError('Native leaf creation/assignment/append differs')
            created.append(leaf)
        names = [c.string_at(leaf + 0x18, 32).split(b'\0')[0].decode('ascii')
                 for leaf in leaves(group)]
        if names != [str(i) for i in range(6)] or leaves(group) != created:
            raise AssertionError('Native unique naming or append order differs')
        before = check_queries(name, group, values)
        sort(root, name.encode())
        after = check_queries(name, group, sorted(values))
        first, last = F(), F()
        endpoints(root, name.encode(), c.byref(first), c.byref(last))
        if (bytes(first) != bytes(F(after[0])) or bytes(last) != bytes(F(after[-1]))):
            raise AssertionError('Native sorted endpoints differ')
        # Change the last-ranked object in place, then let the native comparator
        # establish its new position. No directory vector or names are assigned.
        changed = leaves(group)[-1]
        frame = P.from_address(changed + 0x160).value
        controls = {key: bytes(field(frame, key)) for key in FIELDS if key != name}
        field(frame, name).value = -9.
        after[-1] = -9.
        sort(root, name.encode())
        mutated = check_queries(name, group, sorted(after))
        if leaves(group)[0] != changed or any(bytes(field(frame, key)) != value
                for key, value in controls.items()):
            raise AssertionError('Re-sort changed object identity or unrelated fields')
        groups.append(dict(attribute=name, leaf_count=6, native_names=names,
            insertion_values=before, sorted_values=sorted(values),
            mutated_sorted_values=mutated, endpoints_exact=True,
            leaf_identity_preserved=True, unrelated_fields_preserved=True))

    crt = crt_functions()
    loaded_rows = []
    loaded_root = native.alloc(0x168)
    native.fn(0x10fb30, P, P)(loaded_root)
    try:
        raw = owned_frame()
        for index in range(6):
            metadata = struct.pack('<if', index, index / 8)
            scratch.write_bytes(metadata + raw)
            leaf = native.fn(0x10f880, P)()
            if not leaf or P.from_address(leaf + 0x160).value:
                raise AssertionError('Native empty leaf factory differs')
            handle = crt._wfopen(str(scratch.resolve()), 'rb')
            if not handle:
                raise OSError('Cannot open owned timbre content')
            stream = native.alloc(0x80)
            P.from_address(stream + 8).value = handle
            I.from_address(stream + 0x2c).value = 0x32534d53
            try:
                code = native.fn(0x10f590, Q, P, P)(leaf, stream)
                count = Q.from_address(stream).value
            finally:
                if crt.fclose(handle):
                    raise OSError('Cannot close owned timbre content')
            frame = P.from_address(leaf + 0x160).value
            if (code or count != len(metadata + raw) or not frame
                    or c.string_at(leaf + 0x168, 8) != metadata
                    or I.from_address(P.from_address(frame + 0x208).value).value != 0x1fffff):
                raise AssertionError('Native timbre content reader differs')
            loaded_rows.append(dict(metadata_index=index, consumed_bytes=count,
                allocated_frame=True, metadata_exact=True,
                dynamics=field(frame, 'dynamics').value,
                opening=field(frame, 'opening').value,
                pitch=field(frame, 'pitch').value))
            if (field(frame, 'dynamics').value != 17.125
                    or field(frame, 'opening').value != 16.125
                    or field(frame, 'pitch').value != -1200.):
                raise AssertionError(f'Reader-produced Frame fields differ: {loaded_rows[-1]}')
            if append(loaded_root, leaf, b'dynamics') != 0:
                raise AssertionError('Cannot append reader-created Frame leaf')
            result = F()
            if (query(loaded_root, b'dynamics', index, c.byref(result)) != 0
                    or bytes(result) != bytes(F(17.125))):
                raise AssertionError('Reader-created Frame directory query differs')
            query_count += 1
            loaded_rows[-1]['native_directory_query_exact'] = True
            scratch.unlink()
    finally:
        scratch.unlink(missing_ok=True)
    return dict(dse_sha256=SHA256,
        source_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        owned_fixture_source_sha256=hashlib.sha256(
            Path(__file__).with_name('verify_vqm_feature_dse5_roundtrip.py').read_bytes()).hexdigest(),
        types=identities,
        native_entries=dict(model_constructor='0x10fb30', leaf_constructor='0x10ec80',
            append='0x10fd10', sort='0x1103e0', getter='0x110020',
            dynamics_comparator='0x110690', opening_comparator='0x1106e0',
            pitch_comparator='0x110730', leaf_factory='0x10f880', content_reader='0x10f590'),
        frame_pointer_offset='0x160', frame_feature_pointer_offset='0x208',
        native_groups=groups, exact_query_count=query_count,
        owned_content_read_cases=loaded_rows, scratch_removed=not scratch.exists(),
        scope='Genuine native TimbreModel groups and leaves holding CSMSFrames; '
              'construction, ownership assignment, unique naming, append, finite-value '
              'sorting, getter, endpoints and six independent content loads verified. '
              'No directory pointers, names or child arrays were manually patched. '
              'Feature values are owned inputs, not produced from PCM. The dynamics '
              'name belongs to this timbre-model interface; original VQM authoring '
              'formula, units, other copy paths and full synthesis effect remain unknown.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--scratch', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll, args.scratch)
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(groups=len(report['native_groups']),
        queries=report['exact_query_count'], content_reads=len(report['owned_content_read_cases']),
        scratch_removed=report['scratch_removed'], all_passed=True)))


if __name__ == '__main__':
    main()
