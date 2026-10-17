"""Predict legacy classification power and bands using analytic window/boundary geometry."""
from audit_frm2_numeric import P,I,F,D,Q,f32,stats
from frm2_classification_bands import bands
from frm2_classification_geometry import geometry


class ClassificationObserver:
    def __init__(self,pcm=None):
        self.pcm=pcm
        self.frames={};self.band_values={};self.errors=[];self.power_errors=[];self.geometry_errors=[];self.geometry=None;self.profile=None
    def before(self,ctx):
        count=I.from_address(ctx+0xc18).value
        if not 1<=count<=128:raise AssertionError('Unexpected raw queue')
        queue=P.from_address(ctx+0xc20).value
        frame=P.from_address(P.from_address(queue+8*(count-1)).value+0x28).value
        return frame,Q.from_address(frame+0x50).value,I.from_address(ctx+0xb00).value
    def after(self,ctx,token):
        frame,mask,center=token
        if not Q.from_address(frame+0x50).value&8192:return
        if mask&8192:raise AssertionError('Already initialized raw frame')
        analyzer=P.from_address(ctx+0xca8).value
        if not analyzer:raise AssertionError('Missing classification analyzer')
        config=P.from_address(analyzer+0x3b8).value
        profile=dict(mode=I.from_address(analyzer+0x3b0).value,alternative=I.from_address(config+0x5b4).value,preserved=I.from_address(config+0x5a0).value,bins=I.from_address(analyzer+0x69c).value,rate=I.from_address(analyzer+0x3c0).value)
        if profile['mode'] or profile['alternative'] or profile['preserved'] or not 1<profile['bins']<=16384:
            raise AssertionError(('Unmodeled classification profile',profile))
        if self.profile is not None and profile!=self.profile:raise AssertionError('Changing profile')
        self.profile=profile
        pointer=P.from_address(analyzer+0x6b0).value
        power=[F.from_address(pointer+4*k).value for k in range(profile['bins'])]
        boundaries=[F.from_address(analyzer+0x3e0+4*k).value for k in range(42)]
        window_size=I.from_address(analyzer+0x698).value
        ring_size=I.from_address(analyzer+0x3d0).value
        ring=P.from_address(analyzer+0x3c8).value
        window=P.from_address(analyzer+0x6a8).value
        if not 2<=window_size<=2*profile['bins'] or not window_size<ring_size:
            raise AssertionError('Unexpected legacy window geometry')
        if self.geometry is None:
            self.geometry=geometry(profile['rate'])
        expected_size,expected_half,weights,expected_boundaries=self.geometry
        if window_size!=expected_size or profile['bins']!=expected_half:
            raise AssertionError('Classification geometry differs')
        geometry_errors=[abs(a-b) for a,b in zip(boundaries,expected_boundaries)]
        geometry_errors.extend(abs(w-F.from_address(window+4*k).value) for k,w in enumerate(weights))
        if any(geometry_errors):raise AssertionError(('Classification coefficients differ',stats(geometry_errors)))
        self.geometry_errors.extend(geometry_errors)
        boundaries=expected_boundaries
        start=(center-window_size//2)%ring_size
        data=[0.]*(2*profile['bins'])
        absolute=round(D.from_address(frame+0x48).value*profile['rate'])
        for k in range(window_size-1):
            sample=F.from_address(ring+4*((start+k)%ring_size)).value
            if self.pcm is not None:
                at=absolute-window_size//2+k
                predicted_sample=self.pcm[at] if 0<=at<len(self.pcm) else 0.
                if predicted_sample!=sample:raise AssertionError('Owned classification PCM differs')
                sample=predicted_sample
            data[k]=f32(sample*weights[k])
        predicted_power=[f32(data[0]*data[0])]+[f32(f32(data[2*k]*data[2*k])+f32(data[2*k+1]*data[2*k+1])) for k in range(1,profile['bins'])]
        power_errors=[abs(a-b) for a,b in zip(predicted_power,power)]
        if any(power_errors):raise AssertionError(('Legacy prepared power differs',stats(power_errors),center,window_size))
        endpoint_error=abs(f32(data[1]*data[1])-F.from_address(pointer+4*profile['bins']).value)
        if endpoint_error:raise AssertionError('Legacy endpoint differs')
        self.power_errors.extend(power_errors+[endpoint_error])
        predicted=bands(predicted_power,float(profile['rate']//2),boundaries)
        feature=P.from_address(frame+0x128).value
        errors=[abs(v-F.from_address(feature+0x2c+4*k).value) for k,v in enumerate(predicted)]
        if max(errors)>1e-4:raise AssertionError(('Live classification differs',stats(errors)))
        self.errors.extend(errors)
        count=I.from_address(ctx+0xa5c).value
        if not 1<=count<=40:raise AssertionError('Unsupported selected band count')
        self.band_values[frame]=predicted
        self.frames[frame]=next((v for v in predicted[:count] if v>-100),None)
