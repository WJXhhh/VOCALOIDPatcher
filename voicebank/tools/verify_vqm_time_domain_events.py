"""Precompute normal mode2 PCM-to-events/pitch/candidates before DSE loads.

Commercial PCM and all array predictions remain in memory. Only counts,
configuration, constants, locations and hashes are written to evidence.
"""
import argparse
from collections import Counter
import ctypes as c
import hashlib
import json
import math
import os
from pathlib import Path
import statistics
import struct
import subprocess
import sys
import traceback
import types

import verify_vqm_time_domain_period as previous
from audit_frm2_numeric import P,I,F,D,Q,SHA256
from frm2_candidate_geometry import controls
from frm2_candidate_pcm_source import CandidatePcmSource,raw_at,band_ratio,HOP,RING_SIZE
from frm2_candidate_peak_source import candidate_peak_source
from frm2_candidate_pipeline import CandidatePipeline
from frm2_mode5_math_backend import PublicRealFft,module_loaded
from frm2_phase_controls import PhaseControls
from frm2_time_domain_filter import prepare_filter
from frm2_time_domain_events import TimeDomainEvents
from frm2_time_domain_period import TimeDomainPeriod
from verify_vqm_time_domain_events_owned import owned_event_checks


class EventProduction:
    def __init__(self,args,pcm,frequency):
        assert not module_loaded('DSE.dll')
        assert len(pcm) >= 1408
        configured = controls(frequency,previous.previous.base.PROFILES[args.profile])
        self.configured = configured
        self.omitted_final_sample = int(len(pcm)%128 == 1)
        self.input_length = len(pcm)-self.omitted_final_sample
        self.analysis_pcm = list(pcm)
        if self.omitted_final_sample:
            self.analysis_pcm[-1] = 0.
        self.sources = []
        phase = PhaseControls()
        with PublicRealFft(args.dll.parent/'DSE_DFT.dll') as transform:
            producer = CandidatePcmSource(transform)
            for at in range(math.ceil(self.input_length/HOP)+1):
                power = producer.power_at(self.analysis_pcm,at)
                records = candidate_peak_source(power)['records']
                raw = raw_at(self.analysis_pcm,at,configured['curves'][23] > 0)
                values = raw['feature4'],raw['feature8'],raw['feature12'],band_ratio(records)
                derivative = phase.advance(values[2])['derivative'] if configured['curves'][23] > 0 else 0.
                self.sources.append(dict(power=struct.pack('<2048f',*power),records=records,values=values,derivative=derivative))
            self.fft_calls = transform.calls
        filtered,_,_ = prepare_filter(pcm[:self.input_length])
        events = TimeDomainEvents()
        period = TimeDomainPeriod(configured)
        pipeline = CandidatePipeline(2,configured)
        self.predictions = []
        self.complete = []
        last_normal = (self.input_length-1025)//HOP
        consumed = 0
        for at,source in enumerate(self.sources):
            coarse_at = at+1
            total = min(self.input_length,coarse_at*HOP+1152)+max(0,coarse_at-last_normal)*HOP
            assert consumed <= total <= len(filtered)
            events.push(filtered[consumed:total])
            events.scan(period.periods[-1],period.count)
            consumed = total
            cursor = (coarse_at*HOP+128)%RING_SIZE
            pitch = period.process(cursor,events.indices,events.amplitudes,source)
            self.predictions.append(dict(total=total,cursor=cursor,pitch=pitch,
                events=events.snapshot(),markers=events.marker_bytes(),period=period.state_bytes()))
            prior_energy = self.sources[at-1]['values'][2] if at else None
            self.complete.append(pipeline.process(source,prior_energy,pitch))
        self.events = dict(sorted(events.events.items()))
        self.period_events = dict(sorted(period.events.items()))
        self.prediction_count = len(self.predictions)
        self.filtered_values_consumed = consumed
        self.pipeline_calls = 0
        self.counts = Counter()
        assert not module_loaded('DSE.dll')

    def pipeline_factory(self,mode,configured):
        if mode != 2:
            return CandidatePipeline(mode,configured)
        assert configured == self.configured
        owner = self
        class PreparedPipeline:
            def process(self,source,previous_energy,coarse_pitch=None):
                at = owner.pipeline_calls
                predicted_source = owner.sources[at]
                assert source['power'] == predicted_source['power']
                assert source['records'] == predicted_source['records']
                assert source['values'] == predicted_source['values']
                assert struct.pack('<f',coarse_pitch) == struct.pack('<f',owner.predictions[at]['pitch'])
                assert previous_energy == (owner.sources[at-1]['values'][2] if at else None)
                owner.pipeline_calls += 1
                return owner.complete[at]
        return PreparedPipeline()

    def install(self,native,context):
        obj = P.from_address(context+0xc90).value
        assert obj and c.string_at(obj+0x6c,120) == bytes(120)
        assert c.string_at(obj+0xe8,480) == bytes(480)
        assert c.string_at(P.from_address(obj+0x18).value,RING_SIZE*4) == bytes(RING_SIZE*4)
        table = P.from_address(context).value
        original = c.CFUNCTYPE(Q,P)(P.from_address(table+8).value)
        private = native.alloc(0x100)
        c.memmove(private,table,0x100)
        def step(ctx):
            try:
                old_cursor = I.from_address(obj+0x30).value
                result = original(ctx)
                cursor = I.from_address(obj+0x30).value
                if cursor == old_cursor:
                    return result
                at = self.counts['calls']
                prediction = self.predictions[at]
                assert cursor == prediction['cursor'],(at,'period cursor')
                assert I.from_address(obj+0x2c).value == prediction['total']%RING_SIZE,(at,'input schedule',
                    prediction['total'],I.from_address(obj+0x2c).value,round(D.from_address(ctx+0xaf8).value*44100.))
                for k,(offset,want) in enumerate(zip((0x6c,0xe8,0x1d8,0x2c8,0x2c),prediction['events'])):
                    assert c.string_at(obj+offset,len(want)) == want,(at,'events',k)
                assert c.string_at(P.from_address(obj+0x18).value,RING_SIZE*4) == prediction['markers'],(at,'marker ring')
                assert c.string_at(obj+0x5c,4) == struct.pack('<f',prediction['pitch']),(at,'pitch')
                actual = (c.string_at(obj+0x2d8,32),c.string_at(obj+0x2fc,120),
                    c.string_at(obj+0x374,4),c.string_at(obj+0x384,4),c.string_at(obj+0x48,8),
                    c.string_at(P.from_address(obj+0x60).value,40))
                assert actual == prediction['period'],(at,'period states')
                self.counts.update(calls=1,event_state_values_byte_exact=93,
                    marker_ring_values_byte_exact=RING_SIZE,pitch_values_byte_exact=1,
                    period_state_values_byte_exact=51,input_schedule_checks=1)
                return result
            except BaseException:
                traceback.print_exc()
                os._exit(2)
        callback = c.CFUNCTYPE(Q,P)(step)
        native.keep.extend((original,callback))
        P.from_address(private+8).value = c.cast(callback,P).value
        P.from_address(context).value = private

    def report(self):
        assert self.counts['calls'] == self.prediction_count == self.pipeline_calls
        return dict(active=True,complete_predictions_before_dse_load=True,
            native_events_or_pitch_as_model_inputs=False,precomputed_predictions=self.prediction_count,
            complete_candidate_predictions=self.pipeline_calls,fft_calls=self.fft_calls,
            filtered_values_consumed=self.filtered_values_consumed,controls=dict(sorted(self.counts.items())),
            batch_input_length=self.input_length,omitted_final_sample=self.omitted_final_sample,
            event_branches=self.events,period_branches=self.period_events)


