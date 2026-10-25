//! Opt-in diagnostic copies. These must never select a path, actor or operation.
use super::*;
use crate::model::{
    ClockRecordIdentity, FileCacheOrigin, FileDiagnosticField, FileDiagnosticReport,
    FileDiagnosticSample, FileDiagnosticSchema, FileDiagnosticSchemaField, FileDiagnostics,
};
use ferrisetw::parser::Parser;
use ferrisetw::{parser::ParserError, schema::Schema};
use std::collections::BTreeMap;
use std::fmt::Write;

const FIELD_BYTES: usize = 1024;
const PAYLOAD_BYTES: usize = 256;
const SCHEMA_FIELDS: usize = 24;
const SCHEMA_NAME_BYTES: usize = 64;
const RECORD_BYTES: usize = 16 * 1024;
const TOTAL_BYTES: usize = 8 * 1024 * 1024;
const CONTEXT_SAMPLES: usize = 128;
const CONTEXT_PER_REASON: usize = 16;
const CONTEXT_BYTES: usize = 512 * 1024;

#[derive(Default)]
pub(super) struct Context {
    pub path_source: &'static str,
    pub cache_name: Option<String>,
    pub cache_origin: Option<FileCacheOrigin>,
    pub cache_created: bool,
    pub cache_invalidated_reason: Option<&'static str>,
    pub invalidated_cache_origin: Option<FileCacheOrigin>,
    pub pending_irp: Option<crate::model::FilePendingDiagnostic>,
}

impl Context {
    pub fn selected_path(
        &mut self,
        supplied_source: Option<&'static str>,
        cached: Option<&str>,
        origin: Option<FileCacheOrigin>,
    ) {
        self.path_source = supplied_source.unwrap_or(if cached.is_some() {
            "file_object_cache"
        } else {
            "unresolved"
        });
        self.cache_name = cached.map(str::to_owned);
        self.cache_origin = origin;
    }

    pub fn pending_request(
        &mut self,
        prior: Option<&(String, u64)>,
        current: &str,
        capacity_available: bool,
    ) {
        self.pending_irp = Some(crate::model::FilePendingDiagnostic {
            action: if prior.is_some() {
                "collision_removed"
            } else if capacity_available {
                "inserted"
            } else {
                "capacity_rejected"
            }
            .into(),
            prior_request_id: prior.map(|(request, _)| request.clone()),
            prior_request_timestamp: prior.map(|(_, time)| time.to_string()),
            current_request_id: Some(current.into()),
        });
    }
}

pub(super) struct Diagnostics {
    pub report: FileDiagnosticReport,
    origins: HashMap<u64, FileCacheOrigin>,
    sampled: BTreeMap<&'static str, usize>,
}

impl Diagnostics {
    pub fn configured() -> Option<Self> {
        (std::env::var_os("CONTAIN_FILE_DIAGNOSTICS").as_deref() == Some(std::ffi::OsStr::new("1")))
            .then(Self::new)
    }

    fn new() -> Self {
        Self {
            report: FileDiagnosticReport {
                schema_version: 1,
                enabled: true,
                record_byte_limit: RECORD_BYTES,
                total_byte_limit: TOTAL_BYTES,
                cache_entry_limit: OBJECT_CAPACITY,
                context_sample_limit: CONTEXT_SAMPLES,
                context_samples_per_reason: CONTEXT_PER_REASON,
                context_byte_limit: CONTEXT_BYTES,
                ..Default::default()
            },
            origins: HashMap::new(),
            sampled: BTreeMap::new(),
        }
    }

    pub fn count(&mut self, reason: &'static str) {
        *self.report.counts.entry(reason.into()).or_default() += 1;
    }

    pub fn origin(&self, object: u64) -> Option<FileCacheOrigin> {
        self.origins.get(&object).cloned()
    }

    pub fn invalidate(&mut self, object: u64) {
        self.origins.remove(&object);
    }

    pub fn clear(&mut self) {
        self.origins.clear();
        self.count("clock_context_clear");
    }

