fn main() -> anyhow::Result<()> {
    let arguments: Vec<_> = std::env::args_os().skip(1).collect();
    match arguments.as_slice() {
        [directory] => contain_offline::workflow::generate(std::path::Path::new(directory)),
        [flag, directory, root, alias, installer] if flag == "--report-fixture" => {
            contain_offline::workflow::generate_report_fixture(
                std::path::Path::new(directory),
                std::path::Path::new(root),
                alias
                    .to_str()
                    .ok_or_else(|| anyhow::anyhow!("alias must be Unicode"))?,
                std::path::Path::new(installer),
            )
        }
        _ => anyhow::bail!(
            "usage: read-only-examples <new-output-directory> | --report-fixture <new-output-directory> <fixture-root> <observed-root-alias> <packaged-installer>"
        ),
    }
}
