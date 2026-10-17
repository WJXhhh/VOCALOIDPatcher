"""Independent candidate/history/confidence producer from prepared PCM records.

No analysis-object addresses, observed candidates, history, bounds or feature
values enter this model. Mode 2's separate coarse pitch is an explicit input.
"""
import struct

from frm2_candidate_geometry import search_geometry
from frm2_candidate_peak_filter import filter_candidate_peaks
from frm2_candidate_production import produce_candidates
from frm2_candidate_gate import confidence_gate,exceptional_confidence
from frm2_candidate_confidence import candidate_confidence
from frm2_candidate_history_state import selected_pair,repair_history,update_reference


class CandidatePipeline:
    def __init__(self, mode, configured):
        self.mode = mode
        self.configured = configured
        self.history = [(0.,0.)]*20
        self.state = dict(voiced_count=0,prior_pitch=0.,variation=0.)
        self.last_prediction = None

    def process(self, source, previous_energy, coarse_pitch=None):
        lower,current,upper = search_geometry(self.mode,self.configured,coarse_pitch)
        curves = self.configured['curves']
        automatic = curves[20]
        old_count,old_prior,old_variation = (self.state[key] for key in
                                            ('voiced_count','prior_pitch','variation'))
        previous = [self.history[-2-i][0] for i in range(2)]
        costs = [self.history[-2-i][1] for i in range(2)]
        prior = old_prior if old_prior > 0 else previous[0] if previous[0] > 0 else current
        originals = [(hz,db) for hz,db,span,bin_index in source['records']]
        raw = [(hz,db,span) for hz,db,span,bin_index in source['records']]
        filtered = None
        prediction = None
        peaks = []
        if automatic and raw:
            filtered = filter_candidate_peaks(raw,lower)
            peaks = filtered['peaks']
            prediction = produce_candidates(peaks,prior,previous,old_count,
                lower,upper,current,curves[23],self.configured['eligibility'])
            if prediction is not None:
                self.last_prediction = prediction
        pairs = prediction['pairs'] if prediction else [(0.,0.)]*3
        best_hz,best_cost = min(reversed(pairs),key=lambda pair:pair[1])
        cache = self.last_prediction['pool'][0] if self.last_prediction else (0.,0.)
        gate_state = dict(pairs=pairs,prior_pitch=prior,voiced_count=old_count,
            variation=old_variation,previous=list(zip(previous,costs)),reference_cache=cache)
        values = source['values']
        gate = confidence_gate(originals,peaks,best_hz,best_cost,*values,previous_energy,curves,gate_state)
        confidence = -1.
        if gate['writes']:
            power = struct.unpack('<2048f',source['power'])
            geometry = lower,upper,self.configured['ceiling']
            confidence = (exceptional_confidence(power,originals,best_hz,*geometry,values[0])
                if values[2] == 0 else
                candidate_confidence(power,originals,best_hz,*geometry,values[0],values[2])['confidence'])
        self.history[-1] = selected_pair(gate,(best_hz,best_cost),confidence,
                                       manual_pitch=current if automatic == 0 else None)
        events = []
        if automatic:
            self.history,events = repair_history(self.history,old_count,old_prior,old_variation)
        self.state = update_reference(self.history,old_count)
        self.history = self.history[1:]+[(0.,0.)]
        return dict(geometry=(lower,current,upper),prior=prior,filtered=filtered,
            prediction=prediction,pairs=pairs,gate=gate,confidence=confidence,
            history=list(self.history),state=dict(self.state),repair_events=events,
            reference_cache=cache)