    pub fn establish(&mut self, object: u64, event: &SystemEvent) {
        let Some(generation) = event.raw.object_generation.as_ref() else {
            return;
        };
        let Some(event_id) = event.raw.event_id else {
            self.count("cache_origin_event_id_missing");
            return;
        };
        if self.origins.len() >= OBJECT_CAPACITY {
            self.count("cache_metadata_budget");
            return;
        }
        self.origins.insert(
            object,
            FileCacheOrigin {
                event_key: event
                    .raw
                    .clock
                    .as_ref()
                    .and_then(|clock| clock.event_key.clone()),
                event_id,
                event_timestamp: event.raw.clock.as_ref().map_or_else(
                    || event.timestamp_ticks.to_string(),
                    |clock| clock.header_timestamp.clone(),
                ),
                source_event_id: Some(event.id.clone()),
                object_generation: generation.clone(),
                file_key: event.raw.file_key.clone(),
            },
        );
        self.report.cache_entries_peak = self.report.cache_entries_peak.max(self.origins.len());
    }

    pub fn admit(
        &mut self,
        record: &EventRecord,
        schema: &Schema,
        parser: &Parser<'_, '_>,
        context: Context,
    ) -> Option<Box<FileDiagnostics>> {
        self.report.admitted_records += 1;
        if self.report.attached_bytes as usize >= TOTAL_BYTES {
            self.report.budget_dropped += 1;
            return None;
        }
        let diagnostics = build(record, Some(schema), Some(parser), context);
        self.retain(diagnostics)
    }

    fn retain(&mut self, diagnostics: FileDiagnostics) -> Option<Box<FileDiagnostics>> {
        self.account(&diagnostics);
        let bytes = serde_json::to_vec(&diagnostics)
            .expect("diagnostic fields serialize")
            .len();
        if bytes > RECORD_BYTES {
            self.report.oversized_records += 1;
            return None;
        }
        if bytes > TOTAL_BYTES.saturating_sub(self.report.attached_bytes as usize) {
            self.report.budget_dropped += 1;
            return None;
        }
        self.report.attached_bytes += bytes as u64;
        self.report.attached_records += 1;
        Some(Box::new(diagnostics))
    }

    pub fn ignored(
        &mut self,
        reason: &'static str,
        record: &EventRecord,
        schema: Option<&Schema>,
        parser: Option<&Parser<'_, '_>>,
        context: Context,
    ) {
        self.count(reason);
        if !self.sample_available(reason) {
            self.report.context_samples_omitted += 1;
            return;
        }
        let diagnostics = build(record, schema, parser, context);
        self.account(&diagnostics);
        let sample = FileDiagnosticSample {
            reason: reason.into(),
            clock: super::super::clock::record(record),
            diagnostics,
        };
        self.retain_sample(reason, sample);
    }

    fn sample_available(&self, reason: &'static str) -> bool {
        self.report.context_samples.len() < CONTEXT_SAMPLES
            && self.sampled.get(reason).copied().unwrap_or(0) < CONTEXT_PER_REASON
            && (self.report.context_bytes as usize) < CONTEXT_BYTES
    }

    fn retain_sample(&mut self, reason: &'static str, sample: FileDiagnosticSample) {
        let bytes = serde_json::to_vec(&sample)
            .expect("diagnostic fields serialize")
            .len();
        if bytes > RECORD_BYTES
            || bytes > CONTEXT_BYTES.saturating_sub(self.report.context_bytes as usize)
        {
            self.report.context_samples_omitted += 1;
            return;
        }
        self.report.context_bytes += bytes as u64;
        self.report.context_samples.push(sample);
        *self.sampled.entry(reason).or_default() += 1;
    }

    fn account(&mut self, diagnostic: &FileDiagnostics) {
        // This denominator is parsed diagnostic copies, not all ETW records.
        for field in diagnostic.fields.values() {
            match field.status.as_str() {
                "parsed" => self.report.parsed_fields += 1,
                "not_found" => self.report.missing_fields += 1,
                _ => self.report.parse_errors += 1,
            }
        }
        self.report.truncated_records += u64::from(
            diagnostic.payload_truncated
                || !diagnostic.truncated_fields.is_empty()
                || diagnostic
                    .schema
                    .as_ref()
                    .is_some_and(|schema| schema.truncated),
        );
    }
}

