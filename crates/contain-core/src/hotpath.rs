//! Independent experiment switches. Default remains the starting behavior until
//! the predeclared isolated comparisons justify enabling the combined candidate.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Variant {
    #[default]
    D0,
    D1,
    D2,
    D3,
}
impl Variant {
    pub fn from_env() -> Self {
        match std::env::var("CONTAIN_HOTPATH_VARIANT").as_deref() {
            Ok("D1") => Self::D1,
            Ok("D2") => Self::D2,
            Ok("D3") => Self::D3,
            _ => Self::D0,
        }
    }
    pub fn lifecycle_receiver(self) -> bool {
        matches!(self, Self::D2 | Self::D3)
    }
}
