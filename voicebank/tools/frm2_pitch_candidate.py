"""Default mode4 primary/history candidate, before voicing and frame repair."""
from audit_frm2_numeric import f32


def primary_candidate(scores,nyquist,minimum_hz=40.,maximum_hz=1000.):
    count=len(scores)
    limit=int(f32(f32(min(3000.,nyquist)/nyquist)*count))
    search_start=max(2,int(f32(f32(15/nyquist)*(count*2))))
    minimum=max(0,int(f32(f32(count*f32(minimum_hz/nyquist))+1e-8)))
    maximum=min(limit,int(f32(f32(count*f32(maximum_hz/nyquist))+1e-8))+1)
    best=0;best_value=0.
    for i in range(search_start,limit-1):
        if scores[i]>scores[i-1] and scores[i]>scores[i+1] and scores[i]>best_value:
            best=i;best_value=scores[i]
    chosen=0
    for i in range(max(search_start,minimum),min(maximum,limit-2)):
        if not (best*.1<i and (scores[i]>f32(best_value*f32(.57)) or
                 (best<10 and best*3<i and scores[i]>f32(best_value*f32(.35))))):continue
        if not (scores[i]>scores[i-1] and scores[i]>scores[i+1] and
                scores[i]>scores[i-2] and scores[i+1]>scores[i+2]):continue
        left=i-1
        while left>0 and not (scores[left]<scores[left-1] and scores[left]<scores[left+1]):left-=1
        right=i+1
        while right<=limit-1 and not (scores[right]<scores[right-1] and scores[right]<scores[right+1]):right+=1
        stronger=any(scores[j]>scores[j-1] and scores[j]>scores[j+1] and scores[j]>f32(scores[i]*f32(1.1))
                     for j in range(i+1,int(min(i*1.5,limit-1))+1))
        if not stronger and (f32(scores[left]/scores[i])<f32(.9) or f32(scores[right]/scores[i])<f32(.9)):
            chosen=i;break
    if scores[chosen]<f32(.05):chosen=0
    refined=float(chosen)
    if chosen:
        left,value,right=scores[chosen-1:chosen+2]
        denominator=f32(f32(left-f32(value+value))+right)
        shift=f32(f32(f32(left-right)*.5)/denominator) if denominator else 0.
        refined=f32(refined+shift)
    return dict(index=chosen,refined_bin=refined,hertz=f32(f32(refined/count)*nyquist),
                strongest_index=best,normalization_bins=limit)


def candidate_with_history(scores,nyquist,history,voiced_count,queue_index,previous_frame_f0):
    result=primary_candidate(scores,nyquist)
    chosen=result['index'];original=chosen
    required=5 if queue_index else 2
    oldest=len(history)-1
    sentinel=f32(-3.4028234663852886e38)
    eligible=(oldest>=2 and voiced_count>=required and
              (queue_index==0 or previous_frame_f0>sentinel))
    if eligible:
        eligible=oldest>=required and all(history[oldest-j]>sentinel and
                  abs(f32(history[oldest-j]-history[oldest-j+1]))<=20 for j in range(1,required+1))
    if eligible:
        reference=f32(f32(history[-1]/nyquist)*len(scores))
        lo=max(1,int(reference*.8),int(f32(f32(len(scores)*f32(40/nyquist))+1e-8)))
        hi=min(result['normalization_bins']-1,int(reference*1.25+.5),
               int(f32(f32(len(scores)*f32(1000/nyquist))+1e-8))+1)
        best=0.;replacement=0
        for i in range(lo,hi+1):
            if scores[i]>scores[i-1] and scores[i]>scores[i+1] and scores[i]>scores[chosen]*.3 and scores[i]>best:
                best=scores[i];replacement=i
        if replacement:chosen=replacement
    refined=float(chosen)
    if chosen:
        left,value,right=scores[chosen-1:chosen+2]
        denominator=f32(f32(left-f32(value+value))+right)
        refined=f32(refined+(f32(f32(f32(left-right)*.5)/denominator) if denominator else 0.))
    result.update(index=chosen,primary_index=original,history_eligible=eligible,
                  history_changed_index=chosen!=original,refined_bin=refined,hertz=f32(f32(refined/len(scores))*nyquist))
    return result
