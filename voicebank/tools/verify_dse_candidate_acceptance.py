"""Exercise the original DSE candidate pipeline with private-process inputs.

No DLL code, registry, installed descriptor or lease file is changed. A minimal
VDM interface adapter supplies synthetic descriptors to the unchanged native
function. Default mode uses owned product identities and data; --installed also
uses resource snapshots from installed banks, without exporting calculated keys.
"""

import argparse
import contextlib
import ctypes as c
import datetime as dt
import hashlib
import json
import os
import time
import uuid
from pathlib import Path

import compid_codec as codec
import verify_dse_license_calculations as audit

P = c.c_void_p
RESULTS = {0: 'Undefined', 1: 'MissingLeaseFile', 7: 'ExpiredKey',
           10: 'InvalidKey', 11: 'InvalidSerialNumber', 12: 'InvalidComponent',
           13: 'InvalidHash', 14: 'ValidExpiryKey', 15: 'NoError'}
TABLES = codec.load_tables(audit.EDITOR / 'VDM.dll')


def encode(payload):
    return codec.encode_component_id(payload, TABLES)


def normalize(comp):
    payload = codec.decode_component_id(comp, TABLES)
    return encode(payload[:7] + '0' + payload[8:])


def api(lib, name, result, *args):
    function = getattr(lib, name)
    function.restype, function.argtypes = result, args
    return function


def hardware_candidates():
    vector = (c.c_uint64 * 3)()
    audit.fn(0x1d8030, P, P, P)(None, vector)
    start, end, capacity = vector
    assert 0 <= end - start <= capacity - start < 4096 and (end - start) % 32 == 0
    try:
        return [audit.read_string((c.c_char * 32).from_address(address))
                for address in range(start, end, 32)]
    finally:
        for address in range(start, end, 32):
            audit.destroy_string(address)
        if start:
            audit.free(start)


def key_for(serial, comp, hardware, product_data):
    return audit.md5(serial + normalize(comp).encode('ascii') + hardware
                     + audit.untransform(product_data))


def date_id(date):
    assert 2000 <= date.year <= 2099
    return encode('00A40000' + date.strftime('%y%m%d'))


def expiry_key(base, date):
    identifier = date_id(date).encode('ascii')
    assert identifier[:1] == b'B'
    window = b''.join(f'{byte:02x}'[::-1].encode('ascii')
                      for byte in reversed(identifier[1:]))
    return base[:3] + window + base[3:]


def splice_value(comp, ice_name, product_data):
    pool = audit.read_pool()
    salts = []
    for seed in [123456789, 987654321, 20240601]:
        generator = audit.mt(seed)
        salts.append(bytes(pool[next(generator) % 250] for _ in range(25)))
    return hashlib.sha256(salts[0] + ice_name.encode('utf-8') + salts[1]
                          + comp.encode('utf-8') + product_data + salts[2]).hexdigest()


