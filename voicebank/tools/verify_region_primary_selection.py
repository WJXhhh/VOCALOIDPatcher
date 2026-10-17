"""Bounded native primary-selector checks: separated layers and edge lengths."""
import argparse
import ctypes as c
import itertools
import json
from pathlib import Path
from audit_frm2_numeric import Native, P, I, F, Q, B, SHA256


def run(dll):
    n = Native(dll)
    select = n.fn(0x1b8f40, Q, P, P, I, F, F, B)
    rows = []
    for pitches in itertools.permutations([-800., -400., 0.]):
        work, unit = n.alloc(0x1d00), n.alloc(0x180)
        I.from_address(work + 12).value = 1
        P.from_address(work + 0x410).value = unit
        c.memmove(work + 0x10, b'a\0', 2)
        c.memmove(work + 0x210, b'i\0', 2)
        parts = []
        for pitch in pitches:
            part = n.alloc(0x280)
            F.from_address(part + 0x160).value = pitch
            F.from_address(part + 0x16c).value = .6
            I.from_address(part + 0x1b0).value = 20
            align = n.array(I, [0, 7, 1, 6, 7, 20, 8, 19])
            P.from_address(part + 0x1d8).value = align
            P.from_address(part + 0x1e0).value = align + 32
            parts.append(part)
        P.from_address(unit + 0x148).value = n.array(P, parts)
        I.from_address(unit + 0x150).value = len(parts)
        for target in [-1000., -800., -601., -600., -599., -400., -201., -200., -199., 0., 200.]:
            for trim in [0, 1]:
                total = select(0, work, 100, target, .6, trim)
                index = parts.index(P.from_address(work + 0x810).value)
                expected = min(pitches, key=lambda p: (abs(p-target), p))
                assert pitches[index] == expected
                assert total == (13 if trim else 20)
                rows.append(dict(order=pitches, target=target, trim=trim,
                                 selected_pitch=pitches[index], total_frames=total))
    return dict(dll_sha256=SHA256, cases=len(rows), mismatches=0, rows=rows,
                scope='Single work item, separated pitch layers, equal dynamics; no close-layer or duration-search claim')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll)
    args.output.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(cases=report['cases'], mismatches=report['mismatches'])))
