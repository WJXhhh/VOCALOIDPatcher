"""Resolve the optional VQM.vdb path from an offline VDM manager object.

DSE 6.13.0.1 wraps the VDM manager in its global database object. The VQM
loader takes that wrapper's +0x18 path object, renders it with 0x180106ba0,
replaces the final path component with VQM.vdb, and tries fopen. This probe
reads that path object in a separate process without starting the Editor.
"""

from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
import math
from pathlib import Path

from audit_frm2_numeric import SHA256


class MemoryBasicInformation(c.Structure):
    _fields_ = [
        ("base_address", c.c_void_p),
        ("allocation_base", c.c_void_p),
        ("allocation_protect", c.c_uint32),
        ("align1", c.c_uint32),
        ("region_size", c.c_size_t),
        ("state", c.c_uint32),
        ("protect", c.c_uint32),
        ("type", c.c_uint32),
        ("align2", c.c_uint32),
    ]


def require_readable(address: int, length: int) -> int:
    query = c.windll.kernel32.VirtualQuery
    query.argtypes = [c.c_void_p, c.POINTER(MemoryBasicInformation), c.c_size_t]
    query.restype = c.c_size_t
    info = MemoryBasicInformation()
    if query(address, c.byref(info), c.sizeof(info)) != c.sizeof(info):
        raise ValueError("path object is not mapped")
    if (info.state != 0x1000 or info.protect & 0x101 or
            address + length > info.base_address + info.region_size):
        raise ValueError("path object is not fully readable")
    return info.base_address + info.region_size - address


