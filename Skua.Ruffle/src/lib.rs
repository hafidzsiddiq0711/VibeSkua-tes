//! Headless Ruffle for Skua.
//!
//! Plays skua-mobile.swf (which loads AQW) with no renderer, no audio and no window: only the
//! ActionScript, the network (HTTP + the game's sockets) and timers run. skua-mobile.swf talks to
//! the Skua bot over its usual socket bridge (`skuaHost=127.0.0.1:<port>`), so on the Skua side a
//! headless game looks exactly like a game in the Artix player.
//!
//! Used two ways:
//! - `libskua_ruffle.so` in Skua Mobile: the bot process calls [`skua_ruffle_start`].
//! - the `skua-ruffle` binary on Linux (CI end-to-end test, servers).

use ruffle_common::duration::FloatDuration;
use ruffle_core::PlayerBuilder;
use ruffle_core::backend::navigator::{OwnedFuture, SocketMode};
use ruffle_core::tag_utils::SwfMovieData;
use ruffle_frontend_utils::backends::navigator::{
    ExternalNavigatorBackend, FutureSpawner, NavigatorInterface,
};
use ruffle_frontend_utils::content::{ContentDescriptor, PlayingContent};
use std::ffi::{CStr, CString, c_char};
use std::fs::File;
use std::path::Path;
use std::rc::Rc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, RecvTimeoutError};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::{Duration, Instant};
use url::Url;

/// Where the movie claims to come from. The game's own files are loaded from game.aq.com,
/// so the loader movie lives on the same site.
const MOVIE_URL: &str = "https://game.aq.com/game/skua-mobile.swf";

/// Longest sleep between loop iterations (keeps sockets and timers responsive).
const MAX_WAIT: Duration = Duration::from_millis(50);

pub struct Config {
    pub swf: Vec<u8>,
    /// "host:port" of the Skua bot (the swf's `skuaHost` parameter).
    pub skua_host: String,
    /// Directories with CA certificates (Android's system store); empty: platform default.
    pub ca_dirs: Vec<String>,
}

#[derive(Clone)]
struct Interface;

impl NavigatorInterface for Interface {
    fn navigate_to_website(&self, _url: Url) {}

    async fn open_file(&self, path: &Path) -> std::io::Result<File> {
        File::open(path)
    }

    async fn confirm_socket(&self, _host: &str, _port: u16) -> bool {
        true
    }
}

/// Runs Ruffle's async work (HTTP, sockets) on the player thread, like the desktop player.
struct Executor {
    tx: mpsc::Sender<async_task::Runnable>,
}

impl<E: std::error::Error + 'static> FutureSpawner<E> for Executor {
    fn spawn(&self, future: OwnedFuture<(), E>) {
        let future = async {
            if let Err(e) = future.await {
                log::warn!("async error: {e}");
            }
        };
        let tx = self.tx.clone();
        let (runnable, task) = async_task::spawn_local(future, move |r| {
            let _ = tx.send(r);
        });
        task.detach();
        runnable.schedule();
    }
}

/// Play until `stop` is set. Blocks the calling thread.
pub fn run(config: Config, stop: Arc<AtomicBool>) -> Result<(), String> {
    if !config.ca_dirs.is_empty() {
        // Read by the patched navigator when it builds its HTTP client.
        // SAFETY: set once, before any HTTP client exists in this process.
        unsafe { std::env::set_var("SKUA_CA_DIRS", std::env::join_paths(&config.ca_dirs).map_err(|e| e.to_string())?) };
    }
    #[cfg(target_os = "android")]
    {
        let _ = rustls::crypto::ring::default_provider().install_default();
    }

    let runtime = tokio::runtime::Builder::new_multi_thread()
        .worker_threads(2)
        .enable_all()
        .build()
        .map_err(|e| e.to_string())?;
    let _runtime = runtime.enter();

    let movie_url = Url::parse(MOVIE_URL).unwrap();
    let (tx, rx) = mpsc::channel::<async_task::Runnable>();
    let navigator = ExternalNavigatorBackend::new(
        movie_url.clone(),
        None,
        None,
        Executor { tx },
        None,
        true,
        Default::default(),
        SocketMode::Allow,
        Rc::new(PlayingContent::DirectFile(ContentDescriptor::new_remote(movie_url.clone()))),
        Interface,
    );

    // No renderer, audio or UI backends: the builder's defaults are the null ones.
    let player = PlayerBuilder::new().with_navigator(navigator).build();
    {
        let mut movie = SwfMovieData::from_data(&config.swf, movie_url.to_string(), None, None)
            .map_err(|e| format!("swf: {e}"))?;
        movie.append_parameters(vec![("skuaHost".to_string(), config.skua_host.clone())]);
        let mut p = player.lock().unwrap();
        p.mutate_with_update_context(|context| context.set_root_movie(movie));
        p.set_is_playing(true);
    }
    log::info!("headless game started (Skua at {})", config.skua_host);

    let mut last = Instant::now();
    while !stop.load(Ordering::Relaxed) {
        // Sleep until the next frame is due, running network/timer work as it arrives.
        let wait = player.lock().unwrap().time_til_next_frame().min(MAX_WAIT);
        let deadline = Instant::now() + wait;
        loop {
            match rx.recv_timeout(deadline.saturating_duration_since(Instant::now())) {
                Ok(runnable) => {
                    runnable.run();
                }
                Err(RecvTimeoutError::Timeout) => break,
                Err(RecvTimeoutError::Disconnected) => return Err("executor closed".into()),
            }
        }
        let now = Instant::now();
        let dt = now.duration_since(last);
        last = now;
        player
            .lock()
            .unwrap()
            .tick(FloatDuration::from_millis(dt.as_secs_f64() * 1000.0));
    }
    log::info!("headless game stopped");
    Ok(())
}

