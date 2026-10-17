"""Independent period/voicing/history from actual retained events.

No native pitch, period memory, mean or variation is a model input. Retained
events still come from the actual event producer and remain in memory only.
"""
import argparse
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

import verify_vqm_time_domain_filter as previous
from audit_frm2_numeric import P,I,F,D,Q,f32,SHA256
from frm2_candidate_geometry import controls
from frm2_candidate_pcm_source import CandidatePcmSource,raw_at,band_ratio,HOP,RING_SIZE
from frm2_candidate_peak_source import candidate_peak_source
from frm2_mode5_math_backend import PublicRealFft,module_loaded
from frm2_phase_controls import PhaseControls
from frm2_time_domain_period import TimeDomainPeriod
from verify_vqm_time_domain_period_owned import owned_period_checks


class PeriodObserver:
    def __init__(self,args,pcm,frequency):
        assert not module_loaded('DSE.dll')
        configured = controls(frequency,previous.base.PROFILES[args.profile])
        self.model = TimeDomainPeriod(configured)
        self.sources = []
        self.counts = dict(period_calls=0,period_pitch_byte_exact=0,level_state_values_byte_exact=0,
            period_history_values_byte_exact=0,pitch_history_values_byte_exact=0,
            mean_variation_values_byte_exact=0,event_input_records=0,derivative_error_max=0.,
            derivative_gate_decisions_exact=0,raw_gate_values_byte_exact=0,
            last_peak_values_byte_exact=0,consistency_count_values_byte_exact=0,
            caller_clipped_pitch_values_byte_exact=0)
        phase = PhaseControls()
        with PublicRealFft(args.dll.parent/'DSE_DFT.dll') as transform:
            producer = CandidatePcmSource(transform)
            for index in range(math.ceil(len(pcm)/HOP)+1):
                records = candidate_peak_source(producer.power_at(pcm,index))['records']
                raw = raw_at(pcm,index,configured['curves'][23] > 0)
                fields = raw['feature4'],raw['feature8'],raw['feature12'],band_ratio(records)
                derivative = phase.advance(raw['feature12'])['derivative'] if configured['curves'][23] > 0 else 0.
                self.sources.append(dict(records=records,values=fields,derivative=derivative))
            self.fft_calls = transform.calls
        assert not module_loaded('DSE.dll')

    def install(self,native,context):
        obj = P.from_address(context+0xc90).value
        assert obj and D.from_address(obj+0x2d0).value == .020
        assert I.from_address(obj+0x30).value == 0
        table = P.from_address(context).value
        original = c.CFUNCTYPE(Q,P)(P.from_address(table+8).value)
        private = native.alloc(0x100)
        c.memmove(private,table,0x100)
        def step(ctx):
            try:
                old_cursor = self.model.cursor
                assert I.from_address(obj+0x30).value == old_cursor
                slots = P.from_address(ctx+0xc30).value
                target = P.from_address(P.from_address(slots+8).value+0x28).value
                result = original(ctx)
                cursor = I.from_address(obj+0x30).value
                if cursor == old_cursor:
                    return result
                at = round(D.from_address(obj+0x38).value*44100./HOP)
                assert cursor == (at*HOP+128)%RING_SIZE
                assert 1 <= at <= len(self.sources)
                indices = [I.from_address(obj+0x6c+4*i).value for i in range(30)]
                amplitudes = [D.from_address(obj+0x1d8+8*i).value for i in range(30)]
                source_at = round(D.from_address(target+0x48).value*44100./HOP)
                assert source_at == at-1
                source = self.sources[source_at]
                matches = [target]
                feature = P.from_address(matches[0]+0x128).value
                for offset,value in zip((4,8,12,28),source['values']):
                    assert struct.pack('<f',value) == c.string_at(feature+offset,4),(at,'raw feature',offset)
                self.counts['raw_gate_values_byte_exact'] += 4
                error = abs(source['derivative']-F.from_address(feature+0x28).value)
                self.counts['derivative_error_max'] = max(self.counts['derivative_error_max'],error)
                assert error < 1e-4,(at,'derivative',error)
                assert (source['derivative'] > 3.) == (F.from_address(feature+0x28).value > 3.),(at,'derivative gate')
                self.counts['derivative_gate_decisions_exact'] += 1
                pitch = self.model.process(cursor,indices,amplitudes,source)
                self.counts['period_calls'] += 1
                assert struct.pack('<f',pitch) == c.string_at(obj+0x5c,4),(at,'pitch')
                self.counts['period_pitch_byte_exact'] += 1
                assert struct.pack('<f',pitch) == c.string_at(obj+0x50,4),(at,'caller clipped pitch')
                self.counts['caller_clipped_pitch_values_byte_exact'] += 1
                expected = self.model.state_bytes()
                actual = (c.string_at(obj+0x2d8,32),c.string_at(obj+0x2fc,120),
                    c.string_at(obj+0x374,4),c.string_at(obj+0x384,4),
                    c.string_at(obj+0x48,8),c.string_at(P.from_address(obj+0x60).value,40))
                for k,(want,got) in enumerate(zip(expected,actual)):
                    assert want == got,(at,'state',k)
                self.counts['level_state_values_byte_exact'] += 7
                self.counts['period_history_values_byte_exact'] += 30
                self.counts['pitch_history_values_byte_exact'] += 10
                self.counts['mean_variation_values_byte_exact'] += 2
                self.counts['last_peak_values_byte_exact'] += 1
                self.counts['consistency_count_values_byte_exact'] += 1
                self.counts['event_input_records'] += 30
                return result
            except BaseException:
                traceback.print_exc()
                os._exit(2)
        callback = c.CFUNCTYPE(Q,P)(step)
        native.keep.extend((original,callback))
        P.from_address(private+8).value = c.cast(callback,P).value
        P.from_address(context).value = private

    def report(self):
        return dict(active=True,source_inputs_before_dse_load=True,fft_calls=self.fft_calls,
            controls=self.counts,events=dict(sorted(self.model.events.items())),
            native_retained_events_remain_explicit_inputs=True)


