"""Conditional cache-capacity arithmetic from persisted V4X metadata, without PCM reads."""
import argparse
import hashlib
import json
from pathlib import Path


def audit(report, window, hop):
    if window <= 0 or hop <= 0:
        raise ValueError('Window and hop must be positive')
    data = json.loads(report.read_text(encoding='utf-8'))
    rows = []
    for bank in data['banks']:
        for sample in bank['regions']['short_margin_samples']:
            frames = sample['frames']
            count = frames * hop + sample['pcm_margin']
            required = (frames - 1) * hop + window + 1
            maximum = min(frames - 1, (count - window - 1) // hop)
            rows.append(dict(bank=bank['audit']['bank'], ddi_sha256=bank['ddi_sha256'],
                             key=sample['key'], frames=frames, pcm_count=count,
                             required_samples_for_final_window=required,
                             final_window_shortfall=max(0, required-count),
                             maximum_in_bounds_frame=max(-1, maximum),
                             cache_read_start_relative_to_snd_samples=
                             sample['core_delta_bytes']//2 - window//2))
    return dict(input_sha256=hashlib.sha256(report.read_bytes()).hexdigest(),
                assumed_window_size=window, assumed_hop=hop,
                scope='Conditional capacity calculation only: assumes full PCM cache and final-frame '
                      'window request; does not observe host mode, window size, requests or out-of-bounds reads',
                rows=rows)


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--report', type=Path, required=True)
    p.add_argument('--window-size', type=int, required=True)
    p.add_argument('--hop', type=int, default=256)
    p.add_argument('--output', type=Path, required=True)
    a = p.parse_args()
    r = audit(a.report, a.window_size, a.hop)
    a.output.write_text(json.dumps(r, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(samples=len(r['rows']), conditional_shortfalls=
                          sum(x['final_window_shortfall'] > 0 for x in r['rows']))))
