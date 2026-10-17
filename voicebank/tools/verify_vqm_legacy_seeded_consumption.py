"""Reproduce compact-DVQM consumption with the verified CRT rand seed.

The genuine rand/srand exports in this disposable process are used directly.
No function, binary or installed resource is replaced. All analysis and audio
still pass through the established native configuration and synthesis API.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
from pathlib import Path
import struct

from audit_frm2_numeric import I,Native,P,SHA256
from audit_vqm_v4_module_leads import PE
from verify_vqm_legacy_bit17_consumption import run


def random_reset(dll,seed):
    data=dll.read_bytes()
    assert hashlib.sha256(data).hexdigest()==SHA256
    pe=PE(data)
    rva,size=struct.unpack_from('<II',data,pe.directories+8)
    assert 0<size<=0x10000
    matches=[]
    for at in range(pe.offset(rva),pe.offset(rva)+size,20):
        lookup,timestamp,forwarder,name,iat=struct.unpack_from('<IIIII',data,at)
        if not any((lookup,timestamp,forwarder,name,iat)):
            break
        for k in range(2048):
            slot=struct.unpack_from('<Q',data,pe.offset(lookup or iat)+8*k)[0]
            if not slot:
                break
            if slot<1<<63 and pe.text(slot+2)=='rand':
                matches.append(dict(module=pe.text(name),iat_rva=iat+8*k))
        else:
            raise AssertionError('Import descriptor exceeds bound')
    assert len(matches)==1
    # Three independent stages in the observed audio path call this exact IAT
    # slot: context initialization, source adaptation and reconstruction.
    sites=(0x2d507,0x34ca7,0x39425,0x39580,0x39696,0x39804,0x52a43,0x53204,0x53b10)
    for site in sites:
        at=pe.offset(site)
        assert data[at:at+2]==bytes.fromhex('ff15')
        assert site+6+struct.unpack_from('<i',data,at+2)[0]==matches[0]['iat_rva']
    native=Native(dll)
    address=P.from_address(native.lib._handle+matches[0]['iat_rva']).value
    assert address
    kernel=c.WinDLL('kernel32',use_last_error=True)
    kernel.GetModuleHandleExW.argtypes=[c.c_uint32,P,P]
    kernel.GetModuleHandleExW.restype=c.c_int
    handle=P()
    assert kernel.GetModuleHandleExW(6,address,c.byref(handle))
    kernel.GetModuleFileNameW.argtypes=[P,c.c_wchar_p,c.c_uint32]
    kernel.GetModuleFileNameW.restype=c.c_uint32
    path=c.create_unicode_buffer(32768)
    count=kernel.GetModuleFileNameW(handle,path,len(path))
    assert 0<count<len(path)
    library=c.CDLL(path.value)
    rand=library.rand
    rand.argtypes=[]
    rand.restype=I
    assert c.cast(rand,P).value==address
    srand=library.srand
    srand.argtypes=[c.c_uint32]
    srand.restype=None
    srand(seed)
    first=[rand() for _ in range(16)]
    srand(seed)
    second=[rand() for _ in range(16)]
    assert first==second and len(set(first))>1
    identity=dict(import_module=matches[0]['module'],iat_rva=hex(matches[0]['iat_rva']),
        loaded_module=Path(path.value).name,
        loaded_module_sha256=hashlib.sha256(Path(path.value).read_bytes()).hexdigest(),
        export_equals_loaded_iat=True,own_repeated_draws=16,seed=seed,
        verified_audio_path_call_rvas=[hex(x) for x in sites],
        scope='Verified genuine CRT exports in this disposable Python process; '
            'seed reset immediately before each native audio sequence. Native code '
            'and genuine rand implementation unchanged. No claim about host RNG state.')
    def reset():
        # Keep the CDLL and Native directory handles alive with this closure.
        assert library and native
        srand(seed)
    return reset,identity


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for option in ('dll','directory','reference','output'):
        parser.add_argument('--'+option,type=Path,required=True)
    parser.add_argument('--limit',type=int,default=119)
    parser.add_argument('--blocks',type=int,default=64)
    parser.add_argument('--seed',type=int,default=419)
    parser.add_argument('--second-seed',type=int,default=420)
    args=parser.parse_args()
    assert 1<=args.limit<=119 and 4<=args.blocks<=1024 and 0<=args.seed<2**32
    assert 0<=args.second_seed<2**32 and args.second_seed!=args.seed
    reset,identity=random_reset(args.dll,args.seed)
    report=run(args.dll,args.directory,args.reference,args.limit,args.blocks,reset,True)
    report['random_identity']=identity
    reset_second,identity_second=random_reset(args.dll,args.second_seed)
    second=run(args.dll,args.directory,args.reference,args.limit,args.blocks,reset_second,True)
    second['random_identity']=identity_second
    assert report['selected_samples']==second['selected_samples']
    report['second_seed']=second
    report['two_seed_summary']=dict(seeds=[args.seed,args.second_seed],
        samples_per_seed=args.limit,
        audio_calls=report['summary']['audio_calls']+second['summary']['audio_calls'],
        finite_guarded_audio_values=report['summary']['finite_guarded_audio_values']+second['summary']['finite_guarded_audio_values'],
        changed_baseline_samples=sum(a['trials'][0]['audio']['sha256']!=b['trials'][0]['audio']['sha256']
            for a,b in zip(report['cases'],second['cases'])),
        repeatable_per_seed=[report['summary']['repeatable'],second['summary']['repeatable']],
        bit17_equal_per_seed=[report['summary']['bit17_all_variants_equal'],second['summary']['bit17_all_variants_equal']])
    report['scope']=('All compact-DVQM catalog entries are eligible, including the 99 '
        'whose unseeded short PCM varied in 417. Three genuinely initialized independent '
        'baselines use the same verified CRT seed. Changed source fields are applied '
        'after loadSample and before sampleInfo/configure, preserved through audio. '
        'Controls vary only in-memory bit17 or harmonic amplitudes. Actual native '
        'synthesis and rand code remain unchanged; no injected harmonics or binary '
        'patch. Not a V4X Growl, original authoring or full host result.')
    sources=('verify_vqm_legacy_seeded_consumption.py','verify_vqm_legacy_bit17_consumption.py',
        'audit_vqm_legacy_live_features.py','audit_vqm_v4_module_leads.py',
        'audit_frm2_numeric.py','probe_frm2.py','verify_region_frame_blend.py')
    report['source_sha256']={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources}
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(summary=report['summary'],two_seed_summary=report['two_seed_summary'],random_identity=identity)))


if __name__=='__main__':
    main()
