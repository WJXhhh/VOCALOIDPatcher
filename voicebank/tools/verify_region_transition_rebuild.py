"""Connect an owned work ring to the actual transition preparation dispatcher."""
import ctypes as c
import itertools

from audit_frm2_numeric import P, I, Q, F, D, B
from verify_region_frame_blend import EMPTY


def run_fixture(n, context, controls, part, pitches):
    init = n.fn(0x12a670, P, P)
    dispatch = n.fn(0x16af70, None, P, P)
    invalidate = n.fn(0x1cc0a0, None, P, B)
    ring, records, scores = n.alloc(4 * 0x140), n.alloc(2 * 0x18), n.alloc(2 * 0x2c)
    P.from_address(controls).value = records
    I.from_address(controls + 8).value = 2
    P.from_address(controls + 0x10).value = scores
    I.from_address(controls + 0x18).value = 2
    P.from_address(controls + 0x20).value = ring
    I.from_address(controls + 0x2c).value = 4
    P.from_address(controls + 0x50).value = ring
    I.from_address(controls + 0x58).value = 4
    I.from_address(controls + 0x5c).value = 3
    I.from_address(controls + 0x60).value = 2
    P.from_address(context + 0x1d578).value = context
    I.from_address(context + 0x24158).value = 1
    F.from_address(context + 0x1d560).value = 44100.
    for j in range(2):
        item = ring + j * 0x140
        I.from_address(item).value = j
        I.from_address(item + 4).value = j + 1
        I.from_address(item + 0x10).value = j
        I.from_address(item + 0x14).value = 1
        P.from_address(item + 0x20).value = part
        P.from_address(records + j * 0x18).value = item
        F.from_address(scores + j * 0x2c + 20).value = EMPTY
        frame = n.alloc(0x328)
        init(frame)
        n.fn(0x12ba70, Q, P, I, Q, I)(frame, 16, 0x30, 0)
        P.from_address(context + 0x247b8 + j * 8).value = frame
    B.from_address(ring + 0x140 + 0x98).value = 1
    cache = ring + 0x140 + 0xa0
    P.from_address(cache + 0x58).value = n.array(F, [0.] * 500)
    previous_times = [0., 0.]
    candidate_time = 0.
    pointers = None
    rows = []
    for left, right, flag_left, flag_right in itertools.product([-1., .5, 2.5, 3.5, 4.5],
                                                               [-1., .5, 2.5, 3.5, 4.5], [0, 1], [0, 1]):
        invalidate(cache, 0)
        for j, (position, flag) in enumerate([(left, flag_left), (right, flag_right)]):
            F.from_address(records + j * 0x18 + 8).value = position
            B.from_address(records + j * 0x18 + 0xd).value = flag
        dispatch(context, records)
        assert B.from_address(cache + 0x44).value == 1
        current = [P.from_address(cache + offset).value for offset in [8, 16]]
        assert all(current)
        if pointers is not None:
            assert pointers == current
        pointers = current
        endpoints = []
        for j, (position, flag) in enumerate([(left, flag_left), (right, flag_right)]):
            frame = current[j]
            index = int(position)
            voiced = index >= 0 and pitches[index] > 0
            # All nonnegative positions are exact half-frame points; ties select
            # the first frame, and mixed/unvoiced pairs retain the prior time.
            time = previous_times[j]
            if index < 0:
                # Negative positions clear candidate F0/spectrum but preserve
                # its timestamp, which the whole-frame copy then propagates.
                time = candidate_time
            elif pitches[index] > 0 and pitches[index + 1] > 0:
                time = 10. + position
            elif voiced:
                time = 10. + index
            mask = Q.from_address(frame + 0x150).value
            pitch = F.from_address(frame + 0x1d0).value
            if voiced and not flag:
                assert mask & 0x80000200 == 0x80000200
                assert abs(pitch) < 3e-4
            else:
                assert mask & 0x200 == 0
            assert D.from_address(frame + 0x148).value == time, (j, position, time, D.from_address(frame + 0x148).value)
            previous_times[j] = time
            if index >= 0:
                candidate_time = 10. + index
            endpoints.append(dict(position=position, flag=flag, valid=bool(mask & 0x200), pitch=pitch, time=time))
        snapshots = [c.string_at(frame, 0x328) for frame in current]
        dispatch(context, records)
        assert snapshots == [c.string_at(frame, 0x328) for frame in current]
        rows.append(dict(endpoints=endpoints, pointers_reused=True, prepared_reentry_unchanged=True))
    return dict(cases=len(rows), rows=rows,
                scope='Actual transition dispatcher, two wrappers and cache reuse after native invalidation; '
                      'owned ring, one part, no special/feature objects, F0/flags/time assertions; not host worklist construction')
