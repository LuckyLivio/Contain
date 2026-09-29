"""Score full exports outside the capture timing; emit small metadata for PowerShell."""
import importlib.util
import json
import sys
from pathlib import Path
spec=importlib.util.spec_from_file_location("oracle",Path(__file__).with_name("score-fixture.py"));oracle=importlib.util.module_from_spec(spec);spec.loader.exec_module(oracle)
destination=Path(sys.argv[1]);truth_root=Path(sys.argv[2]);sid=sys.argv[3]
capture=json.loads((destination/"capture.json").read_text(encoding="utf-8-sig"))["data"]
truth=[json.loads(line) for f in sorted(truth_root.glob("ground-truth-*.jsonl")) for line in f.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
if not truth: raise ValueError("Empty ground truth")
report=oracle.score(capture,truth,sid)
(destination/"score.json").write_text(json.dumps(report,indent=2),encoding="utf-8")
(destination/"ground-truth.json").write_text(json.dumps(truth,indent=2),encoding="utf-8")
report.pop("rows")
metadata={k:capture[k] for k in ["stats","backend","quality"]}
metadata["score"]=report
(destination/"metadata.json").write_text(json.dumps(metadata),encoding="utf-8")
