"""Exercise loaded auxiliary VQM objects with an owned target frame offline.

The installed auxiliary VQM material stays inside the native DSE instance.
Only counts and float32 output digests are retained. No Editor process is used.
"""

from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import subprocess
import sys
import tempfile

from audit_frm2_numeric import F, I, Native, P, Q, SHA256
from probe_frm2 import Envelope, MainFrame, MAIN_MASK, serialize_frame
from verify_vqm_feature_dse5_roundtrip import crt_functions


VDM_SHA256 = "73f1e128932521a1ba7ddb4736b59bc2d437e134753a3f3afa2df7751d430db2"
VQM_SHA256 = "a629b36e0b4d571c37f947809d34dcfa283a5af42f12d635cd5f75143ee6203c"


def clear_workspaces(obj: int) -> int:
    total = 0
    for pointer_index in (0xd, 0xf, 0x11, 0x13, 0x15, 0x17, 0x19, 0x1b):
        pointer = P.from_address(obj + pointer_index * 8).value
        count = Q.from_address(obj + (pointer_index + 1) * 8).value
        if not pointer or not 0 < count <= 0x20000:
            raise AssertionError("native VQM workspace layout changed")
        c.memset(pointer, 0, count * 4)
        total += count * 4
    return total


def run_one(vdm_path: Path, dse_path: Path, explib: Path, scratch: Path,
            category: int, gain: float, mutate_field: str | None = None,
            dispatch_controls: str | None = None,
            wrapper_phase_buffer: bool = False,
            large_phase_buffer: bool = False,
            reinterpolate_sources: bool = False) -> dict:
    if (dispatch_controls or wrapper_phase_buffer) and not large_phase_buffer:
        raise ValueError("native phase-pointer calls require owned large workspace")
    if hashlib.sha256(vdm_path.read_bytes()).hexdigest() != VDM_SHA256:
        raise ValueError("VDM binary differs from verified layout")
    if hashlib.sha256(explib.joinpath("VQM.vdb").read_bytes()).hexdigest() != VQM_SHA256:
        raise ValueError("auxiliary VQM file differs from verified sample")
    native = Native(dse_path)
    vdm = c.CDLL(str(vdm_path))
    dse = native.lib
    create_vdm = vdm.VDM_createDatabaseManager
    create_vdm.argtypes = [c.c_wchar_p, c.c_wchar_p, c.POINTER(c.c_int)]
    create_vdm.restype = P
    destroy_vdm = vdm.VDM_DatabaseManager_destroy
    destroy_vdm.argtypes = [P]
    status = I(-1)
    manager = create_vdm("VOCALOID6", str(explib), c.byref(status))
    if not manager or status.value:
        raise RuntimeError("offline VDM manager did not initialize")
    make_dse = dse.VIS_DSE_CreateManager
    make_dse.restype = P
    initialize = dse.VIS_DSE_InitializeManager
    initialize.argtypes = [P, P]
    initialize.restype = I
    terminate = dse.VIS_DSE_TerminateManager
    terminate.argtypes = [P]
    destroy_dse = dse.VIS_DSE_DestroyManager
    destroy_dse.argtypes = [P]
    dse_manager = make_dse()
    initialized = False
    try:
        if not dse_manager or initialize(dse_manager, manager):
            raise RuntimeError("offline DSE manager did not initialize")
        initialized = True
        slot = native.fn(0x1cf5e0, I, I)(0x13000202)
        if slot != 0:
            raise AssertionError("unexpected offline engine slot")
        config = (c.c_ubyte * 32)()
        I.from_buffer(config, 0).value = 44100
        I.from_buffer(config, 4).value = 256
        if native.fn(0x1d0780, Q, I, P)(slot, c.addressof(config)):
            raise RuntimeError("offline DSE engine setup failed")
        engine_slot = dse._handle + 0xaa69d0 + 8 * slot
        engine = P.from_address(engine_slot).value
        if not engine:
            raise AssertionError("no offline DSE engine")
        factors_begin = P.from_address(engine + 0x2b138).value
        factors_end = P.from_address(engine + 0x2b140).value
        timeline_begin = P.from_address(engine + 0x2b168).value
        timeline_end = P.from_address(engine + 0x2b170).value
        if ((factors_begin or factors_end) and
                (not factors_begin or factors_end < factors_begin or
                 (factors_end - factors_begin) % 4)):
            raise AssertionError("invalid VQM factor workspace")
        if ((timeline_begin or timeline_end) and
                (not timeline_begin or timeline_end < timeline_begin or
                 (timeline_end - timeline_begin) % 8)):
            raise AssertionError("invalid VQM timeline vector")
        factor_bin_count = ((factors_end - factors_begin) // 4
                            if factors_begin else 0)
        timeline_count = ((timeline_end - timeline_begin) // 8
                          if timeline_begin else 0)
        begin = P.from_address(engine + 0x2af30).value
        end = P.from_address(engine + 0x2af38).value
        if not begin or end - begin != 24 or category not in (2, 3):
            raise AssertionError("unexpected VQM object vector")
        obj = P.from_address(begin + category * 8 - 8).value
        expected_vtable = {2: 0x246258, 3: 0x2451b0}[category]
        if (not obj or P.from_address(obj).value - dse._handle !=
                expected_vtable or c.c_uint8.from_address(obj + 0x118).value != 1):
            raise AssertionError("loaded VQM class or activation changed")
        record = P.from_address(obj + 8).value
        if not record or I.from_address(record + 0x20).value != category:
            raise AssertionError("loaded object is not bound to its category")
        workspace_bytes = clear_workspaces(obj)
        dispatch_workspace_bytes = 0
        if dispatch_controls:
            other_category = 5 - category
            other = P.from_address(begin + (other_category - 1) * 8).value
            if not other:
                raise AssertionError("second auxiliary VQM object is missing")
            dispatch_workspace_bytes = clear_workspaces(other)
        sample = P.from_address(record + 0x28).value
        if not sample:
            raise AssertionError("loaded category has no material")
        source_start = F.from_address(sample).value
        source_pitch = F.from_address(sample + 8).value
        region = P.from_address(sample + 0x800).value
        frame_count = I.from_address(region + 0x18).value
        frame_vector = P.from_address(region + 0x20).value
        if not frame_vector or frame_count not in (148, 389):
            raise AssertionError("loaded VQM frame vector changed")
        mutation = None
        feature_frames = 0
        source_phase_nonzero_counts = []
        native_offsets = {
            "4": 0x14, "6": 0x1c, "14": 0x20c,
            "15": 0x214, "16": 0x218, "17": 0x21c,
            "18": 0x220,
        }
        for index in range(frame_count):
            source_frame = P.from_address(frame_vector + index * 8).value
            if not source_frame:
                raise AssertionError("null loaded VQM source frame")
            phase_array = P.from_address(source_frame + 0x170).value
            source_phase_nonzero_counts.append(sum(
                F.from_address(phase_array + component * 4).value != 0
                for component in range(350)))
            feature = P.from_address(source_frame + 0x208).value
            feature_frames += bool(feature)
            if mutate_field in native_offsets:
                if not feature:
                    raise AssertionError("loaded VQM source frame has no feature")
                value = F.from_address(feature + native_offsets[mutate_field])
                original = value.value
                value.value = original + 0.75
                if index == 0:
                    mutation = {"field": mutate_field, "source_frames": frame_count,
                                "first_original": original,
                                "first_changed": value.value}
            elif mutate_field in ("outer18", "outer19"):
                if mutate_field == "outer18":
                    value = c.c_uint8.from_address(source_frame + 0x258)
                    original = value.value
                    value.value = 255 if original != 255 else 0
                else:
                    value = c.c_uint32.from_address(source_frame + 0x25c)
                    original = value.value
                    value.value = 4097 if original != 4097 else 0
                if index == 0:
                    mutation = {"field": mutate_field, "source_frames": frame_count,
                                "first_original": original,
                                "first_changed": value.value}
            elif mutate_field == "source_phase":
                phase = P.from_address(source_frame + 0x170).value
                if not phase or I.from_address(source_frame + 0x158).value != 350:
                    raise AssertionError("loaded source phase layout changed")
                for component in range(350):
                    value = F.from_address(phase + component * 4)
                    if math.isfinite(value.value):
                        value.value += 0.1
                if index == 0:
                    mutation = {"field": mutate_field,
                                "source_frames": frame_count,
                                "values_changed": frame_count * 350}
        if feature_frames != frame_count:
            raise AssertionError("loaded VQM feature frame count changed")
        if mutate_field and not mutation:
            raise ValueError("unsupported VQM field mutation")
        first_frame = P.from_address(frame_vector).value
        first_phase = P.from_address(first_frame + 0x170).value
        first_phase_nonzero = sum(
            F.from_address(first_phase + component * 4).value != 0
            for component in range(350))
        # BRWSP stores zero reference pitch; use its observed 300 Hz frame F0.
        target_pitch = source_pitch if source_pitch > 0 else 300.0

        flat = Envelope(0x01820002, (-20000.0, 20000.0, 0.0),
                        (0.0, 0.0, 1000.0, 0.0))
        target = MainFrame(
            0, source_start + 0.01, MAIN_MASK,
            (target_pitch, 2 * target_pitch), (-20.0, -30.0),
            (0.0, 0.0), target_pitch, flat,
            ((target_pitch, -20.0, 0.0),), (0.0, 0.0, 0.0), (),
            Envelope(0x00960002, (-100.0, 10.0, 0.0), (0.0, 0.0)))
        scratch_file = None
        try:
            with tempfile.NamedTemporaryFile(
                    mode="wb", prefix="owned_aux_target_", suffix=".frm2",
                    dir=scratch, delete=False) as stream:
                stream.write(serialize_frame(target))
                scratch_file = Path(stream.name)
            crt = crt_functions()
            handle = crt._wfopen(str(scratch_file.resolve()), "rb")
            if not handle:
                raise OSError("cannot read owned target frame")
            working = native.alloc(0x328)
            native.fn(0x12a670, P, P)(working)
            source = native.alloc(0x80)
            P.from_address(source + 8).value = handle
            I.from_address(source + 0x2c).value = 0x32534d53
            try:
                if native.fn(0x106e20, Q, P, P)(working, source):
                    raise AssertionError("native owned target frame read failed")
            finally:
                crt.fclose(handle)
        finally:
            if scratch_file:
                scratch_file.unlink(missing_ok=True)

        bins = 2049
        native.fn(0x12ba70, Q, P, I, Q, I)(working, bins, 0x30, 0)
        if I.from_address(working + 0x1a8).value != bins:
            raise AssertionError("target spectrum allocation failed")
        amplitudes = P.from_address(working + 0x1b0).value
        phases = P.from_address(working + 0x1b8).value
        for index in range(bins):
            F.from_address(amplitudes + index * 4).value = -40.0
            F.from_address(phases + index * 4).value = 0.0
        if large_phase_buffer:
            if not (wrapper_phase_buffer or dispatch_controls):
                raise ValueError("large phase buffer requires a phase-pointer probe")
            phase_workspace = native.array(F, [0.0] * 4096)
            P.from_address(working + 0x170).value = phase_workspace

        event_output = native.array(F, [0.0] * 4096)
        if dispatch_controls and mutate_field:
            raise ValueError("dispatch and source mutation are separate probes")
        if not dispatch_controls:
            wrapper_output = (P.from_address(working + 0x170).value
                              if wrapper_phase_buffer else event_output)
            native.fn(0x158e40, None, P, P, F, F, I, P, F, P)(
                obj, working, target_pitch, 0.0, 1, wrapper_output, 1.0, None)
        state = P.from_address(obj + 0x20).value
        if not state:
            raise AssertionError("loaded VQM object has no event state")
        state_record = P.from_address(state + 8).value
        dispatch = None
        if dispatch_controls:
            # Mirror the first voiced step of the native phase-state helper:
            # count=1, phase=0, and the current target frequency retained.
            F.from_address(engine + 0x24128).value = 1.0 / target_pitch
            F.from_address(engine + 0x2412c).value = 0.0
            I.from_address(engine + 0x24130).value = 1
            F.from_address(engine + 0x24134).value = target_pitch
            if factor_bin_count:
                raise AssertionError("engine factors were initialized unexpectedly")
            factor_bytes = bins * 4
            factors = native.fn(0x0162b0, P, Q)(factor_bytes)
            if not factors:
                raise MemoryError("native VQM factor allocation failed")
            c.memset(factors, 0, factor_bytes)
            P.from_address(engine + 0x2b138).value = factors
            P.from_address(engine + 0x2b140).value = factors + factor_bytes
            P.from_address(engine + 0x2b148).value = factors + factor_bytes
            control_source = P.from_address(engine + 0x1d568).value
            if not control_source:
                raise AssertionError("offline engine has no control source")
            control_offsets = {"tense": (0x5aae0,),
                               "breath": (0x5aabc, 0x5aadc),
                               "both": (0x5aae0, 0x5aabc, 0x5aadc)}
            if dispatch_controls != "defaults":
                for offset in control_offsets[dispatch_controls]:
                    F.from_address(control_source + offset).value = 1.0
            interpolate_control = native.fn(0x18adc0, F, P, I, I)
            frame_index = I.from_address(engine + 0x24158).value
            control_values = {hex(identifier): interpolate_control(
                control_source, identifier, frame_index)
                for identifier in (0xd, 0x14, 0x15)}
            native.fn(0x171430, None, P, P, c.c_ubyte)(engine, working, 0)
            dispatch_frame = P.from_address(engine + 0x2b130).value
            if not dispatch_frame or I.from_address(
                    dispatch_frame + 0x1a8).value != bins:
                raise AssertionError("VQM dispatch did not create a spectrum")
            dispatch_real = P.from_address(dispatch_frame + 0x1b0).value
            dispatch_imag = P.from_address(dispatch_frame + 0x1b8).value
            dispatch = {
                "control_mode": dispatch_controls,
                "native_control_values": control_values,
                "real_nonzero": sum(F.from_address(
                    dispatch_real + index * 4).value != 0
                    for index in range(bins)),
                "imag_nonzero": sum(F.from_address(
                    dispatch_imag + index * 4).value != 0
                    for index in range(bins)),
                "real_sha256": hashlib.sha256(c.string_at(
                    dispatch_real, factor_bytes)).hexdigest(),
                "imag_sha256": hashlib.sha256(c.string_at(
                    dispatch_imag, factor_bytes)).hexdigest(),
                "factors_sha256": hashlib.sha256(c.string_at(
                    factors, factor_bytes)).hexdigest(),
            }
        interpolated = P.from_address(state + 0x10).value
        replay = None
        if reinterpolate_sources:
            def phase_state():
                count = I.from_address(interpolated + 0x158).value
                pointer = P.from_address(interpolated + 0x170).value
                if not pointer or not 0 <= count <= 4096:
                    raise AssertionError("unexpected interpolated phase allocation")
                return {"count": count,
                        "nonzero": sum(F.from_address(pointer + i * 4).value != 0
                                       for i in range(count)),
                        "sha256": hashlib.sha256(c.string_at(
                            pointer, count * 4)).hexdigest()}
            before = phase_state()
            source_a = state + 0xf0
            source_b = state + 0x418
            source_rate = F.from_address(P.from_address(state + 8).value + 0x58).value
            native.fn(0x1307b0, Q, P, P, P, Q, F, c.c_ubyte,
                      I, I, F, I, I)(
                source_a, source_b, interpolated, 0x1ff7,
                0.5, 3, int(source_rate * 2), 0x80, 1.0, 0, 0)
            replay = {"source_rate": source_rate,
                      "before": before, "after": phase_state()}
        active_count = I.from_address(state + 0x58).value
        if not 0 <= active_count <= 4095:
            raise AssertionError("unexpected VQM event sample count")
        first = P.from_address(state + 0x28).value
        second = P.from_address(state + 0x38).value
        state_bytes = (active_count + 1) * 4
        state_first_sha256 = (hashlib.sha256(c.string_at(
            first, state_bytes)).hexdigest() if first else None)
        state_second_sha256 = (hashlib.sha256(c.string_at(
            second, state_bytes)).hexdigest() if second else None)
        real = native.array(F, [0.0] * bins)
        imag = native.array(F, [0.0] * bins)
        third = native.array(F, [1.0] * bins)
        third_ptr = native.array(P, [third])
        mixer_rva = {2: 0x15fd80, 3: 0x153f50}[category]
        native.fn(mixer_rva, None, P, P, P, F, F, P, P)(
            obj, real, imag, gain, target_pitch, working, third_ptr)
        return {
            "category_code": category,
            "gain": gain,
            "source_start_seconds": source_start,
            "source_end_seconds": F.from_address(sample + 4).value,
            "record_phase_mode": c.c_uint8.from_address(record + 0x10).value,
            "state_record_matches_bound": state_record == record,
            "state_record_phase_modes": [c.c_uint8.from_address(state_record + offset).value
                                         for offset in (0x10, 0x11)],
            "target_pitch_hz": target_pitch,
            "engine_factor_bin_count": factor_bin_count,
            "engine_timeline_count": timeline_count,
            "engine_frame_index": I.from_address(engine + 0x24158).value,
            "engine_pitch_step": F.from_address(engine + 0x2412c).value,
            "engine_event_count": I.from_address(engine + 0x24130).value,
            "mutation": mutation,
            "wrapper_phase_buffer": wrapper_phase_buffer,
            "large_phase_buffer": large_phase_buffer,
            "engine_dispatch": dispatch,
            "loaded_source_feature_frames": feature_frames,
            "first_source_phase_nonzero": first_phase_nonzero,
            "source_phase_nonzero_min": min(source_phase_nonzero_counts),
            "source_phase_nonzero_max": max(source_phase_nonzero_counts),
            "initialized_workspace_bytes": workspace_bytes,
            "other_dispatch_workspace_bytes": dispatch_workspace_bytes,
            "interpolated_frame_present": bool(interpolated),
            "source_interpolation_replay": replay,
            "interpolated_feature_present": bool(
                P.from_address(interpolated + 0x208).value) if interpolated else False,
            "interpolated_outer18": (c.c_uint8.from_address(
                interpolated + 0x258).value if interpolated else None),
            "interpolated_outer19": (c.c_uint32.from_address(
                interpolated + 0x25c).value if interpolated else None),
            "active_count_minus_one": I.from_address(state + 0x58).value,
            "event_first_sha256": state_first_sha256,
            "event_second_sha256": state_second_sha256,
            "event_output_nonzero": sum(
                F.from_address(event_output + index * 4).value != 0
                for index in range(4096)),
            "real_nonzero": sum(F.from_address(real + index * 4).value != 0
                                for index in range(bins)),
            "imag_nonzero": sum(F.from_address(imag + index * 4).value != 0
                                for index in range(bins)),
            "real_sha256": hashlib.sha256(c.string_at(real, bins * 4)).hexdigest(),
            "imag_sha256": hashlib.sha256(c.string_at(imag, bins * 4)).hexdigest(),
        }
    finally:
        if dse_manager:
            if initialized:
                terminate(dse_manager)
            destroy_dse(dse_manager)
        destroy_vdm(manager)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--vdm-dll", required=True, type=Path)
    parser.add_argument("--dse-dll", required=True, type=Path)
    parser.add_argument("--explib", required=True, type=Path)
    parser.add_argument("--scratch", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--single-case", action="store_true")
    parser.add_argument("--category", type=int, choices=(2, 3))
    parser.add_argument("--gain", type=float, choices=(0.0, 1.0))
    parser.add_argument("--mutate-field", choices=("4", "6", "14", "15",
                                                    "16", "17", "18",
                                                    "outer18", "outer19",
                                                    "source_phase"))
    parser.add_argument("--dispatch-controls", choices=("defaults", "tense",
                                                        "breath", "both"))
    parser.add_argument("--wrapper-phase-buffer", action="store_true")
    parser.add_argument("--large-phase-buffer", action="store_true")
    parser.add_argument("--reinterpolate-sources", action="store_true")
    args = parser.parse_args()
    if not args.scratch.is_dir() or not args.explib.joinpath("VQM.vdb").is_file():
        raise ValueError("offline inputs are missing")
    if args.single_case:
        if args.category is None or args.gain is None:
            parser.error("single case requires --category and --gain")
        print(json.dumps(run_one(args.vdm_dll, args.dse_dll, args.explib,
                                 args.scratch, args.category, args.gain,
                                 args.mutate_field,
                                 args.dispatch_controls,
                                 args.wrapper_phase_buffer,
                                 args.large_phase_buffer,
                                 args.reinterpolate_sources)))
        return
    cases = []
    for category in (2, 3):
        for gain in (0.0, 1.0):
            command = [sys.executable, str(Path(__file__).resolve()),
                       "--vdm-dll", str(args.vdm_dll),
                       "--dse-dll", str(args.dse_dll),
                       "--explib", str(args.explib),
                       "--scratch", str(args.scratch),
                       "--output", str(args.output),
                       "--single-case", "--category", str(category),
                       "--gain", str(gain)]
            child = subprocess.run(command, capture_output=True, text=True,
                                   timeout=90, check=False)
            if child.returncode:
                raise RuntimeError(f"category {category}, gain {gain} failed: "
                                   f"{child.stderr[-1200:]}")
            cases.append(json.loads(child.stdout))
    if not all(case["interpolated_frame_present"] for case in cases):
        raise AssertionError("loaded VQM interpolation did not run")
    if not all(case["active_count_minus_one"] >= 0 for case in cases):
        raise AssertionError("loaded VQM event stayed inactive")
    for index in (0, 2):
        zero, positive = cases[index:index + 2]
        if (zero["real_nonzero"] or zero["imag_nonzero"] or
                positive["real_nonzero"] <= 0 or
                positive["imag_nonzero"] <= 0 or
                positive["real_sha256"] == zero["real_sha256"]):
            raise AssertionError("loaded VQM gain control did not separate")
    if cases[1]["real_sha256"] == cases[3]["real_sha256"]:
        raise AssertionError("TENSE and BRWSP output unexpectedly identical")
    report = {"dse_sha256": SHA256,
              "vdm_sha256": VDM_SHA256,
              "vqm_vdb_sha256": VQM_SHA256,
              "cases": cases,
              "scope": "Native loaded auxiliary VQM objects and material "
                       "with an owned ordinary target frame in an offline "
                       "engine; no Editor event or final PCM is observed."}
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")


if __name__ == "__main__":
    main()
