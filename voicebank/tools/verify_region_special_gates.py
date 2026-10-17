"""Verify native special-production admission using owned ring records.

No special resource is fabricated; this tests admission before resource lookup.
"""
import argparse
import ctypes as c
import itertools
import json
import math
from pathlib import Path

from audit_frm2_numeric import Native, P, I, Q, F, D, B, SHA256, f32


def run(dll):
    n = Native(dll)
    call = n.fn(0x179410, Q, P, I, P, F)
    lead = F.from_address(n.lib._handle + 0x5bf09c).value
    cases = []
    for step, time, enabled, adjacent, scenario in itertools.product(
            [.005, .01, .02], [0, 7, 63], [0, 1, 255], [0, 0xA5],
            ['wrong_time', 'missing_work', 'missing_special', 'silence',
             'type_zero', 'wrong_begin']):
        context, controls, work, special = [n.alloc(size) for size in
                                             [0x2b200, 0x5ab00, 0x140, 0x250]]
        records = n.alloc(128 * 0x18)
        P.from_address(context + 0x1d568).value = controls
        D.from_address(context + 0x1d558).value = step
        I.from_address(context + 0x24158).value = time + (scenario == 'wrong_time')
        B.from_address(context + 0x1fc0).value = enabled
        c.memset(context + 0x1fc1, adjacent, 7)
        P.from_address(controls).value = records
        I.from_address(controls + 8).value = 128
        future = time + math.floor(f32(lead / f32(step)))
        assert future >= 0
        record = records + (future % 128) * 0x18
        if scenario != 'missing_work':
            P.from_address(record).value = work
        if scenario != 'missing_special':
            P.from_address(record + 0x10).value = special
        I.from_address(work + 0x10).value = -1 if scenario == 'silence' else 0
        I.from_address(work + 0x14).value = -1
        I.from_address(special + 0x14).value = 0 if scenario == 'type_zero' else 2
        I.from_address(special + 8).value = future + (scenario == 'wrong_begin')
        # Every selected case exits before the resource registry is needed.
        # Null context config would expose accidentally reaching that branch.
        before = [c.string_at(ptr, size) for ptr, size in
                  [(context, 0x2b200), (records, 128 * 0x18), (work, 0x140), (special, 0x250)]]
        result = call(context, time, None, 0.) & 0xffffffff
        expected = 0xffffffff if enabled and scenario in ['missing_work', 'missing_special'] else 0
        assert result == expected, (step, time, enabled, adjacent, scenario, result)
        after = [c.string_at(ptr, size) for ptr, size in
                 [(context, 0x2b200), (records, 128 * 0x18), (work, 0x140), (special, 0x250)]]
        assert before == after
        cases.append(dict(step=step, time=time, enabled=enabled, adjacent_byte=adjacent,
                          scenario=scenario, lookahead_index=future, result=result))
    return dict(dse_sha256=SHA256, lookahead_seconds=lead, cases=len(cases),
                mismatches=0, rows=cases,
                scope='Native enabled special-production admission gates and byte-width; '
                      'does not load CDVQM resources, synthesize special objects, or render PCM')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = run(args.dll)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({k: v for k, v in result.items() if k != 'rows'}))
