fn main() -> anyhow::Result<()> {
    let arguments: Vec<_> = std::env::args_os().skip(1).collect();
    match arguments.as_slice() {
        [directory] => contain_offline::workflow::generate_viewer(std::path::Path::new(directory)),
        _ => anyhow::bail!("usage: viewer-fixtures <new-output-directory>"),
    }
}