// ------------------------------------------------------------------ logging

pub type LogCallback = extern "C" fn(*const c_char);

static LOG_CALLBACK: Mutex<Option<LogCallback>> = Mutex::new(None);
static RECENT: std::sync::LazyLock<Mutex<std::collections::HashMap<String, (Instant, u32)>>> =
    std::sync::LazyLock::new(|| Mutex::new(std::collections::HashMap::new()));

struct Logger;

impl log::Log for Logger {
    fn enabled(&self, metadata: &log::Metadata) -> bool {
        // Our own messages, and only problems from Ruffle (it is chatty).
        metadata.level() <= log::Level::Warn || metadata.target().starts_with("skua_ruffle")
    }

    fn log(&self, record: &log::Record) {
        if !self.enabled(record.metadata()) {
            return;
        }
        let line = format!("[ruffle {}] {}", record.level(), record.args());
        // Games often repeat the same error every frame: pass it on once a minute at most.
        {
            let mut seen = RECENT.lock().unwrap();
            let now = Instant::now();
            seen.retain(|_, (at, _)| now.duration_since(*at) < Duration::from_secs(60));
            let key: String = line.chars().take(200).collect();
            if let Some((_, count)) = seen.get_mut(&key) {
                *count += 1;
                return;
            }
            if seen.len() > 200 {
                seen.clear();
            }
            seen.insert(key, (now, 0));
        }
        match *LOG_CALLBACK.lock().unwrap() {
            Some(cb) => {
                if let Ok(c) = CString::new(line.replace('\0', " ")) {
                    cb(c.as_ptr());
                }
            }
            None => eprintln!("{line}"),
        }
    }

    fn flush(&self) {}
}

pub fn init_logging(callback: Option<LogCallback>) {
    *LOG_CALLBACK.lock().unwrap() = callback;
    static INIT: OnceLock<()> = OnceLock::new();
    INIT.get_or_init(|| {
        if log::set_boxed_logger(Box::new(Logger)).is_ok() {
            log::set_max_level(log::LevelFilter::Info);
        }
    });
}

// ------------------------------------------------------------------ C API (one game per process)

static RUNNING: Mutex<Option<(Arc<AtomicBool>, std::thread::JoinHandle<()>)>> = Mutex::new(None);

unsafe fn c_str(p: *const c_char) -> String {
    if p.is_null() {
        String::new()
    } else {
        unsafe { CStr::from_ptr(p) }.to_string_lossy().into_owned()
    }
}

/// Start the headless game. `ca_dirs`: CA directories separated by ':' (may be empty).
/// Returns 0 when started, 1 when already running, negative on error.
///
/// # Safety
/// The strings must be valid NUL-terminated UTF-8; `log` may be null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn skua_ruffle_start(
    swf_path: *const c_char,
    skua_host: *const c_char,
    ca_dirs: *const c_char,
    log: Option<LogCallback>,
) -> i32 {
    init_logging(log);
    let mut running = RUNNING.lock().unwrap();
    if let Some((_, handle)) = running.as_ref()
        && !handle.is_finished()
    {
        return 1;
    }
    let (swf_path, skua_host, ca_dirs) = unsafe { (c_str(swf_path), c_str(skua_host), c_str(ca_dirs)) };
    let swf = match std::fs::read(&swf_path) {
        Ok(bytes) => bytes,
        Err(e) => {
            log::error!("cannot read {swf_path}: {e}");
            return -1;
        }
    };
    let config = Config {
        swf,
        skua_host,
        ca_dirs: ca_dirs.split(':').filter(|s| !s.is_empty()).map(str::to_string).collect(),
    };
    let stop = Arc::new(AtomicBool::new(false));
    let thread_stop = stop.clone();
    let handle = std::thread::Builder::new()
        .name("skua-ruffle".into())
        .stack_size(16 * 1024 * 1024)
        .spawn(move || {
            let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| run(config, thread_stop)));
            match result {
                Ok(Ok(())) => {}
                Ok(Err(e)) => log::error!("headless game failed: {e}"),
                Err(panic) => {
                    let message = panic
                        .downcast_ref::<&str>()
                        .map(|s| s.to_string())
                        .or_else(|| panic.downcast_ref::<String>().cloned())
                        .unwrap_or_else(|| "?".into());
                    log::error!("headless game crashed: {message}");
                }
            }
        });
    match handle {
        Ok(handle) => {
            *running = Some((stop, handle));
            0
        }
        Err(e) => {
            log::error!("cannot start thread: {e}");
            -2
        }
    }
}

/// Stop the headless game (returns after it has stopped, at most ~2 s).
#[unsafe(no_mangle)]
pub extern "C" fn skua_ruffle_stop() {
    let taken = RUNNING.lock().unwrap().take();
    if let Some((stop, handle)) = taken {
        stop.store(true, Ordering::Relaxed);
        let until = Instant::now() + Duration::from_secs(2);
        while !handle.is_finished() && Instant::now() < until {
            std::thread::sleep(Duration::from_millis(20));
        }
    }
}

/// 1 while the headless game is running.
#[unsafe(no_mangle)]
pub extern "C" fn skua_ruffle_running() -> i32 {
    match RUNNING.lock().unwrap().as_ref() {
        Some((_, handle)) if !handle.is_finished() => 1,
        _ => 0,
    }
}
