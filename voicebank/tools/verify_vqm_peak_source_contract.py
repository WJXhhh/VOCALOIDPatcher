"""Test saved VQM component selection, raw-PCM agreement and peak phase inputs.

Uses installed material in memory only. The amplitude-match selection is a
diagnostic that consumes saved bit16; it is never presented as a producer.
"""
from __future__ import annotations

import argparse
from collections import Counter
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import statistics

from audit_frm2_numeric import Native, SHA256, F, I, P, Q, D, B, f32
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit16_peak_interpolation import auxiliary_sequences, metrics
from frm2_raw_features import raw_features


def summarize(values):
    return dict(count=len(values), minimum=min(values), maximum=max(values),
                mean=statistics.fmean(values))


def circular_errors(pairs):
    return metrics([math.remainder(a - b, math.tau) for a, b in pairs])


def run(args):
    parser_hash, ddi_hash, sequences = load_sequences(args.manifest, args.voice_root,
                                                      args.ddb_tools)
    sequences += auxiliary_sequences(args.aux_vdb)
    native = Native(args.dll)
    cfg, ctx = native.alloc(0x35f0), native.alloc(0x400)
    native.fn(0x90de0, P, P)(cfg)
    native.fn(0x49790, P, P, I, P, I)(ctx, 0, cfg, 0)
    analyze = native.fn(0x4e8e0, Q, P, P, I, B, F, P, P, P, B, P, F, I, I, I, I)
    detect = native.fn(0x51e60, Q, P, P, I, P, P, F, I, F, D, F)
    output = native.alloc(30000)
    rows = []
    for case, seq in enumerate(sequences):
        observations = []
        amplitude_errors, energy_errors, component_errors = [], [], []
        match_frequency_delta, match_ratio, match_rank, nearest_rank = [], [], [], []
        saved_first_ratios = []
        for index in range(4, len(seq['frames']) - 4):
            frame = seq['frames'][index]
            signal = seq['pcm'][index * 256 - 1024:index * 256 + 1025]
            scalar, packed, count = P(), P(), I()
            assert analyze(ctx, native.array(F, signal), 2049, 10, 1., c.byref(scalar),
                           0, c.byref(count), 2, c.byref(packed), 2., 0, 0, 0, 0) == 0
            assert count.value == 2048
            pointer, peaks = P(output), I()
            assert detect(packed, scalar, count.value, c.byref(pointer), c.byref(peaks),
                          44100, 0, 0., 0., 0.) == 0
            components = [tuple(F.from_address(output + j * 20 + k * 4).value
                                for k in range(3)) for j in range(peaks.value)]
            at = min(range(peaks.value), key=lambda j: abs(components[j][0] - frame['f0']))
            first_at = min(range(peaks.value), key=lambda j: abs(
                components[j][0] - frame['first_component_frequency']))
            match = min(range(peaks.value), key=lambda j: abs(
                f32(components[j][1] / 10.) - frame['features'][16]))
            amplitude_errors.append(f32(components[first_at][1] / 10.) - frame['features'][16])
            component_errors.append(frame['first_component_frequency'] - frame['f0'])
            saved_first_ratios.append(frame['first_component_frequency'] / frame['f0'])
            match_frequency_delta.append(components[match][0] - frame['f0'])
            match_ratio.append(components[match][0] / frame['f0'])
            match_rank.append(match)
            nearest_rank.append(at)
            pcm = list(seq['pcm'][index * 256 - 256:index * 256 + 256])
            # A bounded ring with an extra preceding/following sample avoids
            # the native physical-wrap eligibility quirk for this energy check.
            ring = [seq['pcm'][index * 256 - 257]] + pcm + [seq['pcm'][index * 256 + 256]]
            energy = raw_features(ring, 1, 256)['feature12']
            energy_errors.append(energy - frame['features'][2])
            observations.append(dict(index=index, frame=frame, peak=components[at],
                                     first_peak=components[first_at]))
        phase_pairs = {name: [] for name in ('raw', 'center_f0', 'center_peak',
                                             'global_f0', 'global_peak')}
        step_pairs = {name: [] for name in ('peak_minus_f0', 'f0_minus_peak',
                                            'phase_difference', 'phase_minus_f0_step', 'zero')}
        for position, obs in enumerate(observations):
            index, frame, peak = obs['index'], obs['frame'], obs['peak']
            actual = frame['features'][17]
            for name, correction in (
                    ('raw', 0.), ('center_f0', math.tau * 1024 * frame['f0'] / 44100),
                    ('center_peak', math.tau * 1024 * peak[0] / 44100),
                    ('global_f0', -math.tau * (index * 256 - 1024) * frame['f0'] / 44100),
                    ('global_peak', -math.tau * (index * 256 - 1024) * peak[0] / 44100)):
                phase_pairs[name].append((actual, peak[2] + correction))
            if position:
                previous = observations[position - 1]
                saved_step = math.remainder(actual - previous['frame']['features'][17], math.tau)
                frequency_step = math.tau * 256 * (peak[0] - frame['f0']) / 44100
                phase_step = math.remainder(peak[2] - previous['peak'][2], math.tau)
                for name, prediction in (
                        ('peak_minus_f0', frequency_step), ('f0_minus_peak', -frequency_step),
                        ('phase_difference', phase_step),
                        ('phase_minus_f0_step', math.remainder(
                            phase_step - math.tau * 256 * frame['f0'] / 44100, math.tau)),
                        ('zero', 0.)):
                    step_pairs[name].append((saved_step, max(-.25, min(.25, prediction))))
        row = dict(case=seq.get('category', case), frames=len(observations),
                   bit16_nearest_saved_first_component=metrics(amplitude_errors),
                   saved_first_frequency_minus_f0=metrics(component_errors),
                   saved_first_frequency_over_f0=summarize(saved_first_ratios),
                   saved_bit2_from_pcm={**metrics(energy_errors),
                       'mean_signed_error': statistics.fmean(energy_errors),
                       'signed_error_range': [min(energy_errors), max(energy_errors)],
                       'saved_below_reconstructed': sum(value > 0 for value in energy_errors)},
                   amplitude_match_diagnostic=dict(
                       frequency_minus_f0=summarize(match_frequency_delta),
                       frequency_over_f0=summarize(match_ratio),
                       ranks=dict(Counter(match_rank)),
                       equals_nearest_f0=sum(a == b for a, b in zip(match_rank, nearest_rank))),
                   bit17_phase_candidates={name: circular_errors(pairs)
                                           for name, pairs in phase_pairs.items()},
                   bit17_step_candidates={name: circular_errors(pairs)
                                          for name, pairs in step_pairs.items()})
        rows.append(row)
        print(json.dumps(dict(case=row['case'], energy=row['saved_bit2_from_pcm'],
                             selection=row['bit16_nearest_saved_first_component'])), flush=True)
    return dict(dse_sha256=SHA256, parser_sha256=parser_hash, ddi_sha256=ddi_hash,
                auxiliary_sha256=hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest(),
                cases=rows, scope='2049 raw PCM windows and native interpolated peaks. '
                      'Saved-first-component selection and known phase origins are tested '
                      'without fitted rotations, gains or time shifts. Closest amplitude '
                      'peak is diagnostic only. No proprietary PCM or frame payload is saved.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'manifest', 'voice-root', 'ddb-tools', 'aux-vdb', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    args.output.write_text(json.dumps(run(args), indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
