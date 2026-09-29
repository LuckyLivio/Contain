"""Independent one-to-one syscall oracle. Never imported by capture code."""
import argparse
import bisect
from collections import defaultdict, Counter
import json
from pathlib import Path

SUPPORTED = {"write_requested", "rename_requested", "delete_requested", "set_value"}
NOISE = {"unrelated", "noise"}
CONTROL_NAMES = {".contain-demo-marker", ".fixture-go", ".detached-done", ".noise-ready"}

def normalize(path, sid=""):
    path = (path or "").replace("/", "\\").replace("\\\\?\\", "").lower()
    if path.startswith("hkcu\\"):
        path = "\\registry\\user\\" + sid.lower() + "\\" + path[5:]
    return path

def identity(value, event=False):
    if event: value = value.get("evidence", {})
    return value.get("pid"), str(value.get("process_creation_time" if event else "creation_time"))

def score(capture, truth, sid=""):
    events = [e for e in capture.get("events", []) if e.get("event_type") in {"file", "registry"}]
    index = defaultdict(list)
    for i, e in enumerate(events):
        index[(normalize(e["resource"], sid), e["operation"])].append((int(e["timestamp_ticks"]), i))
    for values in index.values(): values.sort()
    def matches(t):
        result=set()
        for path in {normalize(t["resource"],sid), normalize(t.get("destination"),sid)} - {""}:
            entries=index.get((path,t["operation"]), [])
            lo=bisect.bisect_left(entries,(int(t["start_ticks"]),-1))
            hi=bisect.bisect_right(entries,(int(t["end_ticks"]),len(events)))
            result.update(i for _,i in entries[lo:hi])
        return sorted(result)
    used=set(); valid_positives=set(); rows=[]
    # Audit each positive across all eligible oracle intervals. Concurrent calls may
    # overlap in time on the same resource; one actor must not be blamed for another.
    for t in truth:
        if t["operation"] in SUPPORTED and t["role"] not in NOISE:
            valid_positives.update(i for i in matches(t) if events[i]["confidence"] in {"High","Certain"} and identity(events[i],True)==identity(t))
    groups={g: Counter() for g in ["target_success", "noise_success", "failed", "unsupported"]}
    for t in sorted(truth,key=lambda t:(int(t["start_ticks"]),t["pid"],t["operation"],t["resource"])):
        supported=t["operation"] in SUPPORTED; noise=t["role"] in NOISE
        group="unsupported" if not supported else "failed" if not t["success"] else "noise_success" if noise else "target_success"
        g=groups[group]; g["expected"]+=1
        candidates=matches(t) if supported else []
        available=[i for i in candidates if i not in used]
        available.sort(key=lambda i:(identity(events[i],True)!=identity(t),i))
        matched=available[0] if available else None
        if matched is not None: used.add(matched)
        observation="unsupported_out_of_scope" if not supported else "observed" if matched is not None else "unobserved"
        attribution=None
        if matched is not None:
            e=events[matched]; positive=e["confidence"] in {"High","Certain"}
            if positive and (noise or identity(e,True)!=identity(t)): attribution="incorrectly_attributed"
            elif positive and identity(e,True)==identity(t) and not noise: attribution="correctly_attributed"
            elif noise and identity(e,True)==identity(t) and not positive: attribution="correctly_not_assigned_to_target_app"
            else: attribution="observed_but_unresolved"
        g[observation]+=1
        if attribution: g[attribution]+=1
        rows.append(dict(role=t["role"],group=group,operation=t["operation"],resource=t["resource"],pid=t["pid"],success=t["success"],
            observation=observation,attribution=attribution,matched_event_id=events[matched].get("id",str(matched)) if matched is not None else None,
            observed=matched is not None,raw_matches=len(candidates),outcome={"correctly_attributed":"Correct","incorrectly_attributed":"Incorrect"}.get(attribution,"Unknown")))
    tested_roots=[normalize(p,sid).rstrip("\\") for p in capture.get("watch_roots",[])]
    truth_paths={normalize(t["resource"],sid) for t in truth}; positives=set(); excluded_control=[]
    for i,e in enumerate(events):
        path=normalize(e["resource"],sid)
        if e["operation"] not in SUPPORTED or e["confidence"] not in {"High","Certain"}: continue
        if path.rsplit("\\",1)[-1] in CONTROL_NAMES:
            excluded_control.append(e.get("id",str(i))); continue
        if path.endswith("\\transientkey\\transientvalue"): continue  # Exact frozen uninstrumented diagnostic.
        if any(path==r or path.startswith(r+"\\") for r in tested_roots) or path in truth_paths: positives.add(i)
    false=positives-valid_positives
    for g in groups.values():
        for k in ["expected","observed","unobserved","unsupported_out_of_scope","correctly_attributed","incorrectly_attributed","observed_but_unresolved","correctly_not_assigned_to_target_app"]: g.setdefault(k,0)
        den=g["expected"]-g["unsupported_out_of_scope"]
        g["observation_rate"]=g["observed"]/den if den else None
        g["correct_attribution_rate"]=g["correctly_attributed"]/den if den else None
    correct=sum(r["attribution"]=="correctly_attributed" for r in rows); incorrect=sum(r["attribution"]=="incorrectly_attributed" for r in rows)
    return dict(schema_version=2,expected=len(rows),observed=len(used),correctly_attributed=correct,unknown=len(rows)-correct-incorrect,
        incorrect_attribution=incorrect,groups=groups,positive_raw_predictions=len(positives),false_positive_target_events=len(false),
        unexpected_positive_events=[events[i].get("id",str(i)) for i in sorted(false)],supporting_duplicate_raw_events=len((positives & valid_positives)-used),
        excluded_control_positive_events=len(excluded_control),precision=(len(positives)-len(false))/len(positives) if positives else None,
        dropped=capture["stats"]["events_dropped"],etw_events_lost=capture["backend"].get("etw_events_lost"),etw_buffers_lost=capture["backend"].get("etw_buffers_lost"),
        quality=capture["quality"]["level"],definition="Frozen exact syscall oracle; one raw event per row. Unobserved has null attribution. Final state cannot satisfy syscalls. Audit unmatched supported positive mutations in tested scope. No-positive precision is null. ADR 0006.",rows=rows)

if __name__=="__main__":
    p=argparse.ArgumentParser();p.add_argument("--capture",required=True);p.add_argument("--truth-root",required=True);p.add_argument("--sid",default="");a=p.parse_args()
    truth=[json.loads(line) for f in sorted(Path(a.truth_root).glob("ground-truth-*.jsonl")) for line in f.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    if not truth: raise ValueError("Fixture produced no independent ground truth")
    Path(a.truth_root,"ground-truth.json").write_text(json.dumps(truth,indent=2),encoding="utf-8")
    capture=json.loads(Path(a.capture).read_text(encoding="utf-8-sig"))
    print(json.dumps(score(capture,truth,a.sid),ensure_ascii=True))
