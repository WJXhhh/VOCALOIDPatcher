"""Exercise the DSE collection pitch bracket helper on synthetic parts."""
import argparse
import ctypes as c
import json
from pathlib import Path

from audit_frm2_numeric import Native, P, I, F, Q, SHA256, f32


def predict(pitches, target):
    if not pitches:
        return None, None, 0.
    index = 0
    previous = pitches[0]
    while pitches[index] + 1e-8 < target and index < len(pitches)-1:
        previous = pitches[index]
        index += 1
    lower = max(0, index-1)
    weight = 0.
    if previous < pitches[index]:
        weight = f32(f32(target-previous) / f32(pitches[index]-previous))
        if weight >= 0.99999999:
            lower += 1
            weight = 0.
    return lower, lower+1 if lower+1 < len(pitches) else None, weight


def run(dll):
    native = Native(dll)
    select = native.fn(0x113260, Q, P, P, F, P, P, P, P)
    rows = []
    for pitches in [[], [0.], [-800., -400., 0.], [-914.29, -528.97, -211.73, 272.98],
                    [0., 0., 400.], [400., 0., -400.]]:
        pitches = list(map(f32, pitches))
        collection = native.alloc(0x180)
        parts = [native.alloc(0x180) for _ in pitches]
        for part, pitch in zip(parts, pitches):
            F.from_address(part+0x160).value = pitch
        P.from_address(collection+0x148).value = native.array(P, parts)
        I.from_address(collection+0x150).value = len(parts)
        for target in sorted(set([-1400., -800., -600., -400., -1., 0., 1., 200., 400., 1000.] +
                                 [p+d for p in pitches for d in [-0.001, 0., 0.001]])):
            target = f32(target)
            a, b, x, y = P(), P(), F(), F()
            result = select(0, collection, target, c.byref(a), c.byref(b), c.byref(x), c.byref(y))
            actual = (parts.index(a.value) if a.value else None,
                      parts.index(b.value) if b.value else None, x.value)
            assert result == 0 and x.value == y.value
            assert actual == predict(pitches, target), (pitches, target, actual)
            rows.append(dict(pitches=pitches, target=target, lower=actual[0],
                             upper=actual[1], weight=actual[2]))
    return dict(dll_sha256=SHA256, cases=len(rows), mismatches=0, rows=rows,
                scope='Collection helper only; call-site reachability and final audible mixing not established')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = run(args.dll)
    args.output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(cases=result['cases'], mismatches=result['mismatches'])))
