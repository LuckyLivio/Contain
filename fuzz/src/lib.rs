//! Bounded oracles over the production crate. No ETW provider or API is mocked.
use contain_core::{
    correlation::correlate,
    filesystem::Snapshot,
    lifetime::LifetimeCache,
    model::{AttributionEvidence, EvidenceSource, ProcessRecord, RawEvidence, SystemEvent},
};
use std::collections::BTreeMap;

pub fn event_json(data: &[u8]) {
    if data.len() > 65_536 {
        return;
    }
    // Invalid evidence is allowed to be rejected, never silently repaired.
    if let Ok(event) = serde_json::from_slice::<SystemEvent>(data) {
        let encoded = serde_json::to_vec(&event).expect("accepted event must serialize");
        let restored: SystemEvent =
            serde_json::from_slice(&encoded).expect("serialized event must deserialize");
        assert_eq!(restored.timestamp_ticks, event.timestamp_ticks);
        assert_eq!(restored.evidence, event.evidence);
        assert_eq!(
            serde_json::to_value(&restored).unwrap(),
            serde_json::to_value(&event).unwrap(),
            "roundtrip must preserve all retained evidence"
        );
    }
    // Every input also reaches exact-tick serialization, including u64::MAX.
    let mut bytes = [0_u8; 8];
    let len = data.len().min(bytes.len());
    bytes[..len].copy_from_slice(&data[..len]);
    let tick = u64::from_le_bytes(bytes);
    let mut value = serde_json::to_value(SystemEvent {
        timestamp_ticks: tick,
        evidence: AttributionEvidence {
            process_creation_time: Some(tick),
            ..Default::default()
        },
        ..Default::default()
    })
    .unwrap();
    assert_eq!(value["timestamp_ticks"], tick.to_string());
    assert_eq!(
        serde_json::from_value::<SystemEvent>(value.clone())
            .unwrap()
            .timestamp_ticks,
        tick
    );
    // Early numeric evidence remains readable without a lossy float conversion.
    value["timestamp_ticks"] = tick.into();
    value["evidence"]["process_creation_time"] = tick.into();
    let legacy: SystemEvent = serde_json::from_value(value).unwrap();
    assert_eq!(legacy.timestamp_ticks, tick);
    assert_eq!(legacy.evidence.process_creation_time, Some(tick));
}

#[derive(Clone)]
struct Interval {
    image: u8,
    end: Option<u64>,
    observed_from: Option<u64>,
    conflict: bool,
}

fn assert_resolution(
    cache: &LifetimeCache,
    intervals: &BTreeMap<(u32, u64), Interval>,
    pid: u32,
    at: u64,
) {
    // Independent interval oracle: membership, multiplicity, then contradiction.
    let candidates: Vec<_> = intervals
        .iter()
        .filter(|((owner, birth), interval)| {
            *owner == pid
                && *birth <= at
                && interval.end.is_none_or(|end| at <= end)
                && interval.observed_from.is_none_or(|from| from <= at)
        })
        .collect();
    let expected = if candidates.len() == 1 && !candidates[0].1.conflict {
        Some((candidates[0].0).1)
    } else {
        None
    };
    assert_eq!(
        cache.resolve(pid, at).and_then(|p| p.creation_time),
        expected
    );
    let mut event = SystemEvent {
        timestamp_ticks: at,
        evidence: AttributionEvidence {
            source: EvidenceSource::EtwRegistry,
            pid: Some(pid),
            ..Default::default()
        },
        ..Default::default()
    };
    cache.resolve_event(&mut event);
    assert_eq!(event.evidence.process_creation_time, expected);
}

pub fn lifetime_sequences(data: &[u8]) {
    let mut cache = LifetimeCache::default();
    let mut intervals: BTreeMap<(u32, u64), Interval> = BTreeMap::new();
    let mut stops: BTreeMap<(u32, u64), u64> = BTreeMap::new();
    // At most 256 operations / 256 intervals: this exercises identity policy,
    // not quota exhaustion. u16 ticks deliberately cause frequent PID reuse.
    for item in data.as_chunks::<8>().0.iter().take(256) {
        let op = item[0] % 4;
        let pid = u32::from(item[1] % 4) + 1;
        let birth = u64::from(u16::from_le_bytes([item[2], item[3]]));
        let at = u64::from(u16::from_le_bytes([item[4], item[5]]));
        let image = item[6] % 3;
        let key = (pid, birth);
        if op == 1 {
            cache.ingest(&SystemEvent {
                timestamp_ticks: at,
                operation: "process_stop".into(),
                evidence: AttributionEvidence {
                    source: EvidenceSource::EtwProcess,
                    pid: Some(pid),
                    process_creation_time: Some(birth),
                    ..Default::default()
                },
                ..Default::default()
            });
            if let Some(interval) = intervals.get_mut(&key) {
                interval.end = Some(interval.end.map_or(at, |old| old.min(at)));
            } else {
                stops
                    .entry(key)
                    .and_modify(|old| *old = (*old).min(at))
                    .or_insert(at);
            }
        } else if op == 0 || op == 2 {
            let process = ProcessRecord {
                pid,
                creation_time: Some(birth),
                image: format!("image-{image}"),
                ..Default::default()
            };
            if op == 2 {
                cache.seed_snapshot(process, at);
            } else {
                cache.ingest(&SystemEvent {
                    timestamp_ticks: at,
                    operation: "process_start".into(),
                    resource: process.image,
                    evidence: AttributionEvidence {
                        source: EvidenceSource::EtwProcess,
                        pid: Some(pid),
                        process_creation_time: Some(birth),
                        ..Default::default()
                    },
                    ..Default::default()
                });
            }
            if let Some(interval) = intervals.get_mut(&key) {
                if op == 0 {
                    interval.observed_from = None;
                    interval.conflict |= interval.image != image;
                }
            } else {
                intervals.insert(
                    key,
                    Interval {
                        image,
                        end: stops.remove(&key),
                        observed_from: (op == 2).then_some(at),
                        conflict: false,
                    },
                );
            }
        }
        for query in [at, birth.saturating_sub(1), birth, birth + 1] {
            assert_resolution(&cache, &intervals, pid, query);
        }
        assert_eq!(cache.dropped, 0);
    }
}

