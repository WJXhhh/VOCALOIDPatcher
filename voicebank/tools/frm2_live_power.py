"""Live default batch PCM-ring to spectrum model; native FFT is explicit."""
import ctypes as c
from audit_frm2_numeric import F,D,I,P,Q,B,f32
from frm2_raw_feature_observer import RingView
from verify_frm2_analysis_windows import window,prepare,fft


class PowerObserver:
    def __init__(self,native,native_fft=False,pcm=None):
        self.native=native;self.native_fft=native_fft;self.frames={};self.rows=[];self.windows={}
        self.pcm=pcm
        self.complex_frames={}
        self.forward=c.CFUNCTYPE(Q,P,P)(P.from_address(native.lib._handle+0x224058).value)
    def before(self,ctx):
        queue=P.from_address(ctx+0xc20).value;count=I.from_address(ctx+0xc18).value
        if not 1<=count<=128:raise AssertionError('Power queue geometry')
        item=P.from_address(queue+8*(count-1)).value
        return item,P.from_address(item+0x28).value,I.from_address(ctx+0xb00).value
    def after(self,ctx,token):
        item,frame,center=token
        if not Q.from_address(frame+0x50).value&8192:return
        if frame in self.frames:raise AssertionError('Repeated power frame')
        half=I.from_address(item).value
        if half==0:self.frames[frame]=[];return
        size=I.from_address(ctx+0xbb4).value
        kind=B.from_address(ctx+8+0x19c).value
        config=P.from_address(ctx+0xad0).value
        factor=self.native.fn(0x2b050,F,P,I,F)(config,0x68,0.)
        lookup=int(self.native.fn(0x2b050,F,P,I,F)(config,0x6b,0.))
        ring_size=I.from_address(ctx+0xaf0).value
        if not 4<=size<=32768 or not 4<=half<=65536 or not size<=ring_size<=1000000:
            raise AssertionError('PCM/FFT bounds')
        ring=RingView(P.from_address(ctx+0xae0).value,ring_size)
        signal=[ring[(center-size//2+i)%ring_size] for i in range(size)]
        if self.pcm is not None:
            offset=round(D.from_address(frame+0x48).value*44100)-size//2
            predicted=[self.pcm[j] if 0<=j<len(self.pcm) else 0. for j in range(offset,offset+size)]
            pcm_error=max(abs(a-b) for a,b in zip(signal,predicted))
            if pcm_error:raise AssertionError(('PCM differs',offset,pcm_error))
            signal=predicted
        key=(size,kind,lookup)
        if key not in self.windows:self.windows[key]=window(self.native,*key)
        data=prepare(signal,self.windows[key],factor,True)
        if len(data)!=half*2:raise AssertionError('FFT geometry differs')
        if self.native_fft:
            descriptor=P.from_address(ctx+8+0x290+(len(data).bit_length()-1)*8).value
            if not descriptor:raise AssertionError('Missing forward descriptor')
            buffer=(F*(len(data)+2))(*data)
            if self.forward(descriptor,c.addressof(buffer)):raise AssertionError('Forward FFT failed')
            bins=[complex(buffer[2*i],buffer[2*i+1]) for i in range(half)]
        else:
            transformed=fft(data)
            bins=[complex(f32(z.real),f32(z.imag)) for z in transformed[:half]]
        power=[abs(bins[0].real)]+[f32(f32(z.real*z.real)+f32(z.imag*z.imag)) for z in bins[1:]]
        ptr=P.from_address(item+8).value
        actual=[F.from_address(ptr+4*i).value for i in range(half)]
        error=max(abs(a-b) for a,b in zip(actual,power));relative=error/max(max(actual),1.)
        if relative>1e-6:raise AssertionError(('Power mismatch',relative))
        if self.native_fft and error:raise AssertionError(('MKL power differs',error))
        self.frames[frame]=power
        self.complex_frames[frame]=bins
        self.rows.append(dict(size=size,kind=kind,lookup=lookup,factor=factor,half=half,max_error=error,relative_error=relative))
