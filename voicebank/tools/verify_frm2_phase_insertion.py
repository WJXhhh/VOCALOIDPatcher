"""Exercise native phase ENV insertion with owned adversarial harmonic ordering."""
import argparse,json,math,subprocess,sys
from pathlib import Path
from types import SimpleNamespace
from verify_frm2_phase_pipeline import run_case
from audit_frm2_numeric import SHA256,f32


def case(dll,kind):
    frames=[]
    for i in range(87):
        f=[220.*(k+1) for k in range(30)]
        if kind=='reversed':f=f[::-1]
        elif kind=='duplicate':f=[220.*(1+k//3) for k in range(30)]
        elif kind=='rotating':f=f[i%30:]+f[:i%30]
        elif kind=='endpoint':f[4]=22050.
        elif kind=='above_endpoint':f[4]=24000.
        elif kind=='endpoint_only':f=[22050.]*30
        elif kind=='inactive':f=[0.]*30
        elif kind=='late_activation':f=[v if k or i>=10 else 0. for k,v in enumerate(f)]
        elif kind=='sparse':f=[v if (i+k)%3 else 0. for k,v in enumerate(f)]
        frames.append(SimpleNamespace(frequencies_hz=f,amplitudes=[-20.]*30,phases_radians=[f32(2.9*math.sin(k+i*.31)) for k in range(30)]))
    return run_case(dll,'stable',256,0,220,source_frames=frames,source_controls=True)


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--dll',type=Path,required=True);p.add_argument('--output',type=Path);p.add_argument('--case');a=p.parse_args()
    if a.case:print(json.dumps(case(a.dll,a.case)));return
    rows=[]
    for kind in ('reversed','duplicate','rotating','endpoint','above_endpoint','endpoint_only','inactive','sparse','late_activation'):
        r=subprocess.run([sys.executable,__file__,'--dll',str(a.dll),'--case',kind],capture_output=True,text=True,timeout=60)
        if r.returncode:raise RuntimeError(f'{kind}: {r.stderr[-5000:]}')
        rows.append(dict(kind=kind,result=json.loads(r.stdout)));print(kind,flush=True)
    a.output.write_text(json.dumps(dict(dll_sha256=SHA256,cases=rows,scope='Owned harmonic ordering and ENV insertion boundary tests; frequencies at/above Nyquist exercise numeric container behavior, not audio validity'),indent=2)+'\n',encoding='utf-8')


if __name__=='__main__':main()
