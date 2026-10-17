"""Check analytic classification geometry against seven native sample rates."""
import argparse,json
from audit_frm2_numeric import Native,P,I,F,SHA256,stats
from frm2_classification_geometry import geometry
from pathlib import Path


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--dll',type=Path,required=True);p.add_argument('--output',type=Path,required=True);args=p.parse_args()
    n=Native(args.dll);rows=[]
    for rate in (8000,16000,22050,32000,44100,48000,96000):
        cfg=n.alloc(0x35f0);n.fn(0x90de0,P,P)(cfg);I.from_address(cfg+0x54c).value=1
        ctx=n.alloc(0xce0);n.fn(0xcdde0,P,P,I,P)(ctx,rate,cfg);a=P.from_address(ctx+0xca8).value
        size,half,w,b=geometry(rate)
        assert size==I.from_address(a+0x698).value and half==I.from_address(a+0x69c).value
        ptr=P.from_address(a+0x6a8).value
        e=stats([abs(x-F.from_address(ptr+4*k).value) for k,x in enumerate(w)]+[abs(x-F.from_address(a+0x3e0+4*k).value) for k,x in enumerate(b)])
        assert e['max']==0
        rows.append(dict(rate=rate,size=size,bins=half,errors=e))
    args.output.write_text(json.dumps(dict(dll_sha256=SHA256,cases=rows,scope='Constructor geometry only; source phase chain independently tested at 44100 Hz'),indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(cases=len(rows),values=sum(x['errors']['count'] for x in rows))))


if __name__=='__main__':main()
