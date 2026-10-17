"""Verify owned PCM -> DDI/DDB -> native product table -> native license result.

Generated audio is a diagnostic sine, not a trained voice. Resource enumeration
and descriptor inputs use private-process adapters. No commercial data, registry
or installed license is modified; calculated keys and hardware IDs are not saved.
"""

import argparse
import ctypes as c
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile
import wave
from collections import Counter

import build_minimal_sil_a_bank as build_driver
import build_unit_ddb
import dse_product_payload as payload_codec
import finalize_stationary_ddi as finalizer
import verify_dse_candidate_acceptance as acceptance
import verify_dse_license_calculations as audit

P = c.c_void_p
TOOLS = Path(__file__).resolve().parent


def run(arguments, environment=None):
    result = subprocess.run([str(value) for value in arguments], env=environment,
                            capture_output=True, text=True, errors='replace', timeout=60)
    if result.returncode != 0:
        raise RuntimeError(result.stdout[-3000:] + result.stderr[-3000:])


def build_template(scratch, parts):
    wave_path = scratch / 'owned_sine.wav'
    with wave.open(str(wave_path), 'wb') as output:
        output.setparams((1, 2, 44100, 0, 'NONE', 'not compressed'))
        output.writeframes(b''.join(struct.pack('<h', round(
            10000 * math.sin(2 * math.pi * 220 * index / 44100))) for index in range(13230)))
    sms2 = scratch / 'owned.sms2'
    run(['dotnet', build_driver.DRS_DLL, audit.EDITOR / 'DSE.dll', sms2,
         '0.3', '220', 'external', '220', wave_path], build_driver.analysis_environment())
    ddb = scratch / 'owned_source.ddb'
    build_unit_ddb.build(sms2, wave_path, ddb, 'sta')
    ids = ['owned_' + str(index) for index in range(parts)]
    plan = {'format': 'vocaloid-ddi-tree-plan-v1', 'language_id': 0,
            'phonemes': [{'name': 'a', 'unvoiced': False}],
            'stationary': [{'phoneme': 'a', 'stationary_index': 0, 'unit_ids': ids}],
            'articulations': [],
            'part_order': {'stationary_unit_ids': ids, 'articulation_unit_ids': []},
            'summary': {'phonemes': 1, 'stationary_phonemes': 1, 'stationary_parts': parts,
                        'articulation_edges': 0, 'articulation_parts': 0, 'approval_complete': False}}
    plan_path = scratch / 'owned_plan.json'
    plan_path.write_text(json.dumps(plan), encoding='utf-8')
    stem = 'owned_p_resource'
    skeleton_dir = scratch / 'skeleton'
    environment = os.environ.copy()
    for key in list(environment):
        if key.startswith('TREE_HARNESS_'):
            environment.pop(key)
    run(['dotnet', build_driver.TREE_DLL, audit.EDITOR / 'DSE.dll', skeleton_dir,
         stem, '--plan', plan_path], environment)
    raw = (skeleton_dir / (stem + '.tree')).read_bytes()
    info = finalizer.inspect_ddb(ddb)
    positions = finalizer.stationary_positions(raw)
    assert len(positions) == parts
    # All diagnostic parts reference the same owned, valid stationary unit.
    for position in reversed(positions):
        raw, _ = finalizer.insert_stationary_at(raw, position, info, 220, 0, 0.6, 90)
    template = bytearray(raw)
    finalizer.insert_dbse_authentication(template, stem)
    finalizer.normalize_compact_ddi(template)
    assert len(payload_codec.owned_compact_slot_offsets(template)) == parts
    return stem, bytes(template), ddb, info


