use anyhow::{Result, bail};
use rusqlite::Connection;

pub fn check_version(connection: &Connection) -> Result<u32> {
    let version =
        connection.pragma_query_value(None, "user_version", |row| row.get::<_, u32>(0))?;
    if version > 3 {
        bail!(
            "Database schema {version} is newer than this Contain build (3). Use a newer binary; database was not changed."
        );
    }
    Ok(version)
}

pub fn apply(connection: &mut Connection) -> Result<()> {
    let version = check_version(connection)?;
    if version == 3 {
        return Ok(());
    }
    let tx = connection.transaction()?;
    // v0.1 tables stay intact. New typed tables add process lifetimes, source events and evidence.
    if version < 2 {
        tx.execute_batch(include_str!("v2.sql"))?;
    }
    tx.execute_batch(include_str!("v3.sql"))?;
    tx.pragma_update(None, "user_version", 3)?;
    tx.commit()?;
    Ok(())
}
