"""Read-only inventory of real ei entry transitions; no DDB/audio extraction."""
import argparse
import hashlib
import json
from pathlib import Path

from analyze_reference_layers import walk_art
from verify_planned_ddi import load_public_parser


def audit(ddi, parser_root, output):
    data = ddi.read_bytes()
    ddi_hash = hashlib.sha256(data).hexdigest()
    parser_hash = hashlib.sha256((parser_root / "utils/ddi_utils.py").read_bytes()).hexdigest()
    assert ddi_hash == "234207c757d9fdd2832c91c12eac03d0e3a5e11bf8aeab79d4d802fe01d005bb"
    assert parser_hash == "aab606781b80b6fa656be4c1657261c6770b605d7ec3f47fe4d2ecbc5cc0c994"
    model = load_public_parser(parser_root)(data)
    model.read()
    paths = {}
    for root in model.art_data.values():
        for key, samples in walk_art(root):
            assert key not in paths
            paths[key] = samples
    assert len(paths) == 2556
    incoming = [{"path": list(key), "layers": len(samples)} for key, samples in sorted(paths.items())
                if len(key) == 2 and key[-1] == "ei"]
    related = [{"path": list(key), "layers": len(samples)} for key, samples in sorted(paths.items())
               if key[0] == "s" and any("ei" in token for token in key[1:])]
    report = {"ddi_sha256": ddi_hash, "parser_sha256": parser_hash, "art_path_count": len(paths),
              "s_ei_material_exists": ("s", "ei") in paths, "ei_incoming": incoming,
              "s_to_ei_containing_tokens": related,
              "scope": "Inventory only; no claim of acoustic compatibility or borrowing success."}
    output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("ddi", type=Path)
    parser.add_argument("parser_root", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    audit(args.ddi, args.parser_root, args.output)