class CandidateHarness:
    """The adapter supplies inputs; native candidate decisions remain unchanged."""

    def __init__(self):
        self.keep = []
        self.state = {}
        self.descriptors = []
        self.errors = []
        self.calls = {}
        self.manager_global = audit.fn(0x1dc0b0, P)()
        self.native_code_before = self.code_hash()
        self.descriptor_vtable = (c.c_uint64 * 3)()
        for slot, offset, name in [(1, 8, 'serial'), (2, 16, 'key')]:
            self.descriptor_vtable[slot] = self.callback(
                name, P, [P], lambda obj, offset=offset:
                c.c_uint64.from_address(obj + offset).value)
        self.bank_vtable = (c.c_uint64 * 33)()
        self.bank = (c.c_uint64 * 1)(c.addressof(self.bank_vtable))
        for offset, name in [(0x10, 'comp'), (0x38, 'date'), (0x40, 'name'),
                             (0xf8, 'ice_name'), (0x100, 'ice_value')]:
            self.bank_vtable[offset // 8] = self.callback(
                name, P, [P], lambda obj, name=name: c.addressof(self.state[name]))
        self.bank_vtable[0x20 // 8] = self.callback(
            'version', c.c_ubyte, [P, P, P, P], self.version)
        self.bank_vtable[0xa0 // 8] = self.callback(
            'num_licenses', c.c_size_t, [P], lambda obj: len(self.descriptors))
        self.bank_vtable[0xa8 // 8] = self.callback(
            'license', P, [P, P], self.descriptor_by_index)
        self.manager_vtable = (c.c_uint64 * 5)()
        self.manager = (c.c_uint64 * 1)(c.addressof(self.manager_vtable))
        self.manager_vtable[0x10 // 8] = self.callback(
            'num_banks', c.c_size_t, [P, c.c_int],
            lambda obj, kind: int(kind == self.state['kind']))
        self.manager_vtable[0x20 // 8] = self.callback(
            'bank', P, [P, P, c.c_int], self.bank_by_index)
        self.get_comp = api(audit.dll, 'VIS_DSE_GetCompIDFromLicense', c.c_wchar_p, P)
        self.get_name = api(audit.dll, 'VIS_DSE_GetCompNameFromLicense', c.c_wchar_p, P)
        self.get_result = api(audit.dll, 'VIS_DSE_GetResultFromLicense', c.c_int, P)
        self.get_type = api(audit.dll, 'VIS_DSE_GetCompTypeFromLicense', c.c_int, P)
        self.get_expiry = api(audit.dll, 'VIS_DSE_GetExpiryDateFromLicense', c.c_int64, P)

    @staticmethod
    def code_hash():
        return hashlib.sha256(c.string_at(audit.BASE + 0x1dc510, 0x3b60)).hexdigest()

    def callback(self, name, result, args, body):
        def invoke(*values):
            self.calls[name] = self.calls.get(name, 0) + 1
            try:
                return body(*values)
            except Exception as error:
                self.errors.append({'callback': name, 'error_type': type(error).__name__})
                return 0
        function = c.CFUNCTYPE(result, *args)(invoke)
        self.keep.append(function)
        return c.cast(function, P).value

    def version(self, obj, major, minor, revision):
        for pointer, value in zip([major, minor, revision], self.state['version']):
            c.c_int.from_address(pointer).value = value
        return 1

    def descriptor_by_index(self, obj, pointer):
        index = c.c_size_t.from_address(pointer).value
        assert index < len(self.descriptors)
        return c.addressof(self.descriptors[index][0])

    def bank_by_index(self, obj, pointer, kind):
        assert c.c_size_t.from_address(pointer).value == 0 and kind == self.state['kind']
        return c.addressof(self.bank)

    def run(self, name, expected, comp, product_data, candidates, kind=1,
            date=None, bank_name='Owned DSE candidate fixture', version=(5, 0, 0),
            table_comp=None, table_present=True, ice_name='', ice_value='',
            expected_expiry=None, native_product_table=None):
        self.state['kind'], self.state['version'] = kind, version
        for field, value in [('comp', comp), ('date', date or date_id(dt.date(2000, 1, 1))),
                             ('name', bank_name), ('ice_name', ice_name), ('ice_value', ice_value)]:
            self.state[field] = c.create_unicode_buffer(value)
        self.descriptors = []
        for serial, key in candidates:
            serial_buffer, key_buffer = c.create_string_buffer(serial), c.create_string_buffer(key)
            descriptor = (c.c_uint64 * 3)(c.addressof(self.descriptor_vtable),
                                         c.addressof(serial_buffer), c.addressof(key_buffer))
            self.descriptors.append((descriptor, serial_buffer, key_buffer))
        assert len(product_data) <= 128 and b'\0' not in product_data
        if native_product_table is None:
            record = c.create_string_buffer(146)
            table_identity = table_comp or normalize(comp)
            c.memmove(record, audit.transform(table_identity.encode('ascii')), 16)
            c.memmove(c.addressof(record) + 17, product_data, len(product_data))
            table = (c.c_uint64 * 2)(int(table_present), c.addressof(record))
        else:
            table = native_product_table
        try:
            audit.fn(0x1dc510, None, P, P, P)(self.manager_global, table, self.manager)
            assert not self.errors, self.errors
            start = c.c_uint64.from_address(self.manager_global + 0x18).value
            end = c.c_uint64.from_address(self.manager_global + 0x20).value
            assert 0 <= end - start < 8192 and (end - start) % 8 == 0
            voices = []
            for address in range(start, end, 8):
                license_object = c.c_void_p.from_address(address).value
                if self.get_type(license_object) == 2:
                    assert self.get_comp(license_object) == comp
                    assert self.get_name(license_object) == bank_name
                    voices.append((self.get_result(license_object), self.get_expiry(license_object)))
            assert len(voices) == 1, {'case': name, 'voice_count': len(voices)}
            result, expiry = voices[0]
            observation = {'case': name, 'kind': 'DSE' if kind == 0 else 'DNN',
                           'expected': expected, 'actual': result,
                           'result': RESULTS.get(result, str(result)), 'passed': result == expected}
            assert result == expected, observation
            if expected_expiry is not None:
                expected_time = int(time.mktime((expected_expiry.year, expected_expiry.month,
                                                expected_expiry.day, 0, 0, 0, 0, 0, 0)))
                assert expiry == expected_time, {'case': name, 'expiry_matches': False}
                observation['public_expiry_matches'] = True
            return observation
        finally:
            audit.fn(0x1da330, None, P)(self.manager_global + 0x18)
            # The table is a private input buffer, never an installed bank table.
            c.c_uint64.from_address(self.manager_global + 8).value = 0

    def close(self):
        assert self.code_hash() == self.native_code_before
        assert not self.errors


