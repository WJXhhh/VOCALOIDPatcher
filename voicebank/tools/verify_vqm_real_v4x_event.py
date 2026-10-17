"""Observe loaded VQM classes during a real installed V4X DDB note event."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import F, I, P, Q, SHA256
from probe_vqm_loaded_aux_mix import VDM_SHA256, VQM_SHA256
from verify_region_v4x_index_frame_reads import verify
from verify_vqm_loaded_aux_pcm import loaded_auxiliary


MODES = ("unbound", "off", "tense", "breath", "both")


def single_mode(args: argparse.Namespace) -> dict:
    if hashlib.sha256(args.vdm.read_bytes()).hexdigest() != VDM_SHA256:
        raise ValueError("VDM binary differs from verified layout")
    if hashlib.sha256((args.explib / "VQM.vdb").read_bytes()).hexdigest() != VQM_SHA256:
        raise ValueError("auxiliary VQM file differs from verified sample")
    output_blocks: dict[int, str] = {}
    frame_rows = []
    mutation_state = {}

    def observe_output(time: int, block: bytes) -> None:
        if len(block) != 512 or time in output_blocks:
            raise AssertionError("native output block contract changed")
        output_blocks[time] = hashlib.sha256(block).hexdigest()

    def observe_frame(time: int, ctx: int, ring: int) -> None:
        begin = P.from_address(ctx + 0x2af30).value
        end = P.from_address(ctx + 0x2af38).value
        count = (end - begin) // 8 if begin and end and end >= begin else 0
        if count not in (0, 3):
            raise AssertionError("unexpected VQM object count")
        classes = []
        if count:
            for category in (2, 3):
                obj = P.from_address(begin + (category - 1) * 8).value
                state = P.from_address(obj + 0x20).value if obj else 0
                work = P.from_address(state + 0x10).value if state else 0
                classes.append({
                    "category": category,
                    "event_gain": F.from_address(state).value if state else None,
                    "selected_sample": I.from_address(state + 0x5c).value if state else None,
                    "work_harmonics": I.from_address(work + 0x158).value if work else 0,
                    "work_mask": Q.from_address(work + 0x150).value if work else 0,
                    "source_a_harmonics": I.from_address(state + 0xf0 + 0x158).value
                    if state else 0,
                    "source_b_harmonics": I.from_address(state + 0x418 + 0x158).value
                    if state else 0,
                })
        aux = P.from_address(ctx + 0x2b130).value
        bins = I.from_address(aux + 0x1a8).value if aux else 0
        candidate = P.from_address(ctx + 0x247b8).value
        feature = P.from_address(candidate + 0x208).value if candidate else 0
        frame_rows.append({"time": time, "objects": count,
                           "auxiliary_spectrum_bins": bins,
                           "ordinary_candidate_feature": bool(feature),
                           "classes": classes})

    result = verify(
        args.dse, args.bank, args.first, args.second, args.manifest, args.voice_root,
        args.ddb_tools, full_loop_limit=1, full_loop_min_target_time=150,
        third=args.third,
        full_loop_engine_setup=lambda native, voice, model, raw: loaded_auxiliary(
            native, args.vdm, args.explib, args.single_mode,
            source_mutation=args.single_mutation,
            source_category=args.source_category,
            mutation_state=mutation_state),
        full_loop_frame_observer=observe_frame,
        full_loop_output_observer=observe_output)
    loop = result["full_loop"]
    if loop["events"] != 1 or loop["pcm_observer_checks"] != loop[
            "pcm_observer_matches"] or loop.get("sta_pcm_checks", 0) != loop.get(
                "sta_pcm_matches", 0):
        raise AssertionError("real V4X source PCM verification failed")
    if len(output_blocks) != loop["blocks"]:
        raise AssertionError("native output block observation incomplete")
    output = {"bank": args.bank, "mode": args.single_mode,
            "blocks": loop["blocks"], "nonzero_blocks": loop["nonzero_blocks"],
            "art_pcm_checks": loop["pcm_observer_checks"],
            "sta_pcm_checks": loop.get("sta_pcm_checks", 0),
            "output_stream_sha256": loop["rows"][0]["stream_sha256"],
            "output_block_hashes": [output_blocks[index]
                                    for index in range(loop["blocks"])],
            "observed_frame_count": len(frame_rows),
            "objects_histogram": {str(count): sum(row["objects"] == count
                                                  for row in frame_rows)
                                  for count in (0, 3)},
            "auxiliary_spectrum_blocks": sum(row["auxiliary_spectrum_bins"] > 0
                                             for row in frame_rows),
            "ordinary_candidate_feature_blocks": sum(
                row["ordinary_candidate_feature"] for row in frame_rows),
            "categories": {
                str(category): {
                    "source_copies_present": sum(
                        any(item["category"] == category and
                            item["source_a_harmonics"] > 0 and
                            item["source_b_harmonics"] > 0
                            for item in row["classes"]) for row in frame_rows),
                    "work_harmonics_present": sum(
                        any(item["category"] == category and
                            item["work_harmonics"] > 0
                            for item in row["classes"]) for row in frame_rows),
                    "selected_sample_indices": sorted({
                        item["selected_sample"] for row in frame_rows
                        for item in row["classes"]
                        if item["category"] == category}),
                    "work_masks": sorted({
                        f'{item["work_mask"]:x}' for row in frame_rows
                        for item in row["classes"]
                        if item["category"] == category}),
                } for category in (2, 3)},
            "scope": f"Native V4X {'→'.join([args.first, args.second] + ([args.third] if args.third else []))} index event with installed DDI/DDB "
                     "frames and PCM windows; loaded auxiliary VQM objects in "
                     "independent offline DSE process, no Editor host."}
    if (args.first, args.second, args.third) != ("a", "Sil", None):
        output["route"] = [args.first, args.second] + ([args.third]
                                                        if args.third else [])
    if args.single_mutation is not None:
        output["source_mutation"] = args.single_mutation
        output["mutation_state"] = mutation_state
    return output


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
    parser.add_argument("--single-mode", choices=MODES)
    parser.add_argument("--single-mutation", choices=(
        "frequency", "amplitude", "phase", "feature4", "feature6",
        "feature14", "feature15", "feature16", "feature17",
        "feature18", "feature19", "outer18", "outer19"))
    parser.add_argument("--source-category", type=int, choices=(2, 3))
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if (args.single_mutation is None) != (args.source_category is None):
        parser.error("--single-mutation and --source-category must be paired")
    if args.single_mode:
        print(json.dumps(single_mode(args)))
        return
    if args.output is None:
        parser.error("--output is required for a full comparison")
    rows = []
    for mode in MODES:
        command = [sys.executable, str(Path(__file__).resolve()),
                   "--dse", str(args.dse), "--vdm", str(args.vdm),
                   "--explib", str(args.explib), "--manifest", str(args.manifest),
                   "--voice-root", str(args.voice_root),
                   "--ddb-tools", str(args.ddb_tools), "--bank", args.bank,
                   "--first", args.first, "--second", args.second,
                   "--single-mode", mode]
        if args.third is not None:
            command += ["--third", args.third]
        child = subprocess.run(command, capture_output=True, text=True,
                               check=False, timeout=180)
        if child.returncode:
            raise RuntimeError(f"{mode}: {child.stderr[-1500:] or child.returncode}")
        rows.append(json.loads(child.stdout))
    baseline = next(row for row in rows if row["mode"] == "unbound")
    off = next(row for row in rows if row["mode"] == "off")
    if baseline["output_block_hashes"] != off["output_block_hashes"]:
        raise AssertionError("bound auxiliary defaults changed PCM")
    report = {"dse_sha256": SHA256, "vdm_sha256": VDM_SHA256,
              "auxiliary_vqm_sha256": VQM_SHA256, "bank": args.bank,
              "route": [args.first, args.second] + ([args.third]
                                                    if args.third else []),
              "cases": [{key: value for key, value in row.items()
                         if key != "output_block_hashes"} for row in rows],
              "versus_unbound_changed_pcm_blocks": {
                  row["mode"]: sum(left != right for left, right in zip(
                      baseline["output_block_hashes"], row["output_block_hashes"]))
                  for row in rows},
              "scope": "One installed V4X indexed note event, real DDI/DDB "
                       "ART/STA frames and source PCM, five independently "
                       "initialized offline DSE instances. Aggregate state and "
                       "PCM differences only; no sample bytes exported. "
                       "Controller defaults are fixture values, not observed "
                       "Editor note automation."}
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
