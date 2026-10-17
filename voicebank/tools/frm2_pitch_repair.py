"""Five-frame mode4 pitch repair; input is Hz after current-frame voicing.

This is a separate local stage, not an independent pitch estimator. Keep cents
state alongside Hz: native comparisons sometimes intentionally use differences
computed before an earlier replacement in the same stage.
"""
import math
from audit_frm2_numeric import f32

SENTINEL=f32(-3.4028234663852886e38)


def cents(value):
    return f32(math.log(value/440)*1200/math.log(2)) if value>0 else SENTINEL


def repair(values,force_voiced=False):
    if len(values)!=5 or any(not math.isfinite(x) or x<0 for x in values):
        raise ValueError('Expected five finite nonnegative Hz values')
    hz=list(values);pitch=list(map(cents,hz));events=[]
    def distance(a,b):return abs(f32(pitch[a]-pitch[b]))
    def all_voiced():return all(x>0 for x in hz)
    def clear(indices,label):
        events.append(label)
        for i in indices:hz[i]=0.;pitch[i]=SENTINEL
    def set_cents(i,value):
        pitch[i]=value
        hz[i]=0. if value==SENTINEL else f32(f32(2**f32(value/1200))*440)
    def blend(a,b,wa,wb):return f32(f32(a*f32(wa))+f32(b*f32(wb)))
    def interpolate(label):
        events.append(label)
        a,b=pitch[1],pitch[4]
        set_cents(2,blend(b,a,.33,.66));set_cents(3,blend(b,a,.66,.33))
    if not force_voiced and hz[0]==0 and all(x>0 for x in hz[1:]):
        a,b,c=distance(2,1),distance(3,2),distance(4,3)
        if (a>400 and b<300 and c<300) or (a>300 and b<100 and c<100):
            clear([1],'onset_jump')
    if all_voiced():
        a,b,c,d=distance(1,0),distance(2,1),distance(3,2),distance(4,1)
        valley=lambda:pitch[2]<f32(pitch[1]-300) and f32(pitch[2]+300)<pitch[3]
        peak=lambda:f32(pitch[1]+300)<pitch[2] and pitch[3]<f32(pitch[2]-300)
        # The first pass treats a valley with a different left continuity bound.
        if valley():
            if 1050<b<1350 and 1050<c<1350:
                set_cents(2,blend(pitch[1],pitch[3],.5,.5));events.append('octave_valley')
            elif a<150 and d<100:interpolate('valley_bridge')
            elif not force_voiced:clear([2,3],'valley_reject')
        if valley() or peak():
            if 1050<b<1350 and 1050<c<1350:
                set_cents(2,blend(pitch[3],pitch[1],.5,.5));events.append('octave_extremum')
            elif a<100 and d<100:interpolate('extremum_bridge')
            elif not force_voiced:clear([2,3],'extremum_reject')
        a,b,c,d,e=distance(1,0),distance(2,1),distance(3,2),distance(4,3),distance(3,1)
        if a<300 and d<300 and b>700 and c>700 and e<700:
            set_cents(2,blend(pitch[2],pitch[1],.5,.5));events.append('large_excursion_half')
    if all_voiced():
        a,b,c,d,e=distance(1,0),distance(2,1),distance(3,2),distance(4,3),distance(4,1)
        # Retained native interval: no finite value is simultaneously >1300 and <1100.
        if 1300<c<1100 and a<100 and b<100 and c>200 and d<100:
            interpolate('unreachable_interval')
        if a<200 and 1000<b<1350 and c<200 and 1000<d<1350 and e<200:
            interpolate('octave_plateau')
    if all_voiced():
        a,b,c,d,e=distance(2,1),distance(3,2),distance(4,3),distance(3,1),distance(4,1)
        if a>1300 and b<200 and c>1300 and e<200:
            old_left,old_right=hz[1],hz[4]
            hz[2]=blend(old_right,old_left,.33,.66);hz[3]=blend(old_right,old_left,.66,.33)
            pitch[2]=cents(hz[2]);pitch[3]=cents(hz[3]);events.append('hz_plateau_bridge')
        elif a>1300 and b>1300 and d<200:
            hz[2]=blend(hz[3],hz[1],.5,.5);pitch[2]=cents(hz[2]);events.append('hz_peak_bridge')
        elif a>1400 and not force_voiced:clear([2,1],'large_jump_reject')
    if all_voiced() and distance(1,0)<100 and distance(2,1)<100 and distance(3,2)>1800 and not force_voiced:
        clear([3],'late_jump')
    if not force_voiced and hz[0]==0 and all(x>0 for x in hz[1:]):
        a,b,c=distance(2,1),distance(3,2),distance(4,3)
        if ((a<200 and b<200 and 300<c<1100) or
            (a>400 and b<100 and c>700) or (a>300 and b>300 and c<100)):
            clear([1,2],'unstable_onset_pair')
        elif ((a>1800 and b>1800) or
              (a<100 and (b>300 and c>300 or b>500 and c<200))):
            clear([1],'unstable_onset')
        elif a>300 and b>300 and c>300:clear([1,2],'onset_three_jumps')
        elif a>500:clear([1],'onset_large_jump')
    if all(x>0 for x in hz[:4]) and hz[4]==0:
        a,b,c,d=distance(1,0),distance(2,1),distance(3,2),distance(3,1)
        if (a<200 and b>200 and c>300) or (a<300 and b>300 and d>300):
            clear([2,3],'unstable_offset_pair')
        elif c>300:
            clear([3],'unstable_offset')
            if a>300 and d>300:clear([1,2],'offset_three_jumps')
    # One native three-positive/two-zero branch writes zero to an already zero
    # slot. It has no additional Hz effect; island cleanup below remains ordered.
    if not force_voiced and hz[0]==0 and hz[1]>0 and hz[2]>0 and hz[3]==0 and hz[4]==0:
        clear([1,2],'two_frame_island')
    if hz[0]==0 and hz[1]>0 and hz[2]==0 and hz[3]==0:clear([1],'single_frame_island')
    if hz[0]==0 and all(x>0 for x in hz[1:4]) and hz[4]==0:clear([1,2,3],'three_frame_island')
    if hz[0]==0 and hz[1]>0 and hz[2]>0 and hz[3]==0 and hz[4]>0:clear([1,2],'split_island')
    if hz[0]==0 and hz[1]>0 and hz[2]==0:clear([1],'isolated_onset')
    return dict(hertz=hz,events=events)
