"""Independent shuffled-uniform/polar-normal model and native STA random commits.

Only the isolated process's CRT seed is set through srand. No native function,
import pointer, random output, or DLL global is replaced or patched.
"""
import argparse
import ctypes as c
import json
import math
from pathlib import Path
from audit_frm2_numeric import Native, P, I, F, D, B, SHA256, f32
from verify_region_stationary_variation import chunks, HEAD_AMPLITUDE, TAIL_BASE, TAIL_AMPLITUDE
from verify_region_continuation_timing import predict
from verify_region_event_production import run
from region_commit_material import connected


class Gaussian:
    def __init__(self):
        self.shuffle = [0]*32
        self.last = 0
        self.cached = None

    def uniform(self, seed):
        if seed <= 0 or self.last == 0:
            seed = max(-seed, 1)
            for j in range(39, -1, -1):
                seed = seed*16807 % 2147483647
                if j < 32:
                    self.shuffle[j] = seed
            self.last = self.shuffle[0]
        seed = seed*16807 % 2147483647
        j = self.last//67108864
        self.last, self.shuffle[j] = self.shuffle[j], seed
        value = f32(self.last/2147483647)
        return (f32(.99999988) if value > .99999988 else value), seed

    def normal(self, seed):
        if seed >= 0 and self.cached is not None:
            value, self.cached = self.cached, None
            return value, seed
        while True:
            u, seed = self.uniform(seed)
            v, seed = self.uniform(seed)
            x, y = f32(f32(u+u)-1), f32(f32(v+v)-1)
            radius = f32(f32(x*x)+f32(y*y))
            if 0 < radius < 1:
                factor = f32(math.sqrt(-2*math.log(radius)/radius))
                self.cached = f32(factor*x)
                return f32(factor*y), seed


class RandomVariation:
    def __init__(self, seed):
        self.crt = seed
        self.gaussian = Gaussian()
        self.gaussian.normal(-7)  # Same ordinary call used to precondition native state.
        self.calls = 0
        self.zero_seeds = 0

    def rand(self):
        self.crt = (self.crt*214013+2531011) & 0xffffffff
        return (self.crt >> 16) & 0x7fff

    def value(self, base, amplitude, phase):
        seed = int(self.rand()*-100/32768)
        self.zero_seeds += seed == 0
        normal, _ = self.gaussian.normal(seed)
        self.calls += 1
        return f32(f32(base)+f32(f32(normal*f32(amplitude))/f32(2.5)))


def crt_for(n):
    # Resolve the actual imported rand owner rather than assuming a CRT DLL.
    address = P.from_address(n.lib._handle+0x224ad8).value
    kernel = c.WinDLL('kernel32', use_last_error=True)
    kernel.GetModuleHandleExW.argtypes = [c.c_uint, P, c.POINTER(P)]
    kernel.GetModuleFileNameW.argtypes = [P, c.c_wchar_p, c.c_uint]
    module, path = P(), c.create_unicode_buffer(1024)
    assert kernel.GetModuleHandleExW(6, address, c.byref(module))
    assert kernel.GetModuleFileNameW(module, path, len(path))
    crt = c.CDLL(path.value)
    assert c.cast(crt.rand, P).value == address
    crt.srand.argtypes = [c.c_uint]
    crt.srand.restype = None
    crt.rand.restype = I
    return crt


def prime(n):
    obj, seed = n.alloc(64), I(-7)
    n.fn(0x1cc290, P, P)(obj)
    value = n.fn(0x1cc340, F, P, c.POINTER(I))(obj, c.byref(seed))
    expected, final_seed = Gaussian().normal(-7)
    assert value == expected and seed.value == final_seed