pub fn completion_correlation(data: &[u8]) {
    if data.is_empty() {
        return;
    }
    let operation = match data[0] % 3 {
        0 => "write_requested",
        1 => "delete_requested",
        _ => "rename_requested",
    };
    let request = SystemEvent {
        id: "request".into(),
        event_type: "file".into(),
        operation: operation.into(),
        ..Default::default()
    };
    let mut completions = Vec::new();
    for (i, byte) in data[1..].iter().take(32).enumerate() {
        completions.push(SystemEvent {
            id: format!("completion-{i}"),
            event_type: "completion".into(),
            success: match byte % 3 {
                0 => None,
                1 => Some(true),
                _ => Some(false),
            },
            raw: RawEvidence {
                related_event: Some(
                    if byte & 4 == 0 {
                        "request"
                    } else {
                        "unrelated"
                    }
                    .into(),
                ),
                status: Some(if byte % 3 == 2 { 0xc0000022 } else { 0 }),
                ..Default::default()
            },
            ..Default::default()
        });
    }
    let matching: Vec<_> = completions
        .iter()
        .filter(|event| event.raw.related_event.as_deref() == Some("request"))
        .collect();
    let expected = if matching.len() == 1 {
        matching[0].success
    } else {
        None
    };
    let mut expected_refs = vec!["request".to_owned()];
    expected_refs.extend(matching.iter().map(|event| event.id.clone()));
    expected_refs.sort();
    // Both request-first and completion-first orders must obey the same policy.
    for reverse in [false, true] {
        let mut events = vec![request.clone()];
        events.extend(completions.clone());
        if reverse {
            events.reverse();
        }
        let operations = correlate(&mut events, &Snapshot::default(), &Snapshot::default());
        assert_eq!(operations.len(), 1);
        let actual = &operations[0];
        assert_eq!(actual.success, expected);
        let mut refs = actual.raw_events.clone();
        refs.sort();
        assert_eq!(
            refs, expected_refs,
            "every matching raw record must survive"
        );
        if matching.len() > 1 {
            assert!(actual.reason.contains("Multiple completion records"));
        }
        if expected != Some(true) {
            assert_ne!(actual.operation, "Deleted");
        }
        if expected == Some(false) {
            assert_eq!(actual.operation, "Failed");
        }
        if matching.is_empty() && operation == "delete_requested" {
            assert_eq!(actual.operation, "DeleteRequested");
        }
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn reviewed_seed_corpora_replay_without_fuzzer() {
        assert!(
            serde_json::from_slice::<contain_core::model::SystemEvent>(include_bytes!(
                "../corpus/event_json/seed-large-ticks.json"
            ))
            .is_ok()
        );
        assert!(
            serde_json::from_slice::<contain_core::model::SystemEvent>(include_bytes!(
                "../corpus/event_json/seed-invalid-negative.json"
            ))
            .is_err()
        );
        super::event_json(include_bytes!("../corpus/event_json/seed-large-ticks.json"));
        super::event_json(include_bytes!(
            "../corpus/event_json/seed-invalid-negative.json"
        ));
        super::lifetime_sequences(include_bytes!(
            "../corpus/lifetime_sequences/seed-pid-reuse"
        ));
        super::lifetime_sequences(include_bytes!(
            "../corpus/lifetime_sequences/seed-stop-before-start"
        ));
        super::completion_correlation(include_bytes!(
            "../corpus/completion_correlation/seed-conflicting"
        ));
        super::completion_correlation(include_bytes!(
            "../corpus/completion_correlation/seed-duplicate-success"
        ));
        // Deterministic mutation smoke is not a coverage-guided fuzz campaign.
        for seed in 0_u8..=255 {
            let data: Vec<_> = (0_u8..=255).map(|n| seed.wrapping_add(n)).collect();
            super::event_json(&data);
            super::lifetime_sequences(&data);
            super::completion_correlation(&data);
        }
    }
}
