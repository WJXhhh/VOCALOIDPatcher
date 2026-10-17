"""Compare owned VQM energy values through a native continuous output event.

The temporary PCM/FRM2 source lives only at the explicitly provided sibling
research path. The report contains hashes and counters, never sample payloads.
"""

from __future__ import annotations

import argparse
import ctypes as c
from dataclasses import replace
import hashlib
import json
import math
from pathlib import Path
import struct
import subprocess
import sys

from audit_frm2_numeric import B, D, F, I, P, Q, SHA256, f32
from probe_frm2 import (UNVOICED_MASK, UnvoicedFrame, VqmFeatureBlock,
                        VqmFrame, parse_frame, serialize_frame)
from region_commit_material import connected
from verify_region_breath_commit import probe_full_loop
from verify_region_event_production import run as run_events
from verify_region_stationary_consumption import full_marker
from verify_vqm_feature_dse5_roundtrip import owned_frame


FRAME_COUNT = 96
PCM_COUNT = 32768
ENERGIES = (0.015, 0.03, 0.12)


def feature_payload_offset(bit: int) -> int:
    if 0 <= bit <= 9:
        return bit * 4
    if 14 <= bit <= 19:
        return 520 + (bit - 14) * 4
    raise ValueError("probe supports scalar VQM feature bits only")


def feature_object_offset(bit: int) -> int:
    if 0 <= bit <= 9:
        return 4 + bit * 4
    if bit == 14:
        return 0x20c
    if 15 <= bit <= 19:
        return 0x214 + (bit - 15) * 4
    raise ValueError("probe supports scalar VQM feature bits only")


def owned_source(path: Path, energy: float, feature_bit: int | None = None,
                 feature_value: float | None = None,
                 outer_bit18: int = 10, outer_bit19: int = 2049,
                 ordinary_harmonic_capacity: int = 0,
                 ordinary_pitch_hz: float | None = None
                 ) -> dict[str, list[int]]:
    if not path.parent.is_dir() or path.exists():
        raise ValueError("scratch parent must exist and source file must be absent")
    base = parse_frame(owned_frame(outer_bit18, outer_bit19))
    if not isinstance(base, VqmFrame):
        raise AssertionError("owned VQM fixture changed")
    feature_payload = bytearray(base.feature_block.payload)
    struct.pack_into("<f", feature_payload, 8, energy)
    if feature_bit is not None:
        if feature_value is None:
            raise ValueError("feature value is required with feature bit")
        struct.pack_into("<f", feature_payload,
                         feature_payload_offset(feature_bit), feature_value)
    feature = VqmFeatureBlock(base.feature_block.flags, bytes(feature_payload))
    pcm = [int(round(8000 * math.sin(math.tau * 440 * i / 44100)))
           for i in range(PCM_COUNT)]
    blob = bytearray(struct.pack(f"<{PCM_COUNT}h", *pcm))
    offsets = {}
    for label in ("onset", "onset_companion", "stationary", "release"):
        offsets[label] = []
        for index in range(FRAME_COUNT):
            offsets[label].append(len(blob))
            if label == "onset":
                frame = replace(base, time_seconds=float(index),
                                feature_block=feature)
            else:
                frame = full_marker(index, (ordinary_pitch_hz
                                            if ordinary_pitch_hz is not None
                                            else float(220 + index)))
                if ordinary_harmonic_capacity:
                    extra = ordinary_harmonic_capacity - len(frame.frequencies_hz)
                    if extra < 0:
                        raise ValueError("ordinary harmonic capacity is too small")
                    frame = replace(
                        frame,
                        frequencies_hz=frame.frequencies_hz + (0.0,) * extra,
                        amplitudes=frame.amplitudes + (-100.0,) * extra,
                        phases_radians=frame.phases_radians + (0.0,) * extra)
                frame = replace(frame, harmonic_envelope=replace(
                    frame.harmonic_envelope,
                    values=(0., 0., 1. / 3., 0., 2. / 3., 0., 1., 0.)))
                if not frame.secondary_resonances:
                    frame = replace(frame, secondary_resonances=(
                        (1200. + index, .5 + index * .0625, 80. + index),))
            raw = serialize_frame(frame)
            if label == "onset" and feature_bit is not None:
                parsed = parse_frame(raw)
                if not isinstance(parsed, VqmFrame):
                    raise AssertionError("owned onset frame changed type")
                actual = struct.unpack_from("<f", parsed.feature_block.payload,
                                            feature_payload_offset(feature_bit))[0]
                if actual != f32(feature_value):
                    raise AssertionError("owned source feature mutation was not serialized")
            blob.extend(raw)
    with path.open("xb") as output:
        output.write(blob)
    return offsets


