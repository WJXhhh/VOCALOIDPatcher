"""Verify the native cross-source PCM energy fallback with owned samples."""

from __future__ import annotations

import argparse
import ctypes as c
import json
from pathlib import Path
import struct

from audit_frm2_numeric import F, I, Native, P, Q, SHA256, f32


SAMPLE_RATE = 44100
WINDOW = 513
SAMPLES = ((10000,) * 4096 + (5000,) * 4096 +
           (0,) * 4096 + (2000,) * 4096)


def predicted_energy(samples: tuple[int, ...]) -> float:
    total = f32(0.0)
    scale = f32(1.0 / 32768.0)
    for sample in samples:
        normalized = f32(f32(float(sample)) * scale)
        total = f32(total + f32(normalized * normalized))
    return f32(total / f32(len(samples)))


def run(dll: Path, scratch_pcm: Path) -> dict:
    if not scratch_pcm.parent.is_dir() or scratch_pcm.exists():
        raise ValueError("scratch PCM parent must exist and file must be absent")
    native = Native(dll)
    read_energy = native.fn(0x173f70, F, P, P, P, F)
    crt = c.CDLL("ucrtbase.dll")
    crt._wfopen.argtypes, crt._wfopen.restype = [c.c_wchar_p, c.c_wchar_p], P
    crt.fclose.argtypes, crt.fclose.restype = [P], I
    pcm = struct.pack(f"<{len(SAMPLES)}h", *SAMPLES)
    state = native.alloc(0x2B000)
    source = native.alloc(0x200)
    frame = native.alloc(0x328)
    analyzer = native.alloc(0xA0)
    stream = native.alloc(0x80)
    work = native.alloc(WINDOW * 2)
    P.from_address(state + 0x1fc8).value = analyzer
    P.from_address(state + 0x24118).value = work
    F.from_address(state + 0x1d550).value = SAMPLE_RATE
    I.from_address(analyzer + 0x8c).value = WINDOW - 1
    Q.from_address(source + 0x1b8).value = 0
    Q.from_address(source + 0x1c0).value = 0
    I.from_address(source + 0x1d0).value = len(SAMPLES)
    P.from_address(frame + 0x1a0).value = stream
    with scratch_pcm.open("xb") as output:
        output.write(pcm)
    handle = 0
    try:
        handle = crt._wfopen(str(scratch_pcm.resolve()), "rb")
        if not handle:
            raise OSError("cannot open owned PCM with UCRT")
        P.from_address(stream + 8).value = handle
        rows = []
        for center in (0, 2048, 6144, 10240, len(SAMPLES) - 1):
            time_seconds = f32(center / SAMPLE_RATE)
            native_value = read_energy(state, source, frame, time_seconds)
            actual_center = int(f32(f32(time_seconds * SAMPLE_RATE) + f32(0.5)))
            start = actual_center - (WINDOW - 1) // 2
            window = tuple(SAMPLES[index] if 0 <= index < len(SAMPLES) else 0
                           for index in range(start, start + WINDOW))
            if c.string_at(work, WINDOW * 2) != struct.pack(f"<{WINDOW}h", *window):
                raise AssertionError("native PCM window differs from the owned source")
            predicted = predicted_energy(window)
            error = abs(native_value - predicted)
            if error > 2e-7:
                raise AssertionError({"center": center, "native": native_value,
                                      "predicted": predicted, "error": error})
            rows.append({"center_sample": center, "actual_center": actual_center,
                         "window_start": start, "window_count": WINDOW,
                         "sample_level": SAMPLES[center],
                         "zero_padded_samples": sum(index < 0 or index >= len(SAMPLES)
                                                    for index in range(start, start + WINDOW)),
                         "native_mean_square": native_value,
                         "predicted_mean_square": predicted,
                         "absolute_error": error})
        if not rows[1]["native_mean_square"] > rows[2]["native_mean_square"] > 0:
            raise AssertionError("gain response is not positive and ordered")
        if rows[3]["native_mean_square"] != 0:
            raise AssertionError("zero PCM did not produce zero energy")
        if rows[0]["zero_padded_samples"] != 256 or rows[-1]["zero_padded_samples"] != 256:
            raise AssertionError("boundary windows were not half zero-padded")
        return {
            "dse_sha256": SHA256,
            "native_fallback_rva": "0x173f70",
            "native_pcm_window_rva": "0x175c90",
            "pcm_source": "owned constant-level int16 blocks; scratch file removed after run",
            "cases": rows,
            "scope": "The cross-source fallback reads an owned PCM window and returns its normalized mean square when no saved frame feature object is available. This isolated helper test does not reproduce the entire host cross-source branch.",
        }
    finally:
        try:
            if handle and crt.fclose(handle) != 0:
                raise OSError("cannot close owned PCM")
        finally:
            scratch_pcm.unlink()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--scratch-pcm", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll, args.scratch_pcm)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"cases": len(report["cases"]),
                      "max_absolute_error": max(row["absolute_error"]
                                                for row in report["cases"])}))


if __name__ == "__main__":
    main()
