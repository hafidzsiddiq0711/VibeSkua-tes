//! skua-ruffle <skua-mobile.swf> <skuaHost> [seconds] [--until-stdin-closes]
//! Runs the game headless (CI end-to-end test, Skua Server). Stops after `seconds` if given, or
//! with --until-stdin-closes when the parent (the bot) goes away.

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

fn main() {
    skua_ruffle::init_logging(None);
    let all: Vec<String> = std::env::args().skip(1).collect();
    let watch_stdin = all.iter().any(|a| a == "--until-stdin-closes");
    let mut args = all.into_iter().filter(|a| !a.starts_with("--"));
    let swf_path = args.next().expect("usage: skua-ruffle <skua-mobile.swf> <skuaHost> [seconds]");
    let skua_host = args.next().unwrap_or_else(|| "127.0.0.1:5150".into());
    let seconds: Option<u64> = args.next().and_then(|s| s.parse().ok());

    let stop = Arc::new(AtomicBool::new(false));
    if let Some(seconds) = seconds {
        let stop = stop.clone();
        std::thread::spawn(move || {
            std::thread::sleep(std::time::Duration::from_secs(seconds));
            stop.store(true, Ordering::Relaxed);
        });
    }
    if watch_stdin {
        let stop = stop.clone();
        std::thread::spawn(move || {
            let mut sink = Vec::new();
            let _ = std::io::Read::read_to_end(&mut std::io::stdin(), &mut sink);
            stop.store(true, Ordering::Relaxed);
        });
    }
    let config = skua_ruffle::Config {
        swf: std::fs::read(&swf_path).expect("read swf"),
        skua_host,
        ca_dirs: Vec::new(),
    };
    if let Err(e) = skua_ruffle::run(config, stop) {
        eprintln!("error: {e}");
        std::process::exit(1);
    }
}
