"""Read-only, version-pinned DSE calculation audit using owned fixtures.

Run from the repository root. Default mode verifies isolated native helpers;
--installed also enumerates hardware and initializes private-process VDM/DSE
managers to compare installed Splice metadata. Output contains only counts and
booleans. No activation descriptors, registry entries or lease files are written.
Product data and lookup constants are read from the local installation in memory
and never exported. This is a verification harness, not an installer or signer.
"""

import ctypes as c
import hashlib
import json
import os
import struct
import sys
from pathlib import Path

EDITOR = Path(os.environ.get('DSE_AUDIT_EDITOR', r'C:\Program Files\VOCALOID6\Editor'))
if os.name != 'nt' or struct.calcsize('P') != 8:
    raise RuntimeError('This audit requires 64-bit Windows Python.')
if sys.flags.optimize:
    raise RuntimeError('Run without -O; assertions are the verification checks.')
EXPECTED_HASHES = {
    'DSE.dll': '7a4965bd377dbc2b08710e807ae1345b67d9fff6579ca0c01a769f744366f5b5',
    'VDM.dll': '73f1e128932521a1ba7ddb4736b59bc2d437e134753a3f3afa2df7751d430db2',
}
for filename, expected_hash in EXPECTED_HASHES.items():
    if hashlib.sha256((EDITOR/filename).read_bytes()).hexdigest() != expected_hash:
        raise RuntimeError(f'{filename} differs from the audited build; native offsets are unsafe.')
cookie = os.add_dll_directory(str(EDITOR))
dll = c.CDLL(str(EDITOR/'DSE.dll'))
BASE = dll._handle
P = c.c_void_p

def fn(rva, result, *args):
    return c.CFUNCTYPE(result, *args)(BASE+rva)

destroy_string = fn(0x146f0, None, P)
free = fn(0x216490, None, P)

def string(value):
    out = c.create_string_buffer(32)
    if len(value) <= 15:
        c.memmove(out, value, len(value))
        cap = 15
        keep = None
    else:
        keep = c.create_string_buffer(value)
        struct.pack_into('<Q', out, 0, c.addressof(keep))
        cap = len(value)
    struct.pack_into('<QQ', out, 16, len(value), cap)
    return out, keep

def read_string(value):
    size, cap = struct.unpack_from('<QQ', value, 16)
    assert size <= 2048 and cap <= 4096
    pointer = c.addressof(value) if cap <= 15 else struct.unpack_from('<Q', value)[0]
    return c.string_at(pointer, size)

def vector(value):
    keep = c.create_string_buffer(value)
    p = c.addressof(keep)
    return (c.c_uint64*3)(p, p+len(value), p+len(value)), keep

def take_vector(v):
    start, end, capacity = v
    assert 0 <= end-start <= capacity-start <= 8192
    result = c.string_at(start, end-start)
    if start:
        free(start)
    return result

def digest(rva, value, suffix=None, enabled=True):
    v, keep = vector(value)
    out = c.create_string_buffer(32)
    if suffix is None:
        fn(rva, P, P, P)(out, v)
    else:
        context = c.create_string_buffer(40)
        s, s_keep = string(suffix)
        c.memmove(c.addressof(context)+8, s, 32)
        flag = c.c_ubyte(enabled)
        fn(rva, P, P, P, P, P)(context, out, v, c.byref(flag))
    result = read_string(out)
    destroy_string(out)
    return result

def transform(value):
    return bytes(((((b ^ 0x1b) << 2) | (b >> 6)) & 255) for b in value)

def untransform(value):
    return bytes(((((b >> 2) | (b << 6)) & 255) ^ 0x1b) for b in value)

def md5(value):
    return hashlib.md5(value).hexdigest().encode()

def mt(seed):
    state = [seed & 0xffffffff]
    for i in range(1, 624):
        state.append((1812433253*(state[-1] ^ (state[-1] >> 30))+i) & 0xffffffff)
    while True:
        for i in range(624):
            y = (state[i] & 0x80000000) | (state[(i+1)%624] & 0x7fffffff)
            state[i] = state[(i+397)%624] ^ (y >> 1) ^ (0x9908b0df if y & 1 else 0)
        for y in state:
            y ^= y >> 11
            y ^= (y << 7) & 0x9d2c5680
            y ^= (y << 15) & 0xefc60000
            yield y ^ (y >> 18)

