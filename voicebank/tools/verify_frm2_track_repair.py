"""Compare whole owned frame queues against native short-track/gap repair."""
from __future__ import annotations
import argparse
import copy
from itertools import product
import json
from pathlib import Path
from audit_frm2_numeric import Native, SHA256, F, D, I, P, Q, B, f32, stats
from frm2_track_repair import repair


def verify(native):
    config = native.alloc(0x35f0)
    native.fn(0x90de0, P, P)(config)
    context = native.alloc(0x21bf8)
    P.from_address(context).value = config
    B.from_address(context+0x21b34).value = 1
    call = native.fn(0xf1a80, Q, P)
    patterns = ('1110011110110000', '0011010001111100', '1010101010101010',
                '1000000000000001', '0001111111111000', '1111111111111111')
    errors, rows = [], []
    for rate, minimum, gap, first_time, shape, empty in product((100, 200), (0, .02, .03001),
                                                      (0, .02, .04001), (0, .1), range(6), (False, True)):
        for key, value in ((3, -50), (6, minimum), (7, gap)):
            for x in (0., 1.):
                native.add(config+0x5d0+key*0xa0,x,value)
        F.from_address(config+0x18).value = rate
        frames = []
        links, addresses = [], []
        for t in range(16):
            frame = []
            for slot in range(3):
                active = patterns[(shape+slot)%6][t] == '1'
                frame.append([f32(100+70*slot+2.3*t) if active else 0.,
                              f32(-30+slot+t*.13) if active else (10000. if t%2 else -50.),
                              f32(-4+slot*.2+t*.1)])
            if empty and t in (0, 3, 4, 8, 15):
                frame = []
            frames.append(frame)
            address = native.alloc(4096)
            native.frame_ctor(address)
            I.from_address(address+0x58).value = len(frame)
            D.from_address(address+0x48).value = first_time+t/rate
            Q.from_address(address+0x50).value = 7
            for j, at in enumerate((0x60,0x68,0x70)):
                # Empty frames keep null native pointers so the native repair
                # can allocate using its own allocator. No Python buffer is freed.
                if frame:
                    P.from_address(address+at).value = native.array(F,[item[j] for item in frame])
            link = native.alloc(0x30)
            P.from_address(link+0x28).value = address
            links.append(link)
            addresses.append(address)
        I.from_address(context+0x18).value = len(frames)-1
        P.from_address(context+8).value = native.array(P,links)
        expected = copy.deepcopy(frames)
        trace = repair(expected,rate,-50,minimum,gap,first_time)
        assert call(context) == 0
        delta = []
        for t,address in enumerate(addresses):
            assert I.from_address(address+0x58).value == len(expected[t]), (t, empty, expected[t])
            for slot in range(len(expected[t])):
                for j,at in enumerate((0x60,0x68,0x70)):
                    actual = F.from_address(P.from_address(address+at).value+slot*4).value
                    error = abs(actual-expected[t][slot][j])
                    assert error <= 1e-5, (rate,minimum,gap,first_time,shape,empty,t,slot,j,actual,expected[t][slot],trace)
                    delta.append(error)
        errors.extend(delta)
        rows.append(dict(rate=rate,minimum=minimum,gap=gap,first_time=first_time,shape=shape,empty=empty,
                         allocated_frames=sum(not original and bool(final) for original,final in zip(frames,expected)),
                         error=max(delta),**trace))
    return dict(cases=len(rows),parameters=stats(errors),removed=sum(r['removed'] for r in rows),
                allocated_frames=sum(r['allocated_frames'] for r in rows),
                filled=sum(r['filled'] for r in rows),rows=rows,
                scope='Mode 1, 16-frame queues, equal nonempty widths plus empty-frame allocation, static thresholds; mode-2 retirement excluded')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    report=dict(dll_sha256=SHA256,**verify(Native(args.dll)))
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({k:v for k,v in report.items() if k!='rows'}))


if __name__=='__main__':
    main()
