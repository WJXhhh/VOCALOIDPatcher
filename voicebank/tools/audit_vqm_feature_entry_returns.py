"""Bounded entry-parameter and returned-pointer leads for the VQM bit17 cell.

Entry arguments are deliberately untyped. A successful match is an address
lead, not proof of a feature object, a producer, or absence elsewhere.
"""
from __future__ import annotations

import argparse
from bisect import bisect_right
from collections import Counter, deque
import hashlib
import importlib
import json
from pathlib import Path
import struct
import sys

from audit_frm2_numeric import SHA256
from audit_vqm_feature_calls_cfg import merge, runtime_groups, transfer
from audit_vqm_feature_pointer_accesses import ARGUMENTS, KNOWN
from audit_vqm_v4_module_leads import PE
from locate_vqm_feature_accesses import executable_sections


def target_of(op, cs, base):
    if (op.group(cs.CS_GRP_CALL) and len(op.operands) == 1
            and op.operands[0].type == cs.x86.X86_OP_IMM):
        return op.operands[0].imm - base
    return None


def direct_target(op, cs, base):
    if ((op.group(cs.CS_GRP_CALL) or op.group(cs.CS_GRP_JUMP))
            and len(op.operands) == 1 and op.operands[0].type == cs.x86.X86_OP_IMM):
        return op.operands[0].imm-base
    return None


def decode_leaf(entry, stop, data, pe, decoder, cs):
    """Decode only code reachable through direct edges, within a small interval."""
    pending, instructions, gaps, outside, indirect = [entry], {}, set(), set(), set()
    while pending:
        at = pending.pop()
        if at in instructions:
            continue
        if not entry <= at < stop:
            outside.add(at)
            continue
        raw = data[pe.offset(at):pe.offset(at)+min(15, stop-at)]
        ops = list(decoder.disasm(raw, pe.image_base+at, count=1))
        if not ops:
            gaps.add(at)
            continue
        op = ops[0]
        if op.mnemonic in ('int3', 'int', 'ud2'):
            continue
        instructions[at] = op
        if len(instructions) > 2048:
            raise ValueError('Leaf instruction budget exceeded')
        if op.group(cs.CS_GRP_RET):
            continue
        if op.group(cs.CS_GRP_JUMP):
            target = direct_target(op, cs, pe.image_base)
            if target is not None:
                pending.append(target)
            else:
                indirect.add(at)
            if op.mnemonic == 'jmp':
                continue
        pending.append(at+op.size)
    spans = sorted((at, at+op.size) for at, op in instructions.items())
    overlap = any(a[1] > b[0] for a, b in zip(spans, spans[1:]))
    return ([instructions[at] for at in sorted(instructions)] if not overlap else [],
            dict(entry_rva=hex(entry), interval_end_rva=hex(stop),
                 decoded_instructions=len(instructions),
                 undecodable_rvas=[hex(at) for at in sorted(gaps)],
                 outside_direct_edges=[hex(at) for at in sorted(outside)],
                 unresolved_indirect_branch_rvas=[hex(at) for at in sorted(indirect)],
                 overlapping_instruction_spans=overlap))


