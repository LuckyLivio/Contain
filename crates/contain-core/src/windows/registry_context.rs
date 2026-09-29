//! Manifest KeyObject context is not a classic KCB. Cache only proven absolute names.
use std::collections::HashMap;
#[derive(Default)]
pub struct RegistryContext {
    pub dropped: u64,
    paths: HashMap<(u32, u64, u64), (String, u64)>,
}
impl RegistryContext {
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
