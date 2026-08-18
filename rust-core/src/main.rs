use std::io;

fn main() {
    // serve 的 reader 需要 Send + 'static(读线程持有),stdin().lock() 的 MutexGuard 不满足,
    // 因此直接把 stdin 句柄包进 BufReader 传给 serve。
    let reader = io::BufReader::new(io::stdin());
    if let Err(error) = nexus_rust_core::serve(reader, io::stdout().lock()) {
        eprintln!("nexus-rust-core: {error}");
        std::process::exit(1);
    }
}