def read_pool():
    # Parse only the bounded immediate-store initializer in the hash-guarded DLL.
    code = c.string_at(BASE+0x1dbb6e, 0x1dbd2e-0x1dbb6e)
    pool = bytearray(250)
    seen = set()
    i = 0
    while i < len(code):
        width = 4
        if code[i] == 0x66:
            width = 2
            i += 1
        assert code[i] == 0xc7
        modrm = code[i+1]
        i += 2
        if modrm == 0x44:
            assert code[i] == 0x24
            offset = struct.unpack_from('<b', code, i+1)[0] - 0x60
            i += 2
        elif modrm == 0x45:
            offset = struct.unpack_from('<b', code, i)[0] + 0xa0
            i += 1
        elif modrm == 0x85:
            offset = struct.unpack_from('<i', code, i)[0] + 0xa0
            i += 4
        else:
            raise ValueError('unexpected initializer instruction')
        assert 0 <= offset <= 250-width
        pool[offset:offset+width] = code[i:i+width]
        seen.update(range(offset,offset+width))
        i += width
    assert len(seen) == 250
    return pool

def base_key_digest(value, suffix):
    s, s_keep = string(value)
    context = c.create_string_buffer(40)
    ctx_string, ctx_keep = string(suffix)
    c.memmove(c.addressof(context)+8, ctx_string, 32)
    args = (c.c_uint64*2)(c.addressof(s), c.addressof(context))
    out = c.create_string_buffer(32)
    fn(0x1d7ba0, P, P, P)(args, out)
    result = read_string(out)
    destroy_string(out)
    return result

def native_distance(left, right):
    a, keep_a = string(left)
    b, keep_b = string(right)
    args = (c.c_uint64*2)(c.addressof(a),c.addressof(b))
    return fn(0x1d9b90,c.c_double,P)(args)

def fixture_checks():
    import itertools
    import time
    sys.path.insert(0,str(Path(__file__).resolve().parent))
    import compid_codec as codec
    tables = codec.load_tables(EDITOR/'VDM.dll')
    positive = negative = 0
    fixtures = itertools.product(
        ['00A4000F123456','00B40000261016'],
        ['00C40000260101','00D40000261231'],
        [b'fe0012345678',b'001122334455',b'owned-device'],
        [b'owned-product-resource-A',b'owned-product-resource-B'],
        [b'owned-context-A',b'owned-context-B'])
    for cp,sp,hw,body,context in fixtures:
        comp = codec.encode_component_id(cp[:7]+'0'+cp[8:],tables).encode()
        serial = codec.encode_component_id(sp,tables).encode()
        raw = serial+comp+hw+body
        key = md5(raw)
        left = digest(0x1e04f0,transform(raw),context)
        right = base_key_digest(key,context)
        assert left == md5(key+context) == right
        assert native_distance(left,right) == 0.0
        positive += 1
        parts = [serial,comp,hw,body]
        for index in range(4):
            mutated = parts.copy()
            mutated[index] = mutated[index][:-1]+bytes([mutated[index][-1]^1])
            other = digest(0x1e04f0,transform(b''.join(mutated)),context)
            assert native_distance(other,right) > 0.00001
            negative += 1
        altered_key = (b'0' if key[:1] != b'0' else b'1')+key[1:]
        assert native_distance(left,base_key_digest(altered_key,context)) > 0.00001
        negative += 1
    for date in ['000101','240229','261016','991231']:
        identifier = codec.encode_component_id('00A40000'+date,tables).encode()
        s,keep = string(identifier)
        actual = fn(0x1dc200,c.c_int64,P)(s)
        year,month,day = 2000+int(date[:2]),int(date[2:4]),int(date[4:])
        expected = int(time.mktime((year,month,day,0,0,0,0,0,0)))
        assert actual == expected
        assert identifier[:1] == b'B'
        window = b''.join(f'{byte:02x}'[::-1].encode() for byte in reversed(identifier[1:]))
        extended = b'a'*3+window+b'a'*29
        key_string,key_keep = string(extended)
        arg = (c.c_uint64*1)(c.addressof(key_string))
        assert fn(0x1d9c20,c.c_int64,P)(arg) == expected
    print(json.dumps({'ordinary_fixture_checks':{'positive':positive,'single_field_negative':negative,'real_product_or_hardware_used':False},'date_helpers':{'direct':4,'embedded_62':4}}),flush=True)

