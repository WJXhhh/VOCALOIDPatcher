"""Owned multi-frame initialization/reset and feedback at the outer DSE entry."""
from __future__ import annotations
import argparse
from itertools import product
import json
from pathlib import Path
import random

from audit_frm2_numeric import Native, SHA256, F, D, I, P, Q, f32, stats
from frm2_peak_selection import advance_outer
from frm2_track_repair import repair
from verify_frm2_peak_conflicts import check


def verify(native):
    config = native.alloc(0x35f0)
    native.fn(0x90de0, P, P)(config)
    context = native.alloc(0x21bf8)
    P.from_address(context).value = config
    link = native.alloc(0x30)
    queue = native.array(P, [link]*12)
    call = native.fn(0xef9f0, Q, P, P, D)
    errors, rows = [], []
    history_coordinates = removed = filled = 0
    rng = random.Random(590)
    for count, before, after, ratio, temporal in product((1, 8), (0, .6, 1), (0, .4, 1), (1., 1.03), (False, True)):
        values = {0: 1, 2: 4000, 3: -50, 4: count, 6: .03001, 7: .04001, 8: 1, 9: after, 10: 1, 0xd: 220,
                  0x11: before, 0x12: .3, 0x13: 5, 0x21: .7, 0x22: .1,
                  0x27: 20, 0x28: 80, 0x29: .04, 0x33: 0}
        for key, value in values.items():
            for x in (0., 1.):
                native.add(config + 0x5d0 + key * 0xa0, x, value)
        # The ENV flag at +0x50 selects default harmonics. Adding points
        # clears it and enables cfg23 evaluation; it is not a point count.
        env = config + 0x5d0 + 0x23 * 0xa0
        I.from_address(env + 0x50).value = 1
        if ratio != 1:
            native.add(env, 0., ratio)
            native.add(env, 1., ratio)
        I.from_address(context + 0x21b30).value = 0
        I.from_address(context + 0x1c).value = 0
        F.from_address(context + 0x21bd8).value = 0
        F.from_address(config + 0x18).value = 100
        state = dict(tracks=[], history=0., initialized=False)
        frame_history, expected_history = [], []
        for step in range(12):
            f0 = 0. if step in (4, 5) else (220. if step < 7 else 260.)
            timestamp = -1. if step == 2 else step * .01
            values[0] = 0 if step == 9 else 1
            for x in (0., 1.):
                native.add(config + 0x5d0, x, values[0])
            peaks = sorted((f32((i+1)*max(f0,220)+rng.uniform(-40,40)),
                            f32(rng.uniform(1,25)), f32(rng.uniform(-3,3))) for i in range(12))
            if step == 3:
                peaks = []
            frame = native.alloc(4096)
            native.frame_ctor(frame)
            F.from_address(frame + 0xf0).value = f0
            D.from_address(frame + 0x48).value = timestamp
            link = native.alloc(0x30)
            I.from_address(context+0x18).value = step if temporal else 0
            P.from_address(queue+8*(step if temporal else 0)).value = link
            P.from_address(link + 0x28).value = frame
            I.from_address(link + 0x18).value = len(peaks)
            P.from_address(link + 0x20).value = native.array(F, [v for p in peaks for v in (*p,0,0)] or [0]*5)
            # Preserve native track memory throughout the sequence. Only the
            # test-owned occupancy tables are cleared to a shared start value.
            for i in range(len(peaks)):
                I.from_address(context + 0x1ec50 + 4*i).value = 0
                I.from_address(context + 0x203c0 + 4*i).value = 0
            result = advance_outer(state, peaks, f0, values, [0]*len(peaks), f32(ratio), timestamp)
            expected_history.append([list(p) if p is not None else [0.,10000.,0.] for p in result['output']])
            frame_history.append(frame)
            if temporal and timestamp != -1:
                changes = repair(expected_history,100,-50,values[6],values[7],first_time=0.)
                removed += changes['removed']
                filled += changes['filled']
            assert call(context, queue, step*.01) == 0
            assert bool(I.from_address(context + 0x21b30).value) == state['initialized']
            assert I.from_address(context + 0x1c).value == len(state['tracks'])
            assert F.from_address(context + 0x21bd8).value == state['history']
            if result['claimed'] is not None:
                try:
                    check(context, state['tracks'], result['claimed'], result['owners'])
                except AssertionError as error:
                    raise AssertionError((count,before,after,ratio,step,
                        [F.from_address(context+0x24+i*0x54).value for i in range(len(state['tracks']))],error.args)) from error
            for i, item in enumerate(state['tracks']):
                base = context + 0x20 + i * 0x54
                for name, offset, ty in [('amplitude',0,F), ('center',4,F), ('previous',8,I), ('current',12,I),
                                       ('rank',16,I), ('candidates',20,I), ('distance',32,F),
                                       ('metric',44,F), ('cost',56,F), ('order',68,I)]:
                    want = item[name] if isinstance(item[name],list) else [item[name]]
                    got = [ty.from_address(base+offset+j*4).value for j in range(len(want))]
                    delta = [abs(a-b) for a,b in zip(got,want)]
                    assert max(delta) <= 1e-5, (count,before,after,ratio,step,i,name,got,want)
                    errors.extend(delta)
            for i, output in enumerate(result['output']):
                if output is not None and not temporal:
                    got = tuple(F.from_address(P.from_address(frame+at).value+i*4).value for at in (0x60,0x68,0x70))
                    assert got == tuple(output)
            if temporal:
                for old, wanted in zip(frame_history,expected_history):
                    assert I.from_address(old+0x58).value == len(wanted), (count,step,temporal,len(wanted),I.from_address(old+0x58).value)
                    for i,p in enumerate(wanted):
                        got=tuple(F.from_address(P.from_address(old+at).value+i*4).value for at in (0x60,0x68,0x70))
                        assert got==tuple(p), (count,before,after,ratio,step,i,got,p)
                        history_coordinates += 3
            rows.append(dict(count=count,before=before,after=after,ratio=ratio,step=step,temporal=temporal,
                             initialized=state['initialized'],assigned=sum(p is not None for p in result['output'])))
    return dict(cases=len(rows),sequences=len(rows)//12,parameters=stats(errors),
                history_coordinates=history_coordinates,removed=removed,filled=filled,
                assigned_outputs=sum(r['assigned'] for r in rows),rows=rows,
                scope='Mode 0/1, fixed index zero and growing 12-frame queues, static ratio, persistent tracks; includes full historical output repair; FFT peak detection excluded')


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
