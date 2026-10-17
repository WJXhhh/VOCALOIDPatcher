"""Summarize two repeated read-only V4X breath-frame reports."""
import argparse
import hashlib
import json
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--first', type=Path, required=True)
    parser.add_argument('--second', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()

    first = args.first.read_bytes()
    assert first == args.second.read_bytes()
    source = json.loads(first)
    rows = source['rows']
    assert source['shapes'] == len(rows) == 80
    assert source['native_frame_reads'] == sum(row['native_reads'] for row in rows) == 3780
    assert source['pcm_mismatches'] == source['guard_failures'] == 0

    by_bank = {}
    for row in rows:
        bank = row['bank']
        group = by_bank.setdefault(bank, dict(ddi_sha256=row['ddi_sha256'],
                                              ddb_sha256=row['ddb_sha256'],
                                              edges=0, frames=0, nonzero_pcm_windows=0,
                                              short_margin_exceptions=0, entries=[]))
        assert group['ddi_sha256'] == row['ddi_sha256']
        assert group['ddb_sha256'] == row['ddb_sha256']
        group['edges'] += 1
        group['frames'] += row['native_reads']
        group['nonzero_pcm_windows'] += row['nonzero_pcm_windows']
        group['short_margin_exceptions'] += bool(row['short_margin_exception'])
        group['entries'].append(dict(key=row['key'], frames=row['frames'],
                                     pcm_margin=row['pcm_margin'],
                                     core_delta_bytes=row['core_delta_bytes'],
                                     short_margin_exception=row['short_margin_exception'],
                                     nonzero_pcm_windows=row['nonzero_pcm_windows'],
                                     max_front_zero_samples=row['max_front_zero_samples'],
                                     max_tail_zero_samples=row['max_tail_zero_samples']))
    assert len(by_bank) == 8
    assert all(group['edges'] == 10 for group in by_bank.values())
    assert sum(group['short_margin_exceptions'] for group in by_bank.values()) == 8
    result = dict(dse_sha256=source['dll_sha256'],
                  input_sha256=source['input_sha256'],
                  repeat_runs=2, raw_report_sha256=hashlib.sha256(first).hexdigest(),
                  banks=8, edges=80, frame_reads=3780,
                  nonzero_pcm_windows=sum(group['nonzero_pcm_windows']
                                          for group in by_bank.values()),
                  pcm_mismatches=0, guard_failures=0,
                  by_bank=by_bank,
                  scope='Installed V4X DDI/DDB opened read-only. All frames of 80 '
                        'Silence/breath entry and exit ART materials compared through '
                        'native mode-0 frame loader with DDB frame time and bounded '
                        '2049-sample PCM window. This is direct frame reading, not '
                        'an Editor event or final audio output.')
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n',
                           encoding='utf-8')
    print(json.dumps(dict(edges=result['edges'], frame_reads=result['frame_reads'],
                          nonzero_pcm_windows=result['nonzero_pcm_windows'])))


if __name__ == '__main__':
    main()