fn bounded(name: &str, mut value: String, truncated: &mut Vec<String>) -> String {
    if value.len() > FIELD_BYTES {
        let mut end = FIELD_BYTES;
        while !value.is_char_boundary(end) {
            end -= 1;
        }
        value.truncate(end);
        truncated.push(name.into());
    }
    value
}

fn parsed<T: ToString>(result: Result<T, ParserError>) -> FileDiagnosticField {
    match result {
        Ok(value) => FileDiagnosticField {
            status: "parsed".into(),
            value: Some(value.to_string()),
        },
        Err(error) => FileDiagnosticField {
            status: match error {
                ParserError::NotFound => "not_found",
                ParserError::InvalidType => "invalid_type",
                ParserError::ParseError => "parse_error",
                ParserError::LengthMismatch => "length_mismatch",
                ParserError::PropertyError(_) => "property_error",
                ParserError::Utf8Error(_) => "utf8_error",
                ParserError::SliceError(_) => "slice_error",
                ParserError::SddlNativeError(_) => "sddl_error",
                ParserError::TdhNativeError(_) => "tdh_error",
            }
            .into(),
            value: None,
        },
    }
}

fn build(
    record: &EventRecord,
    schema: Option<&Schema>,
    parser: Option<&Parser<'_, '_>>,
    context: Context,
) -> FileDiagnostics {
    let mut fields = BTreeMap::new();
    let mut truncated_fields = Vec::new();
    let schema = schema.map(|schema| {
        let actual = schema.diagnostic_properties(SCHEMA_FIELDS, SCHEMA_NAME_BYTES);
        FileDiagnosticSchema {
            property_count: actual.property_count,
            top_level_property_count: actual.top_level_property_count,
            fields: actual
                .fields
                .into_iter()
                .map(|field| FileDiagnosticSchemaField {
                    name: field.name,
                    flags: field.flags,
                    in_type: field.in_type,
                    out_type: field.out_type,
                    length_or_index: field.length_or_index,
                    count_or_index: field.count_or_index,
                })
                .collect(),
            truncated: actual.truncated,
            invalid_layout: actual.invalid_layout,
        }
    });
    if let Some(parser) = parser {
        // Integer TryParse checks size, not the original TDH type. Only parse
        // scalar types supported for this diagnostic field; never label a
        // same-size float or structured value as a parsed integer.
        for name in ["FileName", "FilePath"] {
            let mut field = typed(schema.as_ref(), name, &[1, 2, 300], || {
                parsed(parser.try_parse::<String>(name))
            });
            field.value = field
                .value
                .map(|value| bounded(name, value, &mut truncated_fields));
            fields.insert(name.into(), field);
        }
        for name in ["FileObject", "FileKey", "Irp", "ByteOffset"] {
            let allowed: &[u16] = if name == "ByteOffset" {
                &[10, 21]
            } else {
                &[10, 16, 21]
            };
            fields.insert(
                name.into(),
                typed(schema.as_ref(), name, allowed, || {
                    parsed(parser.try_parse::<u64>(name))
                }),
            );
        }
        fields.insert(
            "ThreadId".into(),
            thread_id_field(
                schema.as_ref(),
                record.event_flags(),
                || parsed(parser.try_parse::<u32>("ThreadId")),
                || parsed(parser.try_parse::<u64>("ThreadId")),
            ),
        );
        for name in [
            "IssuingThreadId",
            "Status",
            "IOSize",
            "IOFlags",
            "ExtraFlags",
        ] {
            fields.insert(
                name.into(),
                typed(schema.as_ref(), name, &[8, 20], || {
                    parsed(parser.try_parse::<u32>(name))
                }),
            );
        }
    }
    let payload_prefix_hex = record.user_payload_prefix(PAYLOAD_BYTES).map(|bytes| {
        let mut hex = String::with_capacity(bytes.len() * 2);
        for byte in bytes {
            write!(hex, "{byte:02x}").expect("writing to String");
        }
        hex
    });
    FileDiagnostics {
        schema_version: 1,
        descriptor: ClockRecordIdentity {
            provider: uuid::Uuid::from_u128(record.provider_id().to_u128()).to_string(),
            event_id: record.event_id(),
            version: record.version(),
            channel: record.channel(),
            level: record.level(),
            opcode: record.opcode(),
            task: record.task(),
            keyword: format!("{:016x}", record.keyword()),
            process_id: record.process_id(),
            thread_id: record.thread_id(),
        },
        header_flags: record.event_flags(),
        payload_bytes: record.user_payload_len(),
        payload_prefix_hex,
        payload_truncated: record.user_payload_len() > PAYLOAD_BYTES,
        schema,
        fields,
        path_source: context.path_source.into(),
        cache_name: context
            .cache_name
            .map(|value| bounded("cache_name", value, &mut truncated_fields)),
        cache_origin: context.cache_origin,
        cache_created: context.cache_created,
        cache_invalidated_reason: context.cache_invalidated_reason.map(str::to_owned),
        invalidated_cache_origin: context.invalidated_cache_origin,
        pending_irp: context.pending_irp,
        current_path_verified: false,
        truncated_fields,
    }
}

