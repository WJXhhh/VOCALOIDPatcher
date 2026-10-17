"""Verify timbre-axis selection and the installed V4X template-directory scope.

Banks are read-only and hash pinned. The independent TDB walk rejects nonempty
arrays rather than pretending to parse their unknown leaf schema. Native tests
use owned Frames and genuine model/group/leaf constructors; no PCM is saved.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
from pathlib import Path
import struct

from audit_frm2_numeric import F, I, Native, P, Q, SHA256, f32
from verify_reference_ddi import load_public_parser


class TdbCursor:
    def __init__(self, data: bytes, start: int, end: int):
        self.data, self.position, self.end = data, start, end

    def read(self, count: int) -> bytes:
        if count < 0 or self.position + count > self.end:
            raise ValueError('TDB field exceeds bounded parser span')
        result = self.data[self.position:self.position + count]
        self.position += count
        return result

    def integer(self, width=4):
        return int.from_bytes(self.read(width), 'little')

    def string(self):
        count = self.integer()
        if count > 4096:
            raise ValueError('TDB string exceeds bounded size')
        return self.read(count).decode('utf-8')

    def header(self, tag):
        if self.read(8) != bytes([255]) * 8 or self.read(4) != tag:
            raise ValueError('TDB node marker differs')
        size, version = self.integer(), self.integer(8)
        if size != 0 or version != 1:
            raise ValueError('Review a different TDB node layout')


def bank_templates(manifest_path: Path, root: Path, parser_root: Path) -> dict:
    manifest = json.loads(manifest_path.read_bytes())
    parser_hash = hashlib.sha256((parser_root / 'utils/ddi_utils.py').read_bytes()).hexdigest()
    if parser_hash != manifest['parser_sha256']:
        raise ValueError('Public parser hash differs from fixed bank manifest')
    model_type = load_public_parser(parser_root)
    rows = []
    for bank in manifest['banks']:
        matches = list(root.rglob(bank['bank'] + '.ddi'))
        if len(matches) != 1:
            raise ValueError('Expected one installed DDI per pinned bank')
        data = matches[0].read_bytes()
        if hashlib.sha256(data).hexdigest() != bank['ddi_sha256']:
            raise ValueError('Installed bank differs from fixed manifest')
        model = model_type(data)
        model.read()
        start, end = model.offset_map['tdb']
        cursor = TdbCursor(data, start, end)
        cursor.header(b'TDB ')
        count = cursor.integer()
        if count != len(model.tdb_data) or count > 1024:
            raise ValueError('TDB node count differs')
        observed = {}
        groups = dict(pitch=0, dynamics=0, opening=0)
        for _ in range(count):
            cursor.header(b'TMM ')
            index, array_count = cursor.integer(), cursor.integer()
            if index in observed or array_count != 3:
                raise ValueError('TMM index or array count differs')
            for expected in groups:
                cursor.header(b'ARR ')
                leaf_count = cursor.integer()
                if leaf_count != 0:
                    raise ValueError('Nonempty TMM requires a different leaf-schema audit')
                if cursor.string() != expected:
                    raise ValueError('TMM axis name differs')
                groups[expected] += 1
            observed[index] = cursor.string()
        if (cursor.string() != 'timbre' or cursor.position != end
                or observed != model.tdb_data):
            raise ValueError('Independent TDB cursor differs from public parser')
        rows.append(dict(bank=bank['bank'], ddi_sha256=bank['ddi_sha256'],
            tdb_offset=hex(start), tdb_bytes=end - start, models=count,
            empty_axis_groups=groups, template_frame_leaves=0,
            vqm_parts=len(model.vqm_data), independent_parser_match=True))
    return dict(manifest_sha256=hashlib.sha256(manifest_path.read_bytes()).hexdigest(),
        parser_sha256=parser_hash, banks=rows,
        models=sum(row['models'] for row in rows),
        empty_groups=sum(sum(row['empty_axis_groups'].values()) for row in rows),
        template_frame_leaves=0,
        scope='All eight pinned V4X saved TDB/TMM directories, checked with two readers. '
              'Does not cover transient runtime additions, auxiliary databases, other banks '
              'or the template-building capabilities of original authoring software.')


def bracket(values: list[float], target: float):
    if not values:
        return None, None, 0.
    target = f32(target)
    current = previous = values[0]
    index = 0
    while target > current + 1e-8 and index < len(values) - 1:
        previous = current
        index += 1
        current = values[index]
    first = index - 1 if index else 0
    weight = 0.
    if current > previous:
        weight = f32(f32(target - previous) / f32(current - previous))
        if weight >= 0.99999999:
            first += 1
            weight = 0.
    second = first + 1 if weight > 0 and first < len(values) - 1 else None
    return first, second, weight


def native_selection(dll: Path) -> dict:
    native = Native(dll)
    choose = native.fn(0x1101c0, Q, P, c.c_char_p, F, P, P, P)
    append = native.fn(0x10fd10, I, P, P, c.c_char_p)
    find = native.fn(0x1076b0, P, P, c.c_char_p)
    sort = native.fn(0x1103e0, None, P, c.c_char_p)
    cases = []
    layouts = ((), (0.,), (-1., 0., 1.), (-.25, -.25, .5, 3.),
               (0., 1e-8, 1.))
    for values in layouts:
        root = native.alloc(0x168)
        native.fn(0x10fb30, P, P)(root)
        for name in ('dynamics', 'opening', 'pitch'):
            for value in reversed(values):
                frame = native.alloc(0x328)
                native.fn(0x12a670, P, P)(frame)
                feature = native.alloc(0x228)
                P.from_address(frame + 0x208).value = feature
                Q.from_address(frame + 0x150).value = 0x2000
                I.from_address(feature).value = 0x70000
                address = frame + 0x1d0 if name == 'pitch' else feature + (
                    0x21c if name == 'dynamics' else 0x218)
                F.from_address(address).value = value
                leaf = native.alloc(0x170)
                native.fn(0x10ec80, P, P, P)(leaf, frame)
                if append(root, leaf, name.encode()):
                    raise AssertionError('Cannot append owned timbre leaf')
            sort(root, name.encode())
            group = find(root, name.encode())
            vector = P.from_address(group + 0x148).value
            ordered = [P.from_address(vector + 8 * i).value for i in range(len(values))]
            inputs = sorted(set((-5., -.5, 0., .25, .5, .75, 1., 5.,
                1e-9, 1e-8, 2e-8, *values)))
            expected_axis = sorted(map(f32, values))
            for target in inputs:
                expected_first, expected_second, expected_weight = bracket(expected_axis, target)
                first, second, weight = P(1), P(1), F(999.)
                code = choose(root, name.encode(), f32(target), c.byref(first),
                              c.byref(second), c.byref(weight))
                expected_pointers = [ordered[index] if index is not None else None
                                     for index in (expected_first, expected_second)]
                if (code or [first.value, second.value] != expected_pointers
                        or bytes(weight) != bytes(F(expected_weight))):
                    raise AssertionError(f'Timbre selector differs: {name}, {values}, {target}')
                cases.append(dict(axis=name, values=expected_axis, input=f32(target),
                    first_index=expected_first, second_index=expected_second,
                    weight=weight.value, native_identity_and_float32_exact=True))
        first, second, weight = P(1), P(1), F(999.)
        code = choose(root, b'absent', 0., c.byref(first), c.byref(second), c.byref(weight))
        if code or first.value or second.value or weight.value:
            raise AssertionError('Missing axis does not reset selector outputs')
    return dict(selector_rva='0x1101c0', group_sort_rva='0x1103e0',
        getter_rva='0x110020', cases=cases, exact_cases=len(cases), missing_axis_cases=5,
        search_epsilon_double=1e-8, upper_snap_threshold_double=0.99999999,
        scope='Owned finite timbre-axis values: empty/single/multiple/duplicate/near-epsilon '
              'groups, all three axes, interpolation and clamping. It establishes linear '
              'leaf selection, not acoustic synthesis, VQM phase production or actual '
              'Editor reachability. Original V4X saved TMM arrays are independently empty.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--voice-root', type=Path, required=True)
    parser.add_argument('--parser-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    banks = bank_templates(args.manifest, args.voice_root, args.parser_root)
    selection = native_selection(args.dll)
    report = dict(dse_sha256=SHA256,
        source_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        saved_bank_templates=banks, native_selector=selection,
        conclusion='The timbre-model getter does feed a linear template selector. '
                   'All pinned V4X saved TMM axis groups are empty, so they provide no '
                   'saved template Frame input to this route. This route cannot by itself '
                   'explain the observed VQMp bit17 production or prove original units.')
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(banks=len(banks['banks']), models=banks['models'],
        empty_groups=banks['empty_groups'], selector_cases=selection['exact_cases'],
        missing_axis_cases=selection['missing_axis_cases'], all_passed=True)))


if __name__ == '__main__':
    main()
