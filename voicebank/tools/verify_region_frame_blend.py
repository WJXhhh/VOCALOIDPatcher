"""Check the native region interpolation dispatcher using owned runtime frames."""
import argparse
import ctypes as c
import itertools
import json
import math
from pathlib import Path

from audit_frm2_numeric import Native, P, I, Q, F, D, SHA256, f32


EMPTY = f32(-3.4028234663852886e38)


def cents(hz):
    return f32(math.log(hz / 440.0) * 1200.0 / math.log(2.0))


def mix(a, b, fraction):
    return f32(f32(f32(b - a) * fraction) + a)


def run(dll):
    n = Native(dll)
    init = n.fn(0x12a670, P, P)
    blend = n.fn(0x1753a0, None, P, P, P, I)
    context = n.alloc(0x24800)
    frames = [n.alloc(0x328) for _ in range(3)]
    for frame in frames:
        init(frame)
    first, second, output = frames
    rows = []
    for encoded_a, encoded_b, voiced_a, voiced_b, t in itertools.product(
            [False, True], [False, True], [False, True], [False, True],
            [0.0, .125, .499, .5, .501, .875, 1.0]):
        t = f32(t)
        for frame, encoded, voiced, hz, scalar, time in [
                (first, encoded_a, voiced_a, 220.0, 2.0, 10.0),
                (second, encoded_b, voiced_b, 660.0, 8.0, 14.0)]:
            Q.from_address(frame + 0x150).value = 0x600 | (0x80000000 if encoded else 0)
            F.from_address(frame + 0x1d0).value = (
                cents(hz) if encoded else hz) if voiced else (EMPTY if encoded else 0.0)
            F.from_address(frame + 0x1d8).value = scalar
            D.from_address(frame + 0x148).value = time
        Q.from_address(output + 0x150).value = 0x600
        F.from_address(output + 0x1d0).value = 123.0
        F.from_address(output + 0x1d8).value = 77.0
        D.from_address(output + 0x148).value = 99.0
        before = c.string_at(output, 0x328)
        P.from_address(context + 0x247b8).value = first
        P.from_address(context + 0x247c0).value = second
        fraction = F(t)
        blend(context, c.byref(fraction), output, 0x600)
        swapped = t > .5
        assert P.from_address(context + 0x247b8).value == (second if swapped else first)
        assert P.from_address(context + 0x247c0).value == (first if swapped else second)
        both = voiced_a and voiced_b
        copied = not both and (voiced_b if swapped else voiced_a)
        if both:
            # Mixed representations convert the second input into the first's domain.
            ca = cents(220.0)
            hb = f32(440.0 * 2 ** (cents(660.0) / 1200.0)) if encoded_b else 660.0
            cb = cents(660.0) if encoded_a and encoded_b else cents(hb)
            value = mix(ca, cb, t)
            expected_pitch = value if encoded_a else f32(440.0 * 2 ** (value / 1200.0))
            expected_scalar = mix(2.0, 8.0, t)
            expected_time = 10.0 + 4.0 * t
            expected_fraction = f32(1.0 - t) if swapped else t
        elif copied:
            source = second if swapped else first
            expected_pitch = F.from_address(source + 0x1d0).value
            expected_scalar = F.from_address(source + 0x1d8).value
            expected_time = D.from_address(source + 0x148).value
            expected_fraction = 0.0
        else:
            assert c.string_at(output, 0x328) == before, 'Unvoiced selection mutated output'
            expected_pitch, expected_scalar, expected_time, expected_fraction = 123.0, 77.0, 99.0, 0.0
        pitch = F.from_address(output + 0x1d0).value
        assert math.isclose(pitch, expected_pitch, rel_tol=2e-7, abs_tol=2e-4), (pitch, expected_pitch)
        assert F.from_address(output + 0x1d8).value == expected_scalar
        assert D.from_address(output + 0x148).value == expected_time
        assert fraction.value == expected_fraction
        output_cents = encoded_a if both else (encoded_b if swapped else encoded_a) if copied else False
        assert Q.from_address(output + 0x150).value == (0x600 | (0x80000000 if output_cents else 0))
        rows.append(dict(cents_a=encoded_a, cents_b=encoded_b, voiced_a=voiced_a,
                         voiced_b=voiced_b, fraction=t, swapped=swapped,
                         action='blend' if both else 'copy' if copied else 'unchanged',
                         pitch=pitch, scalar=expected_scalar, time=expected_time,
                         remaining_fraction=fraction.value,
                         output_mask=hex(Q.from_address(output + 0x150).value)))
    return dict(dse_sha256=SHA256, entry_rva='0x1753a0', requested_mask='0x600',
                cases=len(rows), rows=rows, scope='Owned F0 and bit10 scalar frames; no full position loader or vocoder')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll)
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(cases=report['cases'], status='passed')))
