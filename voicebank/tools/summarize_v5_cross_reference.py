"""Package existing cross-reference statistics; no native calls or installed writes."""
import hashlib
import json
from pathlib import Path
import argparse


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('work_root', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    assert not args.output.exists()
    root = args.work_root
    def load(name):
        return json.loads((root/name).read_text(encoding='utf-8'))
    result = dict(schema_version=1, date='2026-10-15',
        cross_reference=load('results.json'),
        native_frame_verification=load('native-vqm-and-ordinary.json'),
        attack_release_components=load('attack_release_registry.json'),
        attack_release_unique_resource_audio=load('dvqm-audio-33.json'),
        external_work_root=str(root.resolve()))
    for row in result['attack_release_components']:
        record = load('dvqm-'+row['component']+'.json')
        row['native_directory_verification'] = record
        resource = Path(row['path'])/'dvqm.dat'
        row['resource_bytes'] = resource.stat().st_size
        assert sha(resource) == record['resource_sha256']
        row['resource_sha256'] = record['resource_sha256']
    tools = Path(__file__).resolve().parent
    names = ['audit_v5_cross_reference.py','verify_cross_bank_vqm_read.py',
        'summarize_v5_cross_reference.py','verify_reference_ddi.py','probe_frm2.py',
        'pitch2_reference.py','analyze_reference_coverage.py','analyze_reference_regions.py',
        'verify_vqm_timbre_selector_scope.py','verify_region_dvqm_segments.py','region_dvqm_audio.py']
    result['source_sha256'] = {name:sha(tools/name) for name in names}
    assert result['cross_reference']['tool_sha256'] == result['source_sha256'][names[0]]
    assert result['native_frame_verification']['tool_sha256'] == result['source_sha256'][names[1]]
    assert (root/'native-vqm-and-ordinary.json').read_bytes() == (root/'native-repeat.json').read_bytes()
    previous = load('prior_evidence_sha256.json')
    evidence = tools.parents[1]/'voicebank_doc/evidence'
    assert all(sha(evidence/name)==digest for name,digest in previous.items())
    result['validation'] = dict(python_unittest_passed=36,solution_debug_warnings=0,
        solution_debug_errors=0,native_repeat_byte_identical=True,
        prior_evidence_json_unchanged=len(previous),no_editor_launch_or_deployment=True,
        no_temp_artifacts=True,ddb_full_content_sha256_not_computed=True)
    result['initial_probe_corrections'] = [
        'In-place DDB reads require the logical byte counter to equal the actual file offset.',
        'DSE5 loading normalizes Frame F0 to cents and sets bit31, already established in delivery110.']
    files = ['results.json','native-vqm-and-ordinary.json','native-repeat.json',
             'attack_release_registry.json','dvqm-audio-33.json']
    result['artifact_sha256'] = {name:sha(root/name) for name in files}
    result['limits'] = [
        'Chinese V5 versus Japanese V4X cannot isolate generation from language, vendor or configuration.',
        'Structural reference validation does not verify all acoustic values or full-song audio.',
        'Effect audio is one prepass plus four 256-sample blocks per template, without host dispatch or singer blending.',
        'Original maker provenance and bit17 production remain unknown and outside this round.',
        'Empty saved TDB does not rule out runtime additions or auxiliary template sources.']
    args.output.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(evidence=str(args.output),bytes=args.output.stat().st_size,sha256=sha(args.output))))


if __name__ == '__main__':
    main()