def owned_cases(harness, hardware):
    comp = encode('00Q40000260101')
    serial = encode('00P40000260101').encode('ascii')
    data = audit.transform(b'owned-product-resource-only-no-commercial-input')
    base = key_for(serial, comp, hardware[0], data)
    changed_key = (b'0' if base[:1] != b'0' else b'1') + base[1:]
    today = dt.date.today()
    ice_name = 'owned-dse-acceptance-' + uuid.uuid4().hex
    lease_path = Path(os.environ['LOCALAPPDATA']) / 'SpliceSettings' / 'license' / (ice_name + '.lic')
    assert not lease_path.exists()
    ice_value = splice_value(comp, ice_name, data)
    observations = []
    for kind in [0, 1]:
        def run(name, expected, **changes):
            values = dict(comp=comp, product_data=data, candidates=[(serial, base)], kind=kind)
            values.update(changes)
            observations.append(harness.run(name, expected, **values))
        for index, candidate in enumerate(hardware):
            run('ordinary32_hardware_' + str(index), 15,
                candidates=[(serial, key_for(serial, comp, candidate, data))])
        run('ordinary32_normalized_variant', 15, comp=encode('00Q4000F260101'))
        run('ordinary32_changed_serial', 10,
            candidates=[(encode('00N40000260101').encode('ascii'), base)])
        run('ordinary32_invalid_serial_codec', 11, candidates=[(b'invalid-serial', base)])
        run('ordinary32_invalid_component_codec', 12, comp='invalid-comp-id', table_comp=comp)
        run('ordinary32_wrong_key_length', 10, candidates=[(serial, base[:-1])])
        run('ordinary32_changed_key', 10, candidates=[(serial, changed_key)])
        fake_hardware = b'owned-no-such-hardware'
        assert fake_hardware not in hardware
        run('ordinary32_wrong_hardware', 10,
            candidates=[(serial, key_for(serial, comp, fake_hardware, data))])
        run('ordinary32_changed_product_data', 10,
            product_data=audit.transform(b'owned-different-product-data'))
        run('ordinary32_missing_product_table_entry', 10, table_present=False)
        run('no_candidates', 0, candidates=[])
        run('bad_then_good_candidate', 15, candidates=[(serial, changed_key), (serial, base)])
        run('good_then_bad_candidate', 15, candidates=[(serial, base), (serial, changed_key)])
        for name, expiry, start, expected in [
            ('expiry_future', dt.date(2099, 12, 31), dt.date(2000, 1, 1), 14),
            ('expiry_past', dt.date(2000, 1, 2), dt.date(2000, 1, 1), 7),
            ('start_future', dt.date(2099, 12, 31), today + dt.timedelta(days=1), 7),
            ('expiry_today', today, dt.date(2000, 1, 1), 14),
            ('start_and_expiry_today', today, today, 14),
        ]:
            run(name, expected, candidates=[(serial, expiry_key(base, expiry))],
                date=date_id(start), expected_expiry=expiry)
        run('expiry_wrong_digest', 10,
            candidates=[(serial, expiry_key(changed_key, dt.date(2099, 12, 31)))])
        run('splice_digest_valid_missing_lease', 1, candidates=[(b'splice', base)],
            ice_name=ice_name, ice_value=ice_value)
        run('splice_digest_changed', 13, candidates=[(b'splice', base)], ice_name=ice_name,
            ice_value=('0' if ice_value[:1] != '0' else '1') + ice_value[1:])
        run('splice_ice_name_changed', 13, candidates=[(b'splice', base)],
            ice_name=ice_name + '-changed', ice_value=ice_value)
        run('splice_product_data_changed', 13, candidates=[(b'splice', base)],
            product_data=audit.transform(b'owned-different-product-data'),
            ice_name=ice_name, ice_value=ice_value)
        run('splice_hash_wrong_length', 13, candidates=[(b'splice', base)],
            ice_name=ice_name, ice_value=ice_value[:-1])
    return observations


