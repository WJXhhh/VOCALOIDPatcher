"""Research only: preserve s frequency evolution while compressing its time axis.

Produces native-IFFT contribution packets. No target WAV is modified. Equal
power averaging (not summation) and unchanged native gain are deliberate.
"""
import argparse
import json
from pathlib import Path

import numpy as np

from analyze_explicit_borrow import read_wave


def microprocess(source, length, window_size, activity=False):
    hop = 8
    window = np.sqrt(np.hanning(window_size + 1)[:-1])
    padded = np.pad(source, (window_size // 2, window_size // 2))
    centres = np.arange(0, len(source) + 1, hop)
    spectra = np.array([np.fft.rfft(padded[c:c + window_size] * window) for c in centres])
    advance = 2 * np.pi * np.arange(window_size // 2 + 1) * hop / window_size
    phase = np.angle(spectra)
    instantaneous = np.angle(np.exp(1j * (np.diff(phase, axis=0) - advance)))
    output_centres = np.arange(0, length + 1, hop)
    if activity:
        energy = np.sum(np.abs(spectra) ** 2, axis=1)
        importance = 0.1 + 0.9 * np.sqrt(energy / max(float(energy.max()), 1e-20))
        cumulative = np.concatenate(([0.], np.cumsum(importance)))
        boundaries = np.interp(np.linspace(0, cumulative[-1], len(output_centres) + 1), cumulative, np.arange(len(spectra) + 1))
    else:
        boundaries = np.linspace(0, len(spectra), len(output_centres) + 1)
    output = np.zeros(length + window_size)
    denominator = np.zeros_like(output)
    accumulated_phase = phase[0].copy()
    cells = []
    for index, centre in enumerate(output_centres):
        # Each source analysis cell belongs to exactly one ordered output cell,
        # with fractional area shared at cell boundaries. No endpoint skipping.
        left, right = boundaries[index:index + 2]
        slots = np.arange(int(np.floor(left)), int(np.ceil(right)))
        weights = np.minimum(right, slots + 1) - np.maximum(left, slots)
        weights /= right - left
        power = np.sum(np.abs(spectra[slots]) ** 2 * weights[:, None], axis=0)
        if index:
            delta = np.sum(instantaneous[np.minimum(slots, len(instantaneous) - 1)] * weights[:, None], axis=0)
            accumulated_phase += advance + delta
        values = np.fft.irfft(np.sqrt(power) * np.exp(1j * accumulated_phase), n=window_size)
        output[centre:centre + window_size] += values * window
        denominator[centre:centre + window_size] += window ** 2
        cells.append({"output_centre": int(centre), "first_source_cell": int(slots[0]),
                      "last_source_cell": int(slots[-1]), "weight_sum": float(weights.sum())})
    output /= np.maximum(denominator, 1e-12)
    output = output[window_size // 2:window_size // 2 + length]
    # Keep onset/release at zero for a well-defined compact native window.
    edge = 32
    output[:edge] *= np.sin(np.linspace(0, np.pi / 2, edge)) ** 2
    output[-edge:] *= np.sin(np.linspace(np.pi / 2, 0, edge)) ** 2
    return output, cells, len(spectra)


def packetize(signal, geometry, gains, inverse_scale):
    hop, fft = geometry["hop"], geometry["fft"]
    assert (hop, fft, geometry["mode"], geometry["overlapMode"]) == (256, 2048, 50, 50)
    assert len(signal) in (512, 768)
    window = np.array(geometry["window"])
    blocks = np.zeros((2, fft))
    # The native IFFT emits the trailing half first, then the leading half one
    # hop later. Solve the contribution weights before its original OLA.
    for i, desired in enumerate(signal):
        options = []
        for frame in range(2):
            local = i - frame * hop
            if 0 <= local < hop:
                options.append((frame, fft - hop + local, gains[frame] * window[hop + local]))
            elif hop <= local < 2 * hop:
                options.append((frame, local - hop, gains[frame] * window[local - hop]))
        norm = sum(coefficient ** 2 for _, _, coefficient in options)
        if norm > 1e-12:
            for frame, offset, coefficient in options:
                blocks[frame, offset] = desired * coefficient / norm
        else:
            assert abs(desired) < 1e-6
    packets = []
    for index, block in enumerate(blocks):
        spectrum = np.fft.rfft(block) / (fft * inverse_scale)
        packets.append({"Frame": 3341 + index, "Real": spectrum.real.astype(np.float32).tolist(),
                        "Imag": spectrum.imag.astype(np.float32).tolist(), "Gain": gains[index]})
    return packets


def prepare(root, source_root):
    geometry = json.loads((root / "old/native-synthesis-geometry.json").read_text())
    inverse_scale = json.loads((root / "old/inverse-scaling.json").read_text())["scale"]
    packets, report = {}, []
    carrier = [json.loads((source_root / ("baseline-synth-" + str(frame) + ".json")).read_text()) for frame in (3341, 3342)]
    packets["7-grain-layer-zero"] = carrier
    for vowel in ("7", "a"):
        identity = [json.loads((root / "old" / (vowel + "-mid-synth-" + str(frame) + ".json")).read_text()) for frame in (3341, 3342)]
        packets[vowel + "-grain-identity"] = identity
        packets[vowel + "-grain-zero"] = [dict(p, Real=[0.] * len(p["Real"]), Imag=[0.] * len(p["Imag"])) for p in identity]
        gains = [p["Gain"] for p in identity]
        source_path = source_root
        metadata = next(r for r in json.loads((source_path / "explicit-borrow.json").read_text())
                        if r["kind"] == "complete-source" and r["Vowel"] == vowel)
        complete = read_wave(source_path / ("complete-source-" + vowel + "-reference.wav")) - read_wave(source_path / ("complete-source-" + vowel + "-muted.wav"))
        source = complete[metadata["supportFirstSample"]:metadata["supportLastSample"]]
        assert len(source) == (metadata["split"] - metadata["begin"] + 1) * 256
        for size, length in ((64, 512), (128, 512), (256, 512), (128, 768)):
            signal, cells, source_cells = microprocess(source, length, size)
            name = f"{vowel}-grain-stft{size}-n{length}"
            packets[name] = packetize(signal, geometry, gains, inverse_scale)
            np.save(root / (name + "-expected.npy"), signal)
            covered = {i for c in cells for i in range(c["first_source_cell"], c["last_source_cell"] + 1)}
            assert covered == set(range(source_cells))
            report.append({"mode": name, "source_samples": len(source), "output_samples": length,
                           "window": size, "hop": 8, "source_stft_cells": source_cells, "output_cells": len(cells),
                           "all_source_cells_covered": True, "source_rms_lsb": float(np.sqrt(np.mean(source ** 2))),
                           "output_rms_lsb": float(np.sqrt(np.mean(signal ** 2))), "native_gain_unchanged": True,
                           "cells": cells})
            if (size, length) == (128, 512):
                reverse, _, _ = microprocess(source[::-1], length, size)
                reverse_name = f"{vowel}-grain-reverse128-n512"
                packets[reverse_name] = packetize(reverse, geometry, gains, inverse_scale)
                np.save(root / (reverse_name + "-expected.npy"), reverse)
                shaped, shaped_cells, _ = microprocess(source, length, size, activity=True)
                shaped_name = f"{vowel}-grain-activity128-n512"
                packets[shaped_name] = packetize(shaped, geometry, gains, inverse_scale)
                np.save(root / (shaped_name + "-expected.npy"), shaped)
                layer_name = f"{vowel}-grain-layer128-n512"
                layer_packets = packetize(shaped, geometry, gains, inverse_scale)
                for packet, original in zip(layer_packets, carrier):
                    assert packet["Gain"] == original["Gain"]
                    for key in ("Real", "Imag"):
                        packet[key] = (np.array(packet[key], dtype=np.float32) + np.array(original[key], dtype=np.float32)).tolist()
                packets[layer_name] = layer_packets
                np.save(root / (layer_name + "-expected.npy"), shaped)
                packets[layer_name + "-seam"] = layer_packets
                report.append({"mode": shaped_name, "source_samples": len(source), "output_samples": length,
                               "window": size, "hop": 8, "source_stft_cells": source_cells, "output_cells": len(shaped_cells),
                               "all_source_cells_covered": {i for c in shaped_cells for i in range(c["first_source_cell"], c["last_source_cell"] + 1)} == set(range(source_cells)),
                               "time_map": "activity-weighted, positive 0.1 floor", "source_rms_lsb": float(np.sqrt(np.mean(source ** 2))),
                               "output_rms_lsb": float(np.sqrt(np.mean(shaped ** 2))), "native_gain_unchanged": True, "cells": shaped_cells})
    (root / "micrograin-packets.json").write_text(json.dumps(packets, separators=(",", ":")))
    (root / "micrograin-preparation.json").write_text(json.dumps(report, indent=2))
    print(json.dumps([{k: v for k, v in r.items() if k != "cells"} for r in report], indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("source", type=Path)
    args = parser.parse_args()
    prepare(args.directory, args.source)
