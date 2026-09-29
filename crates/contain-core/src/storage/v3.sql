CREATE TABLE reliability_metadata (
    session_id TEXT PRIMARY KEY REFERENCES installation_sessions(id),
    stats_json TEXT NOT NULL, quality_json TEXT NOT NULL
);
CREATE TABLE observation_details (
    event_id TEXT PRIMARY KEY REFERENCES observations(id),
    sequence INTEGER NOT NULL, raw_json TEXT NOT NULL, dimensions_json TEXT NOT NULL
);
CREATE TABLE normalized_operations (
    id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES installation_sessions(id),
    operation TEXT NOT NULL, resource TEXT NOT NULL, detail_json TEXT NOT NULL
);
CREATE TABLE evidence_edges (
    session_id TEXT NOT NULL REFERENCES installation_sessions(id),
    from_node TEXT NOT NULL, to_node TEXT NOT NULL, relation TEXT NOT NULL,
    confidence TEXT NOT NULL, reason TEXT NOT NULL,
    PRIMARY KEY(session_id,from_node,to_node,relation)
);
CREATE INDEX edges_target ON evidence_edges(session_id,to_node);
