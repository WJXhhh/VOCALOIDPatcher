"""Compare offline VQM auxiliary controls in a complete owned PCM event."""

from __future__ import annotations

import argparse
from contextlib import contextmanager
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import F, I, P, Q, SHA256
from probe_vqm_continuous_output import run_one
from probe_vqm_loaded_aux_mix import (VDM_SHA256, VQM_SHA256,
                                       clear_workspaces)


MODES = ("unbound", "off", "tense", "breath_d", "breath_14",
         "breath", "both",
         "post_zero", "post_one")


@contextmanager
def loaded_auxiliary(native, vdm_path: Path, explib: Path, mode: str,
                     force_gate: bool = False,
                     output_gain: float | None = None,
                     source_mutation: str | None = None,
                     source_category: int | None = None,
                     mutation_state: dict | None = None,
                     prefill_source_copies: bool = False,
                     prefill_interpolated_phase: bool = False,
                     breath_state_phase_mode: str = "default"):
    vdm = c.CDLL(str(vdm_path))
    create = vdm.VDM_createDatabaseManager
    create.argtypes = [c.c_wchar_p, c.c_wchar_p, c.POINTER(I)]
    create.restype = P
    destroy = vdm.VDM_DatabaseManager_destroy
    destroy.argtypes = [P]
    status = I(-1)
    manager = create("VOCALOID6", str(explib), c.byref(status))
    if not manager or status.value:
        raise RuntimeError("offline VDM manager did not initialize")
    dse = native.lib
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
        engine = P.from_address(dse._handle + 0xaa69d0 + slot * 8).value
        if not engine:
            raise AssertionError("offline DSE engine is missing")
        begin = P.from_address(engine + 0x2af30).value
        end = P.from_address(engine + 0x2af38).value
        if not begin or end - begin != 3 * 8:
            raise AssertionError("unexpected loaded VQM object vector")
        for index, vtable_rva in enumerate((0x245500, 0x246258, 0x2451b0)):
            obj = P.from_address(begin + index * 8).value
            if not obj or P.from_address(obj).value - dse._handle != vtable_rva:
                raise AssertionError("loaded VQM class changed")
            if index:
                clear_workspaces(obj)
        if source_mutation is not None:
            if source_category not in (2, 3):
                raise ValueError("source mutation requires auxiliary category 2 or 3")
            obj = P.from_address(begin + (source_category - 1) * 8).value
            record = P.from_address(obj + 8).value
            sample = P.from_address(record + 0x28).value
            region = P.from_address(sample + 0x800).value
            frame_count = I.from_address(region + 0x18).value
            frame_vector = P.from_address(region + 0x20).value
            expected_count = {2: 148, 3: 389}[source_category]
            if frame_count != expected_count or not frame_vector:
                raise AssertionError("loaded auxiliary source collection changed")
            values_changed = 0
            for index in range(frame_count):
                source = P.from_address(frame_vector + index * 8).value
                if not source or I.from_address(source + 0x158).value != 350:
                    raise AssertionError("loaded auxiliary source frame changed")
                if source_mutation in ("frequency", "amplitude", "phase"):
                    offset = {"frequency": 0x160, "amplitude": 0x168,
                              "phase": 0x170}[source_mutation]
                    array = P.from_address(source + offset).value
                    if not array:
                        raise AssertionError("loaded auxiliary component array missing")
                    increment = {"frequency": 5.0, "amplitude": 3.0,
                                 "phase": 0.1}[source_mutation]
                    for component in range(350):
                        cell = F.from_address(array + component * 4)
                        if (math.isfinite(cell.value) and abs(cell.value) < 1e6
                                and (source_mutation != "frequency" or cell.value > 0)):
                            cell.value += increment
                            values_changed += 1
                elif source_mutation.startswith("feature"):
                    feature = P.from_address(source + 0x208).value
                    if not feature:
                        raise AssertionError("loaded auxiliary feature missing")
                    offsets = {"feature4": 0x14, "feature6": 0x1c,
                               "feature14": 0x20c, "feature15": 0x214,
                               "feature16": 0x218, "feature17": 0x21c,
                               "feature18": 0x220, "feature19": 0x224}
                    if source_mutation not in offsets:
                        raise ValueError("unsupported feature mutation")
                    offset = offsets[source_mutation]
                    cell = F.from_address(feature + offset)
                    cell.value += 0.75
                    values_changed += 1
                elif source_mutation in ("outer18", "outer19"):
                    offset = 0x258 if source_mutation == "outer18" else 0x25c
                    ty = c.c_ubyte if source_mutation == "outer18" else c.c_uint32
                    cell = ty.from_address(source + offset)
                    cell.value = 255 if source_mutation == "outer18" else 4097
                    values_changed += 1
                else:
                    raise ValueError("unsupported source mutation")
            if not values_changed:
                raise AssertionError("no loaded source values changed")
            if mutation_state is not None:
                mutation_state.update(category=source_category,
                                      source_frames=frame_count,
                                      values_changed=values_changed)
        if prefill_source_copies or prefill_interpolated_phase:
            if source_category not in (2, 3):
                raise ValueError("source prefill requires auxiliary category")
            obj = P.from_address(begin + (source_category - 1) * 8).value
            state = P.from_address(obj + 0x20).value
            record = P.from_address(obj + 8).value
            sample = P.from_address(record + 0x28).value
            region = P.from_address(sample + 0x800).value
            if not state or not region:
                raise AssertionError("auxiliary source prefill state missing")
            copy_frame = native.fn(0x13a320, Q, P, I, P)
            for offset in (0xf0, 0x418):
                copy_frame(region, 0, state + offset)
                target = state + offset
                if I.from_address(target + 0x158).value != 350:
                    raise AssertionError("auxiliary source prefill failed")
            if prefill_interpolated_phase:
                interpolated = P.from_address(state + 0x10).value
                source_rate = F.from_address(P.from_address(state + 8).value + 0x58).value
                native.fn(0x1307b0, Q, P, P, P, Q, F, c.c_ubyte,
                          I, I, F, I, I)(state + 0xf0, state + 0x418,
                                       interpolated, 0x1ff7, 0.5, 3,
                                       int(source_rate * 2), 0x80, 1.0, 0, 0)
                if I.from_address(interpolated + 0x158).value != 350:
                    raise AssertionError("auxiliary phase prefill failed")
                phase = P.from_address(interpolated + 0x170).value
                phase_nonzero = sum(F.from_address(phase + i * 4).value != 0
                                    for i in range(350))
                if mutation_state is not None:
                    mutation_state["prefilled_phase_nonzero"] = phase_nonzero
        if breath_state_phase_mode != "default":
            obj = P.from_address(begin + 2 * 8).value
            state = P.from_address(obj + 0x20).value
            state_record = P.from_address(state + 8).value
            if [c.c_ubyte.from_address(state_record + i).value
                    for i in (0x10, 0x11)] != [1, 1]:
                raise AssertionError("BRWSP event state mode changed")
            c.c_ubyte.from_address(state_record + 0x10).value = 0
            if breath_state_phase_mode == "clear_both":
                c.c_ubyte.from_address(state_record + 0x11).value = 0

        def prepare(ctx: int, ring: int) -> None:
            if mode != "unbound":
                for offset in (0x2af30, 0x2af38, 0x2af40):
                    P.from_address(ctx + offset).value = P.from_address(engine + offset).value
            bins = I.from_address(ctx + 0x24124).value
            if bins != 2049:
                raise AssertionError("complete PCM fixture changed FFT size")
            factors = native.fn(0x0162b0, P, Q)(bins * 4)
            if not factors:
                raise MemoryError("native factor allocation failed")
            c.memset(factors, 0, bins * 4)
            P.from_address(ctx + 0x2b138).value = factors
            P.from_address(ctx + 0x2b140).value = factors + bins * 4
            P.from_address(ctx + 0x2b148).value = factors + bins * 4
            settings = {
                0x5aae0: 1.0 if mode in ("tense", "both") else 0.5,
                0x5aabc: 1.0 if mode in ("breath_d", "breath", "both") else 0.0,
                0x5aadc: 1.0 if mode in ("breath_14", "breath", "both") else 0.0,
                0x5aac0: (0.0 if mode == "post_zero" else
                          1.0 if mode == "post_one" else 0.5),
            }
            for offset, value in settings.items():
                F.from_address(ring + offset).value = value
            controller = native.fn(0x18adc0, F, P, I, I)
            if output_gain is not None:
                F.from_address(ring + 0x5aa90).value = output_gain
                if abs(controller(ring, 2, 0) - output_gain) > 1e-7:
                    raise AssertionError("owned output gain did not take effect")
            values = [controller(ring, code, 0) for code in (0xd, 0xe, 0x14, 0x15)]
            expected = [settings[0x5aabc], settings[0x5aac0],
                        settings[0x5aadc], settings[0x5aae0]]
            if values != expected:
                raise AssertionError("offline controller setup did not take effect")
            if force_gate:
                records = P.from_address(ring).value
                anchor = P.from_address(records + 100 * 0x18 + 0x10).value
                if not anchor or I.from_address(anchor + 8).value != 100 or \
                        I.from_address(anchor + 0x14).value != 0:
                    raise AssertionError("owned following-anchor layout changed")
                I.from_address(anchor + 0x14).value = 1
                if not native.fn(0x175650, c.c_ubyte, P, I)(ctx, 80):
                    raise AssertionError("synthetic lookahead gate stayed closed")

        yield prepare
    finally:
        if dse_manager:
            if initialized:
                terminate(dse_manager)
            destroy_dse(dse_manager)
        destroy(manager)


