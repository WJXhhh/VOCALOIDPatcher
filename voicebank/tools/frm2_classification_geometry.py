"""Analytic geometry of the verified default classification constructor."""
import math
from audit_frm2_numeric import f32
from verify_frm2_analysis_windows import window


def geometry(rate):
    if not 0<rate<=192000:raise ValueError('Unsupported sample rate')
    size=(rate*1024)//22050+1
    half=int(2**math.ceil(f32(math.log(size-1))/math.log(2))*.5)
    low=math.log(1.0571428571428572)*1127.01048
    step=(math.log(8.142857142857142)*1127.01048-low)/25
    boundaries=[f32((math.exp((i*step+low)/1127.01048)-1)*700) for i in range(42)]
    return size,half,window(None,size,11),boundaries
