use super::*;
use crate::model::RawEvidence;
use ferrisetw::parser::Parser;

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
        self.send(SystemEvent {
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
                event_id: Some(id),
                thread_id: measured!(self.metrics, Properties, p.try_parse::<u32>("ThreadID")).ok(),
                process_key: pointer(&self.metrics, &p, "ProcessSequenceNumber"),
                parent_key: pointer(&self.metrics, &p, "ParentProcessSequenceNumber"),
                ..Default::default()
            },
            ..Default::default()
        });
    }
    pub(super) fn file(&mut self, record: &EventRecord, locator: &SchemaLocator) {
        let id = record.event_id();
        if !FILE_EVENT_IDS.contains(&id) {
            return;
        }
        self.metrics.add(Count::Attempted, 1);
        let Ok(schema) = measured!(self.metrics, Schema, locator.event_schema(record)) else {
            self.errors.fetch_add(1, Ordering::Relaxed);
            return;
        };
        let p = Parser::create(record, &schema);
        let time = record.raw_timestamp().max(0) as u64;
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
                    self.dropped.fetch_add(1, Ordering::Relaxed);
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
                self.dropped.fetch_add(1, Ordering::Relaxed);
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
        let writer = measured!(self.metrics, Identity, native::process_identity(pid))
            .filter(|p| p.creation_time <= time);
        let object =
            measured!(self.metrics, Properties, p.try_parse::<u64>("KeyObject")).unwrap_or(0);
        let status = measured!(self.metrics, Properties, p.try_parse::<u32>("Status")).ok();
        let success = status.and_then(status_success);
        let mut key = measured!(self.metrics, Properties, p.try_parse::<String>("KeyName"))
            .unwrap_or_default()
            .to_lowercase();
        if let Some(w) = &writer {
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
                self.dropped.fetch_add(
                    self.registry_context.dropped - previous_drops,
                    Ordering::Relaxed,
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
            if matches!(id, 3 | 13) {
                self.registry_context.close(owner, object);
            }
        }
        let resolved = key.starts_with("\\registry\\");
        if resolved && !native::in_scope(&key, &[scope]) {
            return;
        }
        if !resolved {
            self.registry_path_gaps.fetch_add(1, Ordering::Relaxed);
            key = "<unresolved registry object>".into();
        }
        if matches!(id, 2 | 13) {
            return;
        }
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
                    3 => "delete_key",
                    5 => "set_value",
                    _ => "delete_value",
                },
            ),
            key,
            EvidenceSource::EtwRegistry,
            writer,
            success,
        );
        e.evidence.pid = Some(pid);
        e.raw = RawEvidence {
            event_id: Some(id),
            header_pid: Some(pid),
            registry_object: Some(format!("{object:016x}")),
            status,
            resource_resolved: resolved,
            ..Default::default()
        };
        self.send(e);
    }
}
