"""Verify Growl event PCM window and FFT with owned frames and samples."""

from __future__ import annotations

import argparse
import ctypes as c
from dataclasses import replace
import json
import math
from pathlib import Path

from audit_frm2_numeric import D, F, I, Native, P, Q, SHA256, f32
from probe_frm2 import Envelope, VqmFrame, parse_frame, serialize_frame
from verify_vqm_feature_dse5_roundtrip import (crt_functions, owned_frame,
                                                read_frame)


def run_case(dll: Path, scratch: Path, scale: int,
             initial_time: float) -> dict:
    if not scratch.is_dir():
        raise ValueError("scratch directory does not exist")
    fixture = parse_frame(owned_frame())
    if not isinstance(fixture, VqmFrame):
        raise AssertionError("owned fixture type changed")
    native = Native(dll)
    crt = crt_functions()
    frames = []
    flat_envelope = Envelope(0x01820002, (-20000.0, 20000.0, 0.0),
                             (0.0, 0.0, 1000.0, 0.0))
    for index in range(3):
        raw = serialize_frame(replace(fixture, time_seconds=0.25 + index*0.01,
                                      component_frequencies_hz=(440.0,),
                                      component_amplitudes=(-20.0,),
                                      f0_hz=440.0,
                                      bit7_envelope=flat_envelope,
                                      bit17_envelope=flat_envelope))
        path = scratch / f"owned_growl_{index}.frm2"
        path.write_bytes(raw)
        try:
            frame, _ = read_frame(native, crt, path)
        finally:
            path.unlink(missing_ok=True)
        frames.append(frame)
    region = native.alloc(0x80)
    I.from_address(region + 0x18).value = len(frames)
    P.from_address(region + 0x20).value = native.array(P, frames)
    D.from_address(region + 0x38).value = 0.01
    pcm = [f32(5000*math.sin(math.tau*440*i/44100)) for i in range(65536)]
    pcm_address = native.array(F, pcm)
    record = native.alloc(0x850)
    F.from_address(record).value = 0.0
    F.from_address(record + 4).value = 1.0
    F.from_address(record + 8).value = 440.0
    P.from_address(record + 0x800).value = region
    P.from_address(record + 0x808).value = pcm_address
    collection = native.alloc(0x40)
    P.from_address(collection + 0x28).value = record
    P.from_address(collection + 0x30).value = record + 0x850
    growl = native.alloc(0x120)
    construct = native.fn(0x155e70, P, P, I, D, F, c.c_ubyte,
                          I, c.c_ubyte, c.c_ubyte, c.c_ubyte)
    if construct(growl, 1024, 0.01, 22050.0, 0, scale, 0, 0, 0) != growl:
        raise AssertionError("Growl constructor failed")
    P.from_address(growl + 8).value = collection
    state = P.from_address(growl + 0x20).value
    D.from_address(state + 0x60).value = initial_time
    D.from_address(state + 0xd0).value = 0.0
    D.from_address(state + 0xd8).value = 1.0
    F.from_address(state + 0xe0).value = 0.0
    F.from_address(state + 0xe8).value = 0.0
    working = frames[1]
    output = native.array(F, [0.0] * 4096)
    event = native.fn(0x1570f0, Q, P, I, P, F, F, I, P, F, P)
    code = event(state, 0, working, 440.0, 0.0, 1,
                 output, 1.0, 0)
    window = P.from_address(growl + 0xd8).value
    count = I.from_address(growl + 0x48).value
    sample_window = native.fn(0x152ae0, None, P, P, D, D, P, I)
    advanced_time = D.from_address(state + 0x60).value
    step = 1.0
    start = 2*22050.0*advanced_time - ((count-1)//2)*step - 16/2 + 1
    expected_window = native.array(F, [0.0] * count)
    sample_window(native.lib._handle + 0xa1fff0, pcm_address,
                  start, step, expected_window, count)
    window_error = max(abs(F.from_address(window + i*4).value
                           - F.from_address(expected_window + i*4).value)
                       for i in range(count))
    fft_magnitude = P.from_address(growl + 0x88).value
    fft_phase = P.from_address(growl + 0x98).value
    bins = 512*scale + 1
    event_magnitudes = [F.from_address(fft_magnitude + i*4).value
                        for i in range(bins)]
    event_phases = [F.from_address(fft_phase + i*4).value
                    for i in range(bins)]
    replay_magnitudes = P()
    replay_phases = P()
    replay_nyquist = I(-1)
    analyze = native.fn(0x14ab30, Q, P, P, I, c.c_ubyte,
                        Q, P, P, P, c.c_ubyte, P, F, I)
    replay_code = analyze(P.from_address(growl + 0x18).value,
                          window, 1025, 0, 0,
                          c.byref(replay_magnitudes), c.byref(replay_phases),
                          c.byref(replay_nyquist), 0, None, float(scale), 1)
    replay_magnitude_error = max(abs(a - F.from_address(
        replay_magnitudes.value + i*4).value)
        for i, a in enumerate(event_magnitudes))
    replay_phase_error = max(abs(a - F.from_address(
        replay_phases.value + i*4).value)
        for i, a in enumerate(event_phases))
    if (code != 1 or count != 1025 or I.from_address(growl + 0x44).value != scale
            or abs(advanced_time - (initial_time + 0.01)) > 1e-9
            or window_error != 0 or replay_code != 0
            or replay_nyquist.value != 512*scale
            or replay_magnitude_error != 0 or replay_phase_error != 0
            or not all(math.isfinite(value) for value in
                       [*event_magnitudes, *event_phases])):
        raise AssertionError("Growl event window or FFT result changed")
    return {"initial_time_seconds": initial_time,
            "advanced_time_seconds": advanced_time,
            "scale": scale, "event_result": code,
            "sample_index": I.from_address(state + 0x5c).value,
            "window_length": count,
            "window_step_samples": step,
            "window_start_input_coordinate": start,
            "window_replay_max_absolute_error": window_error,
            "window_nonzero": sum(F.from_address(window + i*4).value != 0
                                  for i in range(count)),
            "fft_bins_including_nyquist": bins,
            "fft_magnitude_nonzero": sum(value != 0 for value in
                                         event_magnitudes),
            "fft_phase_nonzero": sum(value != 0 for value in event_phases),
            "fft_replay_code": replay_code,
            "fft_replay_nyquist": replay_nyquist.value,
            "fft_replay_magnitude_max_error": replay_magnitude_error,
            "fft_replay_phase_max_error": replay_phase_error,
            "source_has_disk_spectrum": bool(
                Q.from_address(working + 0x150).value & 0x30)}


def run(dll: Path, scratch: Path) -> dict:
    cases = [run_case(dll, scratch, scale, initial_time)
             for scale in (1, 2) for initial_time in (0.26, 0.30)]
    return {"dse_sha256": SHA256, "cases": cases,
            "scope": "Four isolated native Growl events with owned VQM frames "
                     "and generated PCM. Event windows and FFT outputs are "
                     "verified; final rendered audio is not checked."}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", required=True, type=Path)
    parser.add_argument("--scratch", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    report = run(args.dll, args.scratch)
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")
    print(json.dumps({"cases": len(report["cases"]),
                      "fft_bins": [case["fft_bins_including_nyquist"]
                                   for case in report["cases"]]}))


if __name__ == "__main__":
    main()
