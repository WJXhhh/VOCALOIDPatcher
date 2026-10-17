"""Streaming FIR decimation; coefficients are explicit external inputs."""
from audit_frm2_numeric import F,I,P,f32


# Constructor-selected asset locations for the hash-pinned DLL. Coefficients
# remain external assets and are never written to repository evidence.
FILTERS={
    (11025,2):(0xa1da50,46),(11025,4):(0xa1ce90,58),
    (22050,2):(0xa1dff8,12),(22050,4):(0xa1db10,92),(22050,8):(0xa1cce0,106),
    **{(rate,factor):(rva,size) for rate in (44100,48000) for factor,rva,size in
       ((2,0xa1dff8,12),(4,0xa1d890,110),(8,0xa1dc80,222),(16,0xa1cf80,579))}}


def selected_coefficients(native,rate,factor):
    try:rva,size=FILTERS[(rate,factor)]
    except KeyError:raise ValueError('Unsupported FIR asset selection') from None
    return [F.from_address(native.lib._handle+rva+4*i).value for i in range(size)]


class Decimator:
    def __init__(self,factor,coefficients):
        if not 1<=factor<=16 or not factor<=len(coefficients)<=1024:raise ValueError('FIR geometry')
        self.factor=factor;self.coefficients=list(coefficients)
        self.history=[0.]*len(coefficients);self.pending=[]
    def feed(self,samples):
        output=[]
        for value in samples:
            self.pending.append(f32(value))
            if len(self.pending)==self.factor:
                self.history=list(reversed(self.pending))+self.history[:-self.factor]
                self.pending=[];total=0.
                for value,coefficient in zip(self.history,self.coefficients):
                    total=f32(total+f32(value*coefficient))
                output.append(total)
        return output


def runtime_coefficients(resampler):
    """Read bounded runtime coefficients; never serialize the proprietary table."""
    factor=I.from_address(resampler+0x28).value;size=I.from_address(resampler+0x3c).value
    pointer=P.from_address(resampler+0x30).value
    if not pointer or not 1<=factor<=16 or not factor<=size<=1024:raise AssertionError('Native FIR geometry')
    return factor,[F.from_address(pointer+4*i).value for i in range(size)]


class DecimatedSource:
    """Source PCM including zero padding, indexed by the live output schedule."""
    def __init__(self,pcm,factor,coefficients):
        self.pcm=pcm;self.filter=Decimator(factor,coefficients);self.output=[];self.consumed=0
    def window(self,total,cursor,ring_size,start,size):
        if not 0<=total<=1000000 or total<len(self.output):raise AssertionError('Output count regressed')
        required=total*self.filter.factor
        if required>self.consumed:
            signal=[self.pcm[i] if i<len(self.pcm) else 0. for i in range(self.consumed,required)]
            self.output.extend(self.filter.feed(signal));self.consumed=required
        if len(self.output)!=total:raise AssertionError('Decimated source count')
        values=[]
        for i in range(size):
            index=(start+i)%ring_size
            absolute=total-1-((cursor-1-index)%ring_size)
            values.append(self.output[absolute] if absolute>=0 else 0.)
        return values
