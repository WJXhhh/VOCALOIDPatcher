"""Validate native ei-context borrowing and seam traces, without grading sound."""
import argparse
import json
from pathlib import Path

import numpy as np

from analyze_explicit_borrow import read_wave, write_wave


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def analyze(root, seam_root, previous):
    old, new = root / "old-final", root / "new-final"
    assert read_json(old / "explicit-borrow.json")[0]["sdk"] == "6.13.1.1"
    assert read_json(new / "explicit-borrow.json")[0]["sdk"] == "6.13.3.1"
    rows = read_json(old / "ei-context.json")
    assert rows == read_json(new / "ei-context.json")
    files = sorted(old.glob("*.wav"))
    for path in files:
        assert np.array_equal(read_wave(path), read_wave(new / path.name)), path.name
    signals = {r["Name"]: read_wave(new / (r["Name"] + ".wav")) for r in rows}
    for name in ("baseline", "7-mid"):
        assert np.array_equal(signals[name], read_wave(previous / "new-final" / (name + ".wav")))
    summaries = []
    for row in rows:
        name = row["Name"]
        assert row["phones"] == "s ei" and row["bounds"] == [-25, -13, 477]
        assert row["scheduledFrameCount"] == 94
        if name != "warmup":
            assert row["scheduleUnchanged"] and row["originalSourceMapUnchanged"]
        assert not np.any(np.abs(signals[name]) >= 32767)
        for call in row["reads"]:
            assert call["First"] <= call["DonorSource"] <= call["Last"]
            assert (call["Side"] == "s" and 3341 <= call["OutputFrame"] < 3343 or
                    call["Side"] == "ei" and 3343 <= call["OutputFrame"] < 3388)
            if call["Side"] == "ei":
                assert call["DonorTo"] == "ei"
        if "-ei-" in name:
            assert {c["OutputFrame"] for c in row["reads"] if c["Side"] == "ei"} == set(range(3343, 3388))
        first, last = (3341 - 3087) * 256, (3343 - 3087) * 256
        delta = signals[name] - signals["7-mid"]
        active = np.flatnonzero(delta)
        summaries.append({"name": name, "donor_s": row["donorS"], "donor_ei": row["donorEi"],
                          "s_prefix_equal_old_borrow": bool(np.array_equal(signals[name][first:last], signals["7-mid"][first:last])),
                          "changed_samples_vs_old_borrow": int(len(active)),
                          "last_change_seconds": float(active[-1] / 44100) if len(active) else None,
                          "s_prefix_rms_lsb": float(np.sqrt(np.mean(signals[name][first:last] ** 2))),
                          "human_pronunciation_accepted": False})
    assert np.array_equal(signals["baseline"], signals["restore"])
    assert np.array_equal(signals["7-mid-ei-ts"], signals["7-mid-ei-ts-repeat"])

    seam_old, seam_new = seam_root / "old-final2", seam_root / "new-final"
    seam_rows = read_json(seam_old / "seam-audit.json")
    assert seam_rows == read_json(seam_new / "seam-audit.json")
    for path in seam_old.glob("*.wav"):
        assert np.array_equal(read_wave(path), read_wave(seam_new / path.name)), path.name
    observed = []
    for mode in ("baseline", "7-mid", "a-mid", "restore"):
        calls = read_json(seam_old / ("s-ei-" + mode + "-phase.json"))
        assert calls == read_json(seam_new / ("s-ei-" + mode + "-phase.json"))
        entry = [c for c in calls if c["Join"] == 3343]
        release = [c for c in calls if c["Join"] == 3388]
        assert len(entry) == 23 and all(c["Function"] == "spectral" and not c["Succeeded"] for c in entry)
        assert all(c["CurrentIdentity"] == 60 and c["From"] == 60 and c["To"] == 4 for c in entry)
        assert all(c["PhaseBefore"] == c["PhaseAfter"] and c["GainBeforeBits"] == c["GainAfterBits"] for c in entry)
        assert all(not (c["Left"]["Flags"] & 512) and not (c["Right"]["Flags"] & 512) for c in entry)
        assert any(c["Succeeded"] for c in release), "Missing active join positive control."
        row = next(r for r in seam_rows if r["phones"] == "s ei" and r["mode"] == mode)
        assert row["PrepareCalls"] > 3000 and row["OuterCalls"] == 0
        observed.append({"mode": mode, "entry_calls": len(entry), "entry_return_false": True,
                         "observed_entry_parameters_and_gain_unchanged": True,
                         "entry_identity": "Sil->ei", "release_positive_control_calls": sum(c["Succeeded"] for c in release),
                         "separate_phase_consumer_observed_calls": row["OuterCalls"]})
    names = ["7-mid", "7-mid-ei-ts", "7-mid-ei-sr", "continuous-tail-ts"]
    clips = [signals[name][round(1.3 * 44100):round(2.2 * 44100)] for name in names]
    gain = min(16., .9 * 32767 / max(float(np.max(np.abs(c))) for c in clips))
    listening = root / "listening"
    listening.mkdir(exist_ok=True)
    for name, clip in zip(names, clips):
        write_wave(listening / (name + ".wav"), clip * gain)
    write_wave(listening / "old-ei-ts-ei-retroflex-ei-continuous-ts.wav", np.concatenate([
        part for clip in clips for part in (clip * gain, np.zeros(round(.45 * 44100)))]))
    report = {"date": "2026-10-18", "sdk_versions": ["6.13.1.1", "6.13.3.1"],
              "native_context_target_renders_per_sdk": len(rows), "native_context_donor_renders_per_sdk": 5,
              "native_context_cross_sdk_equal_wavs": len(files),
              "readonly_seam_target_renders_per_sdk": len(seam_rows), "readonly_seam_donor_renders_per_sdk": 2,
              "preserved_target_phonemes": "s ei", "preserved_ticks": [-25, -13, 477],
              "seam_observations": observed, "cases": summaries,
              "listening_order": names, "common_playback_gain": gain,
              "new_human_listening_accepted": False,
              "limitations": ["The alternative consonant contexts may color the ei entry.",
                  "Continuous ts/fricative-tail control is not proof of an s pronunciation.",
                  "No perceptual success follows from PCM changes or preserved score labels.",
                  "No formal backend, concurrent routing, project data or UI integration in this probe."],
              "frontend": False, "deployed": False}
    (root / "analysis.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in report.items() if k != "cases"}, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("seam_directory", type=Path)
    parser.add_argument("previous", type=Path)
    args = parser.parse_args()
    analyze(args.directory, args.seam_directory, args.previous)
