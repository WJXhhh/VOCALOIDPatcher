"""Normal candidate search geometry and the reviewed constant control profile.

Mode 1 has no estimated-pitch input. Mode 2 accepts the separate time-domain
estimator's output; producing that output is outside this model.
"""
from audit_frm2_numeric import f32


DEFAULT_CURVES = {15:20.,20:1.,22:3000.,23:.25,24:.15,25:5e-6,28:1.}


def controls(frequency, overrides=None):
    curves = {key:f32(value) for key,value in DEFAULT_CURVES.items()}
    curves.update({key:f32(value) for key,value in (overrides or {}).items()})
    return dict(lower=40.,current=f32(frequency),upper=1000.,ceiling=22050.,
                eligibility=(25.,40.),curves=curves)


def search_geometry(mode, configured, coarse_pitch=None):
    lower,current,upper = (configured[key] for key in ('lower','current','upper'))
    if mode == 1:
        return lower,current,upper
    if mode != 2 or coarse_pitch is None:
        raise ValueError('Mode 2 requires its explicit time-domain pitch input')
    if not coarse_pitch > 0:
        return lower,current,upper
    # The caller multiplies in double precision, then stores single precision.
    return (f32(max(float(lower),float(coarse_pitch)*.8)),
            max(lower,coarse_pitch),
            f32(min(float(upper),max(float(lower),float(coarse_pitch)*1.2))))