class LoadedProductTable:
    """Load a real owned resource file, then call the original table producer."""

    def __init__(self, directory, stem, expected_comp):
        self.table = P()
        self.singer = c.create_string_buffer(0x310)
        audit.fn(0x10c5b0, P, P, P, c.c_char_p, c.c_char_p, c.c_float, c.c_ubyte)(
            self.singer, None, str(directory.resolve()).encode('mbcs'), stem.encode('ascii'), 44100, 0)
        assert audit.fn(0x10d490, c.c_int64, P)(self.singer) == 0
        assert self.singer[0x2d4][0] == 1
        self.wrapper_output = c.create_string_buffer(512)
        assert audit.fn(0x10cf00, c.c_int64, P, P)(self.singer, self.wrapper_output) == 0
        self.direct_output = c.create_string_buffer(512)
        length = c.c_int(-1)
        self.direct_result = audit.fn(0x10cfb0, c.c_int64, P, P, P)(
            self.singer, self.direct_output, c.byref(length))
        self.direct_length = length.value
        self.native_payload = bytes(self.wrapper_output).split(b'\0', 1)[0]
        self.info = (c.c_int32 * 4)(0, 0, 0, 0)
        self.infos = (c.c_uint64 * 1)(c.addressof(self.info))
        self.singers = (c.c_uint64 * 1)(c.addressof(self.singer))
        self.resources = c.create_string_buffer(32)
        struct.pack_into('<i', self.resources, 8, 1)
        struct.pack_into('<QQ', self.resources, 16, c.addressof(self.singers), c.addressof(self.infos))
        self.empty_dnn = c.create_string_buffer(32)
        self.count = c.CFUNCTYPE(c.c_size_t, P, c.c_int)(lambda obj, kind: 0)
        self.vtable = (c.c_uint64 * 3)(0, 0, c.cast(self.count, P).value)
        self.vdm = (c.c_uint64 * 1)(c.addressof(self.vtable))
        self.context = c.create_string_buffer(56)
        struct.pack_into('<Q', self.context, 0, c.addressof(self.vdm))
        struct.pack_into('<Q', self.context, 16, c.addressof(self.resources))
        struct.pack_into('<Q', self.context, 40, c.addressof(self.empty_dnn))
        assert audit.fn(0x180850, c.c_ubyte, P, P)(self.context, c.byref(self.table))
        number = c.c_int.from_address(self.table.value).value
        data = c.c_void_p.from_address(self.table.value + 8).value
        assert 0 < number < 1024 and data
        matches = []
        for index in range(number):
            record = c.string_at(data + index * 146, 146)
            if record[:16] == audit.transform(expected_comp.encode('ascii')):
                matches.append(record[17:146].split(b'\0', 1)[0])
        assert len(matches) == 1
        self.table_payload = matches[0]

    def close(self):
        if self.table.value:
            audit.free(c.c_void_p.from_address(self.table.value + 8).value)
            audit.free(self.table.value)
            self.table.value = None
        # Loaded singer graphs and streams are reclaimed by this short-lived
        # process, rather than invoking an unverified reverse-engineered destructor.


def check_installed_slots():
    find = audit.fn(0x1076b0, P, P, c.c_char_p)
    by_phoneme = audit.fn(0x10a350, P, P, c.c_int)
    comparisons = []
    with acceptance.installed_resources() as banks:
        expected = {bank['comp']: bank['product_data'] for bank in banks if bank['kind'] == 0}
        resources = c.c_void_p.from_address(audit.BASE + 0xa9e7c0 + 16).value
        count = c.c_int.from_address(resources + 8).value
        singers = c.c_void_p.from_address(resources + 16).value
        assert 0 <= count <= 4096
        for index in range(count):
            singer = c.c_void_p.from_address(singers + index * 8).value
            if not singer:
                continue
            path = c.string_at(singer + 0x18).decode('mbcs')
            identity = Path(path).parent.name[:16]
            if identity not in expected:
                continue
            dictionary = c.c_void_p.from_address(singer + 0x170).value
            phonemes = c.c_int.from_address(dictionary + 0x144).value
            stationary = find(find(singer, b'voice'), b'stationary')
            groups = c.c_int.from_address(stationary + 0x150).value
            group_array = c.c_void_p.from_address(stationary + 0x148).value
            assert 0 <= phonemes <= 4096 and 0 <= groups <= 4096
            rows = []
            done = False
            for group_index in range(groups):
                group = c.c_void_p.from_address(group_array + group_index * 8).value
                for phoneme_index in range(phonemes):
                    unit = by_phoneme(group, phoneme_index)
                    if not unit:
                        continue
                    number = c.c_int.from_address(unit + 0x150).value
                    parts = c.c_void_p.from_address(unit + 0x148).value
                    assert 0 <= number <= 1000000
                    for part_index in range(number):
                        part = c.c_void_p.from_address(parts + part_index * 8).value
                        if not part:
                            continue
                        rows.append(tuple(c.c_int.from_address(part + offset).value
                                          for offset in [0x1f0, 0x1f4, 0x1f8, 0x1fc]))
                        decoded = payload_codec.decode_slots(rows)
                        if decoded.helper_success or (rows and rows[0][2] < 1):
                            done = True
                            break
                    if done:
                        break
                if done:
                    break
            decoded = payload_codec.decode_slots(rows)
            assert decoded.helper_success
            assert payload_codec.effective_product_data(decoded) == expected[identity]
            comparisons.append({'compid': identity, 'declared_length': decoded.declared_length,
                                'parts_consumed': len(rows), 'independent_slot_decode_matches': True})
        assert len(comparisons) == len(expected)
    return comparisons


