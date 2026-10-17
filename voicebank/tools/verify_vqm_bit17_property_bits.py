"""Owned IEEE-word checks for the named bit17 property getter plus static leads.

The getter receives fully initialized owned memory, including when flags omit
the field. Nonfinite values are raw-word fixtures, never PCM or model outputs.
This is a property protocol check, not bit17 production or a physical-unit claim.
"""
import argparse
from collections import Counter
import ctypes as c
import hashlib
import json
from pathlib import Path

from audit_frm2_numeric import Native, I, P, Q, SHA256
from audit_vqm_feature_version_sources import run as source_audit
from verify_vqm_save_selection import guard_block, check_guards


PATTERNS = (
    ('positive_zero', 0x00000000), ('negative_zero', 0x80000000),
    ('positive_one', 0x3f800000), ('negative_one', 0xbf800000),
    ('pi_float', 0x40490fdb), ('negative_pi_float', 0xc0490fdb),
    ('two_pi_float', 0x40c90fdb), ('positive_100', 0x42c80000),
    ('negative_100', 0xc2c80000), ('smallest_subnormal', 0x00000001),
    ('negative_smallest_subnormal', 0x80000001), ('largest_finite', 0x7f7fffff),
    ('negative_largest_finite', 0xff7fffff), ('positive_infinity', 0x7f800000),
    ('negative_infinity', 0xff800000), ('quiet_nan_payload', 0x7fc01234),
    ('negative_quiet_nan_payload', 0xffc05678), ('signaling_nan_payload', 0x7f801234),
)


def getter_checks(dll):
    native = Native(dll)
    root = native.alloc(0x168)
    native.fn(0x10fb30, P, P)(root)
    frame_base = native.alloc(0x328 + 64)
    frame = frame_base + 32
    c.memset(frame_base, 0xa5, 32)
    c.memset(frame + 0x328, 0x5a, 32)
    native.fn(0x12a670, P, P)(frame)
    base, feature = guard_block(native, bytes(0x228))
    P.from_address(frame + 0x208).value = feature
    Q.from_address(frame + 0x150).value = 1 << 13
    I.from_address(feature).value = 0x1fffff
    leaf = native.alloc(0x170)
    native.fn(0x10ec80, P, P, P)(leaf, frame)
    assert native.fn(0x10fd10, I, P, P, c.c_char_p)(root, leaf, b'dynamics') == 0
    query = native.fn(0x110020, Q, P, c.c_char_p, I, P)
    rows, counts = [], Counter()
    for name, word in PATTERNS:
        for outer in (0, 1 << 13):
            for flags in (0, 1 << 17, 0x1fffff):
                Q.from_address(frame + 0x150).value = outer
                I.from_address(feature).value = flags
                c.c_uint32.from_address(feature + 0x21c).value = word
                before_frame = c.string_at(frame, 0x328)
                before_feature = c.string_at(feature, 0x228)
                output = (c.c_uint32 * 3)(0xa5a5a5a5, 0xdeadbeef, 0x5a5a5a5a)
                code = query(root, b'dynamics', 0, c.byref(output, 4))
                assert code == 0 and list(output) == [0xa5a5a5a5, word, 0x5a5a5a5a], (name, outer, flags)
                assert c.string_at(frame, 0x328) == before_frame
                assert c.string_at(feature, 0x228) == before_feature
                assert c.string_at(frame_base, 32) == b'\xa5' * 32
                assert c.string_at(frame + 0x328, 32) == b'\x5a' * 32
                check_guards(base)
                rows.append(dict(pattern=name, owned_word=hex(word), frame_mask=hex(outer),
                    feature_flags=hex(flags), exact_four_byte_copy=True,
                    input_and_output_guards_preserved=True))
                counts.update(calls=1, absent_outer_feature_bit=not bool(outer),
                    absent_inner_bit17=not bool(flags & (1 << 17)),
                    nonfinite_word_cases=name.endswith('infinity') or 'nan_payload' in name)
    assert counts['calls'] == 108 and counts['nonfinite_word_cases'] == 30
    return dict(getter_rva='0x110020', leaf_ctor_rva='0x10ec80', append_rva='0x10fd10',
        counts=dict(counts), cases=rows,
        scope='A valid constructed model/leaf and nonnull feature pointer with fully defined owned words; no null-pointer or invalid-index case is called',
        conclusion='The named dynamics getter copies all four bytes unchanged, including signs/payloads; both outer feature bit and internal field bit are ignored by this valid-pointer getter route',
        limits=['No sorting or arithmetic is applied to nonfinite fixtures',
                'A query succeeding with absent flags does not make that field serialized or produced',
                'This named template property does not define the original VQMp bit17 unit or maker algorithm'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'api-dir', 'decoder-dir', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    audit = source_audit(args.dll, args.api_dir, args.decoder_dir)
    getter = getter_checks(args.dll)
    paths = [Path(__file__), *(Path(__file__).with_name(name) for name in (
        'audit_frm2_numeric.py', 'verify_vqm_save_selection.py',
        'verify_vqm_feature_dse5_roundtrip.py', 'verify_reference_ddi.py',
        'audit_v4x_vqm_fields.py', 'probe_frm2.py'))]
    sources = dict(audit['source_sha256'])
    sources.update({p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in paths})
    report = dict(schema=1, dse_sha256=SHA256, source_sha256=sources,
        version_source_audit=audit, owned_property_getter=getter,
        conclusion='The named property reads existing bit17, without a value transform or flag validation. Older reviewed direct accesses add no production formula, and the feature-name table remains unmapped to serialized bits.',
        limits=['Installed modules remain read only; only V6 getter code is loaded into this disposable process',
                'No commercial payload, original authoring assignment or VQM/Growl completion is established'])
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(getter=getter['counts'], modules=len(audit['older_modules']),
        matched_name_tables=4, old_direct_offset_operands=20)))


if __name__ == '__main__':
    main()
