"""Use saved bit0 to constrain BRWSP ring geometry, then verify bit15 separately.

The representative geometry is an existence reconstruction, not a recovered
authoring configuration. Reports never serialize commercial PCM or histograms.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path

from audit_frm2_numeric import Native, F, I, P, Q, SHA256, f32
from frm2_crossing_distribution import crossing_distribution
from frm2_raw_feature_observer import OwnedRingView
from verify_vqm_bit16_peak_interpolation import auxiliary_sequences, metrics


def boundary_candidates(sequence, index):
    signal = sequence['pcm'][index * 256 - 256:index * 256 + 256]
    total = 0.
    for value in signal:
        total = f32(total + value)
    mean = f32(total / 512)
    positions = []
    for offset in range(511):
        left = f32(signal[offset] - mean)
        right = f32(signal[offset + 1] - mean)
        if f32(left * right) < 0 or left == 0:
            positions.append(offset)
    target = round(sequence['frames'][index]['features'][0] * 510)
    return {index * 256 - 256 + cut for cut in range(1, 512)
            if sum(offset >= cut for offset in positions) == target}


def run(args):
    sequence = auxiliary_sequences(args.aux_vdb)[-1]
    assert sequence['category'] == 'BRWSP'
    baseline = []
    for index in range(4, len(sequence['frames']) - 4):
        ring = sequence['pcm'][index * 256 - 257:index * 256 + 257]
        prediction = crossing_distribution(ring, 1, 256)
        delta = prediction['crossings'] - round(sequence['frames'][index]['features'][0] * 510)
        if delta > 20:
            baseline.append(index)
    assert len(baseline) == 4 and baseline[1] == baseline[0] + 1 and baseline[3] == baseline[2] + 1
    first = boundary_candidates(sequence, baseline[0]) & boundary_candidates(sequence, baseline[1])
    second = boundary_candidates(sequence, baseline[2]) & boundary_candidates(sequence, baseline[3])
    assert first and second
    capacities = sorted({b - a for a in first for b in second})
    capacity = min(capacities)
    boundary = next(a for a in sorted(first) if a + capacity in second)
    start_offset = capacity - boundary
    native = Native(args.dll)
    config = native.alloc(0x35f0)
    native.fn(0x90de0, P, P)(config)
    F.from_address(config + 0x18).value = 44100 / 256
    # cdde0's reviewed capacity term uses config ID100 (+540), with ID42=4.
    # This creates the representative ring; it does not recover a maker setting.
    factor = (capacity - 44100) / 256
    assert factor == int(factor) and 0 < factor < 64
    F.from_address(config + 0x540).value = factor
    for at in (0., 1.):
        native.add(config + 0x5d0 + 23 * 0xa0, at, 1.)
    context = native.alloc(0xce0)
    native.fn(0xcdde0, P, P, I, P)(context, 44100, config)
    assert I.from_address(context + 0xaf0).value == capacity
    assert I.from_address(context + 0xbb0).value == 256
    buffer = P.from_address(context + 0xae0).value
    frame, feature = native.alloc(0x1d8), native.alloc(0x228)
    P.from_address(frame + 0x128).value = feature
    Q.from_address(frame + 0x50).value = 1 << 13
    call = native.fn(0xd92e0, Q, P, P)
    errors, count_errors, exceptions = [], [], []
    exact = 0
    for index in range(1, len(sequence['frames'])):
        absolute = index * 256
        center = (absolute + start_offset) % capacity
        view = OwnedRingView(sequence['pcm'], absolute, center, capacity)
        for offset in range(-257, 257):
            at = (center + offset) % capacity
            F.from_address(buffer + at * 4).value = view[at]
        I.from_address(context + 0xb00).value = center
        assert call(context, frame) == 0
        predicted = crossing_distribution(view, (center - 256) % capacity, 256)
        actual = F.from_address(feature + 0x214).value
        assert actual == predicted['imbalance']
        exact += 1
        saved = sequence['frames'][index]['features']
        error = actual - saved[15]
        count_delta = predicted['crossings'] - round(saved[0] * 510)
        errors.append(error)
        count_errors.append(count_delta)
        if abs(error) > 1e-6:
            exceptions.append(dict(index=index, bit15_absolute_error=abs(error),
                                   crossing_count_delta=count_delta))
    # Bit15 excludes the last sample's outgoing crossing. Confirm that the
    # unavailable leading/trailing neighbour cannot affect either edge value.
    edge_controls = 0
    for index, source_index in ((1, -1), (len(sequence['frames']) - 1, len(sequence['pcm']))):
        absolute = index * 256
        center = (absolute + start_offset) % capacity
        baseline_value = None
        for fill in (0., float(sequence['pcm'][-1]), -32768., 32767.):
            for delta in range(-257, 257):
                source = absolute + delta
                value = (fill if source == source_index else
                         sequence['pcm'][source] if 0 <= source < len(sequence['pcm']) else 0.)
                F.from_address(buffer + ((center + delta) % capacity) * 4).value = value
            I.from_address(context + 0xb00).value = center
            assert call(context, frame) == 0
            value = F.from_address(feature + 0x214).value
            if baseline_value is None:
                baseline_value = value
            assert value == baseline_value
            edge_controls += 1
    quantization_controls = []
    for adjustment in (-.5, .5):
        pcm = [f32(value + math.copysign(1., value) * adjustment) if value else 0.
               for value in sequence['pcm']]
        altered_errors = []
        for index in range(1, len(sequence['frames'])):
            absolute = index * 256
            center = (absolute + start_offset) % capacity
            view = OwnedRingView(pcm, absolute, center, capacity)
            value = crossing_distribution(view, (center - 256) % capacity, 256)['imbalance']
            altered_errors.append(value - sequence['frames'][index]['features'][15])
        quantization_controls.append(dict(adjustment_from_zero=adjustment,
                                          **metrics(altered_errors)))
    return dict(dse_sha256=SHA256,
        auxiliary_sha256=hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest(),
        geometry_from_bit0_only=dict(exception_pairs=baseline,
            first_boundary_range=[min(first), max(first)], first_boundary_count=len(first),
            second_boundary_range=[min(second), max(second)], second_boundary_count=len(second),
            possible_capacities=capacities, representative_capacity=capacity,
            representative_start_offset=start_offset, constructor_config100=factor),
        native_to_independent_float32_exact=exact,
        unsaved_neighbour_controls=edge_controls,
        raw_bit15_to_saved=metrics(errors),
        fixed_half_lsb_controls=quantization_controls,
        saved_crossing_counts_match=sum(delta == 0 for delta in count_errors),
        exceptions=exceptions,
        scope='All 388 BRWSP raw windows after frame0. Boundary positions and period '
              'are constrained using saved bit0 only; bit15 is a separate check. '
              'A real DRS constructor allocates the representative ring, populated '
              'per window from installed PCM. Main comparison uses original PCM; '
              'fixed half-LSB inputs are separately labeled controls. No mean fitting or '
              'bit15 field substitutions. Neither original capacity nor configuration '
              'is uniquely proven; remaining small crossing differences persist.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'aux-vdb', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    report = run(args)
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report))


if __name__ == '__main__':
    main()
