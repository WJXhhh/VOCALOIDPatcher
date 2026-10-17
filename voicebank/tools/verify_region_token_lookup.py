"""Exercise pinned DSE phoneme fallback with synthetic, privately owned tables."""
import argparse
import ctypes as c
import json
from pathlib import Path

from audit_frm2_numeric import Native, I, P, SHA256


def predict(tokens, silence, query):
    if query in tokens:
        return tokens.index(query)
    if '*#' in query:
        selected = next((s for s in silence if s[:3].lower() == 'sil'),
                        silence[0] if silence else 'Sil')
        return tokens.index(selected) if selected in tokens else -1
    prefix, marker, _ = query.partition('#')
    return tokens.index(prefix) if marker and prefix and prefix in tokens else -1


def run(dll):
    native = Native(dll)
    lookup = native.fn(0x125140, I, P, c.c_char_p)
    canonical = native.fn(0x1253b0, P, P, c.c_char_p)

    def text(value):
        data = value.encode('ascii') + b'\0'
        address = native.alloc(len(data))
        c.memmove(address, data, len(data))
        return address

    cases = []
    for tokens, silence in [
        (['Sil', 'a', 'a#2', 'n', 'br1'], ['Sil']),
        (['pause', 'silAlt', 'a', 'a*#2', '#2'], ['pause', 'silAlt']),
        (['pause', 'a'], ['pause']),
        (['a'], []),
    ]:
        table = native.alloc(0x180)
        pointers = native.array(P, [text(t) for t in tokens])
        I.from_address(table + 0x144).value = len(tokens)
        P.from_address(table + 0x148).value = pointers
        classes = native.alloc(0x160)
        P.from_address(table + 0x158).value = classes
        if silence:
            record = native.alloc(0x30)
            I.from_address(classes + 0x140).value = 1
            P.from_address(classes + 0x148).value = record
            I.from_address(record).value = len(silence)
            P.from_address(record + 0x10).value = native.array(P, [text(s) for s in silence])
            P.from_address(record + 0x18).value = text('Silence')
        for query in ['', 'a', 'A', 'a#2', 'a#6', 'a#', 'a##2', '#2',
                      'a*#2', '*#2', 'missing*#2', '^a', '^a#2', 'br1', 'missing']:
            expected = predict(tokens, silence, query)
            actual = lookup(table, query.encode('ascii'))
            pointer = canonical(table, query.encode('ascii'))
            value = c.string_at(pointer).decode('ascii') if pointer else None
            assert actual == expected, (tokens, query, actual, expected)
            assert value == (tokens[expected] if expected >= 0 else None)
            cases.append(dict(tokens=tokens, silence=silence, query=query,
                              index=actual, canonical=value))
    return dict(dll_sha256=SHA256, cases=cases, count=len(cases), mismatches=0,
                scope='Synthetic token lookup only; not full articulation selection')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = run(args.dll)
    args.output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(count=result['count'], mismatches=result['mismatches'])))