def single_case(vdm_path: Path, dse_path: Path, explib: Path,
                scratch: Path, mode: str, force_gate: bool = False,
                ordinary_onset: bool = False,
                source_mutation: str | None = None,
                source_category: int | None = None,
                ordinary_pitch_hz: float | None = None,
                prefill_source_copies: bool = False,
                prefill_interpolated_phase: bool = False,
                breath_state_phase_mode: str = "default") -> dict:
    if hashlib.sha256(vdm_path.read_bytes()).hexdigest() != VDM_SHA256:
        raise ValueError("VDM binary differs from verified layout")
    if hashlib.sha256(explib.joinpath("VQM.vdb").read_bytes()).hexdigest() != VQM_SHA256:
        raise ValueError("auxiliary VQM file differs from verified sample")

    mutation_state = {}

    def setup(native):
        return loaded_auxiliary(native, vdm_path, explib, mode, force_gate,
                                0.03 if ordinary_onset else None,
                                source_mutation, source_category,
                                mutation_state, prefill_source_copies,
                                prefill_interpolated_phase,
                                breath_state_phase_mode)

    result = run_one(dse_path, scratch, 0.12, auxiliary_setup=setup,
                     ordinary_onset=ordinary_onset,
                     ordinary_harmonic_capacity=512 if ordinary_onset else 0,
                     ordinary_pitch_hz=ordinary_pitch_hz)
    return {"mode": mode, "forced_following_anchor": force_gate,
            "ordinary_onset": ordinary_onset,
            "ordinary_pitch_hz": ordinary_pitch_hz,
            "source_mutation": source_mutation,
            "source_mutation_state": mutation_state,
            "prefill_source_copies": prefill_source_copies,
            "prefill_interpolated_phase": prefill_interpolated_phase,
            "breath_state_phase_mode": breath_state_phase_mode,
            "blocks_run": result["blocks_run"],
            "nonzero_blocks": result["nonzero_blocks"],
            "output_stream_sha256": result["output_stream_sha256"],
            "output_blocks": result["output_blocks"],
            "observations": result["observations"]}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--vdm-dll", required=True, type=Path)
    parser.add_argument("--dse-dll", required=True, type=Path)
    parser.add_argument("--explib", required=True, type=Path)
    parser.add_argument("--scratch-source", required=True, type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--single-case", action="store_true")
    parser.add_argument("--mode", choices=MODES)
    parser.add_argument("--force-gate", action="store_true")
    parser.add_argument("--ordinary-onset", action="store_true")
    parser.add_argument("--ordinary-onset-matrix", action="store_true")
    parser.add_argument("--source-mutation", choices=(
        "frequency", "amplitude", "phase", "feature6", "feature17",
        "outer18", "outer19"))
    parser.add_argument("--source-category", type=int, choices=(2, 3))
    parser.add_argument("--ordinary-pitch", type=float)
    parser.add_argument("--prefill-source-copies", action="store_true")
    parser.add_argument("--prefill-interpolated-phase", action="store_true")
    parser.add_argument("--breath-state-phase-mode",
                        choices=("default", "clear_10", "clear_both"),
                        default="default")
    args = parser.parse_args()
    if args.single_case:
        if args.mode is None:
            parser.error("single case requires --mode")
        print(json.dumps(single_case(args.vdm_dll, args.dse_dll, args.explib,
                                     args.scratch_source, args.mode,
                                     args.force_gate,
                                     args.ordinary_onset,
                                     args.source_mutation,
                                     args.source_category,
                                     args.ordinary_pitch,
                                     args.prefill_source_copies,
                                     args.prefill_interpolated_phase,
                                     args.breath_state_phase_mode)))
        return
    if (args.force_gate or args.ordinary_onset or args.source_mutation or
            args.prefill_source_copies or args.prefill_interpolated_phase or
            args.breath_state_phase_mode != "default" or
            args.ordinary_pitch is not None):
        parser.error("diagnostic flags are only for isolated single cases")
    cases = []
    for mode in MODES:
        command = [sys.executable, str(Path(__file__).resolve()),
                   "--vdm-dll", str(args.vdm_dll), "--dse-dll", str(args.dse_dll),
                   "--explib", str(args.explib), "--scratch-source",
                   str(args.scratch_source), "--single-case", "--mode", mode]
        if args.ordinary_onset_matrix:
            command.append("--ordinary-onset")
        child = subprocess.run(command, capture_output=True, text=True,
                               check=False, timeout=180)
        if child.returncode:
            raise RuntimeError(f"offline PCM case {mode} failed: "
                               f"{child.stderr[-1300:] or child.returncode}")
        cases.append(json.loads(child.stdout))
    if any(case["blocks_run"] != cases[0]["blocks_run"] for case in cases):
        raise AssertionError("inconsistent PCM event length")
    def changed(field: str, rows: str, left: int, right: int) -> int:
        return sum(a[field] != b[field] for a, b in zip(
            cases[left][rows], cases[right][rows]))
    comparisons = {
        mode: {"auxiliary_spectrum_blocks_changed": changed(
                   "auxiliary_spectrum_sha256", "observations", 1, index),
               "final_spectrum_blocks_changed": changed(
                   "final_spectrum_sha256", "observations", 1, index),
               "output_pcm_blocks_changed": changed(
                   "sha256", "output_blocks", 1, index)}
        for index, mode in enumerate(MODES) if index != 1
    }
    report_cases = [{"mode": case["mode"],
                     "blocks_run": case["blocks_run"],
                     "nonzero_blocks": case["nonzero_blocks"],
                     "output_stream_sha256": case["output_stream_sha256"],
                     "auxiliary_object_counts": sorted({
                         row["auxiliary_object_count"]
                         for row in case["observations"]}),
                     "native_controller_values": sorted({
                         tuple(row["auxiliary_controllers"])
                         for row in case["observations"]}),
                     "lookahead_gate_true_blocks": sum(
                         row["lookahead_gate"] for row in case["observations"]),
                     "auxiliary_spectrum_present_blocks": sum(
                         row["auxiliary_spectrum_sha256"] is not None
                         for row in case["observations"]),
                     "distinct_auxiliary_spectrum_digests": len({
                         row["auxiliary_spectrum_sha256"]
                         for row in case["observations"]}),
                     "candidate_first_harmonic_positive_blocks": sum(
                         (row["candidate_first_harmonic_hz"] or 0) > 0
                         for row in case["observations"]),
                     "candidate_phase_nodes": sorted({
                         row["candidate_phase_nodes"]
                         for row in case["observations"]}),
                     "peak_abs_pcm": max(
                         row["peak_abs"] for row in case["output_blocks"]),
                     "clipped_pcm_samples": sum(
                         row["clipped_samples"] for row in case["output_blocks"])}
                    for case in cases]
    if report_cases[0]["auxiliary_object_counts"] != [0] or any(
            row["auxiliary_object_counts"] != [3] for row in report_cases[1:]):
        raise AssertionError("loaded auxiliary objects were not retained")
    expected_changed = ({"unbound": 0, "tense": 56, "breath_d": 56,
                         "breath_14": 56, "breath": 56, "both": 56,
                         "post_zero": 0, "post_one": 0}
                        if args.ordinary_onset_matrix else
                        {"unbound": 0, "tense": 0, "breath_d": 56,
                         "breath_14": 0, "breath": 56, "both": 56,
                         "post_zero": 56, "post_one": 56})
    if any(comparisons[mode]["output_pcm_blocks_changed"] != count or
           comparisons[mode][("auxiliary_spectrum_blocks_changed"
                               if args.ordinary_onset_matrix else
                               "final_spectrum_blocks_changed")] != count
           for mode, count in expected_changed.items()):
        raise AssertionError(f"auxiliary PCM routing result changed: {comparisons}")
    if any(row["lookahead_gate_true_blocks"] or
           (row["auxiliary_spectrum_present_blocks"] !=
            (56 if args.ordinary_onset_matrix else 0)) or
           row["clipped_pcm_samples"] or
           row["nonzero_blocks"] !=
            ((15 if row["mode"] in ("unbound", "off", "post_zero", "post_one")
              else 56) if args.ordinary_onset_matrix else 56)
           for row in report_cases):
        raise AssertionError(f"auxiliary PCM fixture state changed: {report_cases}")
    report = {"dse_sha256": SHA256, "vdm_sha256": VDM_SHA256,
              "auxiliary_vqm_sha256": VQM_SHA256,
              "comparisons_to_off": comparisons, "cases": report_cases,
              "scope": ("Offline DSE with loaded TENSE/BRWSP objects and an "
                        "owned ordinary-primary two-layer event through "
                        "source-frame frequency dispatch and final int16 PCM. "
                        "No Editor process or installed bank PCM."
                        if args.ordinary_onset_matrix else
                        "Offline DSE with genuinely loaded auxiliary category "
                        "objects and an owned two-layer ordinary event through "
                        "final int16 PCM. The current event shape does not enter "
                        "the source-frame frequency dispatch; its category "
                        "post-processing stage does affect final PCM. This "
                        "does not establish auxiliary source-frame use. "
                        "No Editor process or installed bank PCM.")}
    if args.output:
        args.output.write_text(json.dumps(report, indent=2) + "\n",
                               encoding="utf-8")
    else:
        print(json.dumps(report))


if __name__ == "__main__":
    main()