fn thread_id_field(
    schema: Option<&FileDiagnosticSchema>,
    header_flags: u16,
    parse32: impl FnOnce() -> FileDiagnosticField,
    parse64: impl FnOnce() -> FileDiagnosticField,
) -> FileDiagnosticField {
    let field_type = schema
        .and_then(|schema| schema.fields.iter().find(|field| field.name == "ThreadId"))
        .and_then(|field| field.in_type);
    typed(
        schema,
        "ThreadId",
        &[8, 10, 16, 20, 21],
        || match field_type {
            Some(8 | 20) => parse32(),
            Some(16)
                if header_flags
                    & windows_sys::Win32::System::Diagnostics::Etw::EVENT_HEADER_FLAG_32_BIT_HEADER
                        as u16
                    != 0 =>
            {
                parse32()
            }
            _ => parse64(),
        },
    )
}

fn typed(
    schema: Option<&FileDiagnosticSchema>,
    name: &str,
    allowed: &[u16],
    parse: impl FnOnce() -> FileDiagnosticField,
) -> FileDiagnosticField {
    let Some(schema) = schema else {
        return FileDiagnosticField {
            status: "schema_unavailable".into(),
            value: None,
        };
    };
    if schema.invalid_layout {
        return FileDiagnosticField {
            status: "schema_invalid_layout".into(),
            value: None,
        };
    }
    let Some(field) = schema.fields.iter().find(|field| field.name == name) else {
        return FileDiagnosticField {
            status: if schema.truncated || schema.invalid_layout {
                "schema_incomplete"
            } else {
                "not_found"
            }
            .into(),
            value: None,
        };
    };
    if field.flags & (0x1 | 0x4 | 0x80) != 0
        || field.count_or_index > 1
        || !field.in_type.is_some_and(|kind| allowed.contains(&kind))
    {
        return FileDiagnosticField {
            status: "schema_type_mismatch".into(),
            value: None,
        };
    }
    parse()
}

#[cfg(test)]
mod tests {
    use super::*;
    fn diagnostic() -> FileDiagnostics {
        FileDiagnostics {
            schema_version: 1,
            descriptor: ClockRecordIdentity {
                provider: FILE_PROVIDER.into(),
                event_id: 16,
                version: 1,
                channel: 0,
                level: 4,
                opcode: 0,
                task: 16,
                keyword: "0000000000000000".into(),
                process_id: 4,
                thread_id: 7,
            },
            header_flags: 0,
            payload_bytes: 40,
            payload_prefix_hex: Some("00010203".into()),
            payload_truncated: false,
            schema: None,
            fields: BTreeMap::new(),
            path_source: "file_object_cache".into(),
            cache_name: Some("C:\\fixture\\old.tmp".into()),
            cache_origin: None,
            cache_created: false,
            cache_invalidated_reason: None,
            invalidated_cache_origin: None,
            pending_irp: None,
            current_path_verified: false,
            truncated_fields: Vec::new(),
        }
    }