def hardware_checks():
    shell32 = c.WinDLL('shell32')
    kernel32 = c.WinDLL('kernel32')
    iphlpapi = c.WinDLL('iphlpapi')
    get_folder = shell32.SHGetFolderPathA
    get_folder.restype = c.c_long
    get_folder.argtypes = [P,c.c_int,P,c.c_uint32,P]
    folder = c.create_string_buffer(260)
    assert get_folder(None,0x24,None,0,folder) >= 0
    root = folder.value[:2]+b'\\'
    serial = c.c_uint32()
    flags = c.c_uint32()
    max_length = c.c_uint32()
    volume_name = c.create_string_buffer(1024)
    file_system = c.create_string_buffer(1024)
    get_volume = kernel32.GetVolumeInformationA
    get_volume.restype = c.c_int
    get_volume.argtypes = [c.c_char_p,P,c.c_uint32,P,P,P,P,c.c_uint32]
    get_volume(root,volume_name,1024,c.byref(serial),c.byref(max_length),c.byref(flags),file_system,1024)
    expected = [('fe00'+f'{serial.value or 0xad626448:08x}').encode()]
    get_adapters = iphlpapi.GetAdaptersInfo
    get_adapters.restype = c.c_uint32
    get_adapters.argtypes = [P,P]
    size = c.c_uint32()
    rc = get_adapters(None,c.byref(size))
    assert rc in (111,232)
    adapter_count = 0
    if size.value:
        assert size.value < 1024*1024
        data = c.create_string_buffer(size.value)
        assert get_adapters(data,c.byref(size)) == 0
        current = c.addressof(data)
        while current:
            assert adapter_count < 1024
            length = c.c_uint32.from_address(current+0x194).value
            assert length <= 8
            if length:
                address = c.string_at(current+0x198,length)
                expected.append(address.hex().encode())
            adapter_count += 1
            current = c.c_void_p.from_address(current).value
    native = (c.c_uint64*3)()
    fn(0x1d8030,P,P,P)(None,native)
    start,end,capacity = native
    assert (end-start)%32 == 0 and 0 <= end-start <= capacity-start < 4096
    actual = []
    try:
        for address in range(start,end,32):
            raw = (c.c_char*32).from_address(address)
            actual.append(read_string(raw))
        assert actual == expected
    finally:
        for address in range(start,end,32):
            destroy_string(address)
        if start:
            free(start)
    print(json.dumps({'hardware_candidates':{'ordered_equal':True,'count':len(actual),'volume':1,'nonempty_adapter_addresses':len(actual)-1,'values_saved':False}}),flush=True)

