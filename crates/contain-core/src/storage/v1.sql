PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS applications (
                id TEXT PRIMARY KEY, name TEXT NOT NULL, installer TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS installation_sessions (
                id TEXT PRIMARY KEY, application_id TEXT NOT NULL REFERENCES applications(id),
                started_at TEXT NOT NULL, finished_at TEXT NOT NULL, exit_code INTEGER,
                watch_roots_json TEXT NOT NULL, registry_key TEXT, warnings_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS processes (
                session_id TEXT NOT NULL REFERENCES installation_sessions(id), pid INTEGER NOT NULL,
                parent_pid INTEGER, image TEXT NOT NULL, first_seen TEXT NOT NULL,
                confidence TEXT NOT NULL, reason TEXT NOT NULL,
                PRIMARY KEY(session_id, pid)
            );
            CREATE TABLE IF NOT EXISTS system_events (
                id INTEGER PRIMARY KEY, session_id TEXT NOT NULL REFERENCES installation_sessions(id),
                event_type TEXT NOT NULL, resource TEXT NOT NULL, operation TEXT NOT NULL,
                observed_at TEXT NOT NULL, process_id INTEGER, confidence TEXT NOT NULL,
                reason TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS file_changes (
                event_id INTEGER PRIMARY KEY REFERENCES system_events(id), before_hash TEXT,
                after_hash TEXT, after_size INTEGER, notification_seen INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS registry_changes (
                event_id INTEGER PRIMARY KEY REFERENCES system_events(id), registry_key TEXT NOT NULL,
                value_name TEXT NOT NULL, before_value TEXT, after_value TEXT
            );