"""Check genuine DRS score-writing families on existing VQM PCM.

Use ordinary constructors and batch dispatch for documented modes0..5, without
fitted scoring transforms or injected frames. Installed payloads remain in memory.
Compare an observed batch with a fresh unobserved batch in each disposable case.
"""
from __future__ import annotations

import argparse
from collections import Counter
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import statistics
import struct
import subprocess
import sys

from audit_frm2_numeric import B,D,F,I,P,Q,SHA256
from probe_frm2_batch_outputs import run as analyze_batch
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit16_peak_interpolation import auxiliary_sequences,metrics


OFFSETS = {2:0xc,4:0x14,14:0x20c,15:0x214,17:0x21c,19:0x224}


def frames(native,context,collection):
    track = P.from_address(context+0xcd0).value
    assert track
    regions = P.from_address(track+0x148).value
    result = {}
    for i in range(I.from_address(track+0x140).value):
        region = P.from_address(regions+8*i).value
        pointers = P.from_address(region+0x20).value
        for j in range(I.from_address(region+0x18).value):
            frame = P.from_address(pointers+8*j).value
            feature = P.from_address(frame+0x128).value
            assert feature and Q.from_address(frame+0x50).value&(1<<13)
            time = D.from_address(frame+0x48).value
            at = round(time*44100/256)
            assert abs(time*44100/256-at)<1e-5 and at not in result
            values = {bit:F.from_address(feature+offset).value for bit,offset in OFFSETS.items()}
            assert all(math.isfinite(value) for value in values.values())
            result[at] = dict(values=values,
                source_arrays_sha256=hashlib.sha256(c.string_at(feature+0x2c,0x1e0)).hexdigest(),
                source_array_nonzero=sum(F.from_address(feature+offset).value!=0
                    for offset in list(range(0x2c,0x20c,4))),
                feature_flags=I.from_address(feature).value)
    return result


def child(args,sequence,parser_hash,ddi_hash):
    reference = sequence.get('pitch1',statistics.median(frame['f0'] for frame in sequence['frames']))
    captured=[];contracts=[]
    def finish(native,context,collection):
        captured.append(frames(native,context,collection))
    def setup(native,context):
        config = P.from_address(context+0xad0).value
        mode = B.from_address(config+0x558).value
        assert mode==args.mode
        contracts.append(dict(analysis_mode=mode,
            estimator_objects_present={hex(offset):bool(P.from_address(context+offset).value)
                                       for offset in (0xc70,0xc78,0xc80,0xc88)}))
    options = dict(analysis_mode=args.mode,pcm_override=sequence['pcm'],result_observer=finish)
    observed = analyze_batch(args.dll,args.automatic,1,'steady',reference,setup_observer=setup,**options)
    baseline = analyze_batch(args.dll,args.automatic,1,'steady',reference,**options)
    assert observed==baseline and len(captured)==2 and captured[0]==captured[1]
    produced = captured[0];assert set(produced)==set(range(len(sequence['frames'])))
    comparisons = {}
    interior = range(12,len(sequence['frames'])-12)
    for bit in OFFSETS:
        errors = [produced[at]['values'][bit]-sequence['frames'][at]['features'][bit] for at in interior]
        values = [row['values'][bit] for row in produced.values()]
        comparisons[str(bit)] = dict(**metrics(errors),produced_range=[min(values),max(values)],
                                     produced_nonzero=sum(value!=0 for value in values))
    joint = sum(all(abs(produced[at]['values'][bit]-sequence['frames'][at]['features'][bit])<=1e-6
                        for bit in (4,14,15)) for at in interior)
    statehash = hashlib.sha256()
    for at,row in sorted(produced.items()):
        statehash.update(struct.pack('<i',at))
        for value in row['values'].values():statehash.update(struct.pack('<f',value))
        statehash.update(struct.pack('<II',row['source_array_nonzero'],row['feature_flags']))
        statehash.update(bytes.fromhex(row['source_arrays_sha256']))
    return dict(case=sequence.get('category',args.sample),mode=args.mode,automatic=args.automatic,
        frames=len(produced),contract=contracts[0],observed_matches_fresh_baseline=True,
        comparisons=comparisons,joint_saved_score_matches=joint,
        source_array_nonzero_frames=sum(row['source_array_nonzero']>0 for row in produced.values()),
        flags=dict(Counter(hex(row['feature_flags']) for row in produced.values())),
        state_sha256=statehash.hexdigest(),parser_sha256=parser_hash,ddi_sha256=ddi_hash)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll','manifest','voice-root','ddb-tools','aux-vdb'):
        parser.add_argument('--'+name,type=Path,required=True)
    parser.add_argument('--output',type=Path)
    parser.add_argument('--child',action='store_true')
    parser.add_argument('--sample',type=int,choices=range(5))
    parser.add_argument('--mode',type=int,choices=range(6))
    parser.add_argument('--automatic',type=int,choices=(0,1))
    args = parser.parse_args()
    if args.child:
        parser_hash,ddi_hash,sequences = load_sequences(args.manifest,args.voice_root,args.ddb_tools)
        sequences += auxiliary_sequences(args.aux_vdb)
        print(json.dumps(child(args,sequences[args.sample],parser_hash,ddi_hash)));return
    assert args.output
    rows=[]
    for mode in range(6):
        for automatic in (0,1):
            for sample in range(5):
                command = [sys.executable,'-B',str(Path(__file__).resolve()),'--child',
                    '--sample',str(sample),'--mode',str(mode),'--automatic',str(automatic)]
                for name in ('dll','manifest','voice-root','ddb-tools','aux-vdb'):
                    command += ['--'+name,str(getattr(args,name.replace('-','_')))]
                result = subprocess.run(command,capture_output=True,text=True,timeout=120)
                assert result.returncode==0,result.stderr[-2000:]
                row = json.loads(result.stdout);rows.append(row)
        print(json.dumps(dict(mode=mode,completed_cases=len(rows),
            joint_matches=sum(row['joint_saved_score_matches'] for row in rows if row['mode']==mode))),flush=True)
    sources = ('verify_vqm_alternate_score_paths.py','probe_frm2_batch_outputs.py',
        'verify_vqm_bit16_spectral_power.py','verify_vqm_bit16_peak_interpolation.py','audit_frm2_numeric.py')
    report = dict(schema=1,dse_sha256=SHA256,
        auxiliary_sha256=hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest(),
        source_sha256={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources},
        cases=rows,summary=dict(cases=len(rows),output_frames=sum(row['frames'] for row in rows),
            full_batches=2*len(rows),interior_comparisons=sum(row['comparisons']['4']['frames'] for row in rows),
            joint_saved_score_matches=sum(row['joint_saved_score_matches'] for row in rows)),
        scope='Genuine documented constructor/dispatch modes0..5, automatic/external; default ordinary '
            'PCM protocol, five installed sequences. All returned metadata, six scalars, feature flags '
            'and complete four source-array byte hashes compared between observed/unobserved batches; '
            'feature state read only after batch, not all native output payloads inspected.',
        limits=['Modes share the same recordings; counts across modes are not independent samples',
            'External reference is a fixed prior from bank metadata, not per-frame saved F0 injection',
            'Native final state does not identify which candidate writer executed or original maker configuration',
            'No fitted transforms, native output replacement, commercial PCM or feature arrays persisted',
            'Edges12 excluded; actual source precision, maker settings and prior/future context remain unresolved'],
        installed_files_unchanged=True,no_windows_temp=True)
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(report['summary']))


if __name__=='__main__':
    main()
