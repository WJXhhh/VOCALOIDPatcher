"""Compare installed V4X VQM pitch2 with the pinned DSE frame-mean helper."""

from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
import mmap
from pathlib import Path
import struct

from audit_frm2_numeric import D, I, Native, P, SHA256
import probe_frm2
from verify_pitch2_native_mean import observed_mean
from verify_reference_ddi import load_public_parser, pointer


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--voice-root", type=Path, required=True)
    parser.add_argument("--ddb-tools", type=Path, required=True)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    args = parser.parse_args()

    manifest_data = args.manifest.read_bytes()
    manifest = json.loads(manifest_data)
    parser_hash = hashlib.sha256(
        (args.ddb_tools / "utils" / "ddi_utils.py").read_bytes()
    ).hexdigest()
    if parser_hash != manifest["parser_sha256"]:
        raise ValueError("DDI parser hash differs")
    model_type = load_public_parser(args.ddb_tools)
    native = Native(args.dll)
    mean = native.fn(0xc86a0, None, P, D, I, I, I)

    rows = []
    for entry in manifest["banks"]:
        matches = list(args.voice_root.rglob(entry["bank"] + ".ddi"))
        if len(matches) != 1:
            raise ValueError(f"expected one DDI for {entry['bank']}")
        ddi_path = matches[0]
        ddi = ddi_path.read_bytes()
        if hashlib.sha256(ddi).hexdigest() != entry["ddi_sha256"]:
            raise ValueError(f"DDI hash differs: {ddi_path}")
        model = model_type(ddi)
        model.read()
        with ddi_path.with_suffix(".ddb").open("rb") as stream:
            with mmap.mmap(stream.fileno(), 0, access=mmap.ACCESS_READ) as ddb:
                for sample_index, sample in enumerate((model.vqm_data or {}).values()):
                    f0_values = []
                    for encoded in sample["epr"]:
                        start, _ = pointer(encoded, ddi)
                        size = struct.unpack_from("<I", ddb, start + 4)[0]
                        frame = probe_frm2.parse_frame(ddb[start:start + size])
                        if not isinstance(frame, probe_frm2.VqmFrame):
                            raise ValueError("VQM references a non-VQM frame")
                        f0_values.append(frame.f0_hz)
                    predicted = observed_mean(
                        mean, [(value, False) for value in f0_values],
                        0, len(f0_values) - 1,
                    )
                    saved = float(sample["pitch2"])
                    rows.append({
                        "bank": ddi_path.stem,
                        "sample_index": sample_index,
                        "frame_count": len(f0_values),
                        "native_mean_cent": predicted,
                        "saved_pitch2_cent": saved,
                        "error_cent": abs(predicted - saved),
                        "exact_float32": c.c_float(predicted).value == saved,
                    })

    if len(rows) != 24 or any(row["error_cent"] > 0.0001 for row in rows):
        raise ValueError("V4X VQM native mean differs from saved pitch2")
    result = {
        "dse_sha256": SHA256,
        "manifest_sha256": hashlib.sha256(manifest_data).hexdigest(),
        "parser_sha256": parser_hash,
        "helper_rva": "0xc86a0",
        "sample_count": len(rows),
        "exact_float32_count": sum(row["exact_float32"] for row in rows),
        "max_error_cent": max(row["error_cent"] for row in rows),
        "rows": rows,
        "scope": "Native mean helper on stored VQM frame F0; not the original VQM authoring call site.",
    }
    args.evidence.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: result[key] for key in (
        "sample_count", "exact_float32_count", "max_error_cent"
    )}))


if __name__ == "__main__":
    main()