def analyze(instructions, cs, base, returned_targets=(), switch_edges=None):
    if not instructions:
        return [], [], [], Counter()
    positions = {op.address: i for i, op in enumerate(instructions)}
    states = [None] * len(instructions)
    states[0] = ({r: (0, -i-1) for i, r in enumerate(ARGUMENTS)}, 0, None)
    pending, queued = deque([0]), {0}
    counts = Counter()
    while pending:
        i = pending.popleft()
        queued.remove(i)
        op = instructions[i]
        outgoing = transfer(op, states[i], cs, base)
        if target_of(op, cs, base) in returned_targets:
            outgoing[0]['rax'] = (0, op.address-base)
        counts['transfer_visits'] += 1
        if counts['transfer_visits'] > 500000:
            raise ValueError('CFG convergence exceeds bounded visit budget')
        successors = []
        if not op.group(cs.CS_GRP_RET):
            if op.group(cs.CS_GRP_JUMP):
                if len(op.operands) == 1 and op.operands[0].type == cs.x86.X86_OP_IMM:
                    target = positions.get(op.operands[0].imm)
                    if target is not None:
                        successors.append(target)
                elif switch_edges and op.address-base in switch_edges:
                    successors += [positions[base+at] for at in switch_edges[op.address-base]
                                   if base+at in positions]
                falls = op.mnemonic != 'jmp'
            else:
                falls = True
            if (falls and i+1 < len(instructions)
                    and op.address+op.size == instructions[i+1].address):
                successors.append(i+1)
        for successor in successors:
            new = merge(states[successor], outgoing)
            if new != states[successor]:
                states[successor] = new
                if successor not in queued:
                    queued.add(successor)
                    pending.append(successor)
    accesses, returns, returned_calls = [], [], []
    for op, state in zip(instructions, states):
        if state is None:
            continue
        aliases = state[0]
        if op.group(cs.CS_GRP_RET):
            value = aliases.get('rax')
            returns.append(dict(return_rva=hex(op.address-base), alias=value))
        if target_of(op, cs, base) in returned_targets:
            returned_calls.append(dict(call_rva=hex(op.address-base),
                                       target_rva=hex(target_of(op, cs, base))))
        for i, operand in enumerate(op.operands):
            if operand.type != cs.x86.X86_OP_MEM:
                continue
            previous = aliases.get(op.reg_name(operand.mem.base))
            if previous is None:
                continue
            if operand.mem.index:
                counts['indexed_alias_operands_excluded'] += 1
                continue
            location = previous[0]+operand.mem.disp
            address_only = op.mnemonic == 'lea'
            overlap = (0x21c <= location < 0x220 if address_only else
                       location < 0x220 and 0x21c < location+operand.size)
            if not overlap:
                continue
            read = bool(operand.access & cs.CS_AC_READ)
            write = bool(operand.access & cs.CS_AC_WRITE)
            if op.mnemonic.startswith(('mov', 'vmov')):
                read, write = i != 0, i == 0
            root = previous[1]
            accesses.append(dict(instruction_rva=hex(op.address-base),
                root_rva=hex(root) if root >= 0 else None,
                entry_argument=ARGUMENTS[-root-1] if root < 0 else None,
                effective_offset=location, operand_bytes=operand.size,
                operation=op.mnemonic, address_only=address_only,
                read=read and not address_only, write=write and not address_only))
    counts['decoded_instructions'] = len(instructions)
    counts['reached_instructions'] = sum(state is not None for state in states)
    return accesses, returns, returned_calls, counts


ENTRY_ROLES = {
    0x6c9f0: ('feature_file_io', 'DRS feature writer, bit17 address passed to fwrite'),
    0x6cef0: ('feature_file_io', 'DRS feature reader, bit17 address passed to fread'),
    0x135eb0: ('feature_file_io', 'DSE5 feature reader, bit17 address passed to fread'),
    0x1380a0: ('feature_file_io', 'DSE5 feature writer, bit17 address passed to fwrite'),
    0x109b30: ('other_object', 'CDBDataPhUPart initialization, pointer slot +218'),
    0x109fa0: ('other_object', 'PhUPart collection cleanup, count +208 and pointer array +218'),
    0x10a830: ('other_object', 'PhUPart owned pointer cleanup'),
    0x12a7b0: ('other_object', 'DSE5 Frame allocation, +218 ENV pointer rather than feature scalar'),
    0x12bbc0: ('other_object', 'DSE5 Frame copy with mask +150, feature +208 and ENV +218'),
    0x12c550: ('other_object', 'DSE5 Frame ENV cleanup'),
    0x12c800: ('other_object', 'DSE5 Frame copy/reset with mask +150 and ENV +218'),
    0x12cf40: ('other_object', 'DSE5 Frame mask reset and ENV cleanup'),
    0x1303b0: ('other_object', 'DSE5 Frame initialization clears ENV pointer'),
    0x1307b0: ('other_object', 'DSE5 Frame interpolation, entry-derived +218 is ENV pointer'),
    0x1363a0: ('other_object', 'DSE5 Frame reader allocates/reads ENV, feature at +208'),
    0x138560: ('other_object', 'DSE5 Frame writer writes ENV through pointer +218'),
    0x13fd60: ('other_object', 'Region copy, wide +210 access is not a feature block'),
    0x140240: ('other_object', 'Region reset with mask +350 and owned pointers'),
    0x1cc110: ('other_object', 'Copies Frame ENV into own +18/+20 slots, 1c8 allocation'),
    0x1cc1d0: ('other_object', 'Copies Frame ENV into own +30/+38 slots, 1c8 allocation'),
    0x2197a0: ('other_object', 'Calls free on the second argument pointer slot +218'),
}


