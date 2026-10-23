use super::*;
use crate::model::RawEvidence;
use ferrisetw::parser::{Parser, ParserError};

const REGISTRY_DIAGNOSTIC_FIELD_BYTES: usize = 4096;

fn diagnostic_parse<T>(
    name: &str,
    result: Result<T, ParserError>,
    diagnostics: &mut crate::model::RegistryFieldDiagnostics,
) -> Option<T> {
    match result {
        Ok(value) => Some(value),
        Err(error) => {
            let label = match error {
                ParserError::NotFound => "not_found",
                ParserError::InvalidType => "invalid_type",
                ParserError::ParseError => "parse_error",
                ParserError::LengthMismatch => "length_mismatch",
                ParserError::PropertyError(_) => "property_error",
                ParserError::Utf8Error(_) => "utf8_error",
                ParserError::SliceError(_) => "slice_error",
                ParserError::SddlNativeError(_) => "sddl_error",
                ParserError::TdhNativeError(_) => "tdh_error",
            };
            diagnostics.parse_errors.insert(name.into(), label.into());
            None
        }
    }
}

fn bounded_registry_field(
    name: &str,
    mut value: String,
    diagnostics: &mut crate::model::RegistryFieldDiagnostics,
) -> String {
    if value.len() > REGISTRY_DIAGNOSTIC_FIELD_BYTES {
        let mut end = REGISTRY_DIAGNOSTIC_FIELD_BYTES;
        while !value.is_char_boundary(end) {
            end -= 1;
        }
        value.truncate(end);
        diagnostics.truncated_fields.push(name.into());
    }
    value
}

// Called only after the existing scope/open-close filters. Diagnostic parsing
// cannot turn an unresolved resource into a resolved one or change admission.
fn retained_registry_fields(
    metrics: &Metrics,
    parser: &Parser<'_, '_>,
    version: u8,
    key_name: Result<String, ParserError>,
    context_owner_available: bool,
    gap_reason: Option<&str>,
) -> Box<crate::model::RegistryFields> {
    let mut diagnostics = crate::model::RegistryFieldDiagnostics {
        context_owner_available,
        path_gap_reason: gap_reason.map(str::to_owned),
        ..Default::default()
    };
    let base_object = diagnostic_parse(
        "BaseObject",
        measured!(metrics, Properties, parser.try_parse::<u64>("BaseObject")),
        &mut diagnostics,
    )
    .map(|object| format!("{object:016x}"));
    let mut text = |name, result| {
        diagnostic_parse(name, result, &mut diagnostics)
            .map(|value| bounded_registry_field(name, value, &mut diagnostics))
    };
    let key_name = text("KeyName", key_name).unwrap_or_default();
    let base_name = text(
        "BaseName",
        measured!(metrics, Properties, parser.try_parse::<String>("BaseName")),
    )
    .unwrap_or_default();
    let relative_name = text(
        "RelativeName",
        measured!(
            metrics,
            Properties,
            parser.try_parse::<String>("RelativeName")
        ),
    )
    .unwrap_or_default();
    let value_name = text(
        "ValueName",
        measured!(metrics, Properties, parser.try_parse::<String>("ValueName")),
    );
    Box::new(crate::model::RegistryFields {
        provider: REGISTRY_PROVIDER.into(),
        version,
        base_object,
        key_name,
        base_name,
        relative_name,
        value_name,
        diagnostics: Some(diagnostics),
    })
}

fn pointer(metrics: &Metrics, parser: &Parser<'_, '_>, name: &str) -> Option<String> {
    measured!(metrics, Properties, parser.try_parse::<u64>(name))
        .ok()
        .map(|p| format!("{p:016x}"))
}
pub fn status_success(status: u32) -> Option<bool> {
    if matches!(status, 0x103..=0x105) {
        None
    } else {
        Some((status as i32) >= 0)
    }
}

impl Decoder {
    fn admit_file_time(&mut self, time: u64) -> bool {
        if time < self.file_watermark {
            // FileObject and IRP numbers can be reused. Applying a delayed record
            // to the current generation can invent a resource or completion.
            // Do not rewind context or infer a mapping through this gap.
            self.paths.clear();
            self.pending.clear();
            self.file_time_regressions += 1;
            self.errors.fetch_add(1, Ordering::Relaxed);
            return false;
        }
        self.file_watermark = time;
        true
    }

