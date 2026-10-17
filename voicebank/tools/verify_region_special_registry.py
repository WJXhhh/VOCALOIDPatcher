"""Owned fixtures for the native DVQM resource-ID resolver, not resource loading."""
import argparse
import ctypes as c
import itertools
import json
from pathlib import Path
from audit_frm2_numeric import Native, P, I, B, SHA256


def run(dll):
    n = Native(dll)
    resolve = n.fn(0x182140, P, P, I)
    rows = []
    mappings = [{}, {5: 20}, {5: 20, 20: 30}, {5: -1},
                {1: 30, 5: 20, 9: 10}, {5: 999}, {0: 10, 5: 5, 20: 30}]
    resources = [[], [10], [10, 20, 30], [30, 20, 10], [20, 10, 20], [-1, 5, 20, 30]]
    for ids, aliases in itertools.product(resources, mappings):
        config = n.alloc(0x100)
        objects = []
        for identity in ids:
            obj = n.alloc(0x138)
            I.from_address(obj).value = identity
            objects.append(obj)
        array = n.array(P, objects) if objects else n.alloc(8)
        P.from_address(config + 0x68).value = array
        P.from_address(config + 0x70).value = array + len(objects) * 8
        head = n.alloc(0x30)
        B.from_address(head + 0x19).value = 1

        def tree(entries, parent):
            if not entries:
                return head
            midpoint = len(entries) // 2
            key, value = entries[midpoint]
            node = n.alloc(0x30)
            I.from_address(node + 0x1c).value = key
            I.from_address(node + 0x20).value = value
            P.from_address(node + 8).value = parent
            P.from_address(node).value = tree(entries[:midpoint], node)
            P.from_address(node + 16).value = tree(entries[midpoint + 1:], node)
            return node

        P.from_address(head + 8).value = tree(sorted(aliases.items()), head)
        P.from_address(config + 0x80).value = head
        for query in [-2147483648, -1, 0, 1, 4, 5, 6, 9, 10, 19, 20, 21, 30, 999, 2147483647]:
            target = aliases.get(query, query)
            expected_index = ids.index(target) if query >= 0 and target in ids else None
            actual = resolve(config, query)
            expected = objects[expected_index] if expected_index is not None else None
            assert actual == expected, (ids, aliases, query, expected_index)
            rows.append(dict(ids=ids, aliases=aliases, query=query,
                             resolved_index=expected_index, effective_id=target))
    return dict(dse_sha256=SHA256, cases=len(rows), mismatches=0, rows=rows,
                scope='Actual native resource lookup with owned immutable vectors and search trees; '
                      'single-step aliases and first-match ordering; not native resource registration '
                      'or CDVQM sample load/synthesis')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = run(args.dll)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({key: value for key, value in result.items() if key != 'rows'}))
