"""Replay the complete independently predicted source F0/harmonics in native phase production."""
import argparse,json,subprocess,sys
from pathlib import Path
from types import SimpleNamespace
from audit_frm2_numeric import SHA256
from verify_frm2_pitch_state import verify
from verify_frm2_phase_pipeline import run_case
from probe_frm2_batch_outputs import owned_pcm


def case(dll,shape,frequency):
    rows=[];source=verify(dll,shape,frequency,True,source_outputs=rows)
    count=max(10,max(len(r['harmonics']) for r in rows));frames=[]
    for row in rows:
        h=row['harmonics']+[(0.,10000.,0.)]*(count-len(row['harmonics']))
        frames.append(SimpleNamespace(frequencies_hz=[v[0] for v in h],amplitudes=[v[1] for v in h],phases_radians=[v[2] for v in h]))
    results=[run_case(dll,'stable',256,features,frequency,source_frames=frames,source_pitch=rows,source_controls=True,pcm_override=owned_pcm(shape,frequency)) for features in (0,1)]
    return dict(shape=shape,frequency=frequency,source=source,phase=results)


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--dll',type=Path,required=True);p.add_argument('--output',type=Path);p.add_argument('--case',choices=('steady','attack','gap'));p.add_argument('--frequency',type=float);p.add_argument('--frequencies',type=float,nargs='+',default=[50,75,110,147,196,220,294,330,440,660,880,1000]);a=p.parse_args()
    if a.case:
        print(json.dumps(case(a.dll,a.case,a.frequency)));return
    if a.output is None:p.error('--output required')
    cases=[]
    for shape in ('steady','attack','gap'):
        for frequency in a.frequencies:
            r=subprocess.run([sys.executable,__file__,'--dll',str(a.dll),'--case',shape,'--frequency',str(frequency)],capture_output=True,text=True,timeout=120)
            if r.returncode:raise RuntimeError(f'{shape}/{frequency}: {r.stderr[-6000:]}')
            result=json.loads(r.stdout);cases.append(result)
            print(json.dumps(dict(shape=shape,frequency=frequency,frames=[x['replayed_frames'] for x in result['phase']])),flush=True)
    a.output.write_text(json.dumps(dict(dll_sha256=SHA256,cases=cases,scope='Same source-predicted F0/harmonics replay; private optional pitch rewrite bypassed; native source FFT/FIR dependencies remain; classification and phase independently modeled'),indent=2)+'\n',encoding='utf-8')


if __name__=='__main__':main()