def primitive_checks():
    cases = [b'', b'a', b'abc', bytes(range(256))]
    cases += [bytes((i*31+j)%256 for j in range(n)) for i,n in enumerate([15,16,31,32,55,56,63,64,65,127,128,129,255,256,1024])]
    suffix = b'owned-context-0123456789abcdef'
    counts = {'byte_transform':0, 'md5':0, 'transformed_md5':0, 'context_md5':0, 'context_transformed_md5':0}
    for data in cases:
        assert untransform(transform(data)) == data
        source,source_keep = string(data)
        transformed = (c.c_uint64*3)()
        fn(0x1dc130,P,P,P)(transformed,source)
        assert take_vector(transformed) == transform(data)
        counts['byte_transform'] += 1
        assert digest(0x1e0a70, data) == md5(data)
        counts['md5'] += 1
        assert digest(0x1e2a60, transform(data)) == md5(data)
        counts['transformed_md5'] += 1
        for enabled in [False, True]:
            assert digest(0x1e0390, data, suffix, enabled) == md5(data+(suffix if enabled else b''))
            counts['context_md5'] += 1
            expected = md5(md5(data)+suffix) if enabled else md5(data)
            assert digest(0x1e04f0, transform(data), suffix, enabled) == expected
            counts['context_transformed_md5'] += 1
    base = md5(b'owned-identifier-and-product-fixture')
    for key in [base, base[:3]+b'0123456789'*3+base[3:]]:
        assert base_key_digest(key, suffix) == md5(base+suffix)
    counts['base_key_extraction'] = 2
    for counter in [0,1,0x12345678,0xffffffff]:
        context = c.create_string_buffer(40)
        struct.pack_into('<I',context,0,counter)
        fn(0x1e0070,None,P)(context)
        stored = (c.c_char*32).from_address(c.addressof(context)+8)
        assert read_string(stored) == md5(f'{counter:08x}'.encode())
        assert struct.unpack_from('<I',context)[0] == (counter+1)&0xffffffff
        destroy_string(c.addressof(context)+8)
    counts['context_initialization'] = 4
    pool = read_pool()
    for seed in [123456789,987654321,20240601,0,1,5489,0xffffffff]:
        rng = mt(seed)
        expected = bytes(pool[next(rng)%250] for _ in range(25))
        out = (c.c_uint64*3)()
        fn(0x1dbb20, P, P, c.c_uint32)(out, seed)
        assert take_vector(out) == expected
    counts['salt_mt19937'] = 7
    print(json.dumps({'primitive_checks':counts}), flush=True)

