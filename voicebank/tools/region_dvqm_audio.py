"""Call-count-limited native DVQM audio-block experiment; retain PCM only in memory."""
import ctypes as c
import math
from audit_frm2_numeric import P, I, Q, F, D, B


def consume(n, control, max_blocks=4, harmonic_count=0, gate_pointer=True, prepass=False, pointer_initial=1.):
    assert 1 <= max_blocks <= 1024
    assert 0 <= harmonic_count <= 500
    call = n.fn(0xeb00, Q, I, F, P, P, P, P, P, P, I, P, P,
                F, F, F, I, F, F, F, P, D, D, D, B, B, B, B, B,
                D, D, I, P, P)
    print('audio control', hex(control), flush=True)
    block = I.from_address(control + 0xa4).value
    assert 64 <= block <= 256
    samples = n.alloc(block * 4 + 32)
    amplitudes = n.array(F, [.1] * 500)
    aux = [n.array(F, [pointer_initial if index == 0 else 1.]) for index in range(4)]
    peak = 0.
    nonzero = 0
    cursors = []
    if prepass:
        print('audio prepass begin', flush=True)
        c.memset(samples, 0xA5, block * 4 + 32)
        c.memset(samples + 16, 0, block * 4)
        prepass_status = call(harmonic_count, 440., amplitudes, aux[0] if gate_pointer else None,
                              aux[1], aux[2], aux[3], samples + 16,
                              0, None, None, .5, .5, .5, 1, 1., .5, 0., None,
                              0., 0., 0., 0, 0, 0, 0, 1, 0., 0., 0, None, control)
        assert prepass_status in [0, 1], prepass_status
        assert c.string_at(samples, 16) == b'\xA5' * 16
        assert c.string_at(samples + 16 + block * 4, 16) == b'\xA5' * 16
        assert all(math.isfinite(value) for value in (F * block).from_address(samples + 16))
        print('audio prepass status', prepass_status, flush=True)
    for iteration in range(max_blocks):
        print('audio block', iteration, 'begin', flush=True)
        c.memset(samples, 0xA5, block * 4 + 32)
        c.memset(samples + 16, 0, block * 4)
        result = call(harmonic_count, 440., amplitudes, aux[0] if gate_pointer else None, aux[1], aux[2], aux[3], samples + 16,
                      0, None, None, .5, .5, .5, 2, 1., .5, 0., None,
                      0., 0., 0., 0, 0, 0, 0, 1, 0., 0., 0, None, control)
        assert c.string_at(samples, 16) == b'\xA5' * 16
        assert c.string_at(samples + 16 + block * 4, 16) == b'\xA5' * 16
        assert result in [0, 1], result
        state = P.from_address(P.from_address(control).value + 0x60).value
        cursors.append(I.from_address(state + 0x7c).value if state else None)
        print('audio block', iteration, 'status', result, 'cursor', cursors[-1], flush=True)
        values = list((F * block).from_address(samples + 16))
        assert all(math.isfinite(value) for value in values)
        peak = max(peak, max(abs(value) for value in values))
        nonzero += sum(value != 0 for value in values)
        if result == 1:
            return dict(calls=iteration + 1, return_zero_blocks=iteration,
                        block_size=block, peak=peak, nonzero_samples=nonzero,
                        terminal_status=result, first_cursor=cursors[0], last_cursor=cursors[-1],
                        prepass_status=prepass_status if prepass else None)
    return dict(calls=max_blocks, return_zero_blocks=max_blocks, block_size=block, peak=peak,
                nonzero_samples=nonzero, terminal_status=None,
                first_cursor=cursors[0], last_cursor=cursors[-1], bounded_stop=True,
                prepass_status=prepass_status if prepass else None)