    #[test]
    fn bounded_fields_keep_utf8_and_missing_values_are_not_zero() {
        let mut truncated = Vec::new();
        let value = bounded("FileName", "文".repeat(FIELD_BYTES), &mut truncated);
        assert!(value.len() <= FIELD_BYTES);
        assert_eq!(truncated, ["FileName"]);
        assert_eq!(parsed(Ok(0_u64)).value.as_deref(), Some("0"));
        let missing = parsed::<u64>(Err(ParserError::NotFound));
        assert_eq!(missing.status, "not_found");
        assert!(missing.value.is_none());
    }

    #[test]
    fn cache_origins_do_not_backfill_other_objects_and_are_invalidated() {
        let mut diagnostics = Diagnostics::new();
        let event = SystemEvent {
            id: "original".into(),
            timestamp_ticks: 134_350_000_000_000_017,
            raw: crate::model::RawEvidence {
                event_id: Some(12),
                object_generation: Some("g1".into()),
                file_key: Some("same-key".into()),
                ..Default::default()
            },
            ..Default::default()
        };
        diagnostics.establish(1, &event);
        assert_eq!(
            diagnostics.origin(1).unwrap().source_event_id.as_deref(),
            Some("original")
        );
        assert_eq!(
            diagnostics.origin(1).unwrap().event_timestamp,
            "134350000000000017"
        );
        assert!(diagnostics.origin(2).is_none());
        diagnostics.invalidate(1);
        assert!(diagnostics.origin(1).is_none());
        diagnostics.establish(2, &event);
        diagnostics.clear();
        assert!(diagnostics.origin(2).is_none());
    }

    #[test]
    fn selected_cache_facts_follow_replacement_and_missing_open_name() {
        let previous = FileCacheOrigin {
            object_generation: "old-generation".into(),
            ..Default::default()
        };
        let current = FileCacheOrigin {
            object_generation: "new-generation".into(),
            ..Default::default()
        };
        let mut context = Context {
            cache_name: Some("old-name".into()),
            cache_origin: Some(previous.clone()),
            invalidated_cache_origin: Some(previous),
            ..Default::default()
        };
        context.selected_path(Some("supplied_FileName"), Some("new-name"), Some(current));
        assert_eq!(context.path_source, "supplied_FileName");
        assert_eq!(context.cache_name.as_deref(), Some("new-name"));
        assert_eq!(
            context.cache_origin.as_ref().unwrap().object_generation,
            "new-generation"
        );
        assert_eq!(
            context
                .invalidated_cache_origin
                .as_ref()
                .unwrap()
                .object_generation,
            "old-generation"
        );
        // Existing open handling invalidates the prior object before fallback.
        context.selected_path(None, None, None);
        assert_eq!(context.path_source, "unresolved");
        assert!(context.cache_name.is_none());
        assert!(context.cache_origin.is_none());
        assert!(context.invalidated_cache_origin.is_some());
    }

    #[test]
    fn pending_context_preserves_the_displaced_request_and_exact_time() {
        let mut context = Context::default();
        let prior = ("previous-request".into(), 134_350_000_000_000_017);
        context.pending_request(Some(&prior), "current-request", true);
        let pending = context.pending_irp.as_ref().unwrap();
        assert_eq!(pending.action, "collision_removed");
        assert_eq!(
            pending.prior_request_id.as_deref(),
            Some("previous-request")
        );
        assert_eq!(
            pending.prior_request_timestamp.as_deref(),
            Some("134350000000000017")
        );
        assert_eq!(
            pending.current_request_id.as_deref(),
            Some("current-request")
        );
        assert_eq!(prior.0, "previous-request");
        context.pending_request(None, "current-request", false);
        assert_eq!(
            context.pending_irp.as_ref().unwrap().action,
            "capacity_rejected"
        );
        assert!(
            context
                .pending_irp
                .as_ref()
                .unwrap()
                .prior_request_id
                .is_none()
        );
        context.pending_request(None, "current-request", true);
        assert_eq!(context.pending_irp.as_ref().unwrap().action, "inserted");
    }