def installed_checks():
    import winreg
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    import compid_codec as codec
    tables = codec.load_tables(EDITOR/'VDM.dll')
    normalized_count = 0
    for payload in ['00A40000000000','00A40001000000','00A4000R000001','00B4000F123456']:
        comp = codec.encode_component_id(payload, tables)
        expected = codec.encode_component_id(payload[:7]+'0'+payload[8:], tables).encode()
        s, keep = string(comp.encode())
        args = (c.c_uint64*2)(c.addressof(s),0)
        for rva in [0x1d83b0,0x1d9740]:
            out = (c.c_uint64*3)()
            fn(rva, P, P, P)(args, out)
            assert take_vector(out) == transform(expected)
            normalized_count += 1
    print(json.dumps({'normalization_checks':normalized_count}), flush=True)
    vdm = c.CDLL(str(EDITOR/'VDM.dll'))
    def api(lib, name, result, *args):
        f = getattr(lib,name)
        f.restype, f.argtypes = result,args
        return f
    create = api(vdm,'VDM_createDatabaseManager',P,c.c_wchar_p,c.c_wchar_p,P)
    destroy = api(vdm,'VDM_DatabaseManager_destroy',None,P)
    count = api(vdm,'VDM_DatabaseManager_numVoiceBanks',c.c_size_t,P,c.c_int)
    by_index = api(vdm,'VDM_DatabaseManager_voiceBankByIndex',P,P,c.c_size_t,c.c_int)
    comp_id = api(vdm,'VDM_VoiceBank_compID',c.c_wchar_p,P)
    bank_date = api(vdm,'VDM_VoiceBank_date',c.c_wchar_p,P)
    create_dse = api(dll,'VIS_DSE_CreateManager',P)
    initialize_dse = api(dll,'VIS_DSE_InitializeManager',c.c_int,P,P)
    terminate_dse = api(dll,'VIS_DSE_TerminateManager',c.c_int,P)
    destroy_dse = api(dll,'VIS_DSE_DestroyManager',None,P)
    rc = c.c_int(-1)
    explib = Path(os.environ.get('CommonProgramFiles',r'C:\Program Files\Common Files'))/'VOCALOID6'/'Explib'
    manager = create('VOCALOID6',str(explib.resolve()),c.byref(rc))
    assert rc.value == 0 and manager
    dse_manager = None
    table = c.c_void_p()
    initialized = False
    try:
        banks = []
        for kind in [0,1]:
            for index in range(count(manager,kind)):
                bank = by_index(manager,index,kind)
                banks.append((comp_id(bank),bank))
        dse_manager = create_dse()
        rc = initialize_dse(dse_manager,manager)
        print(json.dumps({'dse_initialize_result':rc}), flush=True)
        assert rc == 0
        initialized = True
        success = fn(0x180850,c.c_ubyte,P,P)(BASE+0xa9e7c0,c.byref(table))
        print(json.dumps({'product_table_success':bool(success)}), flush=True)
        assert success and table.value
        header = c.string_at(table.value,16)
        number = struct.unpack_from('<i',header)[0]
        data = struct.unpack_from('<Q',header,8)[0]
        assert 0 < number < 1024 and data
        records = c.string_at(data,number*146)
        product_data = {}
        for index in range(number):
            record = records[index*146:(index+1)*146]
            comp = untransform(record[:16]).decode('ascii')
            product_data.setdefault(comp,[]).append(record[17:146].split(b'\0',1)[0])
        matched = 0
        lengths = {}
        normalization_changes = 0
        unique_values = set()
        date_slot_matches = 0
        for comp,bank in banks:
            vtable = c.c_void_p.from_address(bank).value
            slot = c.c_void_p.from_address(vtable+0x38).value
            assert c.CFUNCTYPE(c.c_wchar_p,P)(slot)(bank) == bank_date(bank)
            date_slot_matches += 1
            payload = codec.decode_component_id(comp,tables)
            normalized = codec.encode_component_id(payload[:7]+'0'+payload[8:],tables)
            normalization_changes += normalized != comp
            if normalized in product_data:
                matched += 1
                values = product_data[normalized]
                assert len(values) == 1
                plen = len(values[0])
                lengths[plen] = lengths.get(plen,0)+1
                unique_values.add(values[0])
        print(json.dumps({'product_table':{'records':number,'banks':len(banks),'normalized_matches':matched,'normalization_changes':normalization_changes,'product_data_lengths':lengths,'distinct_product_data':len(unique_values)},'date_slot_matches':date_slot_matches}),flush=True)
        pool = read_pool()
        salts = []
        for seed in [123456789,987654321,20240601]:
            rng = mt(seed)
            salts.append(bytes(pool[next(rng)%250] for _ in range(25)))
        splice_results = []
        for comp,bank in banks:
            path = 'SOFTWARE\\VOCALOID6\\Voice\\Components\\'+comp
            try:
                with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,path,0,winreg.KEY_READ|winreg.KEY_WOW64_64KEY) as key:
                    ice_name = winreg.QueryValueEx(key,'IceProductName')[0]
                    ice_value = winreg.QueryValueEx(key,'IceValue')[0]
            except FileNotFoundError:
                continue
            if not ice_name or not ice_value:
                continue
            payload = codec.decode_component_id(comp,tables)
            normalized = codec.encode_component_id(payload[:7]+'0'+payload[8:],tables)
            value, = product_data[normalized]
            composite = salts[0]+ice_name.encode('utf-8')+salts[1]+comp.encode('utf-8')+value+salts[2]
            expected = bytes.fromhex(ice_value)
            actual = hashlib.sha256(composite).digest()
            changed_product = bytearray(composite)
            changed_product[len(salts[0])+len(ice_name)+len(salts[1])] ^= 1
            result = {'match':actual==expected,'product_data_length':len(value),'product_data_inverse_is_hex':all(b in b'0123456789abcdef' for b in untransform(value)), 'changed_product_rejected':hashlib.sha256(changed_product).digest()!=expected}
            splice_results.append(result)
        print(json.dumps({'splice_composite_checks':splice_results}), flush=True)
        assert splice_results and all(x['match'] for x in splice_results)
    finally:
        if table.value:
            free(struct.unpack_from('<Q',c.string_at(table.value,16),8)[0])
            free(table.value)
        if initialized:
            terminate_dse(dse_manager)
        if dse_manager:
            destroy_dse(dse_manager)
        destroy(manager)

if __name__ == '__main__':
    primitive_checks()
    fixture_checks()
    if '--installed' in sys.argv:
        hardware_checks()
        installed_checks()
