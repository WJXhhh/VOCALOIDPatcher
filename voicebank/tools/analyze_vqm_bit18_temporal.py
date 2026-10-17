"""Bound temporal processing between DRS bit18 and saved V4X VQM bit18.

Three independent VQM sequences are analyzed in memory. Only aggregate errors
and cross-sequence fit coefficients are written; no frame or PCM payloads.
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import statistics

from audit_frm2_numeric import SHA256
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit18_saved_band import compare_one


MARGIN = 4


def smooth(values: list[float], index: int, weights: list[float],
           domain: str = "feature") -> float:
    radius = len(weights) // 2
    total = sum(weights)
    if domain == "feature":
        return sum(values[index + offset - radius] * weight
                   for offset, weight in enumerate(weights)) / total
    if domain == "pre_log_ratio":
        ratio = sum(10 ** (5 * (values[index + offset - radius] - 5)) * weight
                    for offset, weight in enumerate(weights)) / total
        return 5 + math.log10(ratio) / 5
    raise ValueError("unknown smoothing domain")


def errors(source: list[float], saved: list[float], weights: list[float],
           domain: str = "feature") -> dict:
    residuals = [abs(smooth(source, index, weights, domain) - saved[index])
                 for index in range(MARGIN, len(saved) - MARGIN)]
    return {
        "frames": len(residuals),
        "mean_absolute_error": statistics.fmean(residuals),
        "max_absolute_error": max(residuals),
    }


def fit_symmetric_five(
        series: list[tuple[list[float], list[float], list[float]]],
        held_out: int) -> list[float]:
    a11 = a12 = a22 = b1 = b2 = 0.0
    for sample_index, (source, saved, _) in enumerate(series):
        if sample_index == held_out:
            continue
        for index in range(MARGIN, len(saved) - MARGIN):
            near = source[index - 1] + source[index + 1] - 2 * source[index]
            far = source[index - 2] + source[index + 2] - 2 * source[index]
            target = saved[index] - source[index]
            a11 += near * near
            a12 += near * far
            a22 += far * far
            b1 += near * target
            b2 += far * target
    determinant = a11 * a22 - a12 * a12
    if determinant <= 1e-15:
        raise AssertionError("bit18 temporal fit is singular")
    near_weight = (b1 * a22 - b2 * a12) / determinant
    far_weight = (b2 * a11 - b1 * a12) / determinant
    center_weight = 1 - 2 * (near_weight + far_weight)
    return [far_weight, near_weight, center_weight,
            near_weight, far_weight]


def run(manifest: Path, voice_root: Path, parser_root: Path, dll: Path) -> dict:
    parser_hash, ddi_hash, sequences = load_sequences(
        manifest, voice_root, parser_root)
    series: list[tuple[list[float], list[float], list[float]]] = []
    for index, sequence in enumerate(sequences):
        compare_one(dll, index, sequence, series)
    if len(series) != 3:
        raise AssertionError("expected three independent VQM sequences")

    gaussian = [math.exp(-0.5 * (offset / 1.1) ** 2)
                for offset in range(-4, 5)]
    candidates = {
        "identity": [1],
        "three_frame_mean": [1, 1, 1],
        "five_frame_mean": [1, 1, 1, 1, 1],
        "five_frame_binomial": [1, 4, 6, 4, 1],
        "five_frame_1_3_4_3_1": [1, 3, 4, 3, 1],
        "nine_frame_gaussian_sigma_1_1": gaussian,
    }
    controls = []
    for name, weights in candidates.items():
        for source_name, source_index in (("drs_pitch", 0),
                                          ("saved_pitch", 2)):
            per_sample = [errors(item[source_index], item[1], weights)
                          for item in series]
            controls.append({"kernel": name, "source": source_name,
                             "per_sample": per_sample})

    domain_controls = []
    for domain in ("feature", "pre_log_ratio"):
        domain_controls.append({
            "domain": domain,
            "kernel": "five_frame_1_3_4_3_1",
            "per_sample": [errors(item[0], item[1],
                                  candidates["five_frame_1_3_4_3_1"], domain)
                           for item in series],
        })

    held_out_checks = []
    for held_out in range(len(series)):
        weights = fit_symmetric_five(series, held_out)
        source, saved, _ = series[held_out]
        held_out_checks.append({
            "held_out_sample_index": held_out,
            "trained_on_sample_indices": [index for index in range(len(series))
                                          if index != held_out],
            "symmetric_weights_left_to_right": weights,
            "held_out_error": errors(source, saved, weights),
            "held_out_identity_error": errors(source, saved, [1]),
        })
    if not all(check["held_out_error"]["mean_absolute_error"]
               < check["held_out_identity_error"]["mean_absolute_error"]
               for check in held_out_checks):
        raise AssertionError("temporal relationship did not transfer")
    return {
        "dse_sha256": SHA256,
        "parser_sha256": parser_hash,
        "ddi_sha256": ddi_hash,
        "common_interior_margin_frames": MARGIN,
        "fixed_kernel_controls": controls,
        "smoothing_domain_controls": domain_controls,
        "leave_one_sequence_out_fits": held_out_checks,
        "scope": "Three independent sequences from one V4X bank; cross-validation bounds a temporal relationship, not the authoring filter or an exact saved-value formula.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--voice-root", type=Path, required=True)
    parser.add_argument("--ddb-tools", type=Path, required=True)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = run(args.manifest, args.voice_root, args.ddb_tools, args.dll)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "held_out_mean_absolute_errors": [
            row["held_out_error"]["mean_absolute_error"]
            for row in report["leave_one_sequence_out_fits"]],
        "identity_mean_absolute_errors": [
            row["held_out_identity_error"]["mean_absolute_error"]
            for row in report["leave_one_sequence_out_fits"]],
    }))


if __name__ == "__main__":
    main()