def verify(dll):
    n = Native(dll)
    crt = crt_for(n)
    # Check the independent CRT recurrence against the actual imported runtime.
    crt_checks = 0
    for seed in [0, 1, 42, 12345, 0xffffffff]:
        crt.srand(seed)
        model = RandomVariation(seed)
        for _ in range(512):
            assert crt.rand() == model.rand()
            crt_checks += 1
    prime(n)
    gaussian = Gaussian()
    gaussian.normal(-7)
    normal_fn = n.fn(0x1cc340, F, P, c.POINTER(I))
    obj = n.alloc(64)
    n.fn(0x1cc290, P, P)(obj)
    normal_checks, normal_max_error = 0, 0.
    for initial in list(range(-99, 0)) + [0, 1, 0, -7, 0, 0]:
        native_seed, model_seed = I(initial), initial
        for _ in range(8):
            expected, model_seed = gaussian.normal(model_seed)
            actual = normal_fn(obj, c.byref(native_seed))
            normal_max_error = max(normal_max_error, abs(actual-expected))
            assert abs(actual-expected) <= 2e-6, (initial, actual, expected)
            assert native_seed.value == model_seed
            normal_checks += 1
    scalar_count, zero_seeds, scalar_max_error = 0, 0, 0.
    fn = n.fn(0x1cd270, F, P, F, F, D)
    state = n.alloc(16)
    B.from_address(state).value = 1
    for seed in [0, 1, 42, 12345, 0xffffffff]:
        prime(n)
        crt.srand(seed)
        model = RandomVariation(seed)
        for _ in range(512):
            for base, amplitude in [(.25, HEAD_AMPLITUDE), (TAIL_BASE, TAIL_AMPLITUDE)]:
                expected = model.value(base, amplitude, 64.)
                actual = fn(state, base, amplitude, 64.)
                scalar_max_error = max(scalar_max_error, abs(actual-expected))
                assert abs(actual-expected) <= 2e-6, (seed, scalar_count, actual, expected)
                scalar_count += 1
        zero_seeds += model.zero_seeds
    rows = []
    milliseconds = [1010, 1810, 1900, 1910, 1920, 2490, 2500, 2510, 3990, 4000]
    for seed in [0, 1, 42, 12345, 0xffffffff]:
        for self_edge in [False, True]:
            for gap in [0, 12, 13, 63]:
                edges = [('Sil', 'a'), ('a', 'Sil')] + ([('a', 'a')] if self_edge else [])
                def build(native, state, *args):
                    material = connected(native, state, *args, edges=edges)
                    B.from_address(state+0x40a8).value = 1
                    B.from_address(state+0x40a9).value = 0
                    prime(native)
                    crt.srand(seed)
                    material['next_gap'] = gap
                    return material
                report = run(dll, True, False, True, build, [['a']], milliseconds)
                for row in report['rows']:
                    if 'commit' not in row:
                        continue
                    duration = row['duration']
                    model = RandomVariation(seed)
                    expected_chunks = chunks(duration-80, model)
                    first, ranges, lengths, sources = predict(duration, gap, self_edge, expected_chunks)
                    commit, after = row['commit'], row['commit']['continuation']
                    assert commit['item_ranges'] == first
                    for key, expected in [('ranges', ranges), ('left_lengths', lengths),
                                          ('source_indices', sources)]:
                        assert after[key] == expected, (seed, duration, gap, key, after[key], expected)
                    indices = [j for j, source in enumerate(sources) if source == 0]
                    expected_sta = [dict(item=j, source_offset=chunk['source_offset'],
                                         requested_length=duration-80, direct=1)
                                    for j, chunk in zip(indices, expected_chunks)]
                    assert len(indices) == len(expected_chunks)
                    assert after['stationary_items'] == expected_sta
                    rows.append(dict(seed=seed, self_edge=self_edge, gap=gap,
                                     milliseconds=row['milliseconds'], slot=row['slot'], pitch=row['pitch'],
                                     split=row['split'], stationary_chunks=expected_chunks, ranges=ranges,
                                     variation_calls=model.calls, zero_seeds=model.zero_seeds))
    return dict(dse_sha256=SHA256, crt_checks=crt_checks, normal_checks=normal_checks,
                normal_max_error=normal_max_error, scalar_max_error=scalar_max_error,
                scalar_checks=scalar_count, scalar_zero_seed_calls=zero_seeds,
                sequences=len(rows), native_commits=2*len(rows), cases=rows,
                scope='Independent random arithmetic and owned two-note STA timing; '
                      'isolated CRT seeded via srand, normal state primed by ordinary native call. '
                      'No hooks or patched native state; not full acoustic rendering.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = verify(args.dll)
    args.output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps({key: result[key] for key in ['normal_checks', 'scalar_checks', 'sequences']}))