def reviewed_switch_data(data, pe, ops, cs):
    """Four hash-pinned switch tables, checked against their actual consumers."""
    by_rva = {op.address-pe.image_base: op for op in ops}
    specs = [
        (0x1b3a84, 3, 0x1b3a90, 97, 0x1b2c07, 96, 0x1b2c13, 0x1b2c1b, 0x1b2c25),
        (0x1b3af4, 8, 0x1b3b14, 26, 0x1b2cc0, 25, 0x1b2ccb, 0x1b2cd3, 0x1b2cdd),
        (0x1b3b30, 26, None, 26, 0x1b2fc4, 25, None, 0x1b2fcf, 0x1b2fd9),
        (0x1b3b98, 11, 0x1b3bc4, 64, 0x1b380f, 63, 0x1b381a, 0x1b3822, 0x1b382c),
    ]
    assert any(op.mnemonic == 'lea' and op.op_str.startswith('rsi,')
               and op.operands[1].type == cs.x86.X86_OP_MEM
               and op.reg_name(op.operands[1].mem.base) == 'rip'
               and op.address+op.size+op.operands[1].mem.disp == pe.image_base for op in ops)
    edges, rows, intervals = {}, [], []
    for table, slots, selector, cases, compare, limit, selector_load, table_load, dispatch in specs:
        raw = data[pe.offset(table):pe.offset(table)+4*slots]
        targets = struct.unpack('<'+str(slots)+'I', raw)
        assert all(target in by_rva for target in targets)
        cmp_op = by_rva[compare]
        assert cmp_op.mnemonic == 'cmp' and cmp_op.operands[1].imm == limit
        guard = by_rva[compare+cmp_op.size]
        assert guard.mnemonic == 'ja' and guard.operands[0].imm == pe.image_base+0x1b3404
        load = by_rva[table_load]
        assert load.mnemonic == 'mov' and load.operands[1].mem.disp == table
        assert load.reg_name(load.operands[1].mem.base) == 'rsi'
        assert load.operands[1].mem.scale == 4
        add = by_rva[table_load+load.size]
        assert add.mnemonic == 'add' and add.op_str == 'rcx, rsi'
        assert by_rva[dispatch].mnemonic == 'jmp' and by_rva[dispatch].op_str == 'rcx'
        intervals.append((table, table+4*slots))
        if selector is not None:
            selected = data[pe.offset(selector):pe.offset(selector)+cases]
            assert max(selected) < slots
            select_op = by_rva[selector_load]
            assert select_op.mnemonic == 'movzx' and select_op.operands[1].size == 1
            assert select_op.operands[1].mem.disp == selector
            intervals.append((selector, selector+cases))
        else:
            selected = bytes(range(slots))
        edges[dispatch] = sorted(set(targets[i] for i in selected))
        rows.append(dict(table_rva=hex(table), slots=slots, case_count=cases,
            selector_rva=hex(selector) if selector else None, selector_max=max(selected),
            target_min_rva=hex(min(targets)), target_max_rva=hex(max(targets)),
            unique_targets=len(set(targets)), all_targets_at_decoded_boundaries=True,
            compare_rva=hex(compare), compare_limit=limit, dispatch_rva=hex(dispatch),
            table_consumer_rva=hex(table_load), selector_consumer_rva=hex(selector_load) if selector else None,
            table_sha256=hashlib.sha256(raw).hexdigest(),
            selector_sha256=hashlib.sha256(selected).hexdigest() if selector else None))
    intervals += [(0x1b3af1, 0x1b3af4), (0x1b3b2e, 0x1b3b30)]
    intervals.sort()
    assert intervals[0][0] == 0x1b3a84 and intervals[-1][1] == 0x1b3c04
    assert all(a[1] == b[0] for a, b in zip(intervals, intervals[1:]))
    assert sum(b-a for a, b in intervals) == 384
    return edges, dict(group_rva='0x1b2ab0', data_start_rva='0x1b3a84',
        data_end_rva='0x1b3c04', data_bytes=384, former_undecoded_bytes=380,
        tables=rows, padding_bytes=5,
        basis='Pinned instructions and bounded selectors; Ghidra DATA xrefs to all seven table/selector starts')