def child(args):
    if args.mode == 1:
        row = previous.child(args)
        row['time_events'] = dict(active=False)
        return row
    geometry = previous.previous.base
    frequency = args.frequency
    if args.sample is None:
        pcm = geometry.owned_signal(args.shape,frequency)
        if args.owned_length is not None:
            pcm = pcm[:args.owned_length]
    else:
        _,_,sequences = geometry.load_sequences(args.manifest,args.voice_root,args.ddb_tools)
        sequences += geometry.auxiliary_sequences(args.aux_vdb)
        sequence = sequences[args.sample]
        pcm = sequence['pcm']
        frequency = sequence.get('pitch1',statistics.median(row['f0'] for row in sequence['frames']))
    producer = EventProduction(args,pcm,frequency)
    def configured_batch(profile):
        batch = geometry.configured_batch(profile)
        def observed(*arguments,**options):
            old_setup = options.get('setup_observer')
            if old_setup is not None:
                def setup(native,context):
                    old_setup(native,context)
                    producer.install(native,context)
                options['setup_observer'] = setup
            return batch(*arguments,**options)
        return observed
    own_geometry_child = types.FunctionType(geometry.child.__code__,
        dict(geometry.child.__globals__,CandidatePipeline=producer.pipeline_factory),geometry.child.__name__,geometry.child.__defaults__)
    if args.owned_length is not None:
        assert args.sample is None
        direct_child = types.FunctionType(own_geometry_child.__code__,
            dict(own_geometry_child.__globals__,configured_batch=configured_batch),own_geometry_child.__name__,own_geometry_child.__defaults__)
        row = direct_child(args,producer.analysis_pcm,frequency)
        if producer.omitted_final_sample:
            original = geometry.configured_batch(args.profile)(args.dll,1,1,args.shape,frequency,analysis_mode=2,pcm_override=pcm)
            normalized = geometry.configured_batch(args.profile)(args.dll,1,1,args.shape,frequency,analysis_mode=2,pcm_override=producer.analysis_pcm)
            assert original == normalized,'The final isolated sample affects actual batch output'
            row['omitted_final_sample_control'] = True
        row['time_events'] = producer.report()
        row['owned_pcm_length'] = len(pcm)
        return row
    geometry_proxy = types.SimpleNamespace(**geometry.__dict__)
    geometry_proxy.child,geometry_proxy.configured_batch = own_geometry_child,configured_batch
    filter_proxy = types.SimpleNamespace(**previous.previous.__dict__)
    filter_proxy.base = geometry_proxy
    independent_child = types.FunctionType(previous.child.__code__,
        dict(previous.child.__globals__,previous=filter_proxy),previous.child.__name__,previous.child.__defaults__)
    row = independent_child(args)
    row['time_events'] = producer.report()
    return row


