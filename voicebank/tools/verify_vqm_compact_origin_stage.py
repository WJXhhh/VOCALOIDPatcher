"""Check the actual ordinary pre-alignment origin stage on compact source fields.

Independent owned frames replay saved harmonic inputs, excluding the DC slot.
This calls a reviewed native leaf, not a PCM maker. Bank arrays remain in memory.
"""
from __future__ import annotations

import argparse
from collections import Counter
import ctypes as c
import hashlib
import importlib
import json
import math
from pathlib import Path
import struct
import sys

from audit_frm2_numeric import F, I, Native, P, Q, SHA256, f32
from audit_vqm_compact_load_identity import frames_in_interval
from audit_vqm_legacy_live_features import RESOURCE_SHA256
from audit_vqm_legacy_phase_relations import INVALID, circular, metrics
from audit_frm2_phase_history import wrap
from audit_vqm_v4_module_leads import PE
from verify_frm2_phase_origin import search_origin, smooth_origin


BRANCHES = ((False, False), (False, True), (True, False), (True, True))


def amplitude_gate(frequencies, amplitudes):
    other = -10000.
    for hz, amp in zip(frequencies[1:10], amplitudes[1:10]):
        if hz > 1600.:
            break
        if hz > 0.:
            other = max(other, amp)
    return f32(amplitudes[0]-20.) > other


def review_phase_order(dll, decoder_dir):
    sys.path.insert(0, str(decoder_dir.resolve()))
    cs = importlib.import_module('capstone')
    data = dll.read_bytes()
    assert hashlib.sha256(data).hexdigest() == SHA256
    pe = PE(data)
    directory, size = struct.unpack_from('<II', data, pe.directories+24)
    records = struct.iter_unpack('<III', data[pe.offset(directory):pe.offset(directory)+size])
    lo, hi, _ = next(row for row in records if row[0] == 0xd2cc0)
    assert (lo, hi) == (0xd2cc0, 0xd92c2) and hi-lo < 40000
    decoder = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_64)
    decoder.detail = True
    ops = list(decoder.disasm(data[pe.offset(lo):pe.offset(lo)+hi-lo], pe.image_base+lo))
    assert ops[-1].address-pe.image_base+ops[-1].size == hi
    by_rva = {op.address-pe.image_base:op for op in ops}
    call = by_rva[0xd760d]
    assert call.mnemonic == 'call' and call.operands[0].type == cs.x86.X86_OP_IMM
    assert call.operands[0].imm-pe.image_base == 0xda730
    before, after = by_rva[0xd7602], by_rva[0xd7615]
    assert before.mnemonic == after.mnemonic == 'movss'
    assert before.operands[1].mem.disp == after.operands[0].mem.disp == 0xa98
    assert before.reg_name(before.operands[1].mem.base) == after.reg_name(after.operands[0].mem.base) == 'rsi'
    assert before.reg_name(before.operands[0].reg) == 'xmm1'
    assert after.reg_name(after.operands[1].reg) == 'xmm6'
    transfers = [op for op in ops if 0xd7612 <= op.address-pe.image_base < 0xd7615]
    assert len(transfers) == 1 and transfers[0].mnemonic == 'movaps'
    assert transfers[0].reg_name(transfers[0].operands[0].reg) == 'xmm6'
    assert transfers[0].reg_name(transfers[0].operands[1].reg) == 'xmm0'
    history = []
    for rva in (0xd770b, 0xd773f, 0xd7846):
        op = by_rva[rva]
        assert op.mnemonic == 'mov' and op.operands[1].type == cs.x86.X86_OP_MEM
        assert op.operands[1].mem.disp == 0xaa0 and op.operands[1].size == 8
        assert op.reg_name(op.operands[1].mem.base) == 'rsi'
        history.append(hex(rva))
    return dict(decoder_version=cs.__version__, parent_range=[hex(lo), hex(hi)],
        decoded_instructions=len(ops), undecoded_tail_bytes=0,
        origin_state_read_rva='0xd7602', origin_call_rva='0xd760d',
        returned_origin_to_state_rva='0xd7615', later_aligned_history_access_rvas=history,
        scope='Hash-pinned local state read/call/return-store and later history accesses; '
              'not a full parent call graph or proof of bit17 maker identity. No code exported.')