def checks(decoder, cs):
    # Only self-authored machine instructions; no installed code is recorded.
    cases = [
        ('entry_direct', 'f30f11811c020000c3', 1),
        ('entry_rebased', '488db980020000f30f11479cc3', 1),
        ('entry_partial_kill', '31c9f30f11811c020000c3', 0),
        ('entry_spill', '48894c244031c9488b4c2440f30f11811c020000c3', 1),
        ('entry_branch_kill', '750231c9f30f11811c020000c3', 0),
        ('entry_nonvolatile', '4889cfe800000000f30f11871c020000c3', 1),
        ('entry_volatile_call', 'e800000000f30f11811c020000c3', 0),
        ('entry_wide', '0f118910020000c3', 1),
        ('entry_adjacent', 'f30f118118020000c3', 0),
        ('entry_indexed_excluded', 'f30f11848a1c020000c3', 0),
    ]
    rows = []
    for name, code, count in cases:
        raw = bytes.fromhex(code)
        ops = list(decoder.disasm(raw, 0x1000))
        assert sum(op.size for op in ops) == len(raw), name
        found, _, _, _ = analyze(ops, cs, 0x1000)
        assert len(found) == count, (name, found)
        rows.append(dict(case=name, passed=True))
    raw = bytes.fromhex('e800000000f30f11801c020000c3')
    ops = list(decoder.disasm(raw, 0x1000))
    assert not analyze(ops, cs, 0x1000)[0]
    found, _, calls, _ = analyze(ops, cs, 0x1000, {5})
    assert len(found) == len(calls) == 1 and found[0]['root_rva'] == '0x0'
    rows.append(dict(case='known_return_pointer', passed=True))
    raw = bytes.fromhex('488b8128010000c3')
    _, returns, _, _ = analyze(list(decoder.disasm(raw, 0x1000)), cs, 0x1000)
    assert returns[0]['alias'] == (0, 0)
    rows.append(dict(case='pointer_shaped_return', passed=True))
    # A jump over non-code bytes must not stop decoding at the inline bytes.
    raw = bytes.fromhex('eb020fff488b8128010000c3')
    class FixturePE:
        image_base = 0x1000

        def offset(self, rva):
            return rva
    ops, coverage = decode_leaf(0, len(raw), raw, FixturePE(), decoder, cs)
    assert len(ops) == 3 and not coverage['undecodable_rvas']
    assert analyze(ops, cs, 0x1000)[1][0]['alias'] == (0, 4)
    rows.append(dict(case='leaf_direct_edge_skips_inline_data', passed=True))
    return rows