    pub(super) fn send(&self, mut event: SystemEvent) {
        event.sequence = self.received.fetch_add(1, Ordering::Relaxed) + 1;
        let _timer = self.metrics.timer(Time::Enqueue);
        let pending = self.metrics.entering();
        match self.tx.try_send((event, Instant::now())) {
            Ok(()) => self.metrics.admitted(pending),
            Err(error) => {
                self.metrics.leaving();
                self.metrics.add(
                    match error {
                        mpsc::TrySendError::Full(_) => Count::Overflow,
                        mpsc::TrySendError::Disconnected(_) => Count::Disconnected,
                    },
                    1,
                );
                self.dropped.fetch_add(1, Ordering::Relaxed);
            }
        }
    }
    pub(super) fn lifecycle(&mut self, record: &EventRecord, locator: &SchemaLocator) {
        let id = record.event_id();
        if !matches!(id, 1..=4) {
            return;
        }
        self.metrics.add(Count::Attempted, 1);
        let Ok(schema) = measured!(self.metrics, Schema, locator.event_schema(record)) else {
            self.errors.fetch_add(1, Ordering::Relaxed);
            return;
        };
        let p = Parser::create(record, &schema);
        let Ok(pid) = measured!(self.metrics, Properties, p.try_parse::<u32>("ProcessID")) else {
            self.errors.fetch_add(1, Ordering::Relaxed);
            return;
        };
        let time = record.raw_timestamp().max(0) as u64;
        let birth = measured!(self.metrics, Properties, p.try_parse::<u64>("CreateTime")).ok();
        let event_time = if id == 2 {
            measured!(self.metrics, Properties, p.try_parse::<u64>("ExitTime")).unwrap_or(time)
        } else {
            time
        };
        let image = measured!(self.metrics, Properties, p.try_parse::<String>("ImageName"))
            .map(|p| {
                measured!(
                    self.metrics,
                    Path,
                    native::normalize_path(&p, &self.devices)
                )
            })
            .unwrap_or_default();
        let _construct = self.metrics.timer(Time::Construct);
        let event = SystemEvent {
            id: uuid::Uuid::new_v4().to_string(),
            timestamp: native::timestamp(event_time),
            timestamp_ticks: event_time,
            event_type: "lifecycle".into(),
            operation: match id {
                1 => "process_start",
                2 => "process_stop",
                3 => "thread_start",
                _ => "thread_stop",
            }
            .into(),
            resource: image.clone(),
            evidence: AttributionEvidence {
                source: EvidenceSource::EtwProcess,
                pid: Some(pid),
                process_creation_time: birth,
                process_image: (!image.is_empty()).then_some(image),
                parent_pid: measured!(
                    self.metrics,
                    Properties,
                    p.try_parse::<u32>("ParentProcessID")
                )
                .ok(),
                ..Default::default()
            },
            raw: RawEvidence {
                clock: Some(super::super::clock::record(record)),
                event_id: Some(id),
                thread_id: measured!(self.metrics, Properties, p.try_parse::<u32>("ThreadID")).ok(),
                process_key: pointer(&self.metrics, &p, "ProcessSequenceNumber"),
                parent_key: pointer(&self.metrics, &p, "ParentProcessSequenceNumber"),
                ..Default::default()
            },
            ..Default::default()
        };
        if self.deferred_registry && matches!(id, 1 | 2) {
            let previous = self.lifetimes.dropped;
            self.lifetimes.ingest(&event);
            self.metrics
                .add(Count::ContextEvictions, self.lifetimes.dropped - previous);
            if id == 2
                && let Some(birth) = birth
            {
                self.registry_context.process_exit((pid, birth));
            }
        }
        self.send(event);
    }
    pub(super) fn file(&mut self, record: &EventRecord, locator: &SchemaLocator) {
        let id = record.event_id();
        if !FILE_EVENT_IDS.contains(&id) {
            return;
        }
        self.metrics.add(Count::Attempted, 1);
        let time = record.raw_timestamp().max(0) as u64;
        if !self.admit_file_time(time) {
            return;
        }
        let Ok(schema) = measured!(self.metrics, Schema, locator.event_schema(record)) else {
            self.errors.fetch_add(1, Ordering::Relaxed);
            return;
        };
        let p = Parser::create(record, &schema);
        let irp = pointer(&self.metrics, &p, "Irp");
        if id == 24 {
            if let Some((request, start)) = irp.as_ref().and_then(|irp| self.pending.remove(irp))
                && time >= start
                && time - start < 300_000_000
            {
                let status = measured!(self.metrics, Properties, p.try_parse::<u32>("Status")).ok();
                let mut e = make_event(
                    &self.metrics,
                    time,
                    ("completion", "operation_end"),
                    String::new(),
                    EvidenceSource::EtwFile,
                    None,
                    status.and_then(status_success),
                );
                e.raw = RawEvidence {
                    clock: Some(super::super::clock::record(record)),
                    event_id: Some(id),
                    irp,
                    status,
                    related_event: Some(request),
                    ..Default::default()
                };
                self.send(e);
            }
            return;
        }
        let Ok(object) = measured!(self.metrics, Properties, p.try_parse::<u64>("FileObject"))
        else {
            self.errors.fetch_add(1, Ordering::Relaxed);
            return;
        };
        let supplied = measured!(self.metrics, Properties, p.try_parse::<String>("FileName"))
            .or_else(|_| measured!(self.metrics, Properties, p.try_parse::<String>("FilePath")))
            .ok()
            .map(|p| {
                measured!(
                    self.metrics,
                    Path,
                    native::normalize_path(&p, &self.devices)
                )
            });
        if matches!(id, 12 | 30) {
            self.paths.remove(&object);
            if let Some(path) = supplied
                .as_ref()
                .filter(|p| native::in_scope(p, &self.roots))
            {
                if self.paths.len() < OBJECT_CAPACITY {
                    self.paths
                        .insert(object, (path.clone(), uuid::Uuid::new_v4().to_string()));
                } else {
                    self.metrics.add(Count::ContextEvictions, 1);
                }
            }
        }
        let generation = self
            .paths
            .get(&object)
            .map(|(_, generation)| generation.clone());
        let path = supplied.or_else(|| self.paths.get(&object).map(|(path, _)| path.clone()));
        if matches!(id, 14 | 27) {
            self.paths.remove(&object);
        }
        let Some(path) = path.filter(|p| native::in_scope(p, &self.roots)) else {
            return;
        };
        let tid = measured!(
            self.metrics,
            Properties,
            p.try_parse::<u32>("IssuingThreadId")
        )
        .ok();
        // Resolve issuing TID using retained event-time lifetimes after drain.
        // No borrowed payload or PID-only identity cache survives the callback.
        let writer = None;
        let mut e = make_event(
            &self.metrics,
            time,
            (
                "file",
                match id {
                    12 => "open_requested",
                    14 => "close",
                    16 => "write_requested",
                    26 => "delete_requested",
                    27 => "rename_requested",
                    _ => "create_new_file",
                },
            ),
            path,
            EvidenceSource::EtwFile,
            writer,
            None,
        );
        e.raw = RawEvidence {
            clock: Some(super::super::clock::record(record)),
            event_id: Some(id),
            thread_id: tid,
            header_pid: Some(record.process_id()),
            file_object: Some(format!("{object:016x}")),
            object_generation: generation,
            file_key: pointer(&self.metrics, &p, "FileKey"),
            irp: irp.clone(),
            resource_resolved: true,
            ..Default::default()
        };
        if matches!(id, 16 | 26 | 27)
            && let Some(irp) = irp
        {
            if self.pending.contains_key(&irp) {
                self.pending.remove(&irp);
                self.errors.fetch_add(1, Ordering::Relaxed);
            } else if self.pending.len() < OBJECT_CAPACITY {
                self.pending.insert(irp, (e.id.clone(), time));
            } else {
                self.metrics.add(Count::ContextEvictions, 1);
            }
        }
        self.send(e);
    }
    pub(super) fn registry(&mut self, record: &EventRecord, locator: &SchemaLocator) {
        let Some(scope) = self.registry_root.clone() else {
            return;
        };
        let id = record.event_id();
        if !matches!(id, 1 | 2 | 3 | 5 | 6 | 13) {
            return;
        }
        self.metrics.add(Count::Attempted, 1);
        let Ok(schema) = measured!(self.metrics, Schema, locator.event_schema(record)) else {
            self.errors.fetch_add(1, Ordering::Relaxed);
            return;
        };
        let p = Parser::create(record, &schema);
        let time = record.raw_timestamp().max(0) as u64;
        let pid = record.process_id();
        // Context needs a process generation, not a query of whoever owns the PID now.
        let writer = if self.deferred_registry {
            let _identity_context = self.metrics.timer(Time::Context);
            self.lifetimes
                .resolve(pid, time)
                .map(|p| native::ProcessIdentity {
                    pid,
                    creation_time: p.creation_time.unwrap(),
                    image: String::new(),
                })
        } else {
            measured!(self.metrics, Identity, native::process_identity(pid))
                .filter(|p| p.creation_time <= time)
        };
        let object =
            measured!(self.metrics, Properties, p.try_parse::<u64>("KeyObject")).unwrap_or(0);
        let status = measured!(self.metrics, Properties, p.try_parse::<u32>("Status")).ok();
        let success = status.and_then(status_success);
        let key_name = measured!(self.metrics, Properties, p.try_parse::<String>("KeyName"));
        let mut key = key_name
            .as_ref()
            .map(|name| name.to_lowercase())
            .unwrap_or_default();
        let context_clock = self.metrics.timer(Time::Context);
        let previous_drops = self.registry_context.dropped;
        let context_owner = writer.as_ref().filter(|w| {
            !self.deferred_registry
                || self
                    .registry_context
                    .observe_object((pid, w.creation_time), object, time)
        });
        self.metrics.add(
            Count::ContextEvictions,
            self.registry_context.dropped - previous_drops,
        );
        let mut generation = None;
        if let Some(w) = context_owner {
            let owner = (pid, w.creation_time);
            if matches!(id, 1 | 2) {
                let previous_drops = self.registry_context.dropped;
                key = self
                    .registry_context
                    .open(
                        owner,
                        object,
                        measured!(self.metrics, Properties, p.try_parse::<u64>("BaseObject"))
                            .unwrap_or(0),
                        &measured!(self.metrics, Properties, p.try_parse::<String>("BaseName"))
                            .unwrap_or_default(),
                        &measured!(
                            self.metrics,
                            Properties,
                            p.try_parse::<String>("RelativeName")
                        )
                        .unwrap_or_default(),
                        time,
                    )
                    .unwrap_or_default();
                self.metrics.add(
                    Count::ContextEvictions,
                    self.registry_context.dropped - previous_drops,
                );
                if success != Some(true) {
                    self.registry_context.close(owner, object);
                }
            } else if !key.starts_with("\\registry\\") {
                key = self
                    .registry_context
                    .get(owner, object, time)
                    .unwrap_or_default();
            }
            if self.deferred_registry {
                generation = self.registry_context.generation(owner, object);
            }
            if matches!(id, 3 | 13) {
                self.registry_context.close(owner, object);
            }
        }
        drop(context_clock);
        let resolved = key.starts_with("\\registry\\");
        if resolved && !native::in_scope(&key, &[scope]) {
            return;
        }
        if !resolved {
            self.metrics.add(Count::Unresolved, 1);
            self.registry_path_gaps.fetch_add(1, Ordering::Relaxed);
            key = "<unresolved registry object>".into();
        }
        if matches!(id, 2 | 13) && (!self.deferred_registry || context_owner.is_some()) {
            return;
        }
        let gap_reason = (!resolved).then_some(if writer.is_none() {
            "process_identity_unavailable"
        } else if context_owner.is_none() {
            "event_time_context_rejected"
        } else if matches!(id, 1 | 2) {
            "open_path_has_no_verified_absolute_base"
        } else {
            "nonabsolute_key_name_and_no_object_context"
        });
        let fields = retained_registry_fields(
            &self.metrics,
            &p,
            record.version(),
            key_name,
            context_owner.is_some(),
            gap_reason,
        );
        if resolved
            && let Ok(value) =
                measured!(self.metrics, Properties, p.try_parse::<String>("ValueName"))
        {
            key = format!("{key}\\{value}");
        }
        let mut e = make_event(
            &self.metrics,
            time,
            (
                "registry",
                match id {
                    1 => "create_or_open_key",
                    2 => "open_key_context",
                    3 => "delete_key",
                    5 => "set_value",
                    13 => "close_key_context",
                    _ => "delete_value",
                },
            ),
            key,
            EvidenceSource::EtwRegistry,
            if self.deferred_registry { None } else { writer },
            success,
        );
        e.evidence.pid = Some(pid);
        e.raw = RawEvidence {
            event_id: Some(id),
            header_pid: Some(pid),
            clock: Some(super::super::clock::record(record)),
            registry_object: Some(format!("{object:016x}")),
            object_generation: generation,
            registry_fields: Some(fields),
            process_key: if self.deferred_registry {
                record
                    .extended_data()
                    .iter()
                    .find_map(|data| match data.to_extended_data_item() {
                        ferrisetw::native::ExtendedDataItem::ProcessStartKey(key) => {
                            Some(format!("{key:016x}"))
                        }
                        _ => None,
                    })
            } else {
                None
            },
            status,
            resource_resolved: resolved,
            ..Default::default()
        };
        if matches!(id, 2 | 13) {
            e.event_type = "registry_context".into();
        }
        self.send(e);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn registry_diagnostics_distinguish_absent_parse_failure_and_bounded_text() {
        let mut diagnostics = crate::model::RegistryFieldDiagnostics::default();
        assert!(
            diagnostic_parse::<String>("KeyName", Err(ParserError::NotFound), &mut diagnostics)
                .is_none()
        );
        assert!(
            diagnostic_parse::<String>(
                "ValueName",
                Err(ParserError::InvalidType),
                &mut diagnostics
            )
            .is_none()
        );
        assert_eq!(diagnostics.parse_errors["KeyName"], "not_found");
        assert_eq!(diagnostics.parse_errors["ValueName"], "invalid_type");
        let text = format!("{}中", "x".repeat(REGISTRY_DIAGNOSTIC_FIELD_BYTES - 1));
        let bounded = bounded_registry_field("RelativeName", text, &mut diagnostics);
        assert_eq!(bounded.len(), REGISTRY_DIAGNOSTIC_FIELD_BYTES - 1);
        assert_eq!(diagnostics.truncated_fields, ["RelativeName"]);
        assert_eq!(
            diagnostic_parse("BaseName", Ok(String::new()), &mut diagnostics),
            Some(String::new())
        );
        assert!(!diagnostics.parse_errors.contains_key("BaseName"));
    }

    #[test]
    fn registry_diagnostic_names_never_supply_resource_or_actor_evidence() {
        let mut event = SystemEvent {
            event_type: "registry".into(),
            operation: "set_value".into(),
            resource: "<unresolved registry object>".into(),
            evidence: AttributionEvidence {
                source: EvidenceSource::EtwRegistry,
                pid: Some(7),
                process_creation_time: Some(10),
                ..Default::default()
            },
            raw: RawEvidence {
                registry_fields: Some(Box::new(crate::model::RegistryFields {
                    key_name: "\\registry\\user\\sid\\software\\fixture".into(),
                    value_name: Some("NoiseOnly".into()),
                    diagnostics: Some(crate::model::RegistryFieldDiagnostics {
                        context_owner_available: true,
                        path_gap_reason: Some("nonabsolute_key_name_and_no_object_context".into()),
                        ..Default::default()
                    }),
                    ..Default::default()
                })),
                ..Default::default()
            },
            ..Default::default()
        };
        crate::attribution::attribute(&mut event, &[], "session");
        assert_eq!(event.confidence, crate::model::Confidence::Unknown);
        assert!(!event.raw.resource_resolved);
        assert_eq!(event.resource, "<unresolved registry object>");
        assert!(event.evidence.session_id.is_none());
        let old = serde_json::json!({"provider":"test","version":0,"base_object":null,
            "key_name":"","base_name":"","relative_name":"","value_name":null});
        let fields: crate::model::RegistryFields = serde_json::from_value(old).unwrap();
        assert!(fields.diagnostics.is_none());
    }

    #[test]
    fn delayed_file_record_cannot_reuse_newer_object_or_irp_context() {
        let (tx, _rx) = mpsc::sync_channel(1);
        let mut decoder = Decoder {
            deferred_registry: false,
            file_watermark: 0,
            file_time_regressions: 0,
            lifetimes: Default::default(),
            paths: HashMap::new(),
            pending: HashMap::new(),
            registry_context: Default::default(),
            roots: vec![],
            registry_root: None,
            devices: vec![],
            tx,
            metrics: Arc::new(Metrics::default()),
            dropped: Arc::new(AtomicU64::new(0)),
            errors: Arc::new(AtomicU64::new(0)),
            registry_path_gaps: Arc::new(AtomicU64::new(0)),
            received: Arc::new(AtomicU64::new(0)),
        };
        assert!(decoder.admit_file_time(30));
        decoder
            .paths
            .insert(7, ("new-file".into(), "new-generation".into()));
        decoder
            .pending
            .insert("irp".into(), ("new-request".into(), 30));
        assert!(!decoder.admit_file_time(20));
        assert!(decoder.paths.is_empty());
        assert!(decoder.pending.is_empty());
        assert_eq!(decoder.file_watermark, 30);
        assert_eq!(decoder.errors.load(Ordering::Relaxed), 1);
        assert_eq!(decoder.file_time_regressions, 1);
        assert!(!decoder.admit_file_time(25));
        assert!(decoder.admit_file_time(30));
        assert!(decoder.admit_file_time(40));
        assert_eq!(decoder.dropped.load(Ordering::Relaxed), 0);
        // Ambiguity counts as decode loss, not a fabricated queue overflow.
        let backend = BackendReport {
            decode_errors: decoder.errors.load(Ordering::Relaxed),
            etw_events_lost: Some(0),
            etw_buffers_lost: Some(0),
            ..Default::default()
        };
        assert!(!backend.source_intact());
    }
}