    #[test]
    fn thread_id_diagnostic_uses_schema_width_and_rejects_unsupported_types() {
        for (in_type, flags, expected) in [
            (8, 0, "32"),
            (20, 0, "32"),
            (10, 0, "64"),
            (21, 0, "64"),
            (16, 0x20, "32"),
            (16, 0x40, "64"),
        ] {
            let schema = FileDiagnosticSchema {
                fields: vec![FileDiagnosticSchemaField {
                    name: "ThreadId".into(),
                    in_type: Some(in_type),
                    count_or_index: 1,
                    ..Default::default()
                }],
                ..Default::default()
            };
            let value = thread_id_field(
                Some(&schema),
                flags,
                || parsed(Ok(32_u32)),
                || parsed(Ok(64_u64)),
            );
            assert_eq!(
                value.status, "parsed",
                "TDH type {in_type}, header flags {flags}"
            );
            assert_eq!(value.value.as_deref(), Some(expected));
            assert_eq!(schema.fields[0].in_type, Some(in_type));
        }
        let mut schema = FileDiagnosticSchema {
            fields: vec![FileDiagnosticSchemaField {
                name: "ThreadId".into(),
                in_type: Some(11),
                count_or_index: 1,
                ..Default::default()
            }],
            ..Default::default()
        };
        let unsupported = thread_id_field(
            Some(&schema),
            0,
            || panic!("float must not be read as u32"),
            || panic!("float must not be read as u64"),
        );
        assert_eq!(unsupported.status, "schema_type_mismatch");
        assert!(unsupported.value.is_none());
        schema.fields.clear();
        let missing = thread_id_field(
            Some(&schema),
            0,
            || panic!("missing field must not be read"),
            || panic!("missing field must not be read"),
        );
        assert_eq!(missing.status, "not_found");
        assert!(missing.value.is_none());
    }

    #[test]
    fn schema_type_checks_reject_same_width_values_and_missing_is_separate() {
        let mut schema = FileDiagnosticSchema {
            fields: vec![FileDiagnosticSchemaField {
                name: "IOSize".into(),
                in_type: Some(11),
                count_or_index: 1,
                ..Default::default()
            }],
            ..Default::default()
        };
        let wrong = typed(Some(&schema), "IOSize", &[8, 20], || {
            panic!("same-width float must not be parsed as integer")
        });
        assert_eq!(wrong.status, "schema_type_mismatch");
        schema.fields[0].in_type = Some(8);
        let correct = typed(Some(&schema), "IOSize", &[8, 20], || parsed(Ok(0_u32)));
        assert_eq!(correct.value.as_deref(), Some("0"));
        schema.fields[0].flags = 0x4;
        let array = typed(Some(&schema), "IOSize", &[8], || {
            panic!("parameter-count array is not a scalar")
        });
        assert_eq!(array.status, "schema_type_mismatch");
        let missing = typed(Some(&schema), "FileName", &[1], || {
            panic!("missing field must not be parsed")
        });
        assert_eq!(missing.status, "not_found");
        let mut sample = diagnostic();
        sample.fields = BTreeMap::from([
            ("IOSize".into(), correct),
            ("Wrong".into(), wrong),
            ("FileName".into(), missing),
        ]);
        let mut diagnostics = Diagnostics::new();
        diagnostics.account(&sample);
        assert_eq!(diagnostics.report.parsed_fields, 1);
        assert_eq!(diagnostics.report.missing_fields, 1);
        assert_eq!(diagnostics.report.parse_errors, 1);
    }

