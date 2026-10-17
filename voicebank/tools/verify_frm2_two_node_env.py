"""Prove two-node DSE cubic dependence on an allocated but unstored third node."""
import argparse
import json
from pathlib import Path

from audit_frm2_numeric import Native, SHA256, F, D, I, P, interpolate, stats


def verify(dll):
    native = Native(dll)
    evaluate = native.fn(0x11acc0, F, P, D)
    cases = []
    for values in ((-3., 9.), (2., -5.), (0., 0.)):
        points = [(0., values[0]), (1., values[1])]
        try:
            interpolate(points, .5, cubic=True)
        except ValueError:
            pass
        else:
            raise AssertionError('Ambiguous two-node cubic must be rejected')
        drs = native.env(points, 3, 0, (-20000, 20000, 0))
        dse = native.dse_env(points, 3)
        if I.from_address(dse + 0x160).value < 3:
            raise AssertionError('No allocated third slot; do not write beyond capacity')
        memory = P.from_address(dse + 0x178).value
        for unused in (-12., 0., 12.):
            # This slot is within capacity but outside the serialized node count.
            F.from_address(memory + 2*16 + 8).value = unused
            drs_errors, dse_errors = [], []
            for i in range(201):
                x = i / 200
                drs_errors.append(abs(native.evaluate(drs, x) - interpolate(points, x)))
                extended = points + [(2., unused)]
                expected = interpolate(extended, x, cubic=True)
                dse_errors.append(abs(evaluate(dse, x) - expected))
            if max(drs_errors + dse_errors) >= 1e-4:
                raise AssertionError((values, unused, stats(dse_errors)))
            if I.from_address(dse + 0x15c).value != 2:
                raise AssertionError('The serialized node count changed')
            cases.append(dict(values=values, unused_capacity_y=unused,
                              drs=stats(drs_errors), dse_with_explicit_backing=stats(dse_errors),
                              dse_midpoint=evaluate(dse, .5)))
    return dict(dll_sha256=SHA256, cases=cases,
        scope='Owned two-node envelopes; third y altered only inside confirmed allocated capacity. DRS mode3 falls back to linear; DSE mode3 reads a third y outside logical count. No serialized-content-only cubic result is claimed for two nodes.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    report = verify(args.dll)
    args.output.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(cases=len(report['cases']), dse_values=sum(x['dse_with_explicit_backing']['count'] for x in report['cases']))))


if __name__ == '__main__':
    main()
