-- Shared durable ingest staging. Historical raw_stream and its indexes stay intact.
-- The PK preserves sequence uniqueness; time index keeps crash readback paged.
CREATE TABLE IF NOT EXISTS raw_ingest (
    session_id TEXT NOT NULL, sequence INTEGER NOT NULL, ticks INTEGER NOT NULL,
    kind TEXT NOT NULL, header_pid INTEGER, related TEXT, document TEXT NOT NULL,
    PRIMARY KEY(session_id,sequence)
);
CREATE INDEX IF NOT EXISTS raw_ingest_time ON raw_ingest(session_id,ticks,sequence);
