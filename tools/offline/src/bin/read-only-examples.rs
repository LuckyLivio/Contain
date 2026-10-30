fn main() -> anyhow::Result<()> {
    let directory = std::env::args_os()
        .nth(1)
        .ok_or_else(|| anyhow::anyhow!("usage: read-only-examples <new-output-directory>"))?;
    contain_offline::workflow::generate(std::path::Path::new(&directory))
}
