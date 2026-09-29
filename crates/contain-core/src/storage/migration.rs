use anyhow::{Result, bail};
use rusqlite::Connection;

pub fn check_version(connection: &Connection) -> Result<u32> {
    let version =
        connection.pragma_query_value(None, "user_version", |row| row.get::<_, u32>(0))?;
    if version > 2 {
        bail!(
            "Database schema {version} is newer than this Contain build (2). Use a newer binary; database was not changed."
        );
    }
    Ok(version)
}

pub fn apply(connection: &mut Connection) -> Result<()> {
    if check_version(connection)? == 2 {
        return Ok(());
    }
    let tx = connection.transaction()?;
    // v0.1 tables stay intact. New typed tables add process lifetimes, source events and evidence.
    tx.execute_batch(include_str!("v2.sql"))?;
    tx.pragma_update(None, "user_version", 2)?;
    tx.commit()?;
    Ok(())
}
