"""Mode4 window/mean model; optional owned-source FIR replaces native ring input."""
import math
from audit_frm2_numeric import F,D,I,P,B,f32


def prepare_pitch_pcm(estimator,time,decimated_source=None):
    size=I.from_address(estimator+0x2c).value
    ring_size=I.from_address(estimator+0x170).value
    pointer=P.from_address(estimator+0x168).value
    if not pointer or not 4<=size<=8192 or not size<=ring_size<=1000000:
        raise AssertionError('Pitch ring geometry')
    epoch=D.from_address(estimator+0x180).value
    rate=D.from_address(estimator+0x188).value
    cursor=I.from_address(estimator+0x174).value
    offset=I.from_address(estimator+0x3c).value
    half=I.from_address(estimator+0x30).value
    start=int(f32(math.floor(f32((time-epoch)*rate+.5))+f32(cursor)))+offset-half
    if B.from_address(estimator+0x1c).value:
        decimation=I.from_address(estimator+0x150).value
        if decimation<=0:raise AssertionError('Invalid decimation factor')
        start-=512//decimation
    if start<0:start+=ring_size
    if not 0<=start<ring_size:raise AssertionError('Pitch ring start')
    samples=[F.from_address(pointer+4*((start+i)%ring_size)).value for i in range(size)]
    if decimated_source is not None:
        predicted=decimated_source.window(I.from_address(estimator+0x178).value,cursor,ring_size,start,size)
        if predicted!=samples:raise AssertionError(('Independent decimated PCM differs',max(abs(a-b) for a,b in zip(predicted,samples))))
        samples=predicted
    total=0.
    for value in samples:total=f32(total+value)
    mean=f32(total/f32(size))
    return [f32(value-mean) for value in samples]
