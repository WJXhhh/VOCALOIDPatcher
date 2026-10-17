"""Read installed compact DVQM in place using its native loader and destructor.

Only file identity and structural counts are written; no sample data is exported.
"""
import argparse
import ctypes as c
import hashlib
import json
from pathlib import Path
from audit_frm2_numeric import Native, P, I, Q, SHA256


def run(dll, directory):
    path = directory / 'dvqm.dat'
    with path.open('rb') as source:
        digest = hashlib.file_digest(source, 'sha256').hexdigest()
    n = Native(dll)
    load = n.fn(0xfea0, P, c.c_char_p)
    free = n.fn(0x10080, Q, P)
    rows = []
    for suffix in ['', '\\', '/', '']:
        obj = load((str(directory.resolve()) + suffix).encode('utf-8'))
        assert obj, 'Native compact DVQM loader returned null'
        try:
            count = I.from_address(obj + 0x50).value
            array = P.from_address(obj + 0x48).value
            stream = P.from_address(obj + 0x70).value
            file = P.from_address(obj + 0x68).value
            assert 0 < count < 100000 and array and stream and file
            assert all((P * count).from_address(array))
            assert P.from_address(stream + 8).value == file
            rows.append(dict(trailing_separator=suffix, child_count=count,
                             file_open=True, stream_connected=True))
        finally:
            assert free(obj) == 0
    assert len({row['child_count'] for row in rows}) == 1
    assert free(None) == 0
    assert not load(b'')
    return dict(dse_sha256=SHA256, resource_sha256=digest, resource_bytes=path.stat().st_size,
                component=directory.name, load_free_cycles=len(rows), rows=rows,
                empty_path_rejected=True, null_free_accepted=True,
                scope='Installed shared compact DVQM loaded read-only in a disposable process; '
                      'native nonempty child array and file/stream linkage verified; '
                      'not CDVQMloadSample, special production, or host synthesis')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--directory', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = run(args.dll, args.directory)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(result))
