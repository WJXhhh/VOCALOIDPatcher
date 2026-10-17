"""Classify installed V4X missing Silence edges without assuming a full graph."""
import argparse
from collections import Counter, deque
import hashlib
import json
from pathlib import Path

from analyze_reference_layers import walk_art
from verify_reference_ddi import load_public_parser


def shape(token: str) -> str:
    if token.startswith('br'):
        return 'breath_token'
    if token.startswith('*#'):
        return 'star_suffix_token'
    if '#' in token:
        return 'numbered_suffix_token'
    return 'plain_token'


def distances(edges: set[tuple[str, str]], start: str, reverse=False) -> dict[str, int]:
    adjacency = {}
    for left, right in edges:
        if reverse:
            left, right = right, left
        adjacency.setdefault(left, set()).add(right)
    result = {start: 0}
    pending = deque([start])
    while pending:
        source = pending.popleft()
        for target in adjacency.get(source, ()):
            if target not in result:
                result[target] = result[source] + 1
                pending.append(target)
    return result


def analyze_bank(path: Path, expected_sha256: str, model_type: type) -> dict:
    raw = path.read_bytes()
    digest = hashlib.sha256(raw).hexdigest()
    assert digest == expected_sha256
    model = model_type(raw)
    model.read()
    sta = {unit['phoneme'] for unit in model.sta_data.values()
           if unit['stap']}
    keys = [(key, parts) for root in model.art_data.values()
            for key, parts in walk_art(root) if len(key) == 2]
    edges = {key for key, parts in keys if parts}
    empty_keys = [list(key) for key, parts in keys if not parts]
    nodes = {token for edge in edges for token in edge}
    from_sil = distances(edges, 'Sil')
    to_sil = distances(edges, 'Sil', reverse=True)
    assert nodes <= set(from_sil)
    missing_entry = sorted(token for token in sta if ('Sil', token) not in edges)
    missing_exit = sorted(token for token in sta if (token, 'Sil') not in edges)
    no_return = sorted(nodes - set(to_sil))
    no_outgoing = sorted(nodes - {left for left, _ in edges})
    no_incoming = sorted(nodes - {right for _, right in edges})
    return dict(bank=path.stem, ddi_sha256=digest,
                sta_keys=len(sta), graph_nodes=len(nodes), pair_edges=len(edges),
                empty_directory_keys=empty_keys,
                sta_without_direct_sil_entry=missing_entry,
                sta_without_direct_sil_entry_by_shape=dict(sorted(
                    Counter(shape(token) for token in missing_entry).items())),
                sta_entry_distance_histogram=dict(sorted(
                    Counter(from_sil[token] for token in sta).items())),
                sta_without_direct_sil_exit=missing_exit,
                nodes_without_route_to_sil=no_return,
                nodes_without_route_to_sil_by_shape=dict(sorted(
                    Counter(shape(token) for token in no_return).items())),
                nodes_without_outgoing_edge=no_outgoing,
                nodes_without_incoming_edge=no_incoming,
                reverse_pair_edges=sum((right, left) in edges
                                       for left, right in edges),
                self_edges=sum(left == right for left, right in edges))


def main():
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
                  pair_edges=sum(row['pair_edges'] for row in rows),
                  empty_directory_keys=sum(len(row['empty_directory_keys'])
                                           for row in rows),
                  sta_without_direct_sil_entry=sum(
                      len(row['sta_without_direct_sil_entry']) for row in rows),
                  sta_without_direct_sil_exit=sum(
                      len(row['sta_without_direct_sil_exit']) for row in rows),
                  nodes_without_route_to_sil=sum(
                      len(row['nodes_without_route_to_sil']) for row in rows),
                  nodes_without_outgoing_edge=sum(
                      len(row['nodes_without_outgoing_edge']) for row in rows),
                  nodes_without_incoming_edge=sum(
                      len(row['nodes_without_incoming_edge']) for row in rows),
                  rows=rows,
                  scope='Read-only installed V4X DDI graph. Missing means absent '
                        'from the nonempty pair ART edges, not an expected or '
                        'required recording. Reachability is structural and does '
                        'not prove Editor-generated paths or audio quality.')
    assert result['pair_edges'] == 7437 and result['empty_directory_keys'] == 7
    args.evidence.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps({key: result[key] for key in
                      ('pair_edges', 'empty_directory_keys',
                       'sta_without_direct_sil_entry',
                       'sta_without_direct_sil_exit',
                       'nodes_without_route_to_sil')}))


if __name__ == '__main__':
    main()
