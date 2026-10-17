"""Independent default mode4 log-spectrum baseline and frequency weighting."""
import math
from audit_frm2_numeric import f32


def preprocess(spectrum,nyquist):
    n=len(spectrum)
    logs=[-200. if v==0 else f32(20*math.log10(f32(math.sqrt(v))/32768)) for v in spectrum]
    floor=[0.]*n
    low=math.floor(f32(f32(f32(50/nyquist)*n)+.5))
    minimum=max(80.,f32(f32(4/n)*nyquist))
    variable=max(minimum,f32(f32(5/n)*nyquist))
    radius=int(f32(f32(f32(variable+minimum)/nyquist)*n))
    transition=f32(f32(700/nyquist)*n)
    weights=[f32(f32(1-f32(i/radius))**2) for i in range(radius+1)]
    total=0.
    for w in weights:total=f32(total+w)
    full_denominator=f32(f32(total+total)-weights[0])
    peak=last_min=0
    for i in range(1,n-1):
        if logs[i]<logs[i-1] or logs[i]<logs[i+1]:
            if logs[i]<=logs[i-1] and logs[i]<=logs[i+1]:
                width=radius
                if peak<transition:
                    width=int(f32(f32(f32(f32(f32(peak*variable)/transition)+minimum)/nyquist)*n))
                numerator=denominator=0.
                start=peak-width;stop=min(n-1,peak+width)
                cached=width==radius and start>2
                for j in range(start,stop+1):
                    if cached:w=weights[abs(j-peak)]
                    else:
                        w=f32(f32(1-f32(abs(j-peak)/width))*min(f32(abs(j)/3),1.))
                        w=f32(w*w)
                        denominator=f32(denominator+w)
                    numerator=f32(numerator+f32(w*logs[abs(j)]))
                if cached:denominator=full_denominator
                average=f32(numerator/denominator) if denominator else numerator
                floor[peak]=average
                if i<low+low:average=f32(average-f32(f32(1-f32(abs(low-i)/low))*6))
                for j in range(last_min,i):floor[j]=average
                if last_min>2 and last_min+2<n:
                    old=floor[last_min-2]
                    for j in range(last_min-1,last_min+2):
                        floor[j]=f32(f32(f32((j-(last_min-2))*.25)*f32(average-old))+old)
                last_min=i
        else:peak=i
    if last_min:
        floor[last_min:]=[floor[last_min-1]]*(n-last_min)
    else:floor=[0.]*n
    result=[max(0.,f32(a-b)) for a,b in zip(logs,floor)]
    first=next((i for i in range(1,n-1) if result[i]<=result[i-1] and result[i]<=result[i+1]),0)
    tail=max(result[first:])
    if tail<result[0]:
        scale=f32(tail/result[0])
        for i in range(first):result[i]=f32(scale*result[i])
    maximum=max(logs);lift=f32(maximum-20)
    for i,value in enumerate(logs):
        frequency=f32(f32(i/n)*nyquist)
        allowance=30. if frequency<150 else 40. if frequency>=300 else f32(40-f32(f32(f32(300-frequency)*10)/150))
        threshold=f32(maximum-allowance)
        if value<threshold:result[i]=f32(result[i]/f32(f32(f32(threshold-value)*.5)+1))
        if value>lift:result[i]=f32(f32(f32(f32(value-lift)/20)+1)*result[i])
    maximum=max(result)
    result[0]=maximum;result[1]=f32(maximum*f32(.9));result[2]=0.
    result=[f32(v*10) for v in result]
    return dict(logs=logs,baseline=floor,processed=result)