def run(dll, directory, reference, decoder_dir):
    data = (directory/'dvqm.dat').read_bytes()
    assert hashlib.sha256(data).hexdigest() == RESOURCE_SHA256
    prior = json.loads(reference.read_text(encoding='utf-8'))
    assert prior['samples'] == 119 and prior['resource_sha256'] == RESOURCE_SHA256
    order = review_phase_order(dll, decoder_dir)
    native = Native(dll)
    call = native.fn(0xda730, F, P, F, F, P)
    context = native.alloc(0x1000)
    phase_history = native.array(F, [0.]*32)
    f0_history = native.array(F, [0.]*32)
    I.from_address(context+0xa80).value = 32
    P.from_address(context+0xa88).value = phase_history
    I.from_address(context+0x428).value = 32
    P.from_address(context+0x420).value = f0_history
    frame = native.alloc(0x230)
    native.frame_ctor(frame)
    Q.from_address(frame+0x50).value = 7
    capacity = 700
    arrays = [native.array(F, [0.]*capacity) for _ in range(3)]
    for offset, pointer in zip((0x60, 0x68, 0x70), arrays):
        P.from_address(frame+offset).value = pointer
    errors = {str(branch):[] for branch in BRANCHES}
    observed, search_errors, model_errors, attainable = [], [], [], []
    phases_vs_feature = []
    counts, cases = Counter(), []
    for case in prior['cases']:
        start = int(case['track_offset'], 16)
        rows = [wire for _, _, wire in frames_in_interval(data, start, start+case['track_bytes'])]
        percase = Counter(source_frames=len(rows))
        for index, row in enumerate(rows):
            pitch = struct.unpack('<f', row['pitch'])[0]
            if pitch <= INVALID or row['count'] < 4:
                percase['unvoiced_or_insufficient_components_excluded'] += 1
                continue
            assert row['count']-1 <= capacity
            frequencies, amplitudes, phases = [struct.unpack('<'+str(row['count'])+'f', values)[1:]
                                              for values in row['components']]
            assert frequencies[0] > 0. and struct.unpack_from('<f', row['components'][0])[0] == 0.
            f0 = f32(440.*math.exp2(pitch/1200.))
            saved = struct.unpack_from('<f', row['feature'], 0x21c)[0]
            phases_vs_feature.append(circular(phases[0]-saved))
            if not index or struct.unpack('<f', rows[index-1]['pitch'])[0] <= INVALID:
                percase['missing_valid_previous_state_excluded'] += 1
                continue
            previous = f32(wrap(struct.unpack_from('<f', rows[index-1]['feature'], 0x21c)[0]))
            I.from_address(frame+0x58).value = len(phases)
            for pointer, values in zip(arrays, (frequencies, amplitudes, phases)):
                c.memmove(pointer, struct.pack('<'+str(len(values))+'f', *values), len(values)*4)
            input_fields = c.string_at(frame+0x50, 0x28)
            gated = amplitude_gate(frequencies, amplitudes)
            percase['amplitude_gated_frames'] += gated
            candidates, searches = [], []
            for locked, continuous in BRANCHES:
                I.from_address(context+0xa90).value = 2 if continuous else 0
                I.from_address(context+0xa94).value = int(locked)
                c.memmove(phase_history, (F*32)(*([previous]*32)), 128)
                c.memmove(f0_history, (F*32)(*([f0]*32)), 128)
                actual = call(context, previous, f0, frame)
                candidate = F.from_address(phase_history+31*4).value
                assert math.isfinite(actual) and math.isfinite(candidate)
                expected = previous if gated else smooth_origin(candidate, previous, locked, continuous)
                model_errors.append(circular(actual-expected))
                error = circular(actual-saved)
                errors[str((locked, continuous))].append(error)
                candidates.append(error)
                searches.append(candidate)
                percase['native_calls'] += 1
            assert len({struct.pack('<f', value) for value in searches}) == 1
            assert c.string_at(frame+0x50, 0x28) == input_fields
            for pointer, values in zip(arrays, (frequencies, amplitudes, phases)):
                assert c.string_at(pointer, len(values)*4) == struct.pack('<'+str(len(values))+'f', *values)
            percase['owned_frame_inputs_preserved'] += 1
            observed.append(circular(searches[0]-saved))
            if percase['conditional_frames'] == 0:
                expected_search = search_origin(frequencies, phases, f0, previous)
                search_errors.append(circular(searches[0]-expected_search))
                percase['independent_full_search_checks'] += 1
            attainable.append(min(abs(error) for error in candidates))
            percase['conditional_frames'] += 1
        counts.update(percase)
        cases.append(dict(sample_index=case['sample_index'], counts=dict(percase)))
    assert len(search_errors) == 119 and max(map(abs, search_errors)) < 1e-5
    assert max(map(abs, model_errors)) < 1e-5
    result = dict(schema=1, dse_sha256=SHA256, resource_sha256=RESOURCE_SHA256,
        reference_sha256=hashlib.sha256(reference.read_bytes()).hexdigest(), samples=119,
        entries=dict(origin_before_alignment_rva='0xda730', phase_origin_state_offset='0xa98',
                     aligned_history_frame_offset='0xaa0', feature_bit17_offset='0x21c'),
        phase_assignment_order=order, counts=dict(counts), cases=cases,
        independent_search_errors=metrics(search_errors),
        independent_smoothing_errors=metrics(model_errors),
        saved_origin_candidate_errors=metrics(observed),
        saved_conditional_origin_errors={name:metrics(values) for name, values in errors.items()},
        minimum_error_over_four_branches=metrics(attainable),
        raw_first_phase_control=metrics(phases_vs_feature),
        scope='All119 compact source intervals; DC slot removed before actual ordinary pre-alignment origin leaf. '
              'Four lock/continuity contracts grant exact previous saved bit17 as putative origin, not fitted coefficients. '
              'One complete independent search per interval; smoothing independently checked on every call. '
              'Not native PCM analysis, original hidden source phases, pre-roll recovery, or an assertion the maker used this leaf. '
              'Only aggregate errors, counts and addresses persisted; original VQM/Growl requirements preserved.')
    dependency = json.loads(Path(__file__).parents[2].joinpath(
        'voicebank_doc/evidence/432a_vqm_compact_phase_lowpass.json').read_text())
    sources = set(dependency['source_sha256']) | {
        'verify_vqm_compact_origin_stage.py', 'verify_frm2_phase_origin.py', 'audit_frm2_phase_history.py'}
    result['source_sha256'] = {name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                              for name in sorted(sources)}
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'directory', 'reference', 'decoder-dir', 'output'):
        parser.add_argument('--'+name, type=Path, required=True)
    args = parser.parse_args()
    result = run(args.dll, args.directory, args.reference, args.decoder_dir)
    args.output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps({name:result[name] for name in ('counts', 'independent_search_errors',
        'independent_smoothing_errors', 'minimum_error_over_four_branches', 'saved_origin_candidate_errors')}))


if __name__ == '__main__':
    main()
