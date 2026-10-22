"""Explicit rates from a frozen fixture score; not a third-party installer oracle."""
import argparse
import json
from pathlib import Path


def fraction(numerator, denominator):
    return numerator / denominator if denominator else None


def metrics(score):
    target = score["groups"]["target_success"]
    noise = score["groups"]["noise_success"]
    targets = target["expected"]
    successful = targets + noise["expected"]
    correct = target["correctly_attributed"]
    wrong = target["incorrectly_attributed"] + noise["incorrectly_attributed"]
    unknown = sum(group["unobserved"] + group["observed_but_unresolved"] for group in (target, noise))
    predictions = score["positive_raw_predictions"]
    false_predictions = score["false_positive_target_events"]
    return dict(
        schema_version=1,
        precision=fraction(predictions - false_predictions, predictions),
        recall=fraction(correct, targets),
        unknown_rate=fraction(unknown, successful),
        false_attribution_rate=fraction(wrong, successful),
        raw_false_discovery_rate=fraction(false_predictions, predictions),
        target_observation_rate=fraction(target["observed"], targets),
        event_loss_rate=None,
        event_loss_rate_reason="ETW delivered callback records and lost records do not share a verified enabled-provider denominator; counts cannot establish a loss percentage.",
        etw_events_lost=score.get("etw_events_lost"), etw_buffers_lost=score.get("etw_buffers_lost"),
        application_drops=score.get("dropped"),
        denominators=dict(target_success=targets, noise_success=noise["expected"], successful_supported_operations=successful,
                          raw_positive_predictions=predictions, failed=score["groups"]["failed"]["expected"],
                          unsupported=score["groups"]["unsupported"]["expected"]),
        definitions={
            "precision": "Correct supported positive raw predictions / all audited supported positive raw predictions. Supporting raw duplicates remain in this domain.",
            "recall": "Correct target successful syscall rows / all independent target successful syscall rows. One raw event cannot satisfy two rows.",
            "unknown_rate": "Unobserved or observed-but-unresolved successful target/noise rows / all supported successful target/noise rows. Verified other actors are not Unknown.",
            "false_attribution_rate": "Incorrectly attributed successful target/noise rows / all supported successful target/noise rows. Extra unmatched raw positives are separately captured by raw_false_discovery_rate.",
            "excluded": "Failed calls and unsupported operations remain in separate denominators. No-positive precision is null; no ETW measurement is null, never zero.",
            "scope": "Synthetic syscall fixture only. No general accuracy claim and no inference from snapshots or capture quality."})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("score", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    report = metrics(json.loads(args.score.read_text(encoding="utf-8-sig")))
    rendered = json.dumps(report, indent=2, allow_nan=False) + "\n"
    if args.output:
        args.output.write_text(rendered, encoding="utf-8")
    else:
        print(rendered, end="")


if __name__ == "__main__":
    main()
