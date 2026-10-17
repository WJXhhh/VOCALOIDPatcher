"""Compare installed V4X layer and graph shapes with prior Chinese-bank rules."""
import argparse
from collections import Counter
import hashlib
import json
import math
from pathlib import Path
import statistics

from analyze_reference_layers import walk_art
from analyze_reference_coverage import reachable
from verify_reference_ddi import load_public_parser


def percentile(values: list[float], fraction: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    return ordered[max(0, math.ceil(len(ordered) * fraction) - 1)]


def median_grid(units: list[list[dict]], layers: int) -> list[float]:
    ranks = [[] for _ in range(layers)]
    for parts in units:
        pitches = sorted(float(part['pitch1']) for part in parts)
        if len(pitches) == layers and all(value > -3e38 for value in pitches):
            for rank, pitch in enumerate(pitches):
                ranks[rank].append(pitch)
    assert all(ranks)
    return [statistics.median(rank) for rank in ranks]


def analyze_bank(path: Path, expected_sha256: str, model_type: type) -> dict:
    raw = path.read_bytes()
    digest = hashlib.sha256(raw).hexdigest()
    assert digest == expected_sha256
    model = model_type(raw)
    model.read()
    sta = {unit['phoneme']: list(unit['stap'].values())
           for unit in model.sta_data.values()}
    pairs = [(key, parts) for root in model.art_data.values()
             for key, parts in walk_art(root) if len(key) == 2 and parts]
    triples = [(key, parts) for root in model.art_data.values()
               for key, parts in walk_art(root) if len(key) == 3 and parts]
    unvoiced = set(model.phdc_data['phoneme']['unvoiced'])
    sta_hist = Counter(len(parts) for parts in sta.values())
    modal_count = max(sta_hist, key=lambda n: (sta_hist[n], n))
    pair_hist = Counter(len(parts) for _, parts in pairs)
    single_layer = [key for key, parts in pairs if len(parts) == 1]
    single_categories = Counter(
        ('Sil' if key[0] == 'Sil' else 'nonSil') + '_to_' +
        ('unvoiced' if key[1] in unvoiced else 'other')
        for key in single_layer)
    single_pitched = [key for key, parts in pairs
                      if len(parts) == 1 and parts[0]['pitch1'] > -3e38]
    single_sil_unvoiced = [key for key in single_layer
                           if key[0] == 'Sil' and key[1] in unvoiced]
    single_other = [key for key in single_layer
                    if key[0] != 'Sil' or key[1] not in unvoiced]
    partial = [key for key, parts in pairs
               if len(parts) not in (1, modal_count)]
    grid = median_grid(list(sta.values()), modal_count)
    deviations = []
    art_ranks = [[] for _ in range(modal_count)]
    for key, parts in pairs:
        pitches = sorted(float(part['pitch1']) for part in parts)
        if len(pitches) == modal_count and all(value > -3e38 for value in pitches):
            deviations.append(max(abs(a - b) for a, b in zip(pitches, grid)))
            for rank, pitch in enumerate(pitches):
                art_ranks[rank].append(pitch)
    art_grid = ([statistics.median(rank) for rank in art_ranks]
                if deviations else None)
    pair_edges = {key for key, _ in pairs}
    nodes = {token for edge in pair_edges for token in edge}
    reverse_sil = reachable(pair_edges, 'Sil', reverse=True)
    sta_tokens = set(sta)
    release_sources = {left for left, right in pair_edges if right == 'Sil'}
    return dict(bank=path.stem, ddi_sha256=digest,
                sta_keys=len(sta), sta_layer_histogram=dict(sorted(sta_hist.items())),
                modal_sta_layers=modal_count, sta_layers_uniform=len(sta_hist) == 1,
                pair_keys=len(pairs), pair_layer_histogram=dict(sorted(pair_hist.items())),
                pair_single_layer=len(single_layer),
                pair_single_layer_categories=dict(sorted(single_categories.items())),
                pair_single_layer_pitched=len(single_pitched),
                pair_single_layer_pitched_examples=[list(key)
                                                    for key in single_pitched[:8]],
                pair_single_sil_to_unvoiced=len(single_sil_unvoiced),
                pair_single_other=len(single_other),
                pair_single_other_examples=[list(key) for key in single_other[:8]],
                pair_partial_vs_modal=len(partial),
                pair_partial_examples=[list(key) for key in partial[:8]],
                sta_modal_pitch1_grid=grid,
                full_modal_pair_pitch1_grid=art_grid,
                full_modal_grid_median_difference_max=(
                    max(abs(a - b) for a, b in zip(grid, art_grid))
                    if art_grid else None),
                full_modal_pair_pitched_keys=len(deviations),
                full_modal_pair_grid_max_error_median=(
                    statistics.median(deviations) if deviations else None),
                full_modal_pair_grid_max_error_p95=percentile(deviations, .95),
                full_modal_pair_grid_max_error_max=max(deviations) if deviations else None,
                full_modal_pair_over_100_cent=sum(value > 100 for value in deviations),
                pair_graph_nodes=len(nodes),
                pair_graph_nodes_without_route_to_sil=len(nodes - reverse_sil),
                release_sources=len(release_sources),
                release_sources_without_sta=len(release_sources - sta_tokens),
                release_sources_without_sta_examples=sorted(release_sources - sta_tokens)[:8],
                triple_keys=len(triples))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--voice-root', type=Path, required=True)
    parser.add_argument('--ddb-tools', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True)
    args = parser.parse_args()
    manifest_bytes = args.manifest.read_bytes()
    manifest = json.loads(manifest_bytes)
    parser_digest = hashlib.sha256((args.ddb_tools / 'utils' / 'ddi_utils.py').read_bytes()).hexdigest()
    assert parser_digest == manifest['parser_sha256']
    model_type = load_public_parser(args.ddb_tools)
    rows = []
    for identity in manifest['banks']:
        matches = list(args.voice_root.rglob(identity['bank'] + '.ddi'))
        assert len(matches) == 1
        rows.append(analyze_bank(matches[0], identity['ddi_sha256'], model_type))
    assert len(rows) == 8
    result = dict(manifest_sha256=hashlib.sha256(manifest_bytes).hexdigest(),
                  parser_sha256=parser_digest, banks=len(rows),
                  uniform_sta_banks=sum(row['sta_layers_uniform'] for row in rows),
                  pair_keys=sum(row['pair_keys'] for row in rows),
                  pair_single_other=sum(row['pair_single_other'] for row in rows),
                  pair_partial_vs_modal=sum(row['pair_partial_vs_modal'] for row in rows),
                  release_sources_without_sta=sum(
                      row['release_sources_without_sta'] for row in rows),
                  triple_keys=sum(row['triple_keys'] for row in rows),
                  rows=rows,
                  scope='Read-only installed V4X DDI comparison metrics. Historical '
                        'Chinese-bank findings are cited in the accompanying '
                        'document and are not remeasured on this device. Layer '
                        'counts, pitch1 coordinates, and graph structure do not '
                        'by themselves prove Editor timing or acoustic quality.')
    assert result['pair_keys'] == 7437 and result['triple_keys'] == 994
    args.evidence.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps({key: result[key] for key in
                      ('banks', 'uniform_sta_banks', 'pair_single_other',
                       'pair_partial_vs_modal', 'release_sources_without_sta',
                       'triple_keys')}))


if __name__ == '__main__':
    main()
