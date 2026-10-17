"""Mode-2 peak preparation and initial tracks, independent of native calls."""
from audit_frm2_numeric import f32
from frm2_peak_selection import select, resolve_collision


def initialize(peaks, capacity, lower, upper):
    """ef9f0 sorting followed by f01d0 initialization; nonnegative capacity."""
    if capacity < 0:
        raise ValueError('Capacity must be nonnegative')
    # Strict maximum search preserves input order for equal amplitudes.
    retained = sorted((list(p) for p in peaks if p[1] > f32(-3e38)),
                      key=lambda p: -p[1])[:capacity+1]
    indices = [i for i,p in enumerate(retained) if lower <= p[0] <= upper][:capacity]
    return retained, indices


def reject_crossing(tracks, claimed, previous_peaks, current_peaks, index):
    """f18a0 mode 2 with valid current indices and positive queue index."""
    current=tracks[index]['current']
    for i in range(len(current_peaks)-1):
        # Native compares this loop's track index with the target peak index.
        if i==current:
            continue
        other=tracks[i]
        if claimed[other['current']]!=1:
            continue
        previous=tracks[index]['previous']
        if previous==-1 or other['previous']==-1 or current==-1 or other['current']==-1:
            return
        a=previous_peaks[previous][0]
        b=previous_peaks[other['previous']][0]
        x=current_peaks[current][0]
        y=current_peaks[other['current']][0]
        if (b<a and x<y) or (a<b and y<x):
            tracks[index]['current']=-1
            return


def blank_track():
    return dict(center=0.,amplitude=0.,previous=0,current=0,rank=0,retired=0,
                candidates=[0]*3,distance=[0.]*3,metric=[0.]*3,cost=[0.]*3,order=[0]*3)


def advance_zero(state, peaks, config):
    """Mode-2 ef9f0 at queue index zero, including immediate retirement."""
    retained,indices=initialize(peaks,int(config[4]),config[0x27],config[2])
    trace=dict(added=0,reused=0,collisions=0,retired=0)
    tracks=state['tracks']
    if not state['initialized']:
        tracks=[]
        for peak in indices:
            t=blank_track()
            t.update(center=retained[peak][0],amplitude=retained[peak][1],current=peak)
            tracks.append(t)
        trace['added']=len(tracks)
        state['tracks']=tracks
        state['initialized']=True
    else:
        claimed,owners=[0]*len(retained),[0]*len(retained)
        for t in tracks:
            t['previous'],t['current']=t['current'],-1
        for i,t in enumerate(tracks):
            if t['center']<=0 or not retained:
                continue
            result=select([p[:2] for p in retained],t['center'],t['amplitude'],0.,i,2,config)
            for key in ('current','candidates','distance','metric','cost'):
                t[key]=result[key]
            if result['order']!=[0]*3:
                t['order']=result['order']
            if result['status']:
                t['center']=0.
                continue
            t['rank']=0
            peak=t['current']
            if claimed[peak]:
                trace['collisions']+=len(resolve_collision(tracks,claimed,owners,i,owners[peak],bool(config[10]),
                    crossing=lambda *_: None))
            else:
                claimed[peak],owners[peak]=1,i
        for peak,p in enumerate(retained):
            if claimed[peak] or not config[0x27]<=p[0]<=config[2]:
                continue
            if len(tracks)<config[4]:
                t=blank_track()
                tracks.append(t)
                trace['added']+=1
            else:
                t=next((t for t in tracks if t['retired'] and t['current']==-1),None)
                if t is None:
                    continue
                trace['reused']+=1
            t.update(center=p[0],amplitude=p[1],current=peak,retired=0)
        for t in tracks:
            if t['center']<=0:
                t['retired']=1
    output=[]
    for t in tracks:
        output.append(retained[t['current']][:3] if t['current']>=0 else [0.,10000.,0.])
        if t['current']>=0:
            t['amplitude']=retained[t['current']][1]
        else:
            t.update(amplitude=10000.,center=0.,retired=1)
            trace['retired']+=1
    return output,trace


def retire_from_history(track, amplitudes, queue_index, rate, threshold, gap):
    """Mode-2 f1a80 retirement tail, after any queue repair has completed.

    amplitudes includes queue_index+1, the additional slot read by native code;
    None means that frame lacks this track's array slot.
    """
    if track['current'] != -1:
        return False
    retire=queue_index<2
    if queue_index>=2 and gap>0:
        if len(amplitudes)<=queue_index+1:
            raise ValueError('The native scan requires one additional queue slot')
        for i in range(queue_index+1,-1,-1):
            amplitude=amplitudes[i]
            if amplitude is not None and amplitude!=10000. and amplitude>f32(threshold):
                retire=f32(gap)<=f32((queue_index+1-i)/f32(rate))
                break
    if retire:
        track.update(retired=1,amplitude=10000.,center=0.)
    return retire
