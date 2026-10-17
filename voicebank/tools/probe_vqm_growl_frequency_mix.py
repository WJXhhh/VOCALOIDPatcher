"""Verify the native Growl event and frequency-domain mix with owned data."""

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

from audit_frm2_numeric import D, F, I, Native, P, Q, SHA256, f32
from probe_frm2 import (Envelope, MainFrame, MAIN_MASK, VqmFeatureBlock,
                        VqmFrame, VQM_NO_SPECTRUM_MASK, parse_frame,
                        serialize_frame)
from verify_vqm_feature_dse5_roundtrip import crt_functions, owned_frame, read_frame


def run(dll: Path, scratch: Path, seed_target_spectrum: bool,
        mix: bool, mix_gain: float, pcm_amplitude: float,
        native_target: bool = False, outer18: int = 10,
        outer19: int = 2049, feature2: float | None = None,
        feature17: float | None = None,
        feature18: float | None = None,
        vqm_spectrum: bool = False,
        vqm_spectrum_peak_db: float = 15.0,
        feature4: float | None = None,
        feature6: float | None = None,
        feature14: float | None = None,
        feature15: float | None = None,
        feature16: float | None = None,
        growl_buffer_fill: int | None = None,
        vqm_class: str = "growl") -> dict:
    if not 0 <= outer18 <= 255 or not 0 <= outer19 <= 0xffffffff:
        raise ValueError("VQM outer fields are outside their stored ranges")
    if vqm_class not in ("growl", "tense", "breathiness"):
        raise ValueError("unsupported VQM class")
    native = Native(dll)
    crt = crt_functions()
    fixture = parse_frame(owned_frame())
    if not isinstance(fixture, VqmFrame):
        raise AssertionError("owned fixture type changed")
    requested_features = {2: feature2, 17: feature17, 18: feature18}
    requested_features.update({bit: value for bit, value in (
        (4, feature4), (6, feature6), (14, feature14),
        (15, feature15), (16, feature16)) if value is not None})
    payload = bytearray(fixture.feature_block.payload)
    offsets = {}
    offset = 0
    for bit in range(20):
        if bit < 10 or bit >= 14:
            offsets[bit] = offset
            offset += 4
        else:
            offset += (40 if bit < 12 else 20) * 4
    for bit, value in requested_features.items():
        if value is not None:
            if not math.isfinite(value):
                raise ValueError("VQM feature variant must be finite")
            struct.pack_into("<f", payload, offsets[bit], value)
    fixture = replace(fixture, feature_block=VqmFeatureBlock(
        fixture.feature_block.flags, bytes(payload)))
    if vqm_spectrum:
        if not math.isfinite(vqm_spectrum_peak_db):
            raise ValueError("VQM spectrum peak must be finite")
        spectrum_bins = 2049
        fixture = replace(
            fixture,
            mask=VQM_NO_SPECTRUM_MASK | (1 << 4) | (1 << 5),
            spectrum_amplitudes=tuple(
                -45.0 + vqm_spectrum_peak_db *
                math.exp(-0.5 * ((index - 41) / 12) ** 2)
                for index in range(spectrum_bins)),
            spectrum_phases_radians=tuple(0.0 for _ in range(spectrum_bins)),
        )
    expected_features = {str(bit): struct.pack("<f", struct.unpack_from(
        "<f", payload, offsets[bit])[0]).hex() for bit in requested_features}
    flat_envelope = Envelope(0x01820002, (-20000.0, 20000.0, 0.0),
                             (0.0, 0.0, 1000.0, 0.0))
    frames = []
    native_read_spectrum_hashes = []
    for index in range(3):
        raw = serialize_frame(replace(fixture, time_seconds=0.25 + index*0.01,
                                      component_frequencies_hz=(440.0,),
                                      component_amplitudes=(-20.0,),
                                      f0_hz=440.0,
                                      bit18_value=outer18,
                                      bit19_value=outer19,
                                      bit7_envelope=flat_envelope,
                                      bit17_envelope=flat_envelope))
        path = scratch / f"owned_growl_audio_{index}.frm2"
        path.write_bytes(raw)
        try:
            frame, _ = read_frame(native, crt, path)
        finally:
            path.unlink(missing_ok=True)
        if ((I.from_address(frame + 0x258).value & 0xff) != outer18
                or I.from_address(frame + 0x25c).value != outer19):
            raise AssertionError("native VQM outer fields changed on read")
        expected_spectrum_count = 2049 if vqm_spectrum else 0
        if I.from_address(frame + 0x1a8).value != expected_spectrum_count:
            raise AssertionError("native VQM spectrum count changed on read")
        native_read_spectrum_hashes.append({
            "amplitude": hashlib.sha256(c.string_at(
                P.from_address(frame + 0x1b0).value,
                expected_spectrum_count * 4)).hexdigest()
            if expected_spectrum_count else None,
            "phase": hashlib.sha256(c.string_at(
                P.from_address(frame + 0x1b8).value,
                expected_spectrum_count * 4)).hexdigest()
            if expected_spectrum_count else None,
        })
        feature = P.from_address(frame + 0x208).value
        if not feature or I.from_address(feature).value != fixture.feature_block.flags:
            raise AssertionError("native VQM feature block changed on read")
        for bit, expected in expected_features.items():
            native_offset = offsets[int(bit)] + (8 if int(bit) >= 15 else 4)
            if c.string_at(feature + native_offset, 4).hex() != expected:
                raise AssertionError(f"native VQM internal bit {bit} changed on read")
        frames.append(frame)
    target = MainFrame(0, 0.26, MAIN_MASK, (440.0, 880.0),
                       (-20.0, -30.0), (0.0, 0.0), 440.0,
                       flat_envelope, ((440.0, -20.0, 0.0),),
                       (0.0, 0.0, 0.0), (),
                       Envelope(0x00960002, (-100.0, 10.0, 0.0),
                                (0.0, 0.0)))
    target_path = scratch / "owned_growl_audio_target.frm2"
    target_path.write_bytes(serialize_frame(target))
    try:
        handle = crt._wfopen(str(target_path.resolve()), "rb")
        if not handle:
            raise OSError("cannot open owned ordinary frame")
        working = native.alloc(0x328)
        native.fn(0x12a670, P, P)(working)
        stream = native.alloc(0x80)
        P.from_address(stream + 8).value = handle
        I.from_address(stream + 0x2c).value = 0x32534d53
        try:
            if native.fn(0x106e20, c.c_uint64, P, P)(working, stream) != 0:
                raise AssertionError("owned ordinary frame read failed")
        finally:
            crt.fclose(handle)
    finally:
        target_path.unlink(missing_ok=True)
    if seed_target_spectrum and not native_target:
        bins = 513
        native.fn(0x12ba70, Q, P, I, Q, I)(working, bins, 0x30, 0)
        if I.from_address(working + 0x1a8).value != bins:
            raise AssertionError("native target spectrum allocation failed")
        amplitude = P.from_address(working + 0x1b0).value
        phase = P.from_address(working + 0x1b8).value
        for index in range(bins):
            F.from_address(amplitude + index*4).value = -40.0
            F.from_address(phase + index*4).value = 0.0
    region = native.alloc(0x80)
    I.from_address(region + 0x18).value = len(frames)
    P.from_address(region + 0x20).value = native.array(P, frames)
    D.from_address(region + 0x38).value = 0.01
    pcm = [f32(pcm_amplitude*math.sin(math.tau*440*i/44100))
           for i in range(65536)]
    pcm_address = native.array(F, pcm)
    record = native.alloc(0x850)
    F.from_address(record).value = 0.25 if native_target else 0.0
    F.from_address(record + 4).value = 1.0
    F.from_address(record + 8).value = 440.0
    P.from_address(record + 0x800).value = region
    P.from_address(record + 0x808).value = pcm_address
    collection = native.alloc(0x40)
    P.from_address(collection + 0x28).value = record
    P.from_address(collection + 0x30).value = record + 0x850
    class_layout = {"growl": (0x120, 0x155e70, 0x158a60),
                    "tense": (0x840, 0x15f8c0, 0x15fd80),
                    "breathiness": (0x7e0, 0x1533c0, 0x153f50)}
    class_size, constructor_rva, mixer_rva = class_layout[vqm_class]
    growl = native.alloc(class_size)
    construct = native.fn(constructor_rva,
                          P, P, I, D, F, c.c_ubyte,
                          I, c.c_ubyte, c.c_ubyte, c.c_ubyte)
    growl_window = 4096 if native_target else 1024
    if construct(growl, growl_window, 0.01, 22050.0, 0, 1, 0, 0, 0) != growl:
        raise AssertionError("Growl constructor failed")
    if growl_buffer_fill is not None:
        if growl_buffer_fill not in (0, 165):
            raise ValueError("unsupported Growl buffer fill")
        for pointer_index in (0xd, 0xf, 0x11, 0x13, 0x15, 0x17, 0x19, 0x1b):
            pointer = P.from_address(growl + pointer_index * 8).value
            count = Q.from_address(growl + (pointer_index + 1) * 8).value
            if not pointer or count <= 0 or count > 0x20000:
                raise AssertionError("Growl constructor buffer layout changed")
            c.memset(pointer, growl_buffer_fill, count * 4)
    P.from_address(growl + 8).value = collection
    state = P.from_address(growl + 0x20).value
    analysis = P.from_address(growl + 0x18).value
    target_producer_count = None
    target_producer_nonzero = None
    if native_target:
        if not seed_target_spectrum:
            raise ValueError("native target spectrum requires allocation")
        target_growl = native.alloc(0x120)
        if construct(target_growl, 1024, 0.01, 22050.0, 0, 1, 0, 0, 0) != target_growl:
            raise AssertionError("native target analysis constructor failed")
        target_analysis = P.from_address(target_growl + 0x18).value
        pcm_count = I.from_address(target_analysis + 0x8c).value + 1
        target_pcm = native.array(c.c_short,
                                  [int(round(5000*math.sin(math.tau*440*i/44100)))
                                   for i in range(pcm_count)])
        produced = I(-1)
        workspace = P()
        producer = native.fn(0x1492b0, Q, P, P, c.c_uint, c.c_ubyte,
                             Q, P, P, P, c.c_ubyte, P, F, I)
        code = producer(target_analysis, target_pcm, pcm_count,
                        c.c_ubyte.from_address(target_analysis + 0x94).value,
                        0, None, None, c.byref(produced), 3,
                        c.byref(workspace), 2.0, 0)
        if code != 0 or produced.value != 2048 or workspace.value is None:
            raise AssertionError("native target spectrum producer failed")
        bins = produced.value + 1
        native.fn(0x12ba70, Q, P, I, Q, I)(working, bins, 0x30, 0)
        amplitude = P.from_address(working + 0x1b0).value
        phase = P.from_address(working + 0x1b8).value
        F.from_address(amplitude).value = F.from_address(workspace.value).value
        F.from_address(phase).value = 0.0
        for index in range(1, bins-1):
            F.from_address(amplitude + index*4).value = F.from_address(
                workspace.value + index*8).value
            F.from_address(phase + index*4).value = F.from_address(
                workspace.value + index*8+4).value
        F.from_address(amplitude + (bins-1)*4).value = F.from_address(
            workspace.value + 4).value
        F.from_address(phase + (bins-1)*4).value = 0.0
        target_producer_count = produced.value
        target_producer_nonzero = sum(
            F.from_address(workspace.value + index*4).value != 0
            for index in range(produced.value*2+2))
    output = native.array(F, [0.0] * 4096)
    wrapper = native.fn(0x158e40, None, P, P, F, F, I, P, F, P)
    wrapper(growl, working, 440.0, 0.0, 1, output, 1.0, None)
    interpolated = P.from_address(state + 0x10).value
    if not interpolated:
        raise AssertionError("Growl interpolated source frame is missing")
    source_spectrum_count = I.from_address(interpolated + 0x1a8).value
    source_amplitudes = P.from_address(interpolated + 0x1b0).value
    source_phases = P.from_address(interpolated + 0x1b8).value
    source_amplitude_sha256 = (
        hashlib.sha256(c.string_at(source_amplitudes,
                                   source_spectrum_count * 4)).hexdigest()
        if source_spectrum_count > 0 and source_amplitudes else None)
    source_phase_sha256 = (
        hashlib.sha256(c.string_at(source_phases,
                                   source_spectrum_count * 4)).hexdigest()
        if source_spectrum_count > 0 and source_phases else None)
    active = I.from_address(state + 0x58).value
    first = P.from_address(state + 0x28).value
    second = P.from_address(state + 0x38).value
    result = {"outer_bit18": outer18,
              "outer_bit19": outer19,
              "outer_fields_preserved_on_native_read": True,
              "vqm_spectrum_present": vqm_spectrum,
              "growl_buffer_fill": growl_buffer_fill,
              "vqm_spectrum_peak_db": (vqm_spectrum_peak_db
                                        if vqm_spectrum else None),
              "internal_feature_float32_hex": expected_features,
              "internal_features_preserved_on_native_read": True,
              "working_frame_primary_count": I.from_address(working + 0x158).value,
              "working_frame_1a8": I.from_address(working + 0x1a8).value,
              "interpolated_vqm_spectrum_count": source_spectrum_count,
              "interpolated_vqm_amplitude_sha256": source_amplitude_sha256,
              "interpolated_vqm_phase_sha256": source_phase_sha256,
              "native_read_spectrum_sha256": native_read_spectrum_hashes[0],
              "target_spectrum_seeded": seed_target_spectrum,
              "target_spectrum_source": ("native_pcm" if native_target else
                                         "constant_fixture" if seed_target_spectrum else
                                         "none"),
              "target_producer_half_count": target_producer_count,
              "target_producer_nonzero": target_producer_nonzero,
              "analysis_window_length": I.from_address(analysis + 0x8c).value,
              "analysis_window_mode": c.c_ubyte.from_address(analysis + 0x94).value,
              "active_count_minus_one": active,
              "advanced_time_seconds": D.from_address(state + 0x60).value,
              "event_output_nonzero": sum(F.from_address(output + i*4).value != 0
                                          for i in range(4096)),
              "first_nonzero": sum(F.from_address(first + i*4).value != 0
                                   for i in range(max(0, min(active+1, 16)))),
              "second_nonzero": sum(F.from_address(second + i*4).value != 0
                                    for i in range(max(0, min(active+1, 16))))}
    if mix:
        if not seed_target_spectrum:
            raise ValueError("mix probe requires seeded target spectrum")
        output_bins = I.from_address(working + 0x1a8).value
        out_real = native.array(F, [0.0] * output_bins)
        out_imag = native.array(F, [0.0] * output_bins)
        out_third = native.array(F, [1.0] * output_bins)
        third_ptr = native.array(P, [out_third])
        mixer = native.fn(mixer_rva, None, P, P, P, F, F, P, P)
        mixer(growl, out_real, out_imag, mix_gain, 440.0, working, third_ptr)
        result.update({
            "vqm_class": vqm_class,
            "mix_gain": mix_gain,
            "pcm_amplitude": pcm_amplitude,
            "mixed_buffer_bins": output_bins,
            "mixed_real_nonzero": sum(F.from_address(out_real + i*4).value != 0
                                      for i in range(output_bins)),
            "mixed_imag_nonzero": sum(F.from_address(out_imag + i*4).value != 0
                                      for i in range(output_bins)),
            "mixed_third_nonzero": sum(F.from_address(out_third + i*4).value != 0
                                       for i in range(output_bins)),
            "mixed_real_max_absolute": max(abs(F.from_address(out_real + i*4).value)
                                           for i in range(output_bins)),
            "mixed_imag_max_absolute": max(abs(F.from_address(out_imag + i*4).value)
                                           for i in range(output_bins)),
            "mixed_real_sha256": hashlib.sha256(
                c.string_at(out_real, output_bins * 4)).hexdigest(),
            "mixed_imag_sha256": hashlib.sha256(
                c.string_at(out_imag, output_bins * 4)).hexdigest(),
        })
        if native_target:
            inverse_length = 2 * (output_bins - 1)
            packed = native.array(F, [0.0] * (inverse_length + 2))
            F.from_address(packed).value = F.from_address(out_real).value
            F.from_address(packed + 4).value = F.from_address(
                out_real + (output_bins-1)*4).value
            for index in range(1, output_bins-1):
                F.from_address(packed + index*8).value = F.from_address(
                    out_real + index*4).value
                F.from_address(packed + index*8+4).value = F.from_address(
                    out_imag + index*4).value
            inverse = native.fn(0x14dea0, Q, P, P, I, I)
            code = inverse(target_analysis, packed, inverse_length, 1)
            samples = [F.from_address(packed + index*4).value
                       for index in range(inverse_length)]
            if code != 0 or not all(math.isfinite(value) for value in samples):
                raise AssertionError("native inverse FFT failed")
            real_bins = [F.from_address(out_real + index*4).value
                         for index in range(output_bins)]
            imag_bins = [F.from_address(out_imag + index*4).value
                         for index in range(output_bins)]
            probe_indices = (0, 1, 2, 5, 64, 512, 1024, 2047, 4095)
            replay_errors = {}
            for phase_sign in (-1, 1):
                errors = []
                for sample_index in probe_indices:
                    expected = (real_bins[0] +
                                real_bins[-1] * (-1 if sample_index & 1 else 1))
                    for bin_index in range(1, output_bins-1):
                        angle = math.tau * sample_index * bin_index / inverse_length
                        expected += 2*(real_bins[bin_index]*math.cos(angle)
                                       + phase_sign*imag_bins[bin_index]*math.sin(angle))
                    errors.append(abs(samples[sample_index]
                                      - expected/inverse_length))
                replay_errors[str(phase_sign)] = max(errors)
            result.update({
                "inverse_fft_length": inverse_length,
                "inverse_fft_nonzero": sum(value != 0 for value in samples),
                "inverse_fft_max_absolute": max(map(abs, samples)),
                "inverse_fft_sha256": hashlib.sha256(
                    c.string_at(packed, inverse_length * 4)).hexdigest(),
                "inverse_replay_max_errors": replay_errors,
            })
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", required=True, type=Path)
    parser.add_argument("--scratch", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--native-target", action="store_true")
    parser.add_argument("--single-case", choices=("native_positive",
                                                  "native_zero_gain"))
    parser.add_argument("--outer18", type=int, default=10)
    parser.add_argument("--outer19", type=int, default=2049)
    parser.add_argument("--feature2", type=float)
    parser.add_argument("--feature4", type=float)
    parser.add_argument("--feature6", type=float)
    parser.add_argument("--feature14", type=float)
    parser.add_argument("--feature15", type=float)
    parser.add_argument("--feature16", type=float)
    parser.add_argument("--feature17", type=float)
    parser.add_argument("--feature18", type=float)
    parser.add_argument("--vqm-spectrum", action="store_true")
    parser.add_argument("--vqm-spectrum-peak-db", type=float, default=15.0)
    parser.add_argument("--growl-buffer-fill", type=int, choices=(0, 165))
    parser.add_argument("--vqm-class", choices=("growl", "tense", "breathiness"),
                        default="growl")
    args = parser.parse_args()
    if not args.scratch.is_dir():
        raise ValueError("scratch directory does not exist")
    if args.single_case:
        gain = 1.0 if args.single_case == "native_positive" else 0.0
        print(json.dumps(run(args.dll, args.scratch, True, True,
                             gain, 5000.0, True,
                             args.outer18, args.outer19,
                             args.feature2, args.feature17,
                             args.feature18, args.vqm_spectrum,
                             args.vqm_spectrum_peak_db,
                             args.feature4, args.feature6, args.feature14,
                             args.feature15, args.feature16,
                             args.growl_buffer_fill, args.vqm_class)))
        return
    if args.native_target:
        cases = []
        for name in ("native_positive", "native_zero_gain"):
            command = [sys.executable, "-u", str(Path(__file__).resolve()),
                       "--dll", str(args.dll), "--scratch", str(args.scratch),
                       "--output", str(args.output), "--single-case", name,
                       "--outer18", str(args.outer18),
                       "--outer19", str(args.outer19),
                       "--vqm-class", args.vqm_class]
            if args.vqm_spectrum:
                command.extend(("--vqm-spectrum", "--vqm-spectrum-peak-db",
                                str(args.vqm_spectrum_peak_db)))
            child = subprocess.run(command, capture_output=True, text=True,
                                   check=False)
            if child.returncode:
                raise RuntimeError(f"native {name} failed: {child.stderr}")
            cases.append(json.loads(child.stdout))
        positive, zero_gain = cases
        valid = (positive["working_frame_1a8"] == 2049
                 and positive["target_producer_half_count"] == 2048
                 and positive["target_producer_nonzero"] == 4096
                 and positive["active_count_minus_one"] >= 0
                 and min(positive["mixed_real_nonzero"],
                         positive["mixed_imag_nonzero"]) > 0
                 and positive["inverse_fft_length"] == 4096
                 and positive["inverse_fft_nonzero"] == 4096
                 and positive["inverse_replay_max_errors"]["-1"] < 0.02
                 and zero_gain["mixed_real_nonzero"] == 0
                 and zero_gain["mixed_imag_nonzero"] == 0
                 and zero_gain["inverse_fft_nonzero"] == 0)
    else:
        cases = [run(args.dll, args.scratch, False, False, 1.0, 5000.0,
                     outer18=args.outer18, outer19=args.outer19,
                     vqm_spectrum=args.vqm_spectrum,
                     vqm_spectrum_peak_db=args.vqm_spectrum_peak_db),
                 run(args.dll, args.scratch, True, True, 1.0, 5000.0,
                     outer18=args.outer18, outer19=args.outer19,
                     vqm_spectrum=args.vqm_spectrum,
                     vqm_spectrum_peak_db=args.vqm_spectrum_peak_db),
                 run(args.dll, args.scratch, True, True, 0.0, 5000.0,
                     outer18=args.outer18, outer19=args.outer19,
                     vqm_spectrum=args.vqm_spectrum,
                     vqm_spectrum_peak_db=args.vqm_spectrum_peak_db),
                 run(args.dll, args.scratch, True, True, 1.0, 0.0,
                     outer18=args.outer18, outer19=args.outer19,
                     vqm_spectrum=args.vqm_spectrum,
                     vqm_spectrum_peak_db=args.vqm_spectrum_peak_db)]
        bare, positive, zero_gain, zero_pcm = cases
        valid = (bare["working_frame_1a8"] == 0
                 and bare["active_count_minus_one"] == -1
                 and positive["working_frame_1a8"] == 513
                 and positive["active_count_minus_one"] >= 0
                 and min(positive["mixed_real_nonzero"],
                         positive["mixed_imag_nonzero"]) > 0
                 and zero_gain["mixed_real_nonzero"] == 0
                 and zero_gain["mixed_imag_nonzero"] == 0
                 and zero_pcm["mixed_real_nonzero"] == 0
                 and zero_pcm["mixed_imag_nonzero"] == 0)
    if not valid:
        raise AssertionError("Growl frequency-domain mix changed: "
                             + json.dumps(cases))
    scope = ("Owned VQM material and generated PCM through native Growl event "
             "wrapper and frequency-domain mixer. The ordinary target frame "
             "spectrum is seeded explicitly; final PCM or full host production "
             "is not verified.")
    if args.native_target:
        scope = ("The ordinary target frame spectrum is calculated from owned "
                 "PCM by the native mode-3 producer and copied into a native "
                 "allocated frame. Growl event, frequency-domain mixer, and "
                 "native inverse FFT produce a time-domain block that matches "
                 "an independent Fourier replay at sampled indices. Full host "
                 "orchestration and final audio output are not verified.")
    report = {"dse_sha256": SHA256, "cases": cases, "scope": scope}
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")
    print(json.dumps({"cases": len(cases),
                      "active_counts": [case["active_count_minus_one"]
                                        for case in cases],
                      "positive_mix_nonzero": positive["mixed_real_nonzero"]}))


if __name__ == "__main__":
    main()
