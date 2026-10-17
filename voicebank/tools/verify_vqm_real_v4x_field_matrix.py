"""Compare loaded VQM source-field changes during a real V4X note event."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import SHA256
from probe_vqm_loaded_aux_mix import VDM_SHA256, VQM_SHA256


MUTATIONS = ("frequency", "amplitude", "phase", "feature4", "feature6",
             "feature14", "feature15", "feature16", "feature17",
             "feature18", "feature19", "outer18", "outer19")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dse", required=True, type=Path)
    parser.add_argument("--vdm", required=True, type=Path)
    parser.add_argument("--explib", required=True, type=Path)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--voice-root", required=True, type=Path)
    parser.add_argument("--ddb-tools", required=True, type=Path)
    parser.add_argument("--bank", required=True)
    parser.add_argument("--first", default="a")
    parser.add_argument("--second", default="Sil")
    parser.add_argument("--third")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    probe = Path(__file__).with_name("verify_vqm_real_v4x_event.py")
    common = [sys.executable, str(probe), "--dse", str(args.dse),
              "--vdm", str(args.vdm), "--explib", str(args.explib),
              "--manifest", str(args.manifest), "--voice-root", str(args.voice_root),
              "--ddb-tools", str(args.ddb_tools), "--bank", args.bank,
              "--first", args.first, "--second", args.second]
    if args.third is not None:
        common += ["--third", args.third]

    def run(category: int, mutation: str | None) -> dict:
        mode = "tense" if category == 2 else "breath"
        command = common + ["--single-mode", mode]
        if mutation is not None:
            command += ["--single-mutation", mutation,
                        "--source-category", str(category)]
        child = subprocess.run(command, capture_output=True, text=True,
                               check=False, timeout=180)
        if child.returncode:
            raise RuntimeError(f"{mode}/{mutation}: "
                               f"{child.stderr[-1500:] or child.returncode}")
        return json.loads(child.stdout)

    cases = []
    for category in (2, 3):
        baseline = run(category, None)
        first_hashes = baseline["output_block_hashes"]
        rows = []
        for mutation in MUTATIONS:
            changed = run(category, mutation)
            if (changed["blocks"] != baseline["blocks"] or
                    changed["art_pcm_checks"] != baseline["art_pcm_checks"] or
                    changed["sta_pcm_checks"] != baseline["sta_pcm_checks"]):
                raise AssertionError("real V4X source event changed across cases")
            count = sum(left != right for left, right in zip(
                first_hashes, changed["output_block_hashes"]))
            rows.append({"mutation": mutation,
                         "source_frames_mutated": changed["mutation_state"][
                             "source_frames"],
                         "values_changed": changed["mutation_state"][
                             "values_changed"],
                         "pcm_blocks_changed": count,
                         "output_stream_sha256": changed["output_stream_sha256"],
                         "source_copies_present": changed["categories"][str(category)][
                             "source_copies_present"],
                         "work_harmonics_present": changed["categories"][str(category)][
                             "work_harmonics_present"]})
        repeat = run(category, None)
        if first_hashes != repeat["output_block_hashes"]:
            raise AssertionError("real V4X baseline is not repeatable")
        cases.append({"category": category, "baseline_blocks": baseline["blocks"],
                      "baseline_nonzero_blocks": baseline["nonzero_blocks"],
                      "baseline_stream_sha256": baseline["output_stream_sha256"],
                      "art_pcm_checks": baseline["art_pcm_checks"],
                      "sta_pcm_checks": baseline["sta_pcm_checks"],
                      "baseline_source_copies_present": baseline[
                          "categories"][str(category)]["source_copies_present"],
                      "mutations": rows})
    report = {"dse_sha256": SHA256, "vdm_sha256": VDM_SHA256,
              "auxiliary_vqm_sha256": VQM_SHA256,
              "manifest_sha256": hashlib.sha256(args.manifest.read_bytes()).hexdigest(),
              "bank": args.bank,
              "route": [args.first, args.second] + ([args.third]
                                                    if args.third else []),
              "cases": cases,
              "scope": "Real installed V4X DDI/DDB source event; each "
                       "auxiliary source change is in memory in an independent "
                       "offline DSE process. Default category control is raised "
                       "for that test and the other category is left at default. "
                       "Only aggregate PCM block differences and hashes retained; "
                       "no installed sample or frame payload exported."}
    if args.output:
        args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    else:
        print(json.dumps(report))


if __name__ == "__main__":
    main()
