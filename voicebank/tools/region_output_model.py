"""Independent frame-position model for the tested coefficient/default branches."""
from audit_frm2_numeric import f32


def positions(groups, counts, reverse, coefficients, preserve_left_start=False):
    sign = -1 if reverse else 1
    bounds = []
    original_inner = []
    for start, end, a, b in groups:
        bounds.append([1-end, 1-start, 1-b, 1-a] if reverse else [start,end,a,b])
        original_inner.append(b-a)
    widths = [b[1]-b[0] for b in bounds]
    inners = list(original_inner)
    last_right_span = float(original_inner[1])
    for side, coefficient in enumerate(coefficients):
        if coefficient >= 1:
            continue
        scaled = max(1., f32(original_inner[side]*coefficient))
        width = int(f32(scaled+.5))
        widths[side] = inners[side] = width
        if side == 0:
            bounds[0][0] = bounds[0][2] = bounds[0][3]-width
        else:
            # Native right-side adjusted endpoint is based on the LEFT inner exit.
            bounds[1][1] = bounds[1][3] = bounds[0][3]+width
            last_right_span = scaled
    result = []
    for side, count in enumerate(counts):
        if count == 0:
            continue
        outer_start, outer_end, inner_start, inner_end = bounds[side]
        width, inner = widths[side], inners[side]
        def clip(x, origin, cap):
            # Preserve the native branch order even for adjusted inverted bounds.
            return min(cap,max(origin,x)) if reverse else (origin if x<origin else min(cap,x))
        if count > width:
            step = f32(1/f32((count-1)/(width-1))) if width>1 else 0.
            values = [clip(f32(outer_start+f32(i*step)),outer_start,outer_end-1) for i in range(count)]
        elif side == 0:
            origin = (inner_start if preserve_left_start else inner_end-count) if count<inner else outer_end-count
            values = [float(origin+i) for i in range(count)]
        elif count < inner:
            cap = inner_end-(inner-count)-1
            values = [clip(float(inner_start+i),inner_start,cap) for i in range(count)]
        elif count == inner:
            values = [clip(float(inner_start+i),inner_start,inner_start+original_inner[1]-1) for i in range(count)]
            values[-1] = clip(f32(f32(last_right_span-1)+inner_start),inner_start,inner_start+original_inner[1]-1)
        else:
            values = [float(inner_start+i) for i in range(count)]
        result.extend(f32(sign*v) for v in values)
    return result


def nasal_positions(groups, counts, reverse, coefficients=(1., 1.)):
    """PHDC Nasals/Liquids with active expansion metadata."""
    sign=-1 if reverse else 1
    result=[]
    left_exit = 1-groups[0][2] if reverse else groups[0][3]
    for side, ((start,end,a,b),count) in enumerate(zip(groups,counts)):
        origin,exit,inner_entry,inner_exit=([1-end,1-start,1-b,1-a] if reverse else [start,end,a,b])
        width=end-start
        if coefficients[side] < 1:
            width=int(f32(max(1.,f32((b-a)*coefficients[side]))+.5))
            if side==0:
                origin=inner_entry=inner_exit-width
            else:
                exit=inner_exit=left_exit+width
        if side==0:
            values=[min(inner_exit-1,origin+max(0,i-(count-width))) for i in range(count)]
        else:
            reverse_tail=exit+1 if inner_entry>=exit-1 else inner_entry
            forward_tail=inner_entry if inner_entry>=exit-1 else exit-1
            values=[min(exit-1,inner_entry+i) if i<width else (reverse_tail if reverse else forward_tail)
                    for i in range(count)]
        result.extend(float(sign*v) for v in values)
    return result


def fricative_curve(x, original, allocated, clamp_left):
    """Symmetric piecewise quadratic warp; float32 arithmetic follows each operation."""
    half=f32(f32(original-1)*.5)
    target_half=f32(f32(f32(f32(allocated/original)*original)-1)*.5)
    knee=f32(half*.25)
    transition=f32(half*.75)
    blend=f32(f32(target_half-half)/f32(target_half-knee))
    remaining=f32(1-blend)
    x=f32(x)
    mirrored=x>target_half
    y=f32(target_half-f32(x-target_half)) if mirrored else x
    if clamp_left:y=max(0.,y)
    if y>knee:
        if y>half:
            delta=f32(y-half)
            base=f32(half-f32(f32(blend*transition)*.5))
            linear=f32(delta*remaining)
            quadratic=f32(f32(f32(f32(-remaining/f32(target_half-half))*.5)*delta)*delta)
            y=f32(f32(base+linear)+quadratic)
        else:
            delta=f32(y-knee)
            y=f32(f32(f32(f32(f32(-blend/transition)*.5)*delta)*delta)+y)
    return f32(f32(half-y)+half) if mirrored else y


def fricative_positions(group,count,side,reverse,original,allocated,coefficient=1.,left_group=None):
    start,end,a,b=group
    width=end-start
    if coefficient<1:
        width=int(f32(max(1.,f32((b-a)*coefficient))+.5))
        if side==0:
            if reverse:end=b=a+width
            else:start=a=b-width
        elif reverse:
            start=a=left_group[2]-width
        else:
            end=b=left_group[3]+width
    outer_origin=end-1 if reverse else start
    lower=start if reverse else a if side else start
    upper=b-1 if reverse and side else end-1
    values=[]
    for i in range(count):
        x=i+allocated-count if side==0 else i
        y=fricative_curve(x,original,allocated,side==0)
        if side==0:y=f32(y-(original-width))
        physical=f32(outer_origin-y if reverse else outer_origin+y)
        values.append(lower if physical<lower else min(upper,physical))
    # This native marking loop flushes on increasing rounded positions or end.
    flags=[0]*count
    previous=-1
    repeats=1
    beginning=0
    for i,value in enumerate(values):
        current=int(f32(value+(.5 if value>=0 else -.5)))
        if current==previous:repeats+=1
        if (current>previous and previous!=-1) or i==count-1:
            if repeats>2:flags[beginning:i]=[1]*(i-beginning)
            repeats=1
            beginning=i
        previous=current
    return values,flags
