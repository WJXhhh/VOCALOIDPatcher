"""Owned public FFT descriptors and system float32 log, without loading DSE.

The specialized creation export/ABI is version-pinned. No analysis-instance
descriptor, private code address, original score or frame enters this backend.
"""
import ctypes as c
import hashlib
import math
import os
from pathlib import Path


FFT_SHA256 = '7cae07fabcede5a9aa6ddbef596eafb73220d25ff7e343943322c93a4db60860'
P, I, F = c.c_void_p, c.c_int32, c.c_float


def module_loaded(name):
    kernel = c.WinDLL('kernel32', use_last_error=True)
    kernel.GetModuleHandleW.argtypes = [c.c_wchar_p]
    kernel.GetModuleHandleW.restype = P
    return bool(kernel.GetModuleHandleW(name))


class PublicRealFft:
    """Single precision, one-dimensional, in-place CCS with owned lifetime."""
    def __init__(self, library, size=4096):
        library = Path(library).resolve()
        if hashlib.sha256(library.read_bytes()).hexdigest() != FFT_SHA256:
            raise ValueError('FFT runtime ABI needs review for this version')
        if c.sizeof(P) != 8 or not 4 <= size <= 32768 or size & (size - 1):
            raise ValueError('Unsupported FFT geometry or platform')
        self.size = size
        self.calls = 0
        self.guards_checked = 0
        self.handle = P()
        self.directory = None
        try:
            self.directory = os.add_dll_directory(str(library.parent))
            self.library = c.CDLL(str(library))
            self.create = self.library.DftiCreateDescriptor_s_1d
            self.create.argtypes, self.create.restype = [c.POINTER(P), I, I], I
            self.get = self.library.DftiGetValue
            self.get.argtypes, self.get.restype = [P, I, P], I
            self.commit = self.library.DftiCommitDescriptor
            self.commit.argtypes, self.commit.restype = [P], I
            self.compute = self.library.DftiComputeForward
            self.compute.argtypes, self.compute.restype = [P, P], I
            self.free = self.library.DftiFreeDescriptor
            self.free.argtypes, self.free.restype = [c.POINTER(P)], I
            self.check(self.create(c.byref(self.handle), 33, size), 'create')
            # This pinned ABI uses Windows LP64's four-byte MKL_LONG and
            # enum values. GetValue is checked with guarded, owned output.
            expected = {0: 33, 1: 1, 2: size, 3: 35, 10: 40, 11: 43, 21: 54}
            self.configuration = {key: self.setting(key) for key in expected}
            if self.configuration != expected or self.setting(4, F) != 1.:
                raise ValueError('Unexpected FFT defaults')
            self.check(self.commit(self.handle), 'commit')
        except BaseException:
            self.close()
            raise

    @staticmethod
    def check(code, step):
        if code:
            raise RuntimeError(f'FFT {step} returned {code}')

    def setting(self, key, ty=I):
        data = c.create_string_buffer(b'\xa5' * 32, 32)
        self.check(self.get(self.handle, key, c.addressof(data)), 'get')
        if data.raw[c.sizeof(ty):] != b'\xa5' * (32 - c.sizeof(ty)):
            raise ValueError('FFT configuration output ABI differs')
        return ty.from_buffer(data).value

    def __call__(self, data):
        if not self.handle:
            raise RuntimeError('FFT descriptor is closed')
        if len(data) != self.size or not all(math.isfinite(value) for value in data):
            raise ValueError('Invalid FFT input')
        guard = F(123456.).value
        buffer = (F * (self.size + 10))(*data, 0., 0., *([guard] * 8))
        self.check(self.compute(self.handle, c.addressof(buffer)), 'forward')
        if any(buffer[index] != guard for index in range(self.size + 2, self.size + 10)):
            raise AssertionError('FFT wrote past CCS output')
        self.calls += 1
        self.guards_checked += 1
        return [complex(buffer[2 * index], buffer[2 * index + 1]) for index in range(self.size // 2 + 1)]

    def close(self):
        if self.handle:
            self.check(self.free(c.byref(self.handle)), 'free')
            if self.handle:
                raise RuntimeError('FFT free retained descriptor')
        if self.directory is not None:
            self.directory.close()
            self.directory = None

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()


class SystemFloatLog:
    def __init__(self):
        self.library = c.CDLL('ucrtbase.dll')
        self.function = self.library.logf
        self.function.argtypes, self.function.restype = [F], F

    def __call__(self, value):
        return self.function(value)

    def metadata(self):
        kernel = c.WinDLL('kernel32', use_last_error=True)
        kernel.GetModuleFileNameW.argtypes = [P, c.c_wchar_p, c.c_uint32]
        kernel.GetModuleFileNameW.restype = c.c_uint32
        name = c.create_unicode_buffer(32768)
        if not kernel.GetModuleFileNameW(self.library._handle, name, len(name)):
            raise RuntimeError('Cannot identify system math runtime')
        path = Path(name.value)
        return dict(name=path.name, sha256=hashlib.sha256(path.read_bytes()).hexdigest())