@contextlib.contextmanager
def installed_resources():
    """Read product resources in this process, without replacing descriptors."""
    vdm = c.CDLL(str(audit.EDITOR / 'VDM.dll'))
    create = api(vdm, 'VDM_createDatabaseManager', P, c.c_wchar_p, c.c_wchar_p, P)
    destroy = api(vdm, 'VDM_DatabaseManager_destroy', None, P)
    count = api(vdm, 'VDM_DatabaseManager_numVoiceBanks', c.c_size_t, P, c.c_int)
    by_index = api(vdm, 'VDM_DatabaseManager_voiceBankByIndex', P, P, c.c_size_t, c.c_int)
    get_comp = api(vdm, 'VDM_VoiceBank_compID', c.c_wchar_p, P)
    get_name = api(vdm, 'VDM_VoiceBank_name', c.c_wchar_p, P)
    get_date = api(vdm, 'VDM_VoiceBank_date', c.c_wchar_p, P)
    get_version = api(vdm, 'VDM_VoiceBank_version', c.c_ubyte, P, P, P, P)
    create_dse = api(audit.dll, 'VIS_DSE_CreateManager', P)
    initialize = api(audit.dll, 'VIS_DSE_InitializeManager', c.c_int, P, P)
    terminate = api(audit.dll, 'VIS_DSE_TerminateManager', c.c_int, P)
    destroy_dse = api(audit.dll, 'VIS_DSE_DestroyManager', None, P)
    result = c.c_int(-1)
    explib = Path(os.environ['CommonProgramFiles']) / 'VOCALOID6' / 'Explib'
    manager = create('VOCALOID6', str(explib.resolve()), c.byref(result))
    assert manager and result.value == 0
    dse_manager = None
    table = c.c_void_p()
    initialized = False
    try:
        dse_manager = create_dse()
        assert dse_manager
        assert initialize(dse_manager, manager) == 0
        initialized = True
        assert audit.fn(0x180850, c.c_ubyte, P, P)(audit.BASE + 0xa9e7c0, c.byref(table))
        number = c.c_int.from_address(table.value).value
        pointer = c.c_void_p.from_address(table.value + 8).value
        assert 0 < number < 1024 and pointer
        raw = c.string_at(pointer, number * 146)
        products = {}
        for index in range(number):
            record = raw[index * 146:(index + 1) * 146]
            identity = audit.untransform(record[:16]).decode('ascii')
            assert identity not in products
            products[identity] = record[17:146].split(b'\0', 1)[0]
        banks = []
        for kind in [0, 1]:
            for index in range(count(manager, kind)):
                bank = by_index(manager, index, kind)
                comp = get_comp(bank)
                version = [c.c_int() for _ in range(3)]
                assert get_version(bank, *[c.byref(value) for value in version])
                vtable = c.c_void_p.from_address(bank).value
                ice_fields = []
                for offset in [0xf8, 0x100]:
                    function = c.c_void_p.from_address(vtable + offset).value
                    ice_fields.append(c.CFUNCTYPE(c.c_wchar_p, P)(function)(bank) or '')
                banks.append(dict(comp=comp, product_data=products[normalize(comp)],
                                  kind=kind, date=get_date(bank), bank_name=get_name(bank),
                                  version=tuple(value.value for value in version),
                                  ice_name=ice_fields[0], ice_value=ice_fields[1]))
        yield banks
    finally:
        if table.value:
            audit.free(c.c_void_p.from_address(table.value + 8).value)
            audit.free(table.value)
        if initialized:
            terminate(dse_manager)
        if dse_manager:
            destroy_dse(dse_manager)
        destroy(manager)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--installed', action='store_true')
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    hardware = hardware_candidates()
    assert hardware
    harness = CandidateHarness()
    owned = owned_cases(harness, hardware)
    print(json.dumps({'owned_cases': len(owned), 'all_passed': True}), flush=True)
    installed = []
    if args.installed:
        with installed_resources() as banks:
            for bank in banks:
                serial = encode('00P40000260101').encode('ascii')
                key = key_for(serial, bank['comp'], hardware[0], bank['product_data'])
                positive = harness.run('installed_resource_ordinary32', 15,
                                       candidates=[(serial, key)], **bank)
                changed = (b'0' if key[:1] != b'0' else b'1') + key[1:]
                negative = harness.run('installed_resource_changed_key', 10,
                                       candidates=[(serial, changed)], **bank)
                entry = {'name': bank['bank_name'], 'compid': bank['comp'],
                         'kind': positive['kind'], 'positive_result': positive['actual'],
                         'changed_key_result': negative['actual'],
                         'descriptor_source': 'synthetic in-memory candidate'}
                if bank['ice_name'] and bank['ice_value']:
                    assert splice_value(bank['comp'], bank['ice_name'], bank['product_data']) == bank['ice_value']
                    lease = Path(os.environ['LOCALAPPDATA']) / 'SpliceSettings' / 'license' / (bank['ice_name'] + '.lic')
                    assert not lease.exists(), 'Installed Splice lease exists; missing-file case does not apply.'
                    splice = harness.run('installed_splice_digest_valid_missing_lease', 1,
                                         candidates=[(b'splice', key)], **bank)
                    entry['splice_result'] = splice['actual']
                    entry['splice_independent_digest_matches'] = True
                installed.append(entry)
        print(json.dumps({'installed_banks': len(installed), 'all_passed': True}), flush=True)
    harness.close()
    report = {
        'schema_version': 1, 'date': dt.date.today().isoformat(),
        'scope': 'Original native candidate pipeline, private-process VDM input adapter',
        'targets': audit.EXPECTED_HASHES, 'native_function': '0x1801dc510',
        'public_result_getter': 'VIS_DSE_GetResultFromLicense',
        'owned_cases': owned, 'installed_resource_cases': installed,
        'summary': {'owned_cases': len(owned), 'installed_ordinary_positive': len(installed),
                    'installed_ordinary_negative': len(installed),
                    'installed_splice': sum('splice_result' in entry for entry in installed),
                    'total': len(owned) + 2 * len(installed)
                             + sum('splice_result' in entry for entry in installed)},
        'hardware_candidates_used': len(hardware), 'all_passed': True,
        'native_candidate_code_unchanged': True,
        'native_code_check': {'rva': '0x1dc510', 'size': '0x3b60',
                              'before_sha256': harness.native_code_before,
                              'after_sha256': harness.code_hash()},
        'adapter_callback_counts': harness.calls,
        'callback_errors': harness.errors,
        'boundaries': {'registry_or_installed_descriptor_writes': False,
                       'key_serial_hardware_or_product_data_values_exported': False,
                       'lease_created_or_signed': False, 'editor_launched_or_modified': False,
                       'independent_resource_payload_production_verified': False,
                       'editor_discovery_or_render_verified': False},
    }
    if args.report:
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'candidate_code_unchanged': True, 'callback_errors': 0,
                      'report_written': bool(args.report)}), flush=True)


if __name__ == '__main__':
    main()
