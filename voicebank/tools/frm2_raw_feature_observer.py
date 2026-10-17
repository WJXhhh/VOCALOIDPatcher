"""Read-only attachment that associates independently computed raw features with frames."""
from audit_frm2_numeric import F,D,I,P,Q
from frm2_raw_features import raw_features


class RingView:
    def __init__(self,pointer,size):self.pointer=pointer;self.size=size
    def __len__(self):return self.size
    def __getitem__(self,index):
        if not 0<=index<self.size:raise IndexError(index)
        return F.from_address(self.pointer+4*index).value


class RawFeatureObserver:
    def __init__(self,pcm=None,phase_controls=False,phase_stage=False,classification_observer=None):
        self.classification_observer=classification_observer
        self.phase_stage=phase_stage
        self.frames={};self.comparisons=[];self.pcm=pcm;self.phase_controls=None
        if phase_controls:
            from frm2_phase_controls import PhaseControls
            self.phase_controls=PhaseControls()
    def before(self,context):
        count=I.from_address(context+0xc18).value
        if not 1<=count<=128:raise AssertionError('Unexpected raw-frame queue length')
        queue=P.from_address(context+0xc20).value
        frame=P.from_address(P.from_address(queue+8*(count-1)).value+0x28).value
        return frame,I.from_address(context+0xb00).value,Q.from_address(frame+0x50).value
    def after(self,context,token):
        frame,center,mask=token
        if not Q.from_address(frame+0x50).value&8192:return
        if mask&8192:raise AssertionError('Raw feature frame already initialized')
        size=I.from_address(context+0xaf0).value;hop=I.from_address(context+0xbb0).value
        pointer=P.from_address(context+0xae0).value
        if not pointer or not 4<=size<=1000000 or not 2<=hop<=8192:
            raise AssertionError('Unexpected raw PCM geometry')
        ring=RingView(pointer,size)
        if self.pcm is not None:
            absolute=round(D.from_address(frame+0x48).value*44100)
            ring=OwnedRingView(self.pcm,absolute,center,size)
            for delta in range(-hop-1,hop+1):
                at=(center+delta)%size
                if ring[at]!=F.from_address(pointer+4*at).value:raise AssertionError('Owned raw PCM differs')
        predicted=raw_features(ring,(center-hop)%size,hop,I.from_address(context+0xad8).value)
        features=P.from_address(frame+0x128).value
        actual={name:F.from_address(features+offset).value for name,offset in [('feature4',4),('feature8',8),('feature12',12)]}
        errors={key:abs(predicted[key]-actual[key]) for key in predicted}
        # The phase branch overwrites +4/+c at d3a7c/d3b88. They no longer
        # represent raw_features(), but +18/+28 still use the original energy.
        # Keep the original prediction for phase controls and verify those
        # directly below; never substitute the overwritten native energy.
        checked_errors=[errors['feature8']] if self.phase_stage else errors.values()
        if any(checked_errors):raise AssertionError(('Live raw features differ',predicted,actual,center,hop,size))
        phase_error=0.
        if self.phase_controls is not None:
            controls=self.phase_controls.advance(predicted['feature12'])
            for key,off in (('level',0x18),('derivative',0x28)):
                phase_error=max(phase_error,abs(controls[key]-F.from_address(features+off).value))
                if phase_error>1e-4:
                    raise AssertionError(('Phase control differs',key,controls[key],F.from_address(features+off).value))
            count=I.from_address(context+0xa5c).value
            if not 1<=count<=40:raise AssertionError('Unexpected classification count')
            expected_classes=self.classification_observer.band_values[frame] if self.classification_observer else [0.]*count
            if any(F.from_address(features+0x2c+4*k).value!=expected_classes[k] for k in range(count)):
                raise AssertionError('Default classification initialization changed')
            if self.classification_observer:
                controls['classification']=self.classification_observer.frames[frame]
            predicted['phase_controls']=controls
        self.frames[frame]=predicted
        self.comparisons.append(dict(max_error=0.,hop=hop,ring_size=size,start=(center-hop)%size,
                                     phase_control_error=phase_error,raw_feature_errors=errors))


class OwnedRingView:
    """Physical ring coordinates around one center, backed by owned source PCM."""
    def __init__(self,pcm,absolute,center,size):
        self.pcm=pcm;self.absolute=absolute;self.center=center;self.size=size
    def __len__(self):return self.size
    def __getitem__(self,index):
        if not 0<=index<self.size:raise IndexError(index)
        delta=(index-self.center+self.size//2)%self.size-self.size//2
        source=self.absolute+delta
        return self.pcm[source] if 0<=source<len(self.pcm) else 0.