def run(vdm_dll: Path, dse_dll: Path, explib: Path,
        exercise_engine: bool = False) -> dict:
    dse_sha256 = hashlib.sha256(dse_dll.read_bytes()).hexdigest()
    if dse_sha256 != SHA256:
        raise ValueError("DSE binary differs from verified 6.13.0.1 layout")
    vdm_sha256 = hashlib.sha256(vdm_dll.read_bytes()).hexdigest()
    library = c.CDLL(str(vdm_dll))
    dse = c.CDLL(str(dse_dll))
    create = library.VDM_createDatabaseManager
    create.argtypes = [c.c_wchar_p, c.c_wchar_p, c.POINTER(c.c_int)]
    create.restype = c.c_void_p
    destroy = library.VDM_DatabaseManager_destroy
    destroy.argtypes = [c.c_void_p]
    destroy.restype = None
    status = c.c_int(-1)
    manager = create("VOCALOID6", str(explib), c.byref(status))
    if not manager or status.value != 0:
        if manager:
            destroy(manager)
        raise RuntimeError("offline VDM manager did not initialize")
    make_dse = dse.VIS_DSE_CreateManager
    make_dse.argtypes = []
    make_dse.restype = c.c_void_p
    initialize = dse.VIS_DSE_InitializeManager
    initialize.argtypes = [c.c_void_p, c.c_void_p]
    initialize.restype = c.c_int
    terminate = dse.VIS_DSE_TerminateManager
    terminate.argtypes = [c.c_void_p]
    terminate.restype = c.c_int
    destroy_dse = dse.VIS_DSE_DestroyManager
    destroy_dse.argtypes = [c.c_void_p]
    destroy_dse.restype = None
    dse_manager = make_dse()
    initialized = False
    try:
        if not dse_manager:
            raise RuntimeError("offline DSE manager was not created")
        if initialize(dse_manager, manager) != 0:
            raise RuntimeError("offline DSE manager did not initialize")
        initialized = True
        global_database = dse._handle + 0xa9e7c0
        require_readable(global_database + 0x18, 8)
        path_object = c.c_void_p.from_address(global_database + 0x18).value
        if not path_object:
            raise ValueError("DSE database wrapper has no path object")
        readable = require_readable(path_object + 0x18, 1)
        buffer = c.string_at(path_object + 0x18, min(1024, readable))
        raw = buffer.split(b"\0", 1)[0]
        if not raw or b"\0" not in buffer:
            raise ValueError("VDM path is not terminated within bounds")
        database_path = Path(raw.decode("utf-8"))
        candidate = database_path.parent / "VQM.vdb"
        expected = explib / "VQM.vdb"
        if not candidate.is_file():
            raise AssertionError("DSE-derived VQM.vdb candidate does not exist")
        if candidate.resolve() != expected.resolve():
            raise AssertionError("DSE-derived candidate is not the supplied Explib file")
        engine_slot = dse._handle + 0xaa69d0
        require_readable(engine_slot, 8)
        engine_created = bool(c.c_void_p.from_address(engine_slot).value)
        result = {
            "dse_sha256": dse_sha256,
            "vdm_sha256": vdm_sha256,
            "database_path_parent_is_explib": database_path.parent.resolve() == explib.resolve(),
            "loader_candidate_is_explib_vqm": True,
            "candidate_sha256": hashlib.sha256(candidate.read_bytes()).hexdigest(),
            "engine_instance_exists_after_manager_init": engine_created,
            "scope": "Offline VDM/DSE manager initialization, live DSE database "
                     "wrapper path, and DSE 6.13.0.1 loader rule. "
                     "The Editor host's runtime call was not observed.",
        }
        if exercise_engine:
            register = c.CFUNCTYPE(c.c_int, c.c_int)(dse._handle + 0x1cf5e0)
            slot = register(0x13000202)
            engine_probe = {"registered_slot": slot}
            if 0 <= slot < 32:
                config = (c.c_ubyte * 32)()
                c.c_int.from_buffer(config, 0).value = 44100
                c.c_int.from_buffer(config, 4).value = 256
                setup = c.CFUNCTYPE(c.c_uint64, c.c_uint32, c.c_void_p)(
                    dse._handle + 0x1d0780)
                setup_result = setup(slot, c.addressof(config))
                engine_probe["setup_result"] = setup_result
                slot_address = engine_slot + 8 * slot
                require_readable(slot_address, 8)
                engine = c.c_void_p.from_address(slot_address).value
                engine_probe["engine_instance_exists"] = bool(engine)
                if engine and setup_result == 0:
                    require_readable(engine + 0x2b118, 8)
                    begin = c.c_void_p.from_address(engine + 0x2b110).value
                    end = c.c_void_p.from_address(engine + 0x2b118).value
                    if not begin or not end or end < begin or (end - begin) % 0x50:
                        raise AssertionError("invalid VQM category vector")
                    count = (end - begin) // 0x50
                    if count > 16:
                        raise AssertionError("unexpected VQM category count")
                    engine_probe["auxiliary_category_count"] = count
                    engine_probe["auxiliary_category_codes"] = [
                        c.c_int.from_address(begin + index * 0x50 + 0x20).value
                        for index in range(count)]
                    material_counts = []
                    for index in range(count):
                        category = begin + index * 0x50
                        sample_begin = c.c_void_p.from_address(
                            category + 0x28).value
                        sample_end = c.c_void_p.from_address(
                            category + 0x30).value
                        if (not sample_begin or not sample_end or
                                sample_end < sample_begin or
                                (sample_end - sample_begin) % 0x850):
                            raise AssertionError("invalid VQM material vector")
                        sample_count = (sample_end - sample_begin) // 0x850
                        if sample_count > 32:
                            raise AssertionError("unexpected VQM material count")
                        frame_counts = []
                        material_windows = []
                        for sample_index in range(sample_count):
                            record = sample_begin + sample_index * 0x850
                            require_readable(record + 0x800, 8)
                            region = c.c_void_p.from_address(record + 0x800).value
                            if not region:
                                raise AssertionError("VQM material has no frame region")
                            require_readable(region + 0x18, 4)
                            frame_counts.append(
                                c.c_int.from_address(region + 0x18).value)
                            material_windows.append({
                                "start_seconds": c.c_float.from_address(
                                    record).value,
                                "record_second_float": c.c_float.from_address(
                                    record + 4).value,
                                "reference_pitch_hz": c.c_float.from_address(
                                    record + 8).value,
                            })
                        material_counts.append({
                            "category_code": engine_probe[
                                "auxiliary_category_codes"][index],
                            "material_count": sample_count,
                            "frame_counts": frame_counts,
                            "material_windows": material_windows,
                        })
                    engine_probe["auxiliary_materials"] = material_counts
                    require_readable(engine + 0x2af38, 8)
                    object_begin = c.c_void_p.from_address(engine + 0x2af30).value
                    object_end = c.c_void_p.from_address(engine + 0x2af38).value
                    if not object_begin or not object_end or object_end < object_begin:
                        raise AssertionError("invalid VQM object vector")
                    engine_probe["vqm_object_count"] = (
                        object_end - object_begin) // 8
                    if (engine_probe["vqm_object_count"] > 16 or
                            (object_end - object_begin) % 8):
                        raise AssertionError("unexpected VQM object vector")
                    objects = []
                    for object_index in range(engine_probe["vqm_object_count"]):
                        obj = c.c_void_p.from_address(
                            object_begin + 8 * object_index).value
                        if not obj:
                            raise AssertionError("null VQM object")
                        require_readable(obj + 0x118, 1)
                        vtable = c.c_void_p.from_address(obj).value
                        record = c.c_void_p.from_address(obj + 8).value
                        if not vtable:
                            raise AssertionError("VQM object has no vtable")
                        require_readable(vtable + 0x48, 8)
                        if record:
                            require_readable(record + 0x20, 4)
                        objects.append({
                            "category_code": (c.c_int.from_address(
                                record + 0x20).value if record else 1),
                            "has_material_record": bool(record),
                            "active_flag": c.c_uint8.from_address(
                                obj + 0x118).value,
                            "vtable_rva": hex(vtable - dse._handle),
                            "bind_method_rva": hex(
                                c.c_void_p.from_address(vtable + 0x08).value
                                - dse._handle),
                            "active_method_rva": hex(
                                c.c_void_p.from_address(vtable + 0x10).value
                                - dse._handle),
                            "activate_method_rva": hex(
                                c.c_void_p.from_address(vtable + 0x18).value
                                - dse._handle),
                            "reset_method_rva": hex(
                                c.c_void_p.from_address(vtable + 0x20).value
                                - dse._handle),
                            "parameter_method_rva": hex(
                                c.c_void_p.from_address(vtable + 0x28).value
                                - dse._handle),
                            "frequency_method_rva": hex(
                                c.c_void_p.from_address(vtable + 0x38).value
                                - dse._handle),
                        })
                    engine_probe["vqm_objects"] = objects
                    if ([(entry["category_code"], entry["active_flag"],
                            entry["vtable_rva"],
                            entry["has_material_record"])
                            for entry in objects] != [
                                (1, 0, "0x245500", False),
                                (2, 1, "0x246258", True),
                                (3, 1, "0x2451b0", True),
                            ]):
                        raise AssertionError("VQM object routing differs")
                    require_readable(engine + 0x24158, 4)
                    require_readable(engine + 0x1d568, 8)
                    frame_index = c.c_int.from_address(engine + 0x24158).value
                    controls = c.c_void_p.from_address(engine + 0x1d568).value
                    if not controls:
                        raise AssertionError("engine has no control source")
                    interpolate_control = c.CFUNCTYPE(
                        c.c_float, c.c_void_p, c.c_int, c.c_int)(
                        dse._handle + 0x18adc0)
                    values = {
                        hex(identifier): interpolate_control(
                            controls, identifier, frame_index)
                        for identifier in (0x0d, 0x0e, 0x13, 0x14, 0x15)
                    }
                    if not all(math.isfinite(value) for value in values.values()):
                        raise AssertionError("non-finite native control value")
                    engine_probe["initial_frame_index"] = frame_index
                    engine_probe["initial_control_values"] = values
                    if (slot != 0 or count != 2 or
                            engine_probe["auxiliary_category_codes"] != [2, 3] or
                            [entry["frame_counts"] for entry in
                             material_counts] != [[148], [389]] or
                            engine_probe["vqm_object_count"] != 3):
                        raise AssertionError("offline VQM category load differs")
            result["engine_probe"] = engine_probe
            result["scope"] = ("Offline DSE engine setup after VDM manager initialization; "
                               "reads live auxiliary VQM records, object classes, "
                               "binding, activation flags, and dispatch slots. "
                               "The Editor host's runtime call was not observed.")
        return result
    finally:
        if dse_manager:
            if initialized:
                terminate(dse_manager)
            destroy_dse(dse_manager)
        destroy(manager)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--vdm-dll", type=Path, required=True)
    parser.add_argument("--dse-dll", type=Path, required=True)
    parser.add_argument("--explib", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--exercise-engine", action="store_true")
    args = parser.parse_args()
    args.output.write_text(json.dumps(run(args.vdm_dll, args.dse_dll,
                                          args.explib, args.exercise_engine),
                                      indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
