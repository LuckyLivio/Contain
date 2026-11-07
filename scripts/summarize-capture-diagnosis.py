"""Retain both predeclared diagnostic samples, without release acceptance claims."""
import argparse
import importlib.util
import json
from pathlib import Path

spec = importlib.util.spec_from_file_location('diag_pair', Path(__file__).with_name('summarize-capture-loss-pair.py'))
PAIR = importlib.util.module_from_spec(spec); spec.loader.exec_module(PAIR)


def main():
    p = argparse.ArgumentParser(description=__doc__); p.add_argument('--directory', type=Path, required=True); args = p.parse_args()
    plan = json.loads((args.directory/'diagnosis-plan.json').read_text(encoding='utf-8-sig'))
    cases = []
    for name in ('profile-off','profile-on'):
        directory = args.directory/name
        case = dict(label=name, capture=PAIR.summarize(directory), identity=None, receiver_profile=None, source_facts=None)
        for key, file in [('identity','follow-identity-diagnostic.json'),('receiver_profile','receiver-profile-summary.json'),('source_facts','source-capture-facts.json')]:
            target = directory/'public'/file
            if target.exists(): case[key] = json.loads(target.read_text(encoding='utf-8-sig'))
        cases.append(case)
    result = dict(schema_version=1, protocol='contain-capture-diagnosis-1', release_accepted=False,
                  purpose='Receiver/identity diagnosis; algorithm change is explicitly identified in plan; profile-on perturbs timing', plan=plan, cases=cases)
    output = args.directory/'public';output.mkdir(exist_ok=True)
    (output/'capture-diagnosis.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')

if __name__=='__main__': main()
