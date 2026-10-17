"""The fixed four-stage PCM filter preceding the normal time-domain estimator.

Inputs and stage memories use the reviewed double arithmetic order. The
original float PCM is promoted once; final gain/clipping does not feed back.
No analysis-object state or filtered PCM is required.
"""
import struct

from audit_frm2_numeric import f32


STATE_OFFSETS = tuple(range(0xb28,0xb88,8))
COEFFICIENTS = {0x5bf1a8:.00295752,0x5bf1e0:.00591504,0x5bf708:1.84036,
    0x5bf5e0:.852189,0x5bf630:.994624,0x5bf730:1.98925,0x5bf728:1.98922,
    0x5bf628:.989276,0x5bf200:.00707352,0x5bf620:.985853,
    0x5bf280:.0209273,0x5bf608:.958145,0x5bf840:5.,
    0x5bfbd0:32768.,0x5bff48:-32768.}


class TimeDomainFilter:
    def __init__(self):
        self.state = [0.]*12
        self.clipped = 0

    def sample(self, value):
        value = float(f32(value))
        s = self.state
        first = (((value*.00295752+s[0]*.00591504)+s[1]*.00295752)+s[2]*1.84036)-s[3]*.852189
        s[0],s[1],s[2],s[3] = value,s[0],first,s[2]
        second = (((first*.994624-s[4]*1.98925)+s[5]*.994624)+s[6]*1.98922)-s[7]*.989276
        s[4],s[5],s[6],s[7] = first,s[4],second,s[6]
        third = (second*.00707352+s[8]*.00707352)+s[9]*.985853
        s[8],s[9] = second,third
        fourth = (s[10]*.0209273+third*.0209273)+s[11]*.958145
        s[10],s[11] = third,fourth
        scaled = fourth*5.
        self.clipped += scaled >= 32768. or scaled <= -32768.
        return min(32768.,max(-32768.,scaled))

    def snapshot(self):
        return struct.pack('<12d',*self.state)


def prepare_filter(pcm, padding=4096):
    """Predict PCM and zero-tail outputs plus every normal push boundary."""
    checkpoints = {0,len(pcm)}
    checkpoints.update(range(128,len(pcm),128))
    checkpoints.update(len(pcm)+n for n in range(256,padding+1,256))
    model = TimeDomainFilter()
    states = {0:model.snapshot()}
    values = []
    for index in range(len(pcm)+padding):
        values.append(model.sample(pcm[index] if index < len(pcm) else 0.))
        if index+1 in checkpoints:
            states[index+1] = model.snapshot()
    return values,states,model.clipped