def review_events(dll,decoder_dir):
    sys.path.insert(0,str(decoder_dir))
    import capstone as cs
    from audit_vqm_v4_module_leads import PE
    from audit_vqm_feature_calls_cfg import runtime_groups
    data = dll.read_bytes()
    assert hashlib.sha256(data).hexdigest() == SHA256
    pe = PE(data)
    groups,_ = runtime_groups(data,pe)
    decoder = cs.Cs(cs.CS_ARCH_X86,cs.CS_MODE_64)
    decoder.detail = True
    operations = {}
    for start,end,_ in groups[0xdff30]:
        operations.update({op.address-pe.image_base:op for op in decoder.disasm(
            data[pe.offset(start):pe.offset(start)+end-start],pe.image_base+start)})
    assert operations[0xe171d].operands[0].imm-pe.image_base == 0xe1930
    constants = {0x5bf650:1.,0x5bf5d8:.85,0x5bf428:.25,0x5bf3a8:.15,
        0x5bf5f8:.93,0x5bf688:1.1,0x5bf6c8:1.35,0x5bf1f0:.006,
        0x5bf270:.020,0x5bf180:.0014,0x5bfea0:-1.,0x5bf5b8:.7,
        0x5bf5e8:.9,0x5bf6a0:1.2,0x5bf5c8:.8,0x5bf540:.5,0x5bf588:.6,
        0x5bf6c0:1.3}
    for rva,value in constants.items():
        assert data[pe.offset(rva):pe.offset(rva)+8] == struct.pack('<d',value),(hex(rva),'constant')
    return dict(event_producer_rva='0xdff30',period_call_site='0xe171d',period_producer_rva='0xe1930',
        reviewed_double_constants={hex(rva):value for rva,value in constants.items()},
        scan_cursor_offset='0x2c',event_position_offset='0x6c',absolute_extremum_offset='0xe8',
        delta_amplitude_offset='0x1d8',plateau_state_offset='0x2c8',marker_ring_pointer_offset='0x18',
        feedback_period_offset='0x370',feedback_consistency_offset='0x384',
        rate=44100,ring_size=RING_SIZE,event_entries=30,
        scan_delay_samples=1,positive_marker_floor=1.,negative_marker_ceiling=-1.,
        marker_storage='Rounded current event or truncated merged delta; signed16-bit conversion then float32',
        normal_input_schedule=dict(first_coarse_input=1408,regular_ahead_of_coarse=1152,hop=HOP,
            required_ahead_of_coarse=1025,last_normal='floor((effective input length-1025)/256)',
            isolated_final_sample='When supplied length modulo128 is1, batch ends before that sample; use effective input length for filter/schedule',
            later_zero_push=256,minimum_verified_pcm_length=1408),
        native_events_or_coarse_pitch_as_production_inputs=False)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll','manifest','voice-root','ddb-tools','aux-vdb','decoder-dir'):
        parser.add_argument('--'+name,type=Path,required=True)
    parser.add_argument('--output',type=Path)
    parser.add_argument('--child',action='store_true')
    parser.add_argument('--sample',type=int,choices=range(5))
    parser.add_argument('--owned-length',type=int,choices=(1408,1409,1535,1536,1537,1663,1664,4096,4097,4223,4224,4225,4351))
    parser.add_argument('--shape',default='steady')
    parser.add_argument('--frequency',type=float,default=220.)
    parser.add_argument('--mode',type=int,choices=(1,2),default=1)
    parser.add_argument('--profile',choices=previous.previous.base.PROFILES,default='default')
    args = parser.parse_args()
    if args.child:
        print(json.dumps(child(args)))
        return
    assert args.output
    baseline = json.loads(Path('voicebank_doc/evidence/455_vqm_time_domain_period.json').read_text(encoding='utf-8'))
    contract = review_events(args.dll,args.decoder_dir)
    owned = owned_event_checks(args.dll)
    def repeat_cases(old_rows):
        rows = []
        for old in old_rows:
            command = [sys.executable,'-B',str(Path(__file__).resolve()),'--child']
            for name in ('dll','manifest','voice-root','ddb-tools','aux-vdb','decoder-dir'):
                command += ['--'+name,str(getattr(args,name.replace('-','_')))]
            for name in ('shape','frequency','mode','profile','sample'):
                if old[name] is not None:
                    command += ['--'+name,str(old[name])]
            process = subprocess.run(command,capture_output=True,text=True,timeout=180)
            assert process.returncode == 0,(old['sample'],old['shape'],old['mode'],old['profile'],process.stderr[-1800:])
            row = json.loads(process.stdout)
            assert all(row[key] == value for key,value in old.items()),'Previous results changed'
            rows.append(row)
            print(json.dumps(dict(sample=row['sample'],shape=row['shape'],mode=row['mode'],profile=row['profile'],
                complete_mode2=row['time_events'].get('precomputed_predictions',0))),flush=True)
        return rows
    rows = repeat_cases(baseline['cases'])
    boundary = repeat_cases(baseline['owned_geometry_checks']['cases'])
    timing = []
    for length in (1408,1409,1535,1536,1537,1663,1664,4096,4097,4223,4224,4225,4351):
        command = [sys.executable,'-B',str(Path(__file__).resolve()),'--child','--mode','2','--owned-length',str(length)]
        for name in ('dll','manifest','voice-root','ddb-tools','aux-vdb','decoder-dir'):
            command += ['--'+name,str(getattr(args,name.replace('-','_')))]
        process = subprocess.run(command,capture_output=True,text=True,timeout=180)
        assert process.returncode == 0,(length,'owned schedule',process.stderr[-1800:])
        row = json.loads(process.stdout)
        assert row['owned_pcm_length'] == length and row['time_events']['complete_predictions_before_dse_load']
        timing.append(row)
        print(json.dumps(dict(owned_pcm_length=length,complete_mode2=row['time_events']['precomputed_predictions'])),flush=True)
    active = [row['time_events'] for row in rows+boundary if row['time_events']['active']]
    assert not any(row['omitted_final_sample'] for row in active),'Legacy455 observer matrix requires complete last input blocks'
    keys = active[0]['controls']
    summary = dict(cases=len(active),complete_predictions_before_dse_load_cases=sum(row['complete_predictions_before_dse_load'] for row in active),
        precomputed_predictions=sum(row['precomputed_predictions'] for row in active),
        complete_candidate_predictions=sum(row['complete_candidate_predictions'] for row in active),
        filtered_values_consumed=sum(row['filtered_values_consumed'] for row in active),
        controls={key:sum(row['controls'][key] for row in active) for key in keys})
    for group in ('event_branches','period_branches'):
        combined = Counter()
        for row in active:
            combined.update(row[group])
        summary[group] = dict(sorted(combined.items()))
    sources = tuple(baseline['source_sha256'])+('verify_vqm_time_domain_events.py','frm2_time_domain_events.py',
        'verify_vqm_time_domain_events_owned.py')
    report = dict(baseline)
    report.update(source_sha256={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources},
        time_event_contract=contract,owned_time_event_checks=owned,cases=rows,owned_input_schedule_checks=dict(cases=timing,
            complete_precomputed_calls=sum(row['time_events']['precomputed_predictions'] for row in timing),
            isolated_final_sample_controls=sum(row.get('omitted_final_sample_control',False) for row in timing),
            frames=sum(row['frames'] for row in timing),
            controls={key:sum(row['time_events']['controls'][key] for row in timing) for key in keys}),
        owned_geometry_checks=dict(baseline['owned_geometry_checks'],cases=boundary),time_event_summary=summary,
        scope='Complete normal mode2 PCM-to-retained-events/coarse-pitch/candidate/history/confidence production before DSE loads; all455 results preserved',
        limits=[limit for limit in baseline['limits'] if not limit.startswith('Retained event indices and amplitudes are still native inputs')]+[
            'Complete prediction covers the stated44.1kHz normal mode2 batch input schedule, PCM lengths at least1408 and constant configuration; other streaming or timing contracts not inferred',
            'The retained455 observer still receives native events as a separate diagnostic check; the new complete producer uses neither native events nor native pitch',
            'Peak phase, original maker scores, bit17, BRWSP maker inputs/configuration and saved zero arrays remain unresolved'])
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(summary))


if __name__ == '__main__':
    main()
