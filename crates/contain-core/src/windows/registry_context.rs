//! Manifest KeyObject context is not a classic KCB. Cache only proven absolute names.
use std::collections::HashMap;
#[derive(Default)]
pub struct RegistryContext {
    pub dropped: u64,
    paths: HashMap<(u32, u64, u64), (String, u64)>,
    watermarks: HashMap<(u32, u64), u64>,
}
impl RegistryContext {
    pub fn observe_object(&mut self, owner: (u32, u64), object: u64, at: u64) -> bool {
        object != 0 && self.observe(owner, at)
    }
    /// Only the lifecycle-based path calls this. A backwards record invalidates
    /// that lifetime's contexts; it cannot close/revive a newer object generation.
    pub fn observe(&mut self, owner: (u32, u64), at: u64) -> bool {
        if let Some(last) = self.watermarks.get(&owner) {
            if at < *last {
                self.paths
                    .retain(|&(pid, birth, _), _| (pid, birth) != owner);
                return false;
            }
        } else if self.watermarks.len() >= 32_768 {
            self.dropped += 1;
            return false;
        }
        self.watermarks.insert(owner, at);
        true
    }
    pub fn process_exit(&mut self, owner: (u32, u64)) {
        self.paths
            .retain(|&(pid, birth, _), _| (pid, birth) != owner);
        self.watermarks.remove(&owner);
    }
    pub fn generation(&self, owner: (u32, u64), object: u64) -> Option<String> {
        self.paths
            .get(&(owner.0, owner.1, object))
            .map(|(_, at)| format!("{}:{}:{object:016x}:{at}", owner.0, owner.1))
    }
    pub fn open(
        &mut self,
        owner: (u32, u64),
        object: u64,
        base: u64,
        base_name: &str,
        relative: &str,
        at: u64,
    ) -> Option<String> {
        self.paths.remove(&(owner.0, owner.1, object));
        let base_name = if base_name.to_lowercase().starts_with("\\registry\\") {
            Some(base_name.to_lowercase())
        } else {
            self.get(owner, base, at)
        };
        let path = if relative.to_lowercase().starts_with("\\registry\\") {
            Some(relative.to_lowercase())
        } else {
            base_name.map(|b| {
                format!(
                    "{}\\{}",
                    b.trim_end_matches('\\'),
                    relative.trim_start_matches('\\')
                )
                .trim_end_matches('\\')
                .to_lowercase()
            })
        };
        if let Some(path) = &path
            && self.paths.len() < 16_384
        {
            self.paths
                .insert((owner.0, owner.1, object), (path.clone(), at));
        }
        if path.is_some() && !self.paths.contains_key(&(owner.0, owner.1, object)) {
            self.dropped += 1;
        }
        path
    }
    pub fn get(&self, owner: (u32, u64), object: u64, at: u64) -> Option<String> {
        self.paths
            .get(&(owner.0, owner.1, object))
            .filter(|(_, start)| *start <= at)
            .map(|(p, _)| p.clone())
    }
    pub fn close(&mut self, owner: (u32, u64), object: u64) {
        self.paths.remove(&(owner.0, owner.1, object));
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn late_close_invalidates_context_without_reviving_reused_object() {
        let mut c = RegistryContext::default();
        let owner = (1, 10);
        assert!(!c.observe_object(owner, 0, 20));
        assert!(c.observe(owner, 20));
        c.open(owner, 2, 0, "\\registry\\user\\sid", "old", 20);
        assert!(c.observe(owner, 30));
        c.open(owner, 2, 0, "\\registry\\user\\sid", "new", 30);
        assert!(!c.observe(owner, 25));
        assert!(c.get(owner, 2, 40).is_none());
        assert!(c.observe(owner, 50));
        c.open(owner, 2, 0, "\\registry\\user\\sid", "later", 50);
        c.process_exit(owner);
        assert!(c.get(owner, 2, 60).is_none());
        assert!(c.get((1, 70), 2, 80).is_none());
    }
    #[test]
    fn relative_path_needs_proven_base_and_does_not_survive_close_or_reuse() {
        let mut c = RegistryContext::default();
        assert!(c.open((1, 10), 3, 2, "", "Software\\X", 20).is_none());
        assert_eq!(
            c.open((1, 10), 2, 0, "\\registry\\user\\sid", "Software", 21)
                .as_deref(),
            Some("\\registry\\user\\sid\\software")
        );
        assert!(
            c.open((1, 10), 3, 2, "", "X", 22)
                .unwrap()
                .ends_with("\\software\\x")
        );
        assert!(c.get((1, 30), 3, 31).is_none());
        c.close((1, 10), 3);
        assert!(c.get((1, 10), 3, 40).is_none());
    }
}