def child(args):
    frequency = args.frequency
    if args.sample is None:
        pcm = previous.base.owned_signal(args.shape,frequency)
    else:
        _,_,sequences = previous.base.load_sequences(args.manifest,args.voice_root,args.ddb_tools)
        sequences += previous.base.auxiliary_sequences(args.aux_vdb)
        sequence = sequences[args.sample]
        pcm = sequence['pcm']
        frequency = sequence.get('pitch1',statistics.median(row['f0'] for row in sequence['frames']))
    if args.mode == 1:
        row = previous.child(args)
        row['time_period'] = dict(active=False)
        return row
    observer = PeriodObserver(args,pcm,frequency)
    def configured_batch(profile):
        batch = previous.base.configured_batch(profile)
        def observed(*arguments,**options):
            old_setup = options.get('setup_observer')
            if old_setup is not None:
                def setup(native,context):
                    old_setup(native,context)
                    observer.install(native,context)
                options['setup_observer'] = setup
            return batch(*arguments,**options)
        return observed
    proxy = types.SimpleNamespace(**previous.base.__dict__)
    proxy.configured_batch = configured_batch
    independent_child = types.FunctionType(previous.child.__code__,
        dict(previous.child.__globals__,base=proxy),previous.child.__name__,previous.child.__defaults__)
    row = independent_child(args)
    row['time_period'] = observer.report()
    return row