def run(dll, decoder_dir):
    data = dll.read_bytes()
    assert hashlib.sha256(data).hexdigest() == SHA256
    pe = PE(data)
    assert pe.width == 8 and pe.machine == 0x8664
    sys.path.insert(0, str(decoder_dir.resolve()))
    cs = importlib.import_module('capstone')
    assert cs.__version__ == '5.0.3'
    decoder = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_64)
    decoder.detail = True
    owned = checks(decoder, cs)
    groups, records = runtime_groups(data, pe)
    counts = Counter(exception_ranges=records, runtime_groups=len(groups))
    gaps, overlaps, returners, caller_groups = {}, [], [], {}
    direct_leads = set()
    switch_review = None

    def decode(root):
        result = []
        for start, end, _ in groups[root]:
            raw = data[pe.offset(start):pe.offset(start)+end-start]
            ops = list(decoder.disasm(raw, pe.image_base+start))
            covered = sum(op.size for op in ops)
            if covered != len(raw):
                gaps[start] = dict(group_rva=hex(root), fragment_rva=hex(start),
                    undecoded_start_rva=hex(start+covered), undecoded_bytes=len(raw)-covered)
            result.extend(ops)
        return result

    # Every chained-unwind group is visited, with no byte-displacement filter.
    # Do not retain every instruction or every entry argument call in memory.
    for number, root in enumerate(sorted(groups), 1):
        ops = decode(root)
        switch_edges = None
        if root == 0x1b2ab0:
            switch_edges, switch_review = reviewed_switch_data(data, pe, ops, cs)
            gaps[0x1b38f2]['review'] = 'Inside verified switch data; not unexamined code'
            # Linear decoding happened to parse the first four data bytes as
            # two instructions. Exclude them after the data bounds are proved.
            ops = [op for op in ops if op.address-pe.image_base < 0x1b3a84]
        found, returns, _, stats = analyze(ops, cs, pe.image_base, switch_edges=switch_edges)
        counts.update(stats)
        for row in found:
            row['group_rva'] = hex(root)
        overlaps.extend(found)
        # An eligible return must have zero feature-relative displacement at
        # every reached RET, with roots from local pointer-shaped Frame loads.
        escaping_jump = any(op.group(cs.CS_GRP_JUMP)
                            and (direct_target(op, cs, pe.image_base) is None
                                 or direct_target(op, cs, pe.image_base) not in
                                 {o.address-pe.image_base for o in ops}) for op in ops)
        if (returns and not escaping_jump and all(row['alias'] is not None and row['alias'][0] == 0
                            and row['alias'][1] >= root for row in returns)):
            returners.append(dict(group_rva=hex(root), returns=returns,
                                  classification='untyped_pointer_shaped_return'))
        for op in ops:
            target = target_of(op, cs, pe.image_base)
            if target is not None:
                caller_groups.setdefault(target, set()).add(root)
            target = direct_target(op, cs, pe.image_base)
            if target is not None:
                direct_leads.add(target)
        if len(overlaps) > 5000 or len(returners) > 500:
            raise ValueError('Candidate count exceeds bounded report budget')
        if number % 500 == 0:
            print(json.dumps(dict(groups_visited=number, overlaps=len(overlaps))), flush=True)

    for row in overlaps:
        if row['entry_argument'] is not None:
            role = ENTRY_ROLES.get(int(row['group_rva'], 16))
            row['review'] = dict(classification=role[0], reason=role[1]) if role else dict(classification='unreviewed')
        else:
            role = KNOWN.get(int(row['instruction_rva'], 16))
            row['review'] = dict(classification='known_feature_access' if role else 'unreviewed', reason=role)

    ranges = sorted((a, e) for members in groups.values() for a, e, _ in members)
    starts = [a for a, _ in ranges]
    code_ranges = [(a, a+len(raw)) for _, a, raw in executable_sections(data)]
    def in_runtime(at):
        i = bisect_right(starts, at)-1
        return i >= 0 and at < ranges[i][1]
    def executable(at):
        return any(a <= at < e for a, e in code_ranges)
    export_leads = {int(row['rva'], 16) for row in pe.exports() if not row['forwarded']}
    leaf_entries = {at for at in direct_leads | export_leads if executable(at) and not in_runtime(at)}
    assert len(leaf_entries) <= 3000
    anchors = sorted(set(starts) | {e for _, e in ranges} | leaf_entries |
                     {e for _, e in code_ranges})
    leaf_coverage, leaf_overlaps, leaf_returners = [], [], []
    leaf_cache = {}
    pending = deque(sorted(leaf_entries))
    while pending:
        root = pending.popleft()
        if root in leaf_cache:
            continue
        stop = min(root+2048, anchors[bisect_right(anchors, root)])
        ops, coverage = decode_leaf(root, stop, data, pe, decoder, cs)
        leaf_cache[root] = ops
        leaf_coverage.append(coverage)
        found, returns, _, stats = analyze(ops, cs, pe.image_base)
        counts['leaf_decoded_instructions'] += len(ops)
        for row in found:
            row['group_rva'] = hex(root)
        leaf_overlaps.extend(found)
        if (returns and not coverage['undecodable_rvas'] and not coverage['outside_direct_edges']
                and not coverage['unresolved_indirect_branch_rvas']
                and all(row['alias'] is not None and row['alias'][0] == 0
                        and row['alias'][1] >= root for row in returns)):
            leaf_returners.append(dict(group_rva=hex(root), returns=returns,
                                      classification='untyped_pointer_shaped_return'))
        for op in ops:
            target = target_of(op, cs, pe.image_base)
            if target is not None:
                caller_groups.setdefault(target, set()).add(root)
            target = direct_target(op, cs, pe.image_base)
            if target is not None and executable(target) and not in_runtime(target):
                if target not in leaf_entries:
                    leaf_entries.add(target)
                    if len(leaf_entries) > 3000:
                        raise ValueError('Leaf candidate budget exceeded')
                    pending.append(target)
    returners += leaf_returners
    targets = {int(row['group_rva'], 16) for row in returners}
    requested = sorted({root for target in targets for root in caller_groups.get(target, ())})
    assert len(requested) <= 500
    returned_rows, return_calls = [], []
    for root in requested:
        found, _, calls, _ = analyze(decode(root) if root in groups else leaf_cache[root],
                                    cs, pe.image_base, targets)
        call_roots = {row['call_rva'] for row in calls}
        for row in found:
            if row['root_rva'] in call_roots:
                row['group_rva'] = hex(root)
                returned_rows.append(row)
        return_calls += [dict(group_rva=hex(root), **row) for row in calls]
    paths = [Path(__file__), Path(__file__).with_name('audit_vqm_feature_calls_cfg.py'),
             Path(__file__).with_name('audit_vqm_feature_pointer_accesses.py'),
             Path(__file__).with_name('audit_vqm_v4_module_leads.py'),
             Path(__file__).with_name('audit_frm2_numeric.py'),
             Path(__file__).with_name('locate_vqm_feature_accesses.py')]
    return dict(schema=1, dse_sha256=SHA256, decoder_version=cs.__version__,
        source_sha256={p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in paths},
        owned_checks=owned, counts=dict(counts), bit17_cell_shaped_overlaps=overlaps,
        entry_parameter_overlap_count=sum(row['entry_argument'] is not None for row in overlaps),
        leaf_entry_count=len(leaf_entries), leaf_coverage=leaf_coverage,
        leaf_bit17_cell_shaped_overlaps=leaf_overlaps,
        overlap_classifications=dict(Counter(row['review']['classification'] for row in overlaps)),
        switch_data_review=switch_review,
        pointer_shaped_returners=returners, returned_pointer_callers=len(requested),
        returned_pointer_calls=return_calls, returned_pointer_overlaps=returned_rows,
        decode_gaps=list(gaps.values()),
        limits=['Entry arguments and Frame-shaped loads are untyped candidates',
                'Coverage is restricted to PE chained-unwind fragments and direct CFG edges',
                'Leaf entries come only from aligned direct transfers and exports, not arbitrary vtable or computed targets',
                'Leaf decoding is limited to 2048 bytes and 2048 instructions per entry; external tail targets are not summarized',
                'Only four integer register arguments are seeded; caller stack arguments are not typed',
                'Tail return forwarding, indirect callees and indexed offsets are not summarized',
                'No type or producer absence inference from overlap or zero result',
                'Reused transfer rules do not track arbitrary pointer arithmetic, escaped stack addresses or cross-module returns',
                'Undecoded tails are retained as gaps, not declared audited'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ('dll', 'decoder-dir', 'output'):
        parser.add_argument('--'+option, type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll, args.decoder_dir)
    args.output.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps({key: report[key] for key in
                     ('counts', 'entry_parameter_overlap_count', 'returned_pointer_callers')}))


if __name__ == '__main__':
    main()
