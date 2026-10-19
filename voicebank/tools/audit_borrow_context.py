"""Audit the exact played s samples and prepare contextual listening controls.

Offline diagnostics only. Longer-s and equal-energy controls do not satisfy
the original fixed-timing/no-gain-change requirement and are not engine fixes.
"""
import argparse
import json
from pathlib import Path

import numpy as np

from analyze_explicit_borrow import read_wave, write_wave


def rms(signal):
    return float(np.sqrt(np.mean(signal ** 2)))


def audit(root, previous):
    report = json.loads((root / "analysis.json").read_text(encoding="utf-8"))
    gain = report["common_playback_gain"]
    directory = root / "listening"
    played_solo = read_wave(directory / "compact-s-only.wav")
    played_attached = read_wave(directory / "compact-s-with-ei.wav")
    played_gap = read_wave(directory / "compact-s-with-ei-after-80ms-gap.wav")
    pad = round(.15 * 44100)
    count = 512
    solo = played_solo[pad:pad + count]
    attached = played_attached[pad:pad + count]
    separated = played_gap[pad:pad + count]
    assert np.array_equal(solo, separated), "Played gap control lost/changed the s."
    assert np.max(np.abs(solo - attached)) <= gain + 1
    assert not np.any(played_gap[pad + count:pad + count + round(.08 * 44100)])

    # Verify the segments in the concatenated file the user actually heard,
    # rather than relying on separate named files or native trace hashes.
    combined = read_wave(directory / "duration-and-masking-diagnostic.wav")
    lengths = [5888, count, len(played_attached) - 2 * pad, len(played_gap) - 2 * pad]
    cursor, embedded = 0, []
    for length in lengths:
        embedded.append(combined[cursor + pad:cursor + pad + length])
        cursor += pad + length + round(.5 * 44100)
    assert cursor == len(combined)
    assert np.array_equal(embedded[1], solo)
    assert np.array_equal(embedded[2][:count], attached)
    assert np.array_equal(embedded[3][:count], solo)

    baseline = read_wave(previous / "new-final/baseline.wav")
    target = read_wave(previous / "new-final/7-grain-layer128-n512.wav")
    start = (3341 - 3087) * 256
    short_s = (target - baseline)[start:start + count]
    assert np.max(np.abs(baseline[start:start + count])) <= 1
    assert np.array_equal(solo, np.rint(short_s * gain))
    assert np.array_equal(attached, np.rint(target[start:start + count] * gain))
    row = next(r for r in json.loads((root / "old/source-audit.json").read_text(encoding="utf-8"))
               if r["vowel"] == "7" and r["pass"] == "reference")
    source_start, source_end = ((row[k] - 3087) * 256 for k in ("begin", "split"))
    long_s = (read_wave(root / "old/source-7-reference.wav") -
              read_wave(root / "old/source-7-left-muted.wav"))[source_start:source_end + 256]
    # Reuse exactly the original target vowel PCM, with its carrier tail. No
    # donor vowel, spectral conversion, filtering, crossfade or resynthesis.
    vowel = target[start + count:round(2.15 * 44100)]
    energy_factor = float(np.sqrt(np.dot(long_s, long_s) / np.dot(short_s, short_s)))
    cases = [("strict-short-s-with-ei", np.concatenate((short_s, vowel))),
             ("long-s-with-identical-ei-diagnostic", np.concatenate((long_s, vowel))),
             ("short-s-equal-energy-with-ei-diagnostic", np.concatenate((short_s * energy_factor, vowel)))]
    peak = max(float(np.max(np.abs(signal))) for _, signal in cases)
    common_gain = min(gain, .9 * 32767 / peak)
    assert common_gain == gain, "Unexpected diagnostic peak changes common playback gain."
    for name, signal in cases:
        write_wave(directory / (name + ".wav"), np.concatenate((np.zeros(pad), signal * gain, np.zeros(pad))))
    output = "context-duration-energy-diagnostic.wav"
    write_wave(directory / output, np.concatenate([
        part for _, signal in cases for part in (np.zeros(pad), signal * gain, np.zeros(round(.5 * 44100)))]))
    feedback_file = root / "context-listening-feedback.json"
    feedback = json.loads(feedback_file.read_text(encoding="utf-8-sig")) if feedback_file.exists() else "not evaluated"
    findings = {"reported_sequence": ["嘶", "嘶", "ei", "ei"],
                "human_listening": {"long_s_solo_audible": True, "compact_s_solo_audible": True,
                    "compact_s_in_ei_audible": False, "compact_s_80ms_before_ei_audible": False},
                "played_samples": {"compact_count": count,
                    "solo_and_80ms_gap_prefix_exactly_equal": True,
                    "solo_and_attached_prefix_max_delta_lsb": float(np.max(np.abs(solo - attached))),
                    "native_quiet_carrier_accounts_for_delta": True,
                    "concatenated_audio_all_four_segments_verified": True,
                    "solo_rms_lsb": rms(solo), "attached_prefix_rms_lsb": rms(attached),
                    "gap_prefix_rms_lsb": rms(separated)},
                "interpretation": "S remains in the combined PCM. It is not recognized in this context; neither engine erasure nor a single cause of perceptual masking follows from listening alone.",
                "new_diagnostics": {"order": [name for name, _ in cases], "output": output,
                    "identical_vowel_pcm_in_all_three": True, "common_playback_gain": gain,
                    "equal_energy_s_multiplier": energy_factor,
                    "equal_energy_s_boost_db": float(20 * np.log10(energy_factor)),
                    "long_s_changes_consonant_duration": True, "equal_energy_changes_s_gain": True,
                    "offline_only_not_a_fixed_timing_solution": True, "human_listening": feedback},
                "frontend": False, "deployed": False}
    (root / "context-audit.json").write_text(json.dumps(findings, indent=2), encoding="utf-8")
    print(json.dumps(findings, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("previous", type=Path)
    args = parser.parse_args()
    audit(args.directory, args.previous)