def review_period(dll,decoder_dir):
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
    def decode(root):
        ops = {}
        for start,end,_ in groups[root]:
            ops.update({op.address-pe.image_base:op for op in decoder.disasm(
                data[pe.offset(start):pe.offset(start)+end-start],pe.image_base+start)})
        return ops
    producer,caller = decode(0xe1930),decode(0xdff30)
    assert caller[0xe171d].operands[0].imm-pe.image_base == 0xe1930
    curve_sites = (0xe26b4,0xe26da,0xe27e4,0xe27fd,0xe2816,0xe28c8)
    assert all(producer[site].operands[0].imm-pe.image_base == 0x2b050 for site in curve_sites)
    assert producer[0xe2a50].operands[0].imm-pe.image_base == 0xe2ab0
    doubles = {0x5bf5b8:.7,0x5bf5e8:.9,0x5bf6a0:1.2,0x5bf6d0:1.4,
        0x5bf6c0:1.3,0x5bf5c8:.8,0x5bf540:.5,0x5bf1c8:.005,0x5bf188:.0015,
        0x5bf4a0:.4,0x5bf588:.6,0x5bf348:.1,0x5bf6e0:1.5,0x5bf778:2.5,
        0x5bf3e8:.2,0x5bf438:.3,0x5bf3d0:.17,0x5bf370:.1151292546497}
    for rva,value in doubles.items():
        assert data[pe.offset(rva):pe.offset(rva)+8] == struct.pack('<d',value),(hex(rva),'constant')
    return dict(period_producer_rva='0xe1930',event_caller_rva='0xdff30',call_site_rva='0xe171d',
        reset_rva='0xe2ab0',curve_consumer_sites=[hex(site) for site in curve_sites],
        reviewed_double_constants={hex(rva):value for rva,value in doubles.items()},
        rate=44100,ring_size=RING_SIZE,event_entries=30,period_entries=30,pitch_entries=10,
        previous_pitch_summary_entries=5,level_decay_seconds=.020,
        event_input='Retained30 indices and30 positive amplitudes; native event production still explicit',
        gate_source='Candidate target frame at coarse cursor frame minus1; do not use shifted queue after call',
        smoothing_weights=[.6,.1,.5],consistent_period_ratios_before4=[.9,1.2],
        consistent_period_ratios_from4=[.7,1.4],positive_pitch_limits=[40.,1000.])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll','manifest','voice-root','ddb-tools','aux-vdb','decoder-dir'):
        parser.add_argument('--'+name,type=Path,required=True)
    parser.add_argument('--output',type=Path)
    parser.add_argument('--child',action='store_true')
    parser.add_argument('--sample',type=int,choices=range(5))
    parser.add_argument('--shape',default='steady')
    parser.add_argument('--frequency',type=float,default=220.)
    parser.add_argument('--mode',type=int,choices=(1,2),default=1)
    parser.add_argument('--profile',choices=previous.base.PROFILES,default='default')
    args = parser.parse_args()
    if args.child:
        print(json.dumps(child(args)))
        return
    assert args.output
    baseline = json.loads(Path('voicebank_doc/evidence/454_vqm_time_domain_filter.json').read_text(encoding='utf-8'))
    contract = review_period(args.dll,args.decoder_dir)
    owned = owned_period_checks(args.dll)
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
                period_calls=row['time_period'].get('controls',{}).get('period_calls',0))),flush=True)
        return rows
    rows = repeat_cases(baseline['cases'])
    boundary = repeat_cases(baseline['owned_geometry_checks']['cases'])
    active = [row['time_period'] for row in rows+boundary if row['time_period']['active']]
    keys = active[0]['controls']
    summary = dict(cases=len(active),source_inputs_before_dse_load_cases=sum(row['source_inputs_before_dse_load'] for row in active),
        controls={key:(max if key.endswith('_max') else sum)(row['controls'][key] for row in active) for key in keys})
    events = {}
    for row in active:
        for name,count in row['events'].items():
            events[name] = events.get(name,0)+count
    summary['events'] = dict(sorted(events.items()))
    sources = tuple(baseline['source_sha256'])+('verify_vqm_time_domain_period.py','frm2_time_domain_period.py',
        'verify_vqm_time_domain_period_owned.py')
    report = dict(baseline)
    report.update(source_sha256={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources},
        time_period_contract=contract,owned_time_period_checks=owned,cases=rows,owned_geometry_checks=dict(baseline['owned_geometry_checks'],cases=boundary),
        time_period_summary=summary,
        scope='Independent normal mode2 period selection, decay, voicing gates and10-pitch history from explicit retained event inputs; all454 candidate/filter results preserved',
        limits=[limit for limit in baseline['limits'] if not limit.startswith((
            'The independent filtered PCM is verified at the actual time-domain consumer; event/period history',
            'Mode2 time-domain coarse pitch remains an explicit native input'))]+[
            'Retained event indices and amplitudes are still native inputs; this is not complete independent PCM-to-coarse-pitch production',
            'Only the reviewed44.1kHz mode2 protocol, constant curves and fixture/sample matrix verified; maker phase and original creation parameters remain unresolved'])
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(summary))


if __name__ == '__main__':
    main()
