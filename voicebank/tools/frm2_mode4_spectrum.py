"""Independent centered kind10 ed230 kernels and real-kernel convolution.

Kernel construction is derived from size, rate and factor. F0 candidate choice
and native cross-call cache behavior are outside this numerical model.
"""
from audit_frm2_numeric import f32
from verify_frm2_analysis_windows import window


def build_kernel_bank(size, sample_rate, factor, kind=10):
    """Recover centered kind10 kernel generation without native arrays."""
    if kind!=10 or size<3 or sample_rate<=0 or factor<=0:
        raise ValueError('Only positive centered kind10 inputs are modeled')
    nfft=min(131072,1<<int(f32(f32(f32((size-1)*factor)+1)-2)).bit_length())
    half=nfft//2
    width=int(f32(f32(factor*40)+1));center=(width-1)//2
    target=int(f32(f32(sample_rate*1024)/44100))
    spacing=f32(f32(sample_rate*.5)/half)
    low=int(f32(200/spacing)//1);high=int(f32(1000/spacing)//1)
    if high<=low:raise ValueError('Collapsed frequency transition')
    slope=f32(1/(high-low))
    lengths=[];selections=[]
    for index in range(half+1):
        blend=0. if index<=low else 1. if index>high else f32((index-low)*slope)
        length=int(f32(f32((target-size)*blend)+size))
        length=length-length%2+1
        if not lengths or length!=lengths[-1]:lengths.append(length)
        selections.append(len(lengths)-1)
    kernels=[]
    for length in lengths:
        if not 3<=length<=nfft+1:raise ValueError('Unmodeled kernel wrap')
        weights=window(None,length,kind)
        middle=(length-1)//2
        packed=[0.]*nfft
        packed[nfft-middle:]=weights[:middle]
        packed[:middle]=weights[middle:2*middle]
        if center*2>=nfft:raise ValueError('Kernel read exceeds packed buffer')
        kernels.append([f32(packed[abs(j-center)*2]/factor) for j in range(width)])
    return dict(nfft=nfft,width=width,center=center,lengths=lengths,
                selections=selections,kernels=kernels,initial_window=[f32(2/size)]*size)


def convolve_packed(data, selections, kernels, center):
    half=len(data)//2
    if len(data)%2 or len(selections)!=half+1:raise ValueError('Invalid packed size')
    output=[0.]*len(data)
    for index,selection in enumerate(selections):
        real=imag=0.
        for offset,weight in enumerate(kernels[selection]):
            source=abs(index-center+offset)
            if source>half:source=len(data)-source
            if not 0<=source<=half:raise ValueError('Kernel exceeds single-reflection range')
            if source==0:
                value=f32(weight*data[0])
            elif source==half:
                value=f32(weight*data[1])
            else:
                value=f32(weight*data[source*2])
                imag=f32(imag+f32(weight*data[source*2+1]))
            real=f32(real+value)
        if index==0:output[0]=real
        elif index==half:output[1]=real
        else:output[index*2:index*2+2]=[real,imag]
    return output
