//! FILETIME exceeds JavaScript's exact integer range. JSON uses decimal strings.
use serde::{Deserialize, Deserializer, Serializer};

#[derive(Deserialize)]
#[serde(untagged)]
enum Input {
    Text(String),
    Number(u64),
}

impl Input {
    fn number<E: serde::de::Error>(self) -> Result<u64, E> {
        match self {
            Self::Text(text) => text.parse().map_err(E::custom),
            Self::Number(n) => Ok(n),
        }
    }
}

pub fn serialize<S: Serializer>(ticks: &u64, serializer: S) -> Result<S::Ok, S::Error> {
    serializer.serialize_str(&ticks.to_string())
}

pub fn deserialize<'de, D: Deserializer<'de>>(deserializer: D) -> Result<u64, D::Error> {
    Input::deserialize(deserializer)?.number()
}

pub mod optional {
    use super::*;
    pub fn serialize<S: Serializer>(ticks: &Option<u64>, serializer: S) -> Result<S::Ok, S::Error> {
        match ticks {
            Some(n) => serializer.serialize_some(&n.to_string()),
            None => serializer.serialize_none(),
        }
    }
    pub fn deserialize<'de, D: Deserializer<'de>>(
        deserializer: D,
    ) -> Result<Option<u64>, D::Error> {
        Option::<Input>::deserialize(deserializer)?
            .map(Input::number)
            .transpose()
    }
}

#[cfg(test)]
mod tests {
    use crate::model::{
        AttributionEvidence, CaptureStats, DrainDiagnostics, DrainProcess, SystemEvent,
    };
    #[test]
    fn drain_diagnostics_are_optional_in_history_and_keep_exact_process_identity() {
        let old: CaptureStats = serde_json::from_str("{\"drain_timed_out\":true}").unwrap();
        assert!(old.drain_timed_out);
        assert!(old.drain.is_none());
        let ticks = 134_385_075_882_875_960;
        let stats = CaptureStats {
            drain_timed_out: true,
            drain: Some(DrainDiagnostics {
                stop_reason: "deadline_pending_process_exit".into(),
                pending_verified_instances: 1,
                pending_examples: vec![DrainProcess {
                    pid: 4032,
                    creation_time: ticks,
                }],
                ..Default::default()
            }),
            ..Default::default()
        };
        let json = serde_json::to_value(stats).unwrap();
        assert_eq!(
            json["drain"]["pending_examples"][0]["creation_time"],
            ticks.to_string()
        );
        let restored: CaptureStats = serde_json::from_value(json).unwrap();
        assert_eq!(
            restored.drain.unwrap().pending_examples[0].creation_time,
            ticks
        );
    }
    #[test]
    fn filetime_json_preserves_precision_and_reads_early_numeric_evidence() {
        let ticks = 134_350_000_000_000_017;
        let event = SystemEvent {
            timestamp_ticks: ticks,
            evidence: AttributionEvidence {
                process_creation_time: Some(ticks - 1),
                ..Default::default()
            },
            ..Default::default()
        };
        let mut value = serde_json::to_value(event).unwrap();
        assert_eq!(value["timestamp_ticks"], ticks.to_string());
        assert_eq!(
            value["evidence"]["process_creation_time"],
            (ticks - 1).to_string()
        );
        let restored: SystemEvent = serde_json::from_value(value.clone()).unwrap();
        assert_eq!(restored.timestamp_ticks, ticks);
        value["evidence"]["process_creation_time"] = serde_json::json!(ticks - 1);
        assert_eq!(
            serde_json::from_value::<SystemEvent>(value)
                .unwrap()
                .evidence
                .process_creation_time,
            Some(ticks - 1)
        );
    }
}