def check_authoring_guards(template):
    offsets = payload_codec.owned_compact_slot_offsets(template)
    wrong_phdc = bytearray(template)
    struct.pack_into('<I', wrong_phdc, wrong_phdc.find(b'PHDC') + 12, 2)
    huge_frames = bytearray(template)
    struct.pack_into('<I', huge_frames, finalizer.stationary_positions(template)[0] + 104, 0xffffffff)
    checks = [
        lambda: payload_codec.encode_slots(b'A' * 129, 33),
        lambda: payload_codec.encode_slots(b'ABCD', 1),
        lambda: payload_codec.encode_slots(b'A\0B', 1),
        lambda: payload_codec.encode_slots(b'A', 0),
        lambda: payload_codec.read_owned_compact(b'invalid'),
        lambda: payload_codec.read_owned_compact(bytes(wrong_phdc)),
        lambda: payload_codec.read_owned_compact(bytes(huge_frames)),
        lambda: payload_codec.read_owned_compact(template[:offsets[-1] + 8]),
        lambda: payload_codec.decode_slots([(0, 0, 129, 0)]),
    ]
    for check in checks:
        try:
            check()
        except payload_codec.PayloadError:
            continue
        raise AssertionError('Invalid owned authoring input was accepted')
    return len(checks)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--scratch', type=Path)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--installed', action='store_true', help='Also compare installed traditional payload slots')
    args = parser.parse_args()
    scratch = args.scratch.resolve() if args.scratch else Path(tempfile.mkdtemp(prefix='dse-owned-p-'))
    scratch.mkdir(parents=True, exist_ok=True)
    if any(scratch.iterdir()):
        raise ValueError('scratch directory must be empty to preserve existing files')
    for project in [build_driver.DRS_PROJECT, build_driver.TREE_PROJECT]:
        run(['dotnet', 'build', project, '-c', 'Release', '--no-restore'])
    stem, template, ddb_source, info = build_template(scratch, 33)
    authoring_guard_checks = check_authoring_guards(template)
    comp = acceptance.encode('00Q40000260101')
    serial = acceptance.encode('00P40000260101').encode('ascii')
    hardware = acceptance.hardware_candidates()
    assert hardware
    harness = acceptance.CandidateHarness()
    extraction_hash = hashlib.sha256(c.string_at(audit.BASE + 0x10cf00, 0x590)).hexdigest()
    observations = []
    decisions = []
    inputs_seen = set()
    loaded = []

    def resource_case(label, payload, directory_comp=comp, altered_ddb=False, upper_bits=False):
        directory = scratch / label / directory_comp
        directory.mkdir(parents=True)
        ddi = payload_codec.write_owned_compact(template, payload)
        if upper_bits:
            modified = bytearray(ddi)
            for part_index, offset in enumerate(payload_codec.owned_compact_slot_offsets(ddi)):
                for slot in range(4):
                    if part_index == 0 and slot == 2:
                        continue  # The first length consumes the full signed i32.
                    low = struct.unpack_from('<i', modified, offset + slot * 4)[0] & 255
                    struct.pack_into('<i', modified, offset + slot * 4, 0x12340000 | low)
            ddi = bytes(modified)
        extraction = payload_codec.read_owned_compact(ddi)
        assert extraction.data == payload and extraction.helper_success == bool(payload)
        (directory / (stem + '.ddi')).write_bytes(ddi)
        ddb_path = directory / (stem + '.ddb')
        shutil.copyfile(ddb_source, ddb_path)
        if altered_ddb:
            with ddb_path.open('r+b') as stream:
                stream.seek(info.snd_offset + finalizer.SND_HEADER.size + 2048)
                byte = stream.read(1)
                stream.seek(-1, 1)
                stream.write(bytes([byte[0] ^ 1]))
        resource = LoadedProductTable(directory, stem, directory_comp)
        loaded.append(resource)  # Retain native roots until process exit.
        assert resource.native_payload == payload == resource.table_payload
        assert (resource.direct_result == 0) == extraction.helper_success
        if payload:
            assert resource.direct_length == len(payload)
            assert bytes(resource.direct_output)[:len(payload)] == payload
        return resource, {'case': label, 'length': len(payload), 'native_extraction_equal': True,
                          'native_table_equal': True, 'root_authenticated': True,
                          'ddi_sha256': hashlib.sha256(ddi).hexdigest()}

    def decide(resource, label, result, key, product_data, metadata_comp=comp):
        observation = harness.run(label, result, metadata_comp, product_data, [(serial, key)], kind=0,
                                  native_product_table=resource.table)
        decisions.append(observation)
        return observation

    try:
        for index, (length, phase) in enumerate(
                [(length, 0) for length in [0, 1, 2, 3, 4, 7, 15, 16, 31, 32, 64, 70, 71, 127, 128]]
                + [(128, 127)]):
            payload = bytes((offset + phase) % 255 + 1 for offset in range(length))
            inputs_seen.update(payload)
            label = 'payload_' + str(length) + '_phase_' + str(phase)
            resource, observation = resource_case(label, payload)
            key = acceptance.key_for(serial, comp, hardware[0], payload)
            observation['positive'] = decide(resource, label + '_accept', 15, key, payload)
            changed = (b'0' if key[:1] != b'0' else b'1') + key[1:]
            observation['negative'] = decide(resource, label + '_changed_key', 10, changed, payload)
            observations.append(observation)
            resource.close()
        original = bytes(range(1, 71))
        old_key = acceptance.key_for(serial, comp, hardware[0], original)
        altered = bytes([2]) + original[1:]
        resource, observation = resource_case('resource_payload_changed', altered)
        observation['old_key'] = decide(resource, 'changed_resource_old_key', 10, old_key, altered)
        observation['new_key'] = decide(resource, 'changed_resource_recalculated_key', 15,
            acceptance.key_for(serial, comp, hardware[0], altered), altered)
        observations.append(observation)
        resource.close()
        resource, observation = resource_case('slot_upper_bits_changed', original, upper_bits=True)
        observation['same_key'] = decide(resource, 'same_low_bytes_different_upper_bits', 15,
                                         old_key, original)
        observations.append(observation)
        resource.close()
        other_comp = acceptance.encode('00N40000260101')
        resource, observation = resource_case('component_directory_changed', original, other_comp)
        observation['old_identity'] = decide(resource, 'directory_identity_mismatch', 10, old_key, original)
        observation['new_identity'] = decide(resource, 'directory_identity_aligned', 15,
            acceptance.key_for(serial, other_comp, hardware[0], original), original, other_comp)
        observations.append(observation)
        resource.close()
        resource, observation = resource_case('ddb_pcm_changed', original, altered_ddb=True)
        observation['same_key'] = decide(resource, 'same_slots_changed_pcm', 15, old_key, original)
        observations.append(observation)
        resource.close()
        assert len(inputs_seen) == 255
        harness.close()
        assert extraction_hash == hashlib.sha256(c.string_at(audit.BASE + 0x10cf00, 0x590)).hexdigest()
    finally:
        for resource in loaded:
            resource.close()
    installed = check_installed_slots() if args.installed else []
    report = {
        'schema_version': 1, 'date': '2026-10-16', 'targets': audit.EXPECTED_HASHES,
        'scope': 'Owned standard DBSe resources through original P/table/candidate functions',
        'owned_audio': {'source': 'generated 220 Hz sine', 'sample_rate': 44100,
                        'source_samples': 13230, 'frames': len(info.frame_offsets),
                        'ddb_sha256': hashlib.sha256(ddb_source.read_bytes()).hexdigest()},
        'stationary_parts': 33, 'resource_cases': observations,
        'summary': {'resource_cases': len(observations), 'native_license_decisions': len(decisions),
                    'license_results': dict(Counter(item['result'] for item in decisions)),
                    'nonzero_byte_values_exercised': len(inputs_seen),
                    'authoring_guard_rejections': authoring_guard_checks,
                    'installed_slot_comparisons': len(installed), 'all_passed': True},
        'installed_reference_slots': installed,
        'native_extraction_code_unchanged': True, 'native_candidate_code_unchanged': True,
        'native_extraction_code_check': {'rva': '0x10cf00', 'size': '0x590',
                                         'before_sha256': extraction_hash,
                                         'after_sha256': hashlib.sha256(c.string_at(
                                             audit.BASE + 0x10cf00, 0x590)).hexdigest()},
        'owned_component_id': comp,
        'native_table_manually_populated': False,
        'adapter_layers': ['resource enumeration container', 'VDM descriptor inputs'],
        'boundaries': {'commercial_data_used_in_owned_fixtures': False,
                       'commercial_resources_read_for_optional_comparison': bool(installed),
                       'registry_or_installed_license_written': False,
                       'real_keys_hardware_identifiers_exported': False, 'editor_started_or_modified': False,
                       'editor_discovery_or_render_verified': False, 'splice_lease_signed': False},
    }
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report['summary']), flush=True)


if __name__ == '__main__':
    main()
