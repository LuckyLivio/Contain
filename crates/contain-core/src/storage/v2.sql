CREATE TABLE capture_metadata (
    session_id TEXT PRIMARY KEY REFERENCES installation_sessions(id),
    manifest_version INTEGER NOT NULL, backend_json TEXT NOT NULL
);
CREATE TABLE process_instances (
    session_id TEXT NOT NULL REFERENCES installation_sessions(id),
    pid INTEGER NOT NULL, creation_time INTEGER NOT NULL, parent_pid INTEGER,
    parent_creation_time INTEGER, image TEXT NOT NULL, first_seen TEXT NOT NULL,
    last_seen TEXT NOT NULL, ended_at INTEGER, confidence TEXT NOT NULL,
    reason TEXT NOT NULL, evidence_json TEXT NOT NULL,
    PRIMARY KEY(session_id,pid,creation_time)
);
CREATE TABLE observations (
    id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES installation_sessions(id),
    timestamp TEXT NOT NULL, timestamp_ticks INTEGER NOT NULL,
    event_type TEXT NOT NULL, operation TEXT NOT NULL, resource TEXT NOT NULL,
    confidence TEXT NOT NULL, reason TEXT NOT NULL, source TEXT NOT NULL,
    pid INTEGER, process_creation_time INTEGER, process_image TEXT,
    parent_pid INTEGER, ancestor_pid INTEGER, attributed_session TEXT, rule TEXT NOT NULL,
    success INTEGER, state_validated INTEGER NOT NULL
);
CREATE INDEX observations_session_time ON observations(session_id,timestamp_ticks);
CREATE TABLE change_evidence (
    session_id TEXT NOT NULL REFERENCES installation_sessions(id), kind TEXT NOT NULL,
    resource TEXT NOT NULL, evidence_json TEXT NOT NULL,
    PRIMARY KEY(session_id,kind,resource)
);
CREATE TABLE inventory_changes (
    id INTEGER PRIMARY KEY, session_id TEXT NOT NULL REFERENCES installation_sessions(id),
    kind TEXT NOT NULL, name TEXT NOT NULL, operation TEXT NOT NULL,
    before_state_json TEXT, after_state_json TEXT,
    confidence TEXT NOT NULL, reason TEXT NOT NULL, evidence_json TEXT NOT NULL
);
