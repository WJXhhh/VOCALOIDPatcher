"""Explicit DSE/MKL FFT backend for score sensitivity experiments.

Uses the completed harness's FFT descriptors on a fresh owned buffer. No native
pitch/score/history values are used. This is NOT the independent Python FFT.
"""
import ctypes as c
from audit_frm2_numeric import F,P,Q,f32


class NativeFftBackend:
    def __init__(self,native,estimator):
        self.native=native;self.math_object=P.from_address(estimator).value
        self.forward=c.CFUNCTYPE(Q,P,P)(P.from_address(native.lib._handle+0x224058).value)
        self.backward=c.CFUNCTYPE(Q,P,P)(P.from_address(native.lib._handle+0x224060).value)
    def __call__(self,data,standard_nyquist):
        size=len(data);index=size.bit_length()-1
        if size!=1<<index or not 1<=index<18:raise ValueError('Unsupported descriptor size')
        forward=P.from_address(self.math_object+0x290+index*8).value
        backward=P.from_address(self.math_object+0x320+index*8).value
        if not forward or not backward:raise AssertionError('Missing FFT descriptors')
        buffer=(F*(size+2))(*data)
        if self.forward(forward,c.addressof(buffer)):raise AssertionError('Forward FFT failed')
        for i in range(2,size,2):
            buffer[i]=f32(f32(buffer[i]**2)+f32(buffer[i+1]**2));buffer[i+1]=0.
        buffer[0]=f32(buffer[0]**2);buffer[1]=f32(buffer[1]**2)
        if standard_nyquist:buffer[size]=f32(buffer[size]**2)
        if self.backward(backward,c.addressof(buffer)):raise AssertionError('Inverse FFT failed')
        return list(buffer[:size])
