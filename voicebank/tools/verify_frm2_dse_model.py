#!/usr/bin/env python3
"""Verify DSE lookup interpolation and model boundaries on self-generated data."""
from __future__ import annotations

import argparse
import json
from pathlib import Path

from audit_frm2_numeric import Native, SHA256, stats
from frm2_dse_model import DseModel


def verify(native):
    single_errors, relative_errors, model_errors, threshold_errors = [], [], [], []
    for center in (200, 333.3):
        for bandwidth in (0, 20, 25, 200.5, 1489, 1490, 1495, 1500, 1600):
            for amplitude in (-1000, -20, 0, 10):
                model = DseModel(native, (-20, 60, -.0002), [(center, amplitude, bandwidth)], [])
                resonance = model.resonances[0]
                for offset in (-10000, -8000, -7995, -7994, -7.5, -2.5, 0, 2.5, 7.5, 7994, 7995, 8000, 10000):
                    hz = center + offset
                    actual = model.single(resonance, hz)
                    expected = model.independent_resonance(resonance, hz)
                    single_errors.append(abs(actual - expected))
                    relative_errors.append(abs(actual - expected) / max(1, abs(actual)))
                    if hz >= 0:
                        actual_model = model.amplitude(hz)
                        model_errors.append(abs(actual_model - model.independent_amplitude(hz)))
                        threshold_errors.append(abs(actual_model - model.amplitude(hz, 0)))
                native.keep.clear()
    result = {"single_lookup_absolute": stats(single_errors), "single_lookup_relative": stats(relative_errors),
              "model": stats(model_errors), "threshold_amplitude": stats(threshold_errors)}
    if (result["single_lookup_relative"]["max"] >= 1e-12 or result["model"]["max"] >= 1e-4
            or result["threshold_amplitude"]["max"] >= 1e-4):
        raise AssertionError(result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = {"dll_sha256": SHA256, "scope": "Self-generated set-0 model; independent interpolation of DLL-owned cells",
              **verify(Native(args.dll))}
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
