"""Validate native breath-note commits with V4X ART geometry and no breath STA."""

import argparse
import ctypes as c
from dataclasses import replace
import hashlib
import json
import math
import os
from pathlib import Path
import struct

from audit_frm2_numeric import Native, P, I, Q, F, D, B, SHA256
from analyze_reference_layers import walk_art
from probe_frm2 import UnvoicedFrame, UNVOICED_MASK, serialize_frame
from region_commit_material import stationary
from verify_region_event_production import run
from verify_region_frame_blend import cents
from verify_region_stationary_consumption import full_marker
from verify_planned_ddi import load_public_parser


_native_probe_base = 0


def report_native_exception_address():
    """Print the instruction address of a native access failure in a probe process."""
    callback_type = c.WINFUNCTYPE(c.c_long, c.c_void_p)

    def on_exception(pointers):
        record = P.from_address(pointers).value
        if record and c.c_uint32.from_address(record).value == 0xc0000005:
            instruction = P.from_address(record + 16).value
            operation = Q.from_address(record + 32).value
            target = Q.from_address(record + 40).value
            rva = instruction - _native_probe_base if _native_probe_base else 0
            os.write(2, f'native-access instruction={instruction:#x} '
                        f'rva={rva:#x} operation={operation} '
                        f'target={target:#x}\n'.encode('ascii'))
        return 0

    callback = callback_type(on_exception)
    kernel = c.WinDLL('kernel32', use_last_error=True)
    kernel.AddVectoredExceptionHandler.argtypes = [Q, callback_type]
    kernel.AddVectoredExceptionHandler.restype = P
    assert kernel.AddVectoredExceptionHandler(1, callback)
    return callback


def owned_short_file(work: Path, bank: str, token: str, edges: list, shorts: dict,
                     voiced_frames: bool = False):
    """Make one local FRM2/PCM file; only the V4X geometry comes from DDI."""
    work.mkdir(parents=True, exist_ok=True)
    blob = bytearray()
    shapes = {}
    for edge_index, edge in enumerate(edges):
        key = tuple(edge['key'])
        short = shorts.get(key)
        count = edge['frames'] * 256 + (short['pcm_margin'] if short is not None else 2048)
        core = short['core_delta_bytes'] // 2 if short is not None else 1024
        prefix = [((i * 137 + edge_index * 19) % 60001) - 30000 for i in range(2048)]
        blob.extend(struct.pack(f'<{len(prefix)}h', *prefix))
        snd_start = len(blob)
        samples = [((i * 193 + edge_index * 251) % 60001) - 30000 for i in range(count)]
        blob.extend(struct.pack(f'<{count}h', *samples))
        suffix = [((i * 271 + edge_index * 43) % 60001) - 30000 for i in range(2048)]
        blob.extend(struct.pack(f'<{len(suffix)}h', *suffix))
        frame_offsets = []
        for index in range(edge['frames']):
            frame_offsets.append(len(blob))
            pitch = float(220 + edge_index * 100 + index)
            if voiced_frames:
                frame = full_marker(index, pitch)
                frame = replace(frame, harmonic_envelope=replace(
                    frame.harmonic_envelope,
                    values=(0., 0., 1. / 3., 0., 2. / 3., 0., 1., 0.)))
                if not frame.secondary_resonances:
                    # The main loop reads secondary slot 0 when this frame's
                    # resonance mask and primary list are present.
                    frame = replace(frame, secondary_resonances=((1200. + index,
                                                                   .5 + index * .0625,
                                                                   80. + index),))
            else:
                frame = UnvoicedFrame(0, float(1000 + index), UNVOICED_MASK, pitch)
            blob.extend(serialize_frame(frame))
        shapes[key] = dict(samples=samples, core=core, snd_start=snd_start,
                           frame_offsets=frame_offsets, edge_index=edge_index,
                           frames=edge['frames'], is_short=short is not None)
    variant = 'voiced' if voiced_frames else 'unvoiced'
    # Concurrent probe processes must not truncate each other's source file.
    path = work / f'owned_{bank}_{token}_{variant}_{os.getpid()}_frames.bin'
    path.write_bytes(blob)
    crt = c.CDLL('ucrtbase.dll')
    crt._wfopen.argtypes, crt._wfopen.restype = [c.c_wchar_p, c.c_wchar_p], P
    crt.fclose.argtypes, crt.fclose.restype = [P], I
    handle = crt._wfopen(str(path.resolve()), 'rb')
    if not handle:
        raise OSError('Cannot open owned short-margin frame file')
    return crt, handle, shapes