    #[test]
    fn diagnostic_budget_and_first_n_samples_remain_explicit_and_bounded() {
        let mut diagnostics = Diagnostics::new();
        let sample = diagnostic();
        let bytes = serde_json::to_vec(&sample).unwrap().len();
        diagnostics.report.attached_bytes = (TOTAL_BYTES - bytes) as u64;
        assert!(diagnostics.retain(sample.clone()).is_some());
        assert_eq!(diagnostics.report.attached_bytes, TOTAL_BYTES as u64);
        assert!(diagnostics.retain(sample.clone()).is_none());
        assert_eq!(diagnostics.report.budget_dropped, 1);
        let mut oversized = sample.clone();
        oversized.payload_prefix_hex = Some("f".repeat(RECORD_BYTES));
        assert!(diagnostics.retain(oversized).is_none());
        assert_eq!(diagnostics.report.oversized_records, 1);
        for _ in 0..CONTEXT_PER_REASON {
            assert!(diagnostics.sample_available("out_of_scope"));
            diagnostics.retain_sample(
                "out_of_scope",
                FileDiagnosticSample {
                    reason: "out_of_scope".into(),
                    clock: super::super::super::clock::record_header(17),
                    diagnostics: sample.clone(),
                },
            );
        }
        assert!(!diagnostics.sample_available("out_of_scope"));
        assert!(diagnostics.sample_available("schema_failure"));
        assert!(diagnostics.report.context_bytes <= CONTEXT_BYTES as u64);
        diagnostics.report.context_bytes = CONTEXT_BYTES as u64;
        assert!(!diagnostics.sample_available("schema_failure"));
        // Exhausting diagnostic budgets does not affect the independent source
        // loss decision. Any queue/storage loss remains in its original fields.
        let backend = BackendReport {
            file_diagnostics: Some(diagnostics.report),
            etw_events_lost: Some(0),
            etw_buffers_lost: Some(0),
            ..Default::default()
        };
        assert!(backend.source_intact());
    }

    #[test]
    fn old_json_and_reopened_database_keep_diagnostics_without_promoting_evidence() {
        let old = serde_json::to_value(crate::model::RawEvidence::default()).unwrap();
        assert!(old.get("file_diagnostics").is_none());
        let read: crate::model::RawEvidence = serde_json::from_value(old).unwrap();
        assert!(read.file_diagnostics.is_none());
        let mut sample = diagnostic();
        sample.pending_irp = Some(crate::model::FilePendingDiagnostic {
            action: "collision_removed".into(),
            prior_request_id: Some("prior".into()),
            prior_request_timestamp: Some("134350000000000017".into()),
            current_request_id: Some("current".into()),
        });
        let event = SystemEvent {
            id: "diagnostic-event".into(),
            event_type: "file".into(),
            operation: "write_requested".into(),
            raw: crate::model::RawEvidence {
                file_diagnostics: Some(Box::new(sample)),
                ..Default::default()
            },
            ..Default::default()
        };
        let capture = crate::model::Capture {
            id: "diagnostic-capture".into(),
            name: "diagnostic".into(),
            schema_version: 3,
            events: vec![event],
            backend: BackendReport {
                file_diagnostics: Some(Diagnostics::new().report),
                ..Default::default()
            },
            ..Default::default()
        };
        let directory = std::env::temp_dir().join(format!(
            "contain-file-diagnostic-test-{}",
            uuid::Uuid::new_v4()
        ));
        let path = directory.join("evidence.db");
        {
            let mut db = crate::storage::Storage::open(&path).unwrap();
            db.save(&capture).unwrap();
        }
        {
            let db = crate::storage::Storage::open(&path).unwrap();
            let full = db.load(&capture.id).unwrap();
            let page = db.events_page(&capture.id, 0, 1).unwrap();
            let explanation = db.for_event("diagnostic-event").unwrap();
            let expected = serde_json::to_value(&capture.events[0].raw.file_diagnostics).unwrap();
            for event in [&full.events[0], &page[0], &explanation.events[0]] {
                assert_eq!(
                    serde_json::to_value(&event.raw.file_diagnostics).unwrap(),
                    expected
                );
                assert_eq!(event.confidence, crate::model::Confidence::Unknown);
                assert!(!event.raw.resource_resolved);
                assert!(!event.state_validated);
            }
            assert!(
                db.load_summary(&capture.id)
                    .unwrap()
                    .backend
                    .file_diagnostics
                    .unwrap()
                    .enabled
            );
        }
        std::fs::remove_dir_all(directory).unwrap();
    }
}