def run_one(dll: Path, scratch: Path, energy: float,
            companion: bool = True, feature_bit: int | None = None,
            feature_value: float | None = None,
            outer_bit18: int = 10, outer_bit19: int = 2049,
            auxiliary_setup=None, ordinary_onset: bool = False,
            ordinary_harmonic_capacity: int = 0,
            ordinary_pitch_hz: float | None = None) -> dict:
    offsets = owned_source(scratch, energy, feature_bit, feature_value,
                           outer_bit18, outer_bit19,
                           ordinary_harmonic_capacity,
                           ordinary_pitch_hz)
    crt = c.CDLL("ucrtbase.dll")
    crt._wfopen.argtypes, crt._wfopen.restype = [c.c_wchar_p, c.c_wchar_p], P
    crt.fclose.argtypes, crt.fclose.restype = [P], I
    handle = 0
    try:
        handle = crt._wfopen(str(scratch.resolve()), "rb")
        if not handle:
            raise OSError("cannot open owned source")
        selected = []
        groups = [(0, 50, 5, 45), (50, FRAME_COUNT, 55, 90)]

        def layer(label: str, stationary: bool = False) -> dict:
            pitch = (-200. if label == "onset" else
                     200. if label == "onset_companion" else 0.)
            return dict(pitch1=pitch, pitch2=0., dynamics=.6,
                        frames=FRAME_COUNT,
                        groups=([(0, FRAME_COUNT, 0, FRAME_COUNT)]
                                if stationary else groups),
                        frame_offsets=offsets[({"onset": "onset_companion",
                                                "onset_companion": "onset"}.get(label, label)
                                               if ordinary_onset else label)],
                        snd=0, core=4096,
                        pcm_count=FRAME_COUNT * 256 + 2048)

        def build(native, state, context, voice, table, sta, art):
            onset_layers = [layer("onset")]
            if companion:
                onset_layers.append(layer("onset_companion"))
            material = connected(
                native, state, context, voice, table, sta, art,
                edges=(("Sil", "a"), ("a", "Sil")),
                edge_layers={("Sil", "a"): onset_layers,
                             ("a", "Sil"): [layer("release")]},
                stationary_layers=[layer("stationary", stationary=True)])
            stream = native.alloc(0x80)
            P.from_address(stream + 8).value = handle
            I.from_address(stream + 0x2c).value = 0x32534D53
            P.from_address(voice + 0x1a0).value = stream

            def inspect(n, _state, ring, items, _material):
                active_slot = 1 - I.from_address(_state + 0x7a8).value
                active_pitch = F.from_address(
                    _state + 0x58 + active_slot * 0x3a8 + 8).value
                if (selected or B.from_address(ring + 0x78).value != 0 or
                        active_pitch != 0.0):
                    return {"selected": False}
                count = I.from_address(ring + 0x34).value
                if count != 3:
                    raise AssertionError("unexpected owned event work count")
                onset = items + 0x140
                begin = I.from_address(onset).value
                end = I.from_address(onset + 4).value
                target = end - 1
                if not 0 <= begin <= target < 1024:
                    raise AssertionError("invalid owned onset range")
                observations = []
                output_blocks = []

                def observe_output(time, block):
                    if time < begin:
                        return
                    samples = struct.unpack("<256h", block)
                    output_blocks.append({
                        "time": time,
                        "sha256": hashlib.sha256(block).hexdigest(),
                        "peak_abs": max(abs(sample) for sample in samples),
                        "clipped_samples": sum(sample in (-32768, 32767)
                                               for sample in samples),
                        "sum_squares": sum(sample * sample for sample in samples),
                    })

                def spectrum(frame):
                    if not frame:
                        return None
                    bins = I.from_address(frame + 0x1a8).value
                    real = P.from_address(frame + 0x1b0).value
                    imag = P.from_address(frame + 0x1b8).value
                    if not 0 < bins <= 4096 or not real or not imag:
                        return None
                    return hashlib.sha256(c.string_at(real, bins * 4) +
                                          c.string_at(imag, bins * 4)).hexdigest()

                def observe(time, ctx):
                    if time < begin:
                        return
                    frame = P.from_address(ctx + 0x247b8).value
                    feature_ptr = (P.from_address(frame + 0x208).value
                                   if frame else 0)
                    observations.append({
                        "time": time,
                        "feature_energy": (F.from_address(feature_ptr + 0xc).value
                                           if feature_ptr else None),
                        "candidate_spectrum_bins": (
                            I.from_address(frame + 0x1a8).value if frame else 0),
                        "candidate_spectrum_sha256": spectrum(frame),
                        "second_candidate_spectrum_sha256": spectrum(
                            P.from_address(ctx + 0x247c0).value),
                        "final_spectrum_sha256": spectrum(ctx + 0x2240),
                    })
                    if auxiliary_setup is not None:
                        observations[-1]["auxiliary_spectrum_sha256"] = spectrum(
                            P.from_address(ctx + 0x2b130).value)
                        observations[-1]["final_frame_has_harmonics"] = bool(
                            Q.from_address(ctx + 0x2240 + 0x150).value & 0x200)
                        observations[-1]["candidate_has_harmonics"] = bool(
                            Q.from_address(frame + 0x150).value & 0x200) if frame else False
                        curve = P.from_address(frame + 0x218).value if frame else 0
                        observations[-1]["candidate_phase_nodes"] = (
                            I.from_address(curve + 0x15c).value if curve else 0)
                        render_frame = P.from_address(ctx + 0x2238).value
                        observations[-1]["render_frame_harmonics"] = bool(
                            Q.from_address(render_frame + 0x150).value & 0x200)
                        observations[-1]["render_frame_pitch"] = (
                            F.from_address(render_frame + 0x1d0).value)
                        for label, candidate in (("final", ctx + 0x2240),
                                                 ("candidate", frame)):
                            frequency = (P.from_address(candidate + 0x160).value
                                         if candidate else 0)
                            observations[-1][label + "_first_harmonic_hz"] = (
                                F.from_address(frequency).value if frequency else None)
                        observations[-1]["auxiliary_object_count"] = (
                            ((P.from_address(ctx + 0x2af38).value or 0) -
                             (P.from_address(ctx + 0x2af30).value or 0)) // 8)
                        obj_begin = P.from_address(ctx + 0x2af30).value
                        if obj_begin:
                            states = []
                            for category in (2, 3):
                                obj = P.from_address(obj_begin + (category - 1) * 8).value
                                state = P.from_address(obj + 0x20).value if obj else 0
                                interpolated = P.from_address(state + 0x10).value if state else 0
                                harmonics = (I.from_address(interpolated + 0x158).value
                                             if interpolated else 0)
                                frequencies = (P.from_address(interpolated + 0x160).value
                                               if interpolated else 0)
                                amplitudes = (P.from_address(interpolated + 0x168).value
                                              if interpolated else 0)
                                phases = (P.from_address(interpolated + 0x170).value
                                          if interpolated else 0)
                                def array_hash(pointer):
                                    return (hashlib.sha256(c.string_at(
                                        pointer, harmonics * 4)).hexdigest()
                                        if 0 < harmonics <= 4096 and pointer else None)
                                def source_hash(offset, field):
                                    frame_ptr = state + offset if state else 0
                                    count = I.from_address(frame_ptr + 0x158).value if frame_ptr else 0
                                    pointer = P.from_address(frame_ptr + field).value if frame_ptr else 0
                                    return (hashlib.sha256(c.string_at(pointer, count * 4)).hexdigest()
                                            if 0 < count <= 4096 and pointer else None)
                                def nonzero_count(pointer, count):
                                    return (sum(F.from_address(pointer + i * 4).value != 0
                                                for i in range(count))
                                            if pointer and 0 <= count <= 4096 else None)
                                states.append({
                                    "category": category,
                                    "interpolated": bool(interpolated),
                                    "harmonics": harmonics,
                                    "event_gain": F.from_address(state).value if state else None,
                                    "selected_sample": I.from_address(state + 0x5c).value if state else None,
                                    "active_frequency_count": I.from_address(state + 0x58).value if state else None,
                                    "source_a_harmonics": I.from_address(state + 0xf0 + 0x158).value if state else None,
                                    "source_b_harmonics": I.from_address(state + 0x418 + 0x158).value if state else None,
                                    "source_a_mask": Q.from_address(state + 0xf0 + 0x150).value if state else None,
                                    "source_b_mask": Q.from_address(state + 0x418 + 0x150).value if state else None,
                                    "interpolated_mask": Q.from_address(interpolated + 0x150).value if interpolated else None,
                                    "source_a_amplitude_sha256": source_hash(0xf0, 0x168),
                                    "source_a_phase_sha256": source_hash(0xf0, 0x170),
                                    "source_b_amplitude_sha256": source_hash(0x418, 0x168),
                                    "source_b_phase_sha256": source_hash(0x418, 0x170),
                                    "source_a_phase_nonzero": nonzero_count(
                                        P.from_address(state + 0xf0 + 0x170).value,
                                        I.from_address(state + 0xf0 + 0x158).value) if state else None,
                                    "source_a_phase_first_active_nonzero": nonzero_count(
                                        P.from_address(state + 0xf0 + 0x170).value,
                                        harmonics) if state else None,
                                    "source_b_phase_nonzero": nonzero_count(
                                        P.from_address(state + 0x418 + 0x170).value,
                                        I.from_address(state + 0x418 + 0x158).value) if state else None,
                                    "interpolated_phase_nonzero": nonzero_count(phases, harmonics),
                                    "frequency_sha256": array_hash(frequencies),
                                    "amplitude_sha256": array_hash(amplitudes),
                                    "phase_sha256": array_hash(phases),
                                    "feature_present": bool(
                                        P.from_address(interpolated + 0x208).value)
                                    if interpolated else False,
                                    "outer18": (B.from_address(interpolated + 0x258).value
                                                if interpolated else None),
                                    "outer19": (I.from_address(interpolated + 0x25c).value
                                                if interpolated else None),
                                })
                            observations[-1]["auxiliary_states"] = states
                        observations[-1]["lookahead_gate"] = bool(n.fn(
                            0x175650, B, P, I)(ctx, time))
                        observations[-1]["producer_lookahead_state"] = bool(
                            B.from_address(ctx + 0x1fc0).value)
                        lookahead = time + math.floor(
                            F.from_address(n.lib._handle + 0x5bf09c).value /
                            D.from_address(ctx + 0x1d558).value)
                        records = P.from_address(ring).value
                        future = records + lookahead * 0x18
                        future_item = P.from_address(future).value
                        future_anchor = P.from_address(future + 0x10).value
                        observations[-1]["lookahead_record"] = {
                            "index": lookahead,
                            "logical_count": n.fn(0x18aba0, I, P)(ring),
                            "item": bool(future_item),
                            "anchor": bool(future_anchor),
                            "item_source_ids": ([I.from_address(future_item + offset).value
                                                 for offset in (0x10, 0x14)]
                                                if future_item else None),
                            "anchor_end_index": (I.from_address(future_anchor + 8).value
                                                 if future_anchor else None),
                            "anchor_following_source": (
                                I.from_address(future_anchor + 0x14).value
                                if future_anchor else None),
                        }
                        controller = n.fn(0x18adc0, F, P, I, I)
                        observations[-1]["auxiliary_controllers"] = [
                            controller(ring, code, time)
                            for code in (0xd, 0xe, 0x14, 0x15)]
                    if feature_bit is not None:
                        observations[-1]["probed_feature_value"] = (
                            F.from_address(feature_ptr +
                                           feature_object_offset(feature_bit)).value
                            if feature_ptr else None)
                    if (outer_bit18, outer_bit19) != (10, 2049):
                        observations[-1]["probed_outer_bit18"] = (
                            B.from_address(frame + 0x258).value if frame else None)
                        observations[-1]["probed_outer_bit19"] = (
                            I.from_address(frame + 0x25c).value if frame else None)

                if auxiliary_setup is None:
                    result = probe_full_loop(
                        n, context, ring, target, replay_from_start=True,
                        analysis_mode=2, initialize_output_gate=True,
                        initialize_native_candidate=True,
                        source_event_mode=0, compact_full_loop_trace=True,
                        pcm_observer=observe,
                        output_block_observer=observe_output)
                else:
                    with auxiliary_setup(n) as prepare:
                        result = probe_full_loop(
                            n, context, ring, target, replay_from_start=True,
                            analysis_mode=2, initialize_output_gate=True,
                            initialize_native_candidate=True,
                            source_event_mode=0, compact_full_loop_trace=True,
                            pcm_observer=observe,
                            output_block_observer=observe_output,
                            engine_prepare=prepare)
                summary = {
                    "selected": True, "onset_begin": begin,
                    "onset_end": end, "target_time": target,
                    "selected_onset_part_pitch": F.from_address(
                        P.from_address(onset + 0x20).value + 0x160).value,
                    "blocks_run": result["blocks_run"],
                    "nonzero_blocks": result["nonzero_blocks"],
                    "analysis_nonzero_blocks": result["analysis_nonzero_blocks"],
                    "output_stream_sha256": result["output_stream_sha256"],
                    "output_blocks": output_blocks,
                    "observations": observations,
                }
                selected.append(summary)
                return summary

            material["verify_consumption"] = inspect
            return material

        events = run_events(dll, commit=True, unknown_token=False,
                            no_material=True, material_builder=build,
                            token_sequences=[["a"]], duration_values=(125,),
                            material_modes=(0,))
        if len(selected) != 1:
            raise AssertionError("continuous output case not selected")
        summary = {"saved_energy": f32(energy),
                "has_companion_layer": companion,
                "native_event_cases": events["cases"], **selected[0]}
        if feature_bit is not None:
            summary["probed_feature_bit"] = feature_bit
            summary["probed_feature_value"] = f32(feature_value)
        if (outer_bit18, outer_bit19) != (10, 2049):
            summary["probed_outer"] = [outer_bit18, outer_bit19]
        return summary
    finally:
        try:
            if handle and crt.fclose(handle) != 0:
                raise OSError("cannot close owned source")
        finally:
            scratch.unlink(missing_ok=True)


def run(dll: Path, scratch: Path) -> dict:
    cases = []
    for energy, companion in ([(energy, True) for energy in
                               ENERGIES + (ENERGIES[0],)] +
                              [(ENERGIES[0], False), (ENERGIES[-1], False)]):
        command = [sys.executable, "-u", str(Path(__file__).resolve()),
                   "--dll", str(dll), "--scratch-source", str(scratch),
                   "--single-energy", str(energy)]
        if not companion:
            command.append("--no-companion")
        child = subprocess.run(command, capture_output=True, text=True,
                               check=False, timeout=120)
        if child.returncode:
            raise RuntimeError(f"owned native event failed for {energy}: "
                               f"{child.stderr[-1300:]}")
        cases.append(json.loads(child.stdout))
    if cases[3] != cases[0]:
        raise AssertionError("repeat event output changed")
    if cases[4]["output_stream_sha256"] != cases[5]["output_stream_sha256"]:
        raise AssertionError("single-layer control changed final output")
    if cases[0]["output_stream_sha256"] == cases[1]["output_stream_sha256"]:
        raise AssertionError("two-layer VQM energy did not reach final output")
    def different(first, second, rows, field):
        left, right = cases[first][rows], cases[second][rows]
        if len(left) != len(right):
            raise AssertionError("inconsistent output block count")
        return sum(a[field] != b[field] for a, b in zip(left, right))

    comparisons = {
        "two_layer_low_vs_high": {
            "saved_feature_blocks_changed": different(0, 2, "observations", "feature_energy"),
            "candidate_spectrum_blocks_changed": different(
                0, 2, "observations", "candidate_spectrum_sha256"),
            "final_spectrum_blocks_changed": different(
                0, 2, "observations", "final_spectrum_sha256"),
            "output_pcm_blocks_changed": different(0, 2, "output_blocks", "sha256"),
        },
        "single_layer_low_vs_high": {
            "saved_feature_blocks_changed": different(4, 5, "observations", "feature_energy"),
            "candidate_spectrum_blocks_changed": different(
                4, 5, "observations", "candidate_spectrum_sha256"),
            "final_spectrum_blocks_changed": different(
                4, 5, "observations", "final_spectrum_sha256"),
            "output_pcm_blocks_changed": different(4, 5, "output_blocks", "sha256"),
        },
    }
    if comparisons["two_layer_low_vs_high"]["output_pcm_blocks_changed"] != 56:
        raise AssertionError("expected all active output blocks to change")
    if any(block["clipped_samples"] for case in cases
           for block in case["output_blocks"]):
        raise AssertionError("clipped PCM invalidates energy comparison")
    return {"dse_sha256": SHA256, "comparisons": comparisons,
            "cases": cases,
            "scope": "Owned VQM and ordinary frames in one native continuous output "
                     "event; no installed voicebank PCM or Editor host."}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--scratch-source", type=Path, required=True)
    parser.add_argument("--single-energy", type=float)
    parser.add_argument("--no-companion", action="store_true")
    parser.add_argument("--feature-bit", type=int)
    parser.add_argument("--feature-value", type=float)
    parser.add_argument("--outer-bit18", type=int, default=10)
    parser.add_argument("--outer-bit19", type=int, default=2049)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if (args.feature_bit is None) != (args.feature_value is None):
        parser.error("--feature-bit and --feature-value must be paired")
    result = (run_one(args.dll, args.scratch_source, args.single_energy,
                      not args.no_companion, args.feature_bit,
                      args.feature_value, args.outer_bit18, args.outer_bit19)
              if args.single_energy is not None else
              run(args.dll, args.scratch_source))
    if args.output:
        args.output.write_text(json.dumps(result, indent=2) + "\n",
                               encoding="utf-8")
    else:
        print(json.dumps(result))


if __name__ == "__main__":
    main()