def dispatch_breath_boundary(n: Native, context: int, ring: int, items: int,
                             material: dict):
    """Pass the committed ring to DSE's actual transition preparation dispatcher."""
    count = I.from_address(ring + 0x34).value
    assert count == 3
    previous, current = items + 0x140, items + 2 * 0x140
    assert P.from_address(previous + 0x20).value
    assert P.from_address(current + 0x20).value
    assert B.from_address(current + 0x98).value == 1
    assert B.from_address(current + 0xe4).value == 0
    assert P.from_address(current + 0xf8).value
    records = P.from_address(ring).value
    time = I.from_address(previous).value
    record = records + time * 0x18
    assert P.from_address(record).value == previous
    ctx, analysis = n.alloc(0x2b200), n.alloc(0x330)
    P.from_address(ctx).value = P.from_address(context).value
    P.from_address(ctx + 0x1d568).value = ring
    P.from_address(ctx + 0x1d578).value = ctx
    F.from_address(ctx + 0x2208).value = 440.
    F.from_address(ctx + 0x1d550).value = 256.
    F.from_address(ctx + 0x1d560).value = 44100.
    F.from_address(ctx + 0x1d564).value = 22050.
    D.from_address(ctx + 0x1d558).value = .01
    I.from_address(ctx + 0x24120).value = 1
    I.from_address(ctx + 0x24124).value = 2048
    I.from_address(ctx + 0x24158).value = I.from_address(current).value
    P.from_address(ctx + 0x24118).value = n.alloc(2049 * 2 + 32)
    pcm = n.alloc(2049 * 2 + 32)
    c.memset(pcm, 0xa5, 2049 * 2 + 32)
    P.from_address(ctx + 0x29510).value = pcm + 16
    n.fn(0x147d10, P, P, I, B, F, I, I, F, I)(
        analysis, 256, 0, 8., 50, 0, 0., 1)
    assert I.from_address(analysis + 0x8c).value == 2048
    P.from_address(ctx + 0x1fc8).value = analysis
    init = n.fn(0x12a670, P, P)
    for offset in (0x247b8, 0x247c0):
        frame = n.alloc(0x328)
        init(frame)
        n.fn(0x12ba70, Q, P, I, Q, I)(frame, 16, 0x30, 0)
        P.from_address(ctx + offset).value = frame
    # Use the native no-layer-mix setting from the established 143–144
    # transition fixture. The committed work items and source positions remain.
    B.from_address(ring + 0x78).value = 1
    n.fn(0x16af70, None, P, P)(ctx, record)
    prepared = B.from_address(current + 0xe4).value
    endpoints = []
    boundary_time = I.from_address(current).value
    for offset, source_item, source_time in (
            (0xa8, previous, boundary_time - 1),
            (0xb0, current, boundary_time)):
        frame = P.from_address(current + offset).value
        part = P.from_address(source_item + 0x20).value
        shape = material['all_shapes_by_part'][part]
        position = F.from_address(records + source_time * 0x18 + 8).value
        neighbors = sorted({max(0, min(shape['frames'] - 1, math.floor(position))),
                            max(0, min(shape['frames'] - 1, math.ceil(position)))})
        safe = (None if not shape['is_short'] else min(
            shape['frames'] - 1, (len(shape['samples']) - 2049) // 256))
        endpoints.append(dict(present=bool(frame),
                              key=material['all_keys_by_part'][part],
                              source_position=position, source_frames=neighbors,
                              cached_window_max_in_bounds_frame=safe,
                              beyond_complete_cache_capacity=(
                                  None if safe is None else max(neighbors) > safe),
                              time=D.from_address(frame + 0x148).value if frame else None,
                              pitch=F.from_address(frame + 0x1d0).value if frame else None,
                              mask=Q.from_address(frame + 0x150).value if frame else None))
    assert prepared == 1
    assert all(endpoint['present'] and
               abs(endpoint['time'] - (1000 + endpoint['source_position'])) < 1e-3
               for endpoint in endpoints)
    for source_item in (previous, current):
        part = P.from_address(source_item + 0x20).value
        if material['all_shapes_by_part'][part]['is_short']:
            assert I.from_address(part + 0x200).value == 0
            assert not P.from_address(part + 0x258).value
    fft = P.from_address(ctx + 0x247c8).value
    fft_nonzero = bool(fft and any(F.from_address(fft + k * 4).value != 0
                                   for k in range(16)))
    assert fft_nonzero
    assert c.string_at(pcm, 16) == b'\xa5' * 16
    assert c.string_at(pcm + 16 + 2049 * 2, 16) == b'\xa5' * 16
    current_shape = material['all_shapes_by_part'][P.from_address(current + 0x20).value]
    last_frame = endpoints[1]['source_frames'][-1]
    start = current_shape['core'] + last_frame * 256 - 1024
    samples = current_shape['samples']
    expected_pcm = [samples[k] if 0 <= k < len(samples) else 0
                    for k in range(start, start + 2049)]
    pcm_matches_current_last_frame = list((c.c_int16 * 2049).from_address(pcm + 16)) == expected_pcm
    assert pcm_matches_current_last_frame, (endpoints[1]['key'], endpoints[1]['source_position'])
    pcm_buffer_changed = c.string_at(pcm + 16, 2049 * 2) != b'\xa5' * (2049 * 2)
    assert pcm_buffer_changed
    return dict(prepared=prepared, boundary_time=boundary_time,
                endpoints=endpoints, fft_nonzero=fft_nonzero,
                pcm_buffer_changed=pcm_buffer_changed,
                pcm_matches_current_last_frame=pcm_matches_current_last_frame,
                last_pcm_source_frame=last_frame)


def probe_full_loop(n: Native, context: int, ring: int, time: int,
                    replay_from_start: bool = False,
                    analysis_mode: int = 1,
                    initialize_output_gate: bool = False,
                    reset_rng_each_block: bool = False,
                    clear_candidate_spectrum: bool = False,
                    initialize_native_candidate: bool = False,
                    spectrum_selection_byte: int = -1,
                    spectrum_snapshot_time: int = -1,
                    source_event_mode: int = -1,
                    compact_full_loop_trace: bool = False,
                    pcm_observer=None,
                    output_block_observer=None,
                    engine_prepare=None) -> dict:
    """Process owned records through the output loop in fresh native state."""
    global _native_probe_base
    _native_probe_base = n.lib._handle
    ctx = n.alloc(0x2b200)
    n.fn(0x163f40, P, P, P, P)(ctx, 0, 0)
    source_registry = P.from_address(context).value
    registry = n.alloc(0x100)
    c.memmove(registry, source_registry, 0x100)
    empty_tree = n.alloc(0x30)
    for offset in (0, 8, 16):
        P.from_address(empty_tree + offset).value = empty_tree
    B.from_address(empty_tree + 0x19).value = 1
    P.from_address(registry + 0x38).value = empty_tree
    P.from_address(ctx).value = registry
    P.from_address(ctx + 0x1d568).value = ring
    P.from_address(ctx + 0x1d570).value = ring
    P.from_address(ctx + 0x1d578).value = ctx
    original_spectrum_selection = B.from_address(ring + 0x78).value
    effective_spectrum_selection = (source_event_mode if spectrum_selection_byte < 0
                                    else spectrum_selection_byte)
    if effective_spectrum_selection >= 0:
        B.from_address(ring + 0x78).value = effective_spectrum_selection
    F.from_address(ctx + 0x2208).value = 440.
    F.from_address(ctx + 0x1d550).value = 256.
    F.from_address(ctx + 0x1d560).value = 44100.
    F.from_address(ctx + 0x1d564).value = 22050.
    D.from_address(ctx + 0x1d558).value = .01
    start_time = 0 if replay_from_start else time
    D.from_address(ctx + 0x24150).value = start_time * .01
    I.from_address(ctx + 0x24158).value = start_time
    spectral_control = n.alloc(0x80)
    n.fn(0x162aa0, P, P, P, F)(spectral_control, ring, 44100.)
    P.from_address(ctx + 0x2ae98).value = spectral_control
    noise_control = n.alloc(0x18)
    n.fn(0x1cdea0, P, P, P, F)(noise_control, ring, 44100.)
    P.from_address(ctx + 0x2aea0).value = noise_control
    records = P.from_address(ring).value
    item = P.from_address(records + time * 0x18).value
    I.from_address(ctx + 0x247e8).value = I.from_address(item + 0x11c).value
    I.from_address(ctx + 0x247ec).value = I.from_address(item + 0x120).value
    assert analysis_mode in (1, 2)
    I.from_address(ctx + 0x24120).value = analysis_mode
    analysis_window = 2048
    max_fft_bin = ((analysis_window - 1) * analysis_mode - 1).bit_length()
    # Keep the earlier mode-1 fixture's scratch size for evidence 219–221.
    fft_bins = (1 << (max_fft_bin - 1)) + 1 if analysis_mode == 2 else 2048
    I.from_address(ctx + 0x24124).value = fft_bins
    harmonic_scratch_floats = 2 * I.from_address(ctx + 0x24124).value + 2
    P.from_address(ctx + 0x1938).value = n.alloc(harmonic_scratch_floats * 4)
    Q.from_address(ctx + 0x1940).value = harmonic_scratch_floats
    P.from_address(ctx + 0x24118).value = n.alloc(2049 * 2 + 32)
    P.from_address(ctx + 0x29510).value = n.alloc(2049 * 2 + 32) + 16
    config = ctx + 0x2ee0
    I.from_address(config + 0x1a238).value = 256
    F.from_address(config + 0x1a234).value = 8.
    F.from_address(config + 0x1a23c).value = 50.
    B.from_address(config + 0x1a230).value = 11
    analysis = n.alloc(0x3d8)
    analysis_rate = F.from_address(n.lib._handle + 0x5bfe08).value
    n.fn(0x141df0, P, P, P, F, I)(analysis, config, analysis_rate, analysis_mode)
    assert I.from_address(analysis + 0x8c).value == 2048
    P.from_address(ctx + 0x1fc8).value = analysis
    secondary_analysis = n.alloc(0x3d8)
    n.fn(0x141df0, P, P, P, F, I)(secondary_analysis, config, analysis_rate,
                                 analysis_mode)
    P.from_address(ctx + 0x2b128).value = secondary_analysis
    if initialize_output_gate:
        # FUN_180170860 sets this byte after creating the analysis objects.
        B.from_address(ctx + 0x2af28).value = 1
    init = n.fn(0x12a670, P, P)
    previous = n.alloc(0x328)
    init(previous)
    P.from_address(ctx + 0x1950).value = previous
    initial_candidates = []
    for offset in (0x247b8, 0x247c0):
        frame = n.alloc(0x328)
        init(frame)
        frame_bins = I.from_address(ctx + 0x24124).value if initialize_native_candidate else 16
        n.fn(0x12ba70, Q, P, I, Q, I)(frame, frame_bins, 0x30, 0)
        if clear_candidate_spectrum or initialize_native_candidate:
            # FUN_1801757a0 clears both spectra after allocating the full size.
            bins = I.from_address(frame + 0x1a8).value
            assert 0 < bins <= 4096
            for spectrum_offset in (0x1b0, 0x1b8):
                spectrum = P.from_address(frame + spectrum_offset).value
                assert spectrum
                c.memset(spectrum, 0, bins * 4)
        P.from_address(ctx + offset).value = frame
        initial_candidates.append(frame)
    previous_record = records + (time - 1) * 0x18 if time > 0 else 0
    previous_ready = bool(not replay_from_start and previous_record and
                          P.from_address(previous_record).value and
                          F.from_address(previous_record + 8).value >= 0)
    if previous_ready:
        n.fn(0x16a870, Q, P, I, P, P, B, B)(
            ctx, time - 1, previous, previous_record, 1, 1)
    events, output = n.alloc(16), n.alloc(0x408 + 32)
    I.from_address(events).value = 0
    if engine_prepare is not None:
        engine_prepare(ctx, ring)
    produce = n.fn(0x168940, Q, P, P, P, P, P)
    controller_value = n.fn(0x18adc0, F, P, I, I)
    seed_random = None
    if reset_rng_each_block:
        crt = c.CDLL('ucrtbase.dll')
        seed_random = crt.srand
        seed_random.argtypes = [I]
        seed_random.restype = None
    nonzero_blocks = 0
    analysis_nonzero_blocks = 0
    analysis_nonzero_times = []
    analysis_trace = []
    first_nonzero_time = None
    max_active_voices = 0
    frame_state_trace = []
    spectrum_snapshots = []
    output_stream_digest = hashlib.sha256()
    for expected_time in range(start_time, time + 1):
        c.memset(output, 0xa5, 0x408 + 32)
        I.from_address(output + 0x400).value = 0
        I.from_address(output + 0x404).value = 0
        if seed_random is not None:
            # Diagnostic only: control the CRT stream before this output block.
            seed_random(0x17)
        try:
            status = produce(ctx, events, output, 0, 0)
        except OSError as exc:
            raise RuntimeError(f'output loop failed at block {expected_time}, '
                               f'target block {time}') from exc
        byte_count = I.from_address(output + 0x404).value
        assert status == 0 and I.from_address(output + 0x400).value == 1
        assert byte_count == 512
        assert I.from_address(ctx + 0x24158).value == expected_time + 1
        assert c.string_at(output + 0x408, 32) == b'\xa5' * 32
        block = c.string_at(output, byte_count)
        output_stream_digest.update(block)
        if output_block_observer is not None:
            output_block_observer(expected_time, block)
        if pcm_observer is not None:
            pcm_observer(expected_time, ctx)
        frame = ctx + 0x2240
        harmonics = I.from_address(frame + 0x158).value
        harmonic_frequency = P.from_address(frame + 0x160).value
        harmonic_amplitude = P.from_address(frame + 0x168).value
        harmonic_phase = P.from_address(frame + 0x170).value
        harmonic_bytes = (c.string_at(harmonic_frequency, harmonics * 4) +
                          c.string_at(harmonic_amplitude, harmonics * 4) +
                          c.string_at(harmonic_phase, harmonics * 4)
                          if 0 < harmonics <= 64 and harmonic_frequency and
                          harmonic_amplitude and harmonic_phase else b'')
        spectrum_bins = I.from_address(frame + 0x1a8).value
        spectrum_real = P.from_address(frame + 0x1b0).value
        spectrum_imag = P.from_address(frame + 0x1b8).value
        spectrum_bytes = (c.string_at(spectrum_real, spectrum_bins * 4) +
                          c.string_at(spectrum_imag, spectrum_bins * 4)
                          if 0 < spectrum_bins <= 4096 and spectrum_real and
                          spectrum_imag else b'')
        candidate_frame = P.from_address(ctx + 0x247b8).value
        candidate_bins = I.from_address(candidate_frame + 0x1a8).value if candidate_frame else 0
        candidate_real = P.from_address(candidate_frame + 0x1b0).value if candidate_frame else 0
        candidate_imag = P.from_address(candidate_frame + 0x1b8).value if candidate_frame else 0
        candidate_spectrum = (c.string_at(candidate_real, candidate_bins * 4) +
                              c.string_at(candidate_imag, candidate_bins * 4)
                              if 0 < candidate_bins <= 4096 and candidate_real and
                              candidate_imag else b'')
        if expected_time == spectrum_snapshot_time:
            input_pcm = P.from_address(ctx + 0x29510).value
            scratch_pcm = P.from_address(ctx + 0x24118).value
            fft_work = P.from_address(ctx + 0x247c8).value
            alternate_frame = P.from_address(ctx + 0x247c0).value
            alternate_bins = I.from_address(alternate_frame + 0x1a8).value if alternate_frame else 0
            alternate_real = P.from_address(alternate_frame + 0x1b0).value if alternate_frame else 0
            alternate_imag = P.from_address(alternate_frame + 0x1b8).value if alternate_frame else 0
            alternate_spectrum = (c.string_at(alternate_real, alternate_bins * 4) +
                                  c.string_at(alternate_imag, alternate_bins * 4)
                                  if 0 < alternate_bins <= 4096 and alternate_real and
                                  alternate_imag else b'')
            spectrum_snapshots.append(dict(
                time=expected_time,
                candidate_bins=candidate_bins,
                spectrum_selection_byte=B.from_address(ring + 0x78).value,
                spectrum_controller_18=controller_value(ring, 0x12, expected_time),
                candidate_mask=Q.from_address(candidate_frame + 0x150).value
                if candidate_frame else None,
                candidate_initial_slot=initial_candidates.index(candidate_frame)
                if candidate_frame in initial_candidates else None,
                candidate_spectrum_hex=candidate_spectrum.hex(),
                alternate_bins=alternate_bins,
                alternate_mask=Q.from_address(alternate_frame + 0x150).value
                if alternate_frame else None,
                alternate_initial_slot=initial_candidates.index(alternate_frame)
                if alternate_frame in initial_candidates else None,
                alternate_spectrum_hex=alternate_spectrum.hex(),
                final_spectrum_hex=spectrum_bytes.hex(),
                input_pcm_sha256=hashlib.sha256(c.string_at(input_pcm, 4098)).hexdigest()
                if input_pcm else None,
                scratch_pcm_sha256=hashlib.sha256(c.string_at(scratch_pcm, 4098)).hexdigest()
                if scratch_pcm else None,
                # Mode 2 allocates 2 * 2049 floats for this FFT work array.
                fft_work_sha256=hashlib.sha256(c.string_at(fft_work,
                    candidate_bins * 8)).hexdigest()
                if fft_work and analysis_mode == 2 and candidate_bins == 2049 else None))
        context_scalars = tuple(I.from_address(ctx + offset).value for offset in
                                (0x1930, 0x2412c, 0x2415c, 0x24160,
                                 0x249c0, 0x2ab64, 0x2aef8))
        if not compact_full_loop_trace:
            frame_state_trace.append((expected_time,
                                  Q.from_address(frame + 0x148).value,
                                  Q.from_address(frame + 0x150).value,
                                  I.from_address(frame + 0x158).value,
                                  I.from_address(frame + 0x1d0).value,
                                  B.from_address(ctx + 0x2af28).value,
                                  B.from_address(ctx + 0x2b180).value,
                                  I.from_address(ctx + 0x247e8).value,
                                  I.from_address(ctx + 0x247ec).value,
                                  hashlib.sha256(harmonic_bytes).hexdigest(),
                                  hashlib.sha256(spectrum_bytes).hexdigest(),
                                  spectrum_bins,
                                  sum(value != 0 for value in spectrum_bytes),
                                  hashlib.sha256(candidate_spectrum).hexdigest(),
                                  context_scalars))
        analysis_pcm = P.from_address(analysis + 0x3d0).value
        if analysis_pcm and any(c.string_at(analysis_pcm, byte_count)):
            analysis_nonzero_blocks += 1
            analysis_nonzero_times.append(expected_time)
            analysis_trace.append(dict(time=expected_time,
                                       active_analysis_matches=(P.from_address(ctx + 0x1fc8).value
                                                                == analysis),
                                       cutoff=B.from_address(ctx + 0x2b180).value,
                                       cutoff_end=I.from_address(ctx + 0x2b184).value,
                                       mix_gate=B.from_address(ctx + 0x2af28).value,
                                       output_nonzero_bytes=sum(value != 0 for value in block)))
        if any(block):
            nonzero_blocks += 1
            if first_nonzero_time is None:
                first_nonzero_time = expected_time
        active_begin = P.from_address(ctx + 0x2b168).value
        active_end = P.from_address(ctx + 0x2b170).value
        if active_begin:
            max_active_voices = max(max_active_voices,
                                    (active_end - active_begin) // 8)
    candidate = P.from_address(ctx + 0x247b8).value
    phase_curve = P.from_address(candidate + 0x218).value if candidate else 0
    analysis_pcm = P.from_address(analysis + 0x3d0).value
    analysis_pcm_bytes = c.string_at(analysis_pcm, byte_count) if analysis_pcm else b''
    post_mix = None
    if any(analysis_pcm_bytes):
        mixed_output = n.alloc(0x408 + 32)
        c.memset(mixed_output, 0xa5, 0x408 + 32)
        n.fn(0x171e10, None, P, P, B, P, B, F)(ctx, mixed_output, 0, 0, 0, 1.)
        mixed_bytes = I.from_address(mixed_output + 0x404).value
        assert I.from_address(mixed_output + 0x400).value == 1
        assert mixed_bytes == byte_count
        assert c.string_at(mixed_output + 0x408, 32) == b'\xa5' * 32
        mixed = c.string_at(mixed_output, mixed_bytes)
        post_mix = dict(nonzero_bytes=sum(value != 0 for value in mixed),
                        sha256=hashlib.sha256(mixed).hexdigest())
    final_frame = ctx + 0x2240
    active_begin = P.from_address(ctx + 0x2b168).value
    active_end = P.from_address(ctx + 0x2b170).value
    B.from_address(ring + 0x78).value = original_spectrum_selection
    return dict(time=time, analysis_mode=analysis_mode,
                initialized_output_gate=initialize_output_gate,
                reset_rng_each_block=reset_rng_each_block,
                cleared_candidate_spectrum=(clear_candidate_spectrum or initialize_native_candidate),
                initialized_native_candidate=initialize_native_candidate,
                original_spectrum_selection_byte=original_spectrum_selection,
                spectrum_selection_override=spectrum_selection_byte,
                effective_spectrum_selection_byte=effective_spectrum_selection,
                source_event_mode=source_event_mode,
                previous_frame_loaded=previous_ready, status=status,
                blocks_run=time - start_time + 1,
                nonzero_blocks=nonzero_blocks,
                analysis_nonzero_blocks=analysis_nonzero_blocks,
                analysis_nonzero_times=analysis_nonzero_times,
                analysis_trace=analysis_trace,
                frame_state_trace=frame_state_trace,
                spectrum_snapshots=spectrum_snapshots,
                compact_full_loop_trace=compact_full_loop_trace,
                first_nonzero_time=first_nonzero_time,
                max_active_voice_count=max_active_voices,
                output_ready=I.from_address(output + 0x400).value,
                output_bytes=byte_count,
                nonzero_output_bytes=sum(value != 0 for value in block),
                output_sha256=hashlib.sha256(block).hexdigest(),
                output_stream_sha256=output_stream_digest.hexdigest(),
                candidate_harmonics=I.from_address(candidate + 0x158).value if candidate else 0,
                candidate_phase_curve_nodes=(I.from_address(phase_curve + 0x15c).value
                                             if phase_curve else 0),
                final_frame_flags=I.from_address(final_frame + 0x150).value,
                final_frame_harmonics=I.from_address(final_frame + 0x158).value,
                final_frame_pitch=F.from_address(final_frame + 0x1d0).value,
                final_mix_gate=B.from_address(ctx + 0x2af28).value,
                active_analysis_matches=P.from_address(ctx + 0x1fc8).value == analysis,
                end_cutoff_gate=B.from_address(ctx + 0x2b180).value,
                gain_controller=controller_value(ring, 2, time),
                pitch_controller=controller_value(ring, 9, time),
                analysis_nonzero_bytes=sum(value != 0 for value in analysis_pcm_bytes),
                post_mix=post_mix,
                active_voice_count=(active_end - active_begin) // 8 if active_begin else 0,
                guard_intact=True, next_time=time + 1)


def consume_main_records(n: Native, context: int, ring: int, items: int,
                         material: dict, all_records: bool = False,
                         wrapper_budget: list[int] | None = None,
                         all_wrapper: bool = False,
                         next_stage_budget: list[int] | None = None,
                         full_loop_budget: list[int] | None = None,
                         full_loop_seen: set | None = None,
                         replay_full_loop: bool = False,
                         analysis_mode: int = 1,
                         initialize_output_gate: bool = False,
                         reset_rng_each_block: bool = False,
                         clear_candidate_spectrum: bool = False,
                         initialize_native_candidate: bool = False,
                         spectrum_selection_byte: int = -1,
                         spectrum_snapshot_time: int = -1,
                         source_event_mode: int = -1,
                         full_loop_per_short_item: bool = False,
                         compact_full_loop_trace: bool = False):
    """Read committed records through the main loop's frame-selection stages."""
    ctx, analysis = n.alloc(0x2b200), n.alloc(0x330)
    P.from_address(ctx).value = P.from_address(context).value
    P.from_address(ctx + 0x1d568).value = ring
    F.from_address(ctx + 0x2208).value = 440.
    F.from_address(ctx + 0x1d550).value = 256.
    F.from_address(ctx + 0x1d560).value = 44100.
    F.from_address(ctx + 0x1d564).value = 22050.
    D.from_address(ctx + 0x1d558).value = .01
    I.from_address(ctx + 0x24120).value = 1
    I.from_address(ctx + 0x24124).value = 2048
    P.from_address(ctx + 0x24118).value = n.alloc(2049 * 2 + 32)
    pcm = n.alloc(2049 * 2 + 32)
    P.from_address(ctx + 0x29510).value = pcm + 16
    n.fn(0x147d10, P, P, I, B, F, I, I, F, I)(
        analysis, 256, 0, 8., 50, 0, 0., 1)
    assert I.from_address(analysis + 0x8c).value == 2048
    P.from_address(ctx + 0x1fc8).value = analysis
    init = n.fn(0x12a670, P, P)
    if next_stage_budget is not None:
        for offset in (0x27ef8, 0x28220, 0x28548, 0x28870,
                       0x28b98, 0x28ec0, 0x291e8, 0x29538):
            init(ctx + offset)
    for offset in (0x247b8, 0x247c0):
        candidate = n.alloc(0x328)
        init(candidate)
        n.fn(0x12ba70, Q, P, I, Q, I)(candidate, 16, 0x30, 0)
        P.from_address(ctx + offset).value = candidate
    output = n.alloc(0x328)
    init(output)
    clear = n.fn(0x12cf40, Q, P, Q, I, I)
    select = n.fn(0x16a870, Q, P, I, P, P, B, B)
    wrapper = (n.fn(0x16a250, Q, P, I, P, P, P, B)
               if wrapper_budget is not None or all_wrapper else None)
    next_stage = (n.fn(0x165350, None, P, I, P, P)
                  if next_stage_budget is not None else None)
    score = n.alloc(0x100) if wrapper is not None else 0
    B.from_address(ring + 0x78).value = 1
    records = P.from_address(ring).value
    results = {}
    full_loop_candidates = []
    for j in range(I.from_address(ring + 0x34).value):
        item = items + j * 0x140
        part = P.from_address(item + 0x20).value
        shape = material['short_shapes_by_part'].get(part)
        if shape is None:
            continue
        assert I.from_address(item + 0x10).value != -1
        assert I.from_address(item + 0x14).value != -1
        assert I.from_address(part + 0x200).value == 0
        assert not P.from_address(part + 0x258).value
        begin, end = I.from_address(item).value, I.from_address(item + 4).value
        positions = [(t, F.from_address(records + t * 0x18 + 8).value)
                     for t in range(begin, end)]
        assert all(P.from_address(records + t * 0x18).value == item
                   for t, _ in positions)
        valid = [(t, position) for t, position in positions
                 if math.isfinite(position) and position >= 0]
        assert valid
        safe = min(shape['frames'] - 1, (len(shape['samples']) - 2049) // 256)
        candidate = lambda position: min(shape['frames'] - 1, math.ceil(position))
        beyond = [(t, position) for t, position in valid if candidate(position) > safe]
        sampled = {valid[0][0], valid[-1][0], max(valid, key=lambda pair: pair[1])[0]}
        if beyond:
            sampled.add(beyond[0][0])
        sampled_rows = []
        wrapped = []
        staged = []
        full_loop = []
        digest = hashlib.sha256()
        wrapper_digest = hashlib.sha256()
        selected_above = 0
        wrapper_above = 0
        wrapper_count = 0
        selected_times = ([t for t, _ in valid]
                          if all_records or all_wrapper else sorted(sampled))
        wrapper_target = beyond[0][0] if beyond else valid[0][0]
        edge_id = (material['bank_name'], tuple(material['short_keys_by_part'][part]))
        full_loop_candidates.extend((j, edge_id, category, target)
                                    for category, target in (
                                        ('first', valid[0][0]),
                                        ('condition_or_last', wrapper_target if beyond else valid[-1][0])))
        for t in selected_times:
            position = F.from_address(records + t * 0x18 + 8).value
            index = candidate(position)
            start = shape['core'] + index * 256 - 1024
            samples = shape['samples']
            expected_pcm = [samples[k] if 0 <= k < len(samples) else 0
                            for k in range(start, start + 2049)]
            run_wrapper = (all_wrapper or (wrapper_budget is not None and
                                          wrapper_budget[0] > 0 and t == wrapper_target))
            if run_wrapper:
                clear(output, 0x6f0fffffff, 1, 0)
                c.memset(pcm, 0xa5, 2049 * 2 + 32)
                c.memset(score, 0, 0x100)
                P.from_address(ctx + 0x2238).value = output
                I.from_address(ctx + 0x24158).value = t
                assert wrapper(ctx, t, output, records + t * 0x18, score, 0) == 0
                assert list((c.c_int16 * 2049).from_address(pcm + 16)) == expected_pcm
                assert c.string_at(pcm, 16) == b'\xa5' * 16
                assert c.string_at(pcm + 16 + 2049 * 2, 16) == b'\xa5' * 16
                wrapper_time = D.from_address(output + 0x148).value
                wrapper_pitch = F.from_address(output + 0x1d0).value
                assert abs(wrapper_time - (1000 + position)) < 1e-3
                assert math.isfinite(wrapper_pitch)
                wrapper_mask = Q.from_address(output + 0x150).value
                wrapper_digest.update(struct.pack('<IfIQfd', t, position, index,
                                                  wrapper_mask, wrapper_pitch, wrapper_time))
                wrapper_count += 1
                wrapper_above += index > safe
                if not all_wrapper or t in sampled:
                    wrapped.append(dict(output_time=t, source_position=position,
                                        last_source_frame=index, output_mask=wrapper_mask,
                                        output_pitch=wrapper_pitch,
                                        output_frame_time=wrapper_time))
                if wrapper_budget is not None:
                    wrapper_budget[0] -= 1
                if next_stage is not None and next_stage_budget[0] > 0:
                    before_bytes = c.string_at(output, 0x328)
                    next_stage(ctx, t, output, records + t * 0x18)
                    after_bytes = c.string_at(output, 0x328)
                    changed_offsets = [offset for offset, (before, after) in
                                       enumerate(zip(before_bytes, after_bytes))
                                       if before != after]
                    staged.append(dict(output_time=t, source_position=position,
                                       before_mask=wrapper_mask,
                                       after_mask=Q.from_address(output + 0x150).value,
                                       before_pitch=wrapper_pitch,
                                       after_pitch=F.from_address(output + 0x1d0).value,
                                       before_frame_time=wrapper_time,
                                       after_frame_time=D.from_address(output + 0x148).value,
                                       changed_output_bytes=len(changed_offsets),
                                       first_changed_offsets=changed_offsets[:16]))
                    next_stage_budget[0] -= 1
            if all_wrapper:
                continue
            clear(output, 0x6f0fffffff, 1, 0)
            c.memset(pcm, 0xa5, 2049 * 2 + 32)
            select(ctx, t, output, records + t * 0x18, 1, 1)
            time = D.from_address(output + 0x148).value
            assert abs(time - (1000 + position)) < 1e-3, (j, t, position, time)
            assert list((c.c_int16 * 2049).from_address(pcm + 16)) == expected_pcm, (j, t, index)
            assert c.string_at(pcm, 16) == b'\xa5' * 16
            assert c.string_at(pcm + 16 + 2049 * 2, 16) == b'\xa5' * 16
            fft = P.from_address(ctx + 0x247c8).value
            assert fft and any(F.from_address(fft + k * 4).value != 0 for k in range(16))
            digest.update(struct.pack('<IfId', t, position, index, time))
            selected_above += index > safe
            if not all_records or t in sampled:
                sampled_rows.append(dict(output_time=t, source_position=position,
                                         last_source_frame=index, frame_time=time,
                                         above_complete_cache_capacity=index > safe))
        results[j] = dict(key=material['short_keys_by_part'][part],
                          generated_records=len(positions), nonnegative_records=len(valid),
                          records_above_complete_cache_capacity=len(beyond),
                          max_candidate_frame=max(candidate(position) for _, position in valid),
                          cached_window_max_in_bounds_frame=safe,
                          all_records_checked=all_records,
                          native_main_selector_reads=0 if all_wrapper else len(selected_times),
                          native_selector_records_above_cache=selected_above,
                          validated_read_sha256=digest.hexdigest(), sampled=sampled_rows,
                          wrapped=wrapped, all_wrapper_records_checked=all_wrapper,
                          native_wrapper_reads=wrapper_count,
                          wrapper_records_above_cache=wrapper_above,
                          validated_wrapper_sha256=wrapper_digest.hexdigest(),
                          next_stage=staged, full_loop=full_loop)
    if full_loop_budget is not None:
        for j, edge_id, category, target in full_loop_candidates:
            if full_loop_budget[0] == 0:
                break
            if full_loop_per_short_item and category != 'condition_or_last':
                continue
            marker = (*edge_id, category)
            if not full_loop_per_short_item and marker in full_loop_seen:
                continue
            results[j]['full_loop'].append(dict(category=category,
                                                **probe_full_loop(n, context, ring, target,
                                                                  replay_full_loop,
                                                                  analysis_mode,
                                                                  initialize_output_gate,
                                                                  reset_rng_each_block,
                                                                  clear_candidate_spectrum,
                                                                  initialize_native_candidate,
                                                                  spectrum_selection_byte,
                                                                  spectrum_snapshot_time,
                                                                  source_event_mode,
                                                                  compact_full_loop_trace)))
            if not full_loop_per_short_item:
                full_loop_seen.add(marker)
            full_loop_budget[0] -= 1
    return results


def consume_short_frames(n: Native, context: int, ring: int, items: int, material: dict,
                         dispatch: bool = False, main_read: bool = False,
                         all_main_records: bool = False,
                         wrapper_budget: list[int] | None = None,
                         all_main_wrapper: bool = False,
                         next_stage_budget: list[int] | None = None,
                         full_loop_budget: list[int] | None = None,
                         full_loop_seen: set | None = None,
                         replay_full_loop: bool = False,
                         analysis_mode: int = 1,
                         initialize_output_gate: bool = False,
                         reset_rng_each_block: bool = False,
                         clear_candidate_spectrum: bool = False,
                         initialize_native_candidate: bool = False,
                         spectrum_selection_byte: int = -1,
                         spectrum_snapshot_time: int = -1,
                         full_loop_per_short_item: bool = False,
                         compact_full_loop_trace: bool = False):
    """Load the committed maximum-position source frames through native 175dd0."""
    ctx, controls, analysis = n.alloc(0x2b200), n.alloc(0x100), n.alloc(0x100)
    P.from_address(ctx).value = P.from_address(context).value
    P.from_address(ctx + 0x1d568).value = controls
    B.from_address(controls + 0x78).value = 1
    P.from_address(controls).value = n.alloc(0x18)
    I.from_address(controls + 8).value = 1
    P.from_address(controls + 0x10).value = n.alloc(0x58)
    I.from_address(controls + 0x18).value = 2
    I.from_address(analysis + 0x8c).value = 2048
    P.from_address(ctx + 0x1fc8).value = analysis
    F.from_address(ctx + 0x1d550).value = 256.
    F.from_address(ctx + 0x1d564).value = 22050.
    D.from_address(ctx + 0x1d558).value = .01
    output = n.alloc((2049 * 2) + 32)
    P.from_address(ctx + 0x29510).value = output + 16
    load = n.fn(0x175dd0, None, P, P, I, P, I, I, P)
    init = n.fn(0x12a670, P, P)
    records = P.from_address(ring).value
    result = []
    for j in range(I.from_address(ring + 0x34).value):
        item = items + j * 0x140
        part = P.from_address(item + 0x20).value
        shape = material['short_shapes_by_part'].get(part)
        if shape is None:
            continue
        assert I.from_address(part + 0x200).value == 0
        assert not P.from_address(part + 0x258).value, (
            material['short_keys_by_part'][part],
            I.from_address(part + 0x200).value,
            P.from_address(part + 0x258).value,
            I.from_address(part + 0x1d0).value)
        begin, end = I.from_address(item).value, I.from_address(item + 4).value
        positions = [F.from_address(records + t * 0x18 + 8).value for t in range(begin, end)]
        assert positions and all(math.isfinite(position) for position in positions)
        maximum = max(positions)
        indices = sorted({max(0, min(shape['frames'] - 1, math.floor(maximum))),
                          max(0, min(shape['frames'] - 1, math.ceil(maximum)))})
        loaded = []
        for index in indices:
            frame = n.alloc(0x328)
            init(frame)
            c.memset(output, 0xa5, 2049 * 2 + 32)
            pcm_present = I()
            load(ctx, item, index, frame, 0, 1, c.byref(pcm_present))
            assert pcm_present.value == 1
            assert D.from_address(frame + 0x148).value == 1000 + index
            pitch = F.from_address(frame + 0x1d0).value
            expected_pitch = cents(220 + shape['edge_index'] * 100 + index)
            assert abs(pitch - expected_pitch) < 1e-3, (
                material['short_keys_by_part'][part], index, pitch, expected_pitch)
            start = shape['core'] + index * 256 - 1024
            samples = shape['samples']
            expected = [samples[k] if 0 <= k < len(samples) else 0
                        for k in range(start, start + 2049)]
            actual = list((c.c_int16 * 2049).from_address(output + 16))
            assert actual == expected, (material['short_keys_by_part'][part], index)
            assert c.string_at(output, 16) == b'\xa5' * 16
            assert c.string_at(output + 16 + 2049 * 2, 16) == b'\xa5' * 16
            loaded.append(dict(frame=index, front_zeros=max(0, -start),
                               tail_zeros=max(0, start + 2049 - len(samples))))
        result.append(dict(key=material['short_keys_by_part'][part],
                           item=j, max_source_position=maximum, loaded=loaded,
                           transition_eligible=bool(B.from_address(item + 0x98).value),
                           transition_prepared=bool(B.from_address(item + 0xe4).value),
                           transition_buffer_present=bool(P.from_address(item + 0xf8).value),
                           ring_end_index=I.from_address(ring + 0x60).value,
                           ring_capacity=I.from_address(ring + 0x58).value,
                           record_item_matches=all(
                               P.from_address(records + t * 0x18).value == item
                               for t in range(begin, end))))
    source_event_mode = B.from_address(ring + 0x78).value
    assert source_event_mode in (0, 1)
    if dispatch:
        result[0]['automatic_dispatch'] = dispatch_breath_boundary(
            n, context, ring, items, material)
    if main_read:
        main_results = consume_main_records(n, context, ring, items, material,
                                            all_main_records, wrapper_budget,
                                            all_main_wrapper, next_stage_budget,
                                            full_loop_budget, full_loop_seen,
                                            replay_full_loop, analysis_mode,
                                            initialize_output_gate,
                                            reset_rng_each_block,
                                            clear_candidate_spectrum,
                                            initialize_native_candidate,
                                            spectrum_selection_byte,
                                            spectrum_snapshot_time,
                                            source_event_mode,
                                            full_loop_per_short_item,
                                            compact_full_loop_trace)
        for row in result:
            row['main_record_read'] = main_results[row['item']]
    B.from_address(ring + 0x78).value = source_event_mode
    return result


def verify(dll: Path, report: Path, voice_root: Path, ddb_tools: Path,
           consume_work: Path | None = None, dispatch: bool = False,
           main_read: bool = False, all_main_records: bool = False,
           main_wrapper_limit: int = 0,
           all_main_wrapper: bool = False,
           next_stage_limit: int = 0,
           full_loop_limit: int = 0,
           voiced_frames: bool = False,
           replay_full_loop: bool = False,
           analysis_mode: int = 1,
           initialize_output_gate: bool = False,
           reset_rng_each_block: bool = False,
           clear_candidate_spectrum: bool = False,
           initialize_native_candidate: bool = False,
           spectrum_selection_byte: int = -1,
           spectrum_snapshot_time: int = -1,
           full_loop_per_short_item: bool = False,
           compact_full_loop_trace: bool = False) -> dict:
    assert not (dispatch or main_read) or consume_work is not None
    assert not all_main_records or main_read
    assert 0 <= main_wrapper_limit <= 576 and (main_wrapper_limit == 0 or main_read)
    assert not all_main_wrapper or (main_read and not all_main_records and
                                    main_wrapper_limit == 0)
    assert 0 <= next_stage_limit <= main_wrapper_limit
    assert 0 <= full_loop_limit <= (576 if full_loop_per_short_item else 16)
    assert full_loop_limit == 0 or main_read
    assert not replay_full_loop or full_loop_limit > 0
    assert analysis_mode in (1, 2)
    assert spectrum_selection_byte in (-1, 0, 1)
    assert spectrum_snapshot_time < 0 or (analysis_mode == 2 and replay_full_loop)
    assert not voiced_frames or consume_work is not None
    wrapper_budget = [main_wrapper_limit] if main_wrapper_limit else None
    next_stage_budget = [next_stage_limit] if next_stage_limit else None
    full_loop_budget = [full_loop_limit] if full_loop_limit else None
    full_loop_seen = set()
    data = json.loads(report.read_text(encoding='utf-8'))
    parser = load_public_parser(ddb_tools)
    durations = (10, 125, 500, 1000, 2000, 5000)
    summary = []
    for bank in data['banks']:
        shorts = {tuple(sample['key']): sample
                  for sample in bank['regions']['short_margin_samples']}
        if not shorts:
            continue
        bank_name = bank['audit']['bank']
        matches = list(voice_root.rglob(f'{bank_name}.ddi'))
        assert len(matches) == 1, (bank_name, len(matches))
        raw = matches[0].read_bytes()
        assert hashlib.sha256(raw).hexdigest() == bank['ddi_sha256']
        model = parser(raw)
        model.read()
        assert not any(unit['phoneme'].startswith('br')
                       for unit in model.sta_data.values())
        art_parts = {}
        for root in model.art_data.values():
            for key, parts in walk_art(root):
                if len(key) == 2 and ('Sil' in key and any(
                        name.startswith('br') for name in key)):
                    assert len(parts) == 1, (bank_name, key, len(parts))
                    part = parts[0]
                    art_parts[key] = dict(key=key, frames=len(part['epr']),
                                          groups=part['frame_align'],
                                          pitch1=part['pitch1'], pitch2=part['pitch2'])
        for key, sample in shorts.items():
            source = art_parts[key]
            assert source['frames'] == sample['frames']
            assert source['groups'] == sample['groups']
            assert source['pitch1'] == sample['pitch1']
            assert source['pitch2'] == sample['pitch2']

        tokens = sorted({name for key in shorts for name in key if name.startswith('br')})
        for token in tokens:
            edges = [art_parts[('Sil', token)], art_parts[(token, 'Sil')]]
            owned = (owned_short_file(consume_work, bank_name, token, edges, shorts,
                                      voiced_frames)
                     if consume_work is not None else None)

            def material(n: Native, state: int, context: int, voice: int,
                         table: int, sta: int, art: int) -> dict:
                result = stationary(n, state, context, voice, table, sta, art)
                parents = {}
                callback = c.CFUNCTYPE(P, P)(lambda node: parents.get(node, 0))
                n.keep.append(callback)
                vtable = n.alloc(0x68)
                P.from_address(vtable + 0x60).value = c.cast(callback, P).value
                groups_by_part = {}
                short_shapes_by_part = {}
                short_keys_by_part = {}
                all_shapes_by_part = {}
                all_keys_by_part = {}
                if owned is not None:
                    stream = n.alloc(0x80)
                    P.from_address(stream + 8).value = owned[1]
                    I.from_address(stream + 0x2c).value = 0x32534d53
                    P.from_address(voice + 0x1a0).value = stream
                for sample in edges:
                    previous = art
                    for name in sample['key']:
                        node = n.alloc(0x180)
                        P.from_address(node).value = vtable
                        encoded = name.encode('ascii') + b'\0'
                        c.memmove(node + 0x18, encoded, len(encoded))
                        parents[node] = previous
                        previous = node
                    part = n.alloc(0x280)
                    P.from_address(part).value = vtable
                    parents[part] = node
                    F.from_address(part + 0x160).value = sample['pitch1']
                    F.from_address(part + 0x164).value = sample['pitch2']
                    F.from_address(part + 0x16c).value = .6
                    I.from_address(part + 0x1b0).value = sample['frames']
                    alignment = n.array(I, [group[field] for group in sample['groups']
                                             for field in ('start', 'end', 'start2', 'end2')])
                    P.from_address(part + 0x1d8).value = alignment
                    P.from_address(part + 0x1e0).value = alignment + 32
                    P.from_address(node + 0x148).value = n.array(P, [part])
                    I.from_address(node + 0x150).value = 1
                    n.fn(0x18d790, None, P, P)(voice + 0x2e0, node)
                    result['parts'].append(part)
                    if owned is not None:
                        shape = owned[2][tuple(sample['key'])]
                        P.from_address(part + 0x1a8).value = n.array(Q, shape['frame_offsets'])
                        Q.from_address(part + 0x1b8).value = shape['snd_start']
                        Q.from_address(part + 0x1c0).value = shape['snd_start'] + shape['core'] * 2
                        I.from_address(part + 0x1d0).value = len(shape['samples'])
                        all_shapes_by_part[part] = shape
                        all_keys_by_part[part] = sample['key']
                        if shape['is_short']:
                            short_shapes_by_part[part] = shape
                            short_keys_by_part[part] = sample['key']
                    groups_by_part[part] = [tuple(group[field] for field in
                                                  ('start', 'end', 'start2', 'end2'))
                                            for group in sample['groups']]
                result['groups_by_part'] = groups_by_part
                result['bank_name'] = bank_name
                if owned is not None:
                    result['short_shapes_by_part'] = short_shapes_by_part
                    result['short_keys_by_part'] = short_keys_by_part
                    result['all_shapes_by_part'] = all_shapes_by_part
                    result['all_keys_by_part'] = all_keys_by_part
                    result['verify_consumption'] = lambda native, producer, ring, items, source: (
                        consume_short_frames(native, context, ring, items, source, dispatch,
                                             main_read, all_main_records, wrapper_budget,
                                             all_main_wrapper, next_stage_budget,
                                             full_loop_budget, full_loop_seen,
                                             replay_full_loop, analysis_mode,
                                             initialize_output_gate,
                                             reset_rng_each_block,
                                             clear_candidate_spectrum,
                                             initialize_native_candidate,
                                             spectrum_selection_byte,
                                             spectrum_snapshot_time,
                                             full_loop_per_short_item,
                                             compact_full_loop_trace))
                return result

            try:
                cases = run(dll, True, False, True, material, [[token]], durations)
            finally:
                if owned is not None:
                    assert owned[0].fclose(owned[1]) == 0
            commits = [row for row in cases['rows'] if 'commit' in row]
            assert len(commits) == 12 * len(durations)
            for row in commits:
                result = row['commit']
                used_parts = []
                for check in result['position_checks']:
                    part_index = result['material_indices'][check['item']]
                    assert part_index in (1, 2)
                    edge = edges[part_index - 1]
                    short = shorts.get(tuple(edge['key']))
                    safe = (None if short is None else min(
                        edge['frames'] - 1,
                        (edge['frames'] * 256 + short['pcm_margin'] - 2049) // 256))
                    position = check['max_position']
                    potential_frame = min(edge['frames'] - 1, math.ceil(position))
                    used_parts.append(dict(key=list(edge['key']), item=check['item'],
                                           source_frames=edge['frames'],
                                           output_frames=check['frames'],
                                           max_source_position=position,
                                           max_potential_frame=potential_frame,
                                           cached_window_max_in_bounds_frame=safe,
                                           beyond_complete_cache_capacity=(
                                               None if safe is None else potential_frame > safe),
                                           unit_model_max_error=check['unit_model_max_error']))
                summary.append(dict(bank=bank_name, token=token,
                                    slot=row['slot'], mode=row['mode'], pitch=row['pitch'],
                                    split=row['split'], milliseconds=row['milliseconds'],
                                    pending=result['pending'], anchor=result['anchor'],
                                    work_items=result['work_items'],
                                    ranges=result['item_ranges'],
                                    material_indices=result['material_indices'],
                                    reverse=result['reverse'], used_parts=used_parts,
                                    **({'consumption': result['consumption']}
                                       if 'consumption' in result else {})))
    assert len(summary) == 6 * 12 * len(durations)
    short_edge_summary = {}
    for row in summary:
        for part in row['used_parts']:
            if part['cached_window_max_in_bounds_frame'] is None:
                continue
            key = (row['bank'], *part['key'])
            edge = short_edge_summary.setdefault(key, dict(
                bank=row['bank'], key=part['key'], commits=0,
                cached_window_max_in_bounds_frame=part['cached_window_max_in_bounds_frame'],
                max_potential_frame=-1, beyond_complete_cache_capacity=0,
                duration_overruns={str(duration): 0 for duration in durations}))
            edge['commits'] += 1
            edge['max_potential_frame'] = max(edge['max_potential_frame'],
                                              part['max_potential_frame'])
            if part['beyond_complete_cache_capacity']:
                edge['beyond_complete_cache_capacity'] += 1
                edge['duration_overruns'][str(row['milliseconds'])] += 1
    assert len(short_edge_summary) == 8
    assert all(edge['commits'] == 72 for edge in short_edge_summary.values())
    consumed_edges = 0
    frame_loads = 0
    automatic_dispatches = 0
    automatic_boundary_short_checks = 0
    automatic_boundary_above_cache = 0
    main_edges = 0
    main_generated_records = 0
    main_records_above_cache = 0
    main_native_selector_reads = 0
    main_sampled_above_cache = 0
    main_validated_records_above_cache = 0
    main_wrapper_reads = 0
    main_wrapper_records_above_cache = 0
    main_next_stage_reads = 0
    full_loop_reads = 0
    if consume_work is not None:
        for row in summary:
            for consumed in row['consumption']:
                edge = short_edge_summary[(row['bank'], *consumed['key'])]
                edge['native_file_frame_loads'] = edge.get('native_file_frame_loads', 0) + len(consumed['loaded'])
                edge['max_front_zero_samples'] = max(
                    edge.get('max_front_zero_samples', 0),
                    *(load['front_zeros'] for load in consumed['loaded']))
                edge['max_tail_zero_samples'] = max(
                    edge.get('max_tail_zero_samples', 0),
                    *(load['tail_zeros'] for load in consumed['loaded']))
                consumed_edges += 1
                frame_loads += len(consumed['loaded'])
                if 'main_record_read' in consumed:
                    main = consumed['main_record_read']
                    assert main['generated_records'] == main['nonnegative_records']
                    assert (main['records_above_complete_cache_capacity'] == 0 or
                            (any(sample['above_complete_cache_capacity']
                                 for sample in main['sampled']) if not all_main_wrapper
                             else any(sample['last_source_frame'] >
                                      main['cached_window_max_in_bounds_frame']
                                      for sample in main['wrapped'])))
                    main_edges += 1
                    main_generated_records += main['generated_records']
                    main_records_above_cache += main['records_above_complete_cache_capacity']
                    main_native_selector_reads += main['native_main_selector_reads']
                    main_sampled_above_cache += sum(
                        sample['above_complete_cache_capacity'] for sample in main['sampled'])
                    main_validated_records_above_cache += main['native_selector_records_above_cache']
                    main_wrapper_reads += main['native_wrapper_reads']
                    main_wrapper_records_above_cache += main['wrapper_records_above_cache']
                    main_next_stage_reads += len(main['next_stage'])
                    full_loop_reads += len(main['full_loop'])
                    for source_field, target_field in (
                            ('generated_records', 'main_generated_records'),
                            ('records_above_complete_cache_capacity', 'main_records_above_cache'),
                            ('native_main_selector_reads', 'main_native_selector_reads')):
                        edge[target_field] = edge.get(target_field, 0) + main[source_field]
                    edge['main_wrapper_reads'] = (edge.get('main_wrapper_reads', 0) +
                                                  main['native_wrapper_reads'])
                    edge['main_max_candidate_frame'] = max(
                        edge.get('main_max_candidate_frame', -1), main['max_candidate_frame'])
                if 'automatic_dispatch' in consumed:
                    automatic_dispatches += 1
                    dispatch_result = consumed['automatic_dispatch']
                    assert dispatch_result['prepared'] == 1
                    assert dispatch_result['fft_nonzero']
                    assert dispatch_result['pcm_matches_current_last_frame']
                    for endpoint in dispatch_result['endpoints']:
                        if endpoint['cached_window_max_in_bounds_frame'] is None:
                            continue
                        boundary_edge = short_edge_summary[(row['bank'], *endpoint['key'])]
                        boundary_edge['automatic_boundary_checks'] = (
                            boundary_edge.get('automatic_boundary_checks', 0) + 1)
                        boundary_edge['automatic_boundary_max_frame'] = max(
                            boundary_edge.get('automatic_boundary_max_frame', -1),
                            max(endpoint['source_frames']))
                        boundary_edge['automatic_boundary_above_cache'] = (
                            boundary_edge.get('automatic_boundary_above_cache', 0) +
                            endpoint['beyond_complete_cache_capacity'])
                        automatic_boundary_short_checks += 1
                        automatic_boundary_above_cache += endpoint['beyond_complete_cache_capacity']
        assert consumed_edges == 576 and frame_loads >= consumed_edges
        assert automatic_dispatches == (432 if dispatch else 0)
        assert main_edges == (576 if main_read else 0)
        if all_main_records:
            assert main_native_selector_reads == main_generated_records
            assert main_validated_records_above_cache == main_records_above_cache
        if all_main_wrapper:
            assert main_wrapper_reads == main_generated_records
            assert main_wrapper_records_above_cache == main_records_above_cache
        else:
            assert main_wrapper_reads == (main_wrapper_limit - wrapper_budget[0]
                                          if wrapper_budget is not None else 0)
            assert main_wrapper_reads == main_wrapper_limit
        assert main_next_stage_reads == next_stage_limit
        assert full_loop_reads == full_loop_limit
    return dict(dll_sha256=SHA256, input_sha256=hashlib.sha256(report.read_bytes()).hexdigest(),
                bank_token_cases=6, duration_values=list(durations),
                native_commits=len(summary), consumed=consume_work is not None,
                automatic_dispatch=dispatch, main_record_read=main_read,
                all_main_records=all_main_records, all_main_wrapper=all_main_wrapper,
                voiced_frames=voiced_frames, replay_full_loop=replay_full_loop,
                analysis_mode=analysis_mode,
                initialized_output_gate=initialize_output_gate,
                reset_rng_each_block=reset_rng_each_block,
                cleared_candidate_spectrum=(clear_candidate_spectrum or initialize_native_candidate),
                initialized_native_candidate=initialize_native_candidate,
                spectrum_selection_override=spectrum_selection_byte,
                spectrum_snapshot_time=spectrum_snapshot_time,
                full_loop_per_short_item=full_loop_per_short_item,
                compact_full_loop_trace=compact_full_loop_trace,
                consumed_short_edges=consumed_edges, native_file_frame_loads=frame_loads,
                automatic_dispatches=automatic_dispatches,
                automatic_boundary_short_checks=automatic_boundary_short_checks,
                automatic_boundary_above_cache=automatic_boundary_above_cache,
                main_short_edges=main_edges, main_generated_short_records=main_generated_records,
                main_records_above_cache=main_records_above_cache,
                main_native_selector_reads=main_native_selector_reads,
                main_sampled_above_cache=main_sampled_above_cache,
                main_validated_records_above_cache=main_validated_records_above_cache,
                main_wrapper_reads=main_wrapper_reads,
                main_wrapper_records_above_cache=main_wrapper_records_above_cache,
                main_next_stage_reads=main_next_stage_reads,
                full_loop_reads=full_loop_reads,
                short_edge_summary=list(short_edge_summary.values()), cases=summary,
                scope='Owned br1–br5 note events, real V4X Sil→br/br→Sil ART alignment '
                      'and sentinel pitches, unrelated a STA but no br STA; full native '
                      'event/index commit and source positions. '
                      + ('Maximum-position neighboring frames explicitly passed to native '
                         'file-backed 175dd0 with owned FRM2 and PCM. ' if consume_work is not None else '')
                      + ('The native 16af70 dispatcher also prepares each committed ART boundary '
                         'and its final PCM window matches the owned samples. ' if dispatch else '')
                      + ('Generated short-ART positions counted for every record; selected '
                         'records passed to native 16a870 with the main caller\'s two enabled '
                         'flags; every generated short-ART record is read when all_main_records '
                         'is true. Selected records are also passed to 16a250 before the direct '
                         'read when main_wrapper_limit is positive; all records use the wrapper '
                         'when all_main_wrapper is true. '
                         if main_read else '')
                      + ('A selected record completes consecutive 168940 output-block calls '
                         'from time zero in one newly initialized context. '
                         if replay_full_loop else
                         'Selected records also complete one 168940 output-block call '
                         'each with newly initialized context; this does not establish '
                         'continuous voiced output. ' if full_loop_limit else
                         'The 168940 output-block loop is not exercised. ')
                      + 'No Editor host cache mode observation. '
                      + ('Owned ordinary voiced FRM2 markers used. ' if voiced_frames else
                         'Owned unvoiced FRM2 markers used. ')
                      + 'No commercial frames/PCM or Editor-host synthesis.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--voice-root', type=Path, required=True)
    parser.add_argument('--ddb-tools', type=Path, required=True)
    parser.add_argument('--consume-work-dir', type=Path)
    parser.add_argument('--dispatch', action='store_true')
    parser.add_argument('--main-read', action='store_true')
    parser.add_argument('--all-main-records', action='store_true')
    parser.add_argument('--main-wrapper-limit', type=int, default=0)
    parser.add_argument('--all-main-wrapper', action='store_true')
    parser.add_argument('--next-stage-limit', type=int, default=0)
    parser.add_argument('--full-loop-limit', type=int, default=0)
    parser.add_argument('--full-loop-per-short-item', action='store_true')
    parser.add_argument('--compact-full-loop-trace', action='store_true')
    parser.add_argument('--replay-full-loop', action='store_true')
    parser.add_argument('--analysis-mode', type=int, choices=(1, 2), default=1)
    parser.add_argument('--initialize-output-gate', action='store_true')
    parser.add_argument('--reset-rng-each-block', action='store_true')
    parser.add_argument('--clear-candidate-spectrum', action='store_true')
    parser.add_argument('--initialize-native-candidate', action='store_true')
    parser.add_argument('--spectrum-selection-byte', type=int, choices=(0, 1), default=-1)
    parser.add_argument('--spectrum-snapshot-time', type=int, default=-1)
    parser.add_argument('--voiced-frames', action='store_true')
    parser.add_argument('--report-native-exception', action='store_true')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    exception_reporter = (report_native_exception_address()
                          if args.report_native_exception else None)
    result = verify(args.dll, args.report, args.voice_root, args.ddb_tools,
                    args.consume_work_dir, args.dispatch, args.main_read,
                    args.all_main_records, args.main_wrapper_limit,
                    args.all_main_wrapper, args.next_stage_limit,
                    args.full_loop_limit, args.voiced_frames, args.replay_full_loop,
                    args.analysis_mode, args.initialize_output_gate,
                    args.reset_rng_each_block,
                    args.clear_candidate_spectrum,
                    args.initialize_native_candidate,
                    args.spectrum_selection_byte,
                    args.spectrum_snapshot_time,
                    args.full_loop_per_short_item,
                    args.compact_full_loop_trace)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(native_commits=result['native_commits'],
                          native_file_frame_loads=result['native_file_frame_loads'],
                          automatic_dispatches=result['automatic_dispatches'],
                          main_native_selector_reads=result['main_native_selector_reads'],
                          main_wrapper_reads=result['main_wrapper_reads'],
                          main_next_stage_reads=result['main_next_stage_reads'],
                          full_loop_reads=result['full_loop_reads'],
                          work_item_counts=sorted({row['work_items'] for row in result['cases']}))))
