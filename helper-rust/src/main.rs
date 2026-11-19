#![cfg_attr(windows, windows_subsystem = "windows")]
#![cfg_attr(not(windows), allow(dead_code))]

use axum::{
    extract::{State, Json},
    http::{HeaderMap, StatusCode, Request, HeaderValue},
    middleware::{self, Next},
    response::{IntoResponse, Response},
    routing::{get, post},
    Router,
};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    collections::HashMap,
    fs,
    path::PathBuf,
    sync::{Arc, Mutex},
};
use subtle::ConstantTimeEq;
use sysinfo::{Disks, System};
use tower_http::cors::CorsLayer;
use uuid::Uuid;
use winreg::{enums::{HKEY_CURRENT_USER, KEY_READ, KEY_WRITE}, RegKey};

const PORT: u16 = 47831;
const ALLOWED_ORIGIN: &str = "https://pc-booster-ai.onrender.com";

#[derive(Clone)]
struct AppState {
    token_hash: Arc<Vec<u8>>,
    pairing_token: Arc<String>,
    journal: Arc<Mutex<Journal>>,
    journal_path: Arc<PathBuf>,
}

#[derive(Default, Serialize, Deserialize)]
struct Journal {
    applied: HashMap<String, Vec<OriginalValue>>,
}

#[derive(Clone, Serialize, Deserialize)]
struct OriginalValue {
    key: String,
    name: String,
    kind: String,
    existed: bool,
    dword: Option<u32>,
    string: Option<String>,
}

#[derive(Deserialize)]
struct ApplyRequest {
    id: String,
}

#[derive(Deserialize)]
struct RollbackRequest {
    id: String,
}

#[tokio::main]
async fn main() {
    let token = Uuid::new_v4().simple().to_string();
    let token_hash = Sha256::digest(token.as_bytes()).to_vec();
    let journal_path = data_path();

    let journal = fs::read(&journal_path)
        .ok()
        .and_then(|bytes| serde_json::from_slice::<Journal>(&bytes).ok())
        .unwrap_or_default();

    let state = AppState {
        token_hash: Arc::new(token_hash),
        pairing_token: Arc::new(token.clone()),
        journal: Arc::new(Mutex::new(journal)),
        journal_path: Arc::new(journal_path),
    };

    let cors = CorsLayer::new()
        .allow_origin(ALLOWED_ORIGIN.parse::<axum::http::HeaderValue>().unwrap())
        .allow_methods([axum::http::Method::GET, axum::http::Method::POST, axum::http::Method::OPTIONS])
        .allow_headers([
            axum::http::header::CONTENT_TYPE,
            axum::http::header::HeaderName::from_static("x-pc-booster-token"),
        ]);

    let app = Router::new()
        .route("/health", get(health))
        .route("/request-access", post(request_access))
        .route("/diagnostics", get(diagnostics))
        .route("/tweaks", get(tweaks))
        .route("/tweaks/apply", post(apply_tweak))
        .route("/tweaks/rollback", post(rollback_tweak))
        .with_state(state)
        .layer(cors)
        .layer(middleware::from_fn(add_private_network_header));

    #[cfg(windows)]
    show_notice(
        "PC Booster Helper запущен.\n\nВернись на сайт PC Booster и нажми «Разрешить проверку ПК». Код подключения вводить не нужно.",
        "PC Booster Helper 0.2.0",
        windows_sys::Win32::UI::WindowsAndMessaging::MB_OK | windows_sys::Win32::UI::WindowsAndMessaging::MB_ICONINFORMATION,
    );

    let listener = tokio::net::TcpListener::bind(("127.0.0.1", PORT)).await
        .expect("Не удалось открыть локальный порт 47831");
    if let Err(error) = axum::serve(listener, app).await {
        eprintln!("Helper stopped: {error}");
    }
}

async fn add_private_network_header(request: Request<axum::body::Body>, next: Next) -> Response {
    let mut response = next.run(request).await;
    response.headers_mut().insert(
        "access-control-allow-private-network",
        HeaderValue::from_static("true"),
    );
    response
}

fn data_path() -> PathBuf {
    let base = std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(std::env::temp_dir);
    let dir = base.join("PCBooster");
    let _ = fs::create_dir_all(&dir);
    dir.join("rollback-journal.json")
}

fn authorized(headers: &HeaderMap, state: &AppState) -> bool {
    let supplied = match headers.get("x-pc-booster-token").and_then(|v| v.to_str().ok()) {
        Some(value) => value,
        None => return false,
    };
    let supplied_hash = Sha256::digest(supplied.as_bytes());
    supplied_hash.as_slice().ct_eq(state.token_hash.as_slice()).into()
}

#[cfg(windows)]
fn show_notice(message: &str, title: &str, flags: u32) -> i32 {
    use std::os::windows::ffi::OsStrExt;
    let message: Vec<u16> = std::ffi::OsStr::new(message).encode_wide().chain(Some(0)).collect();
    let title: Vec<u16> = std::ffi::OsStr::new(title).encode_wide().chain(Some(0)).collect();
    unsafe { windows_sys::Win32::UI::WindowsAndMessaging::MessageBoxW(std::ptr::null_mut(), message.as_ptr(), title.as_ptr(), flags) }
}

async fn request_access(State(state): State<AppState>, headers: HeaderMap) -> impl IntoResponse {
    let origin = headers.get("origin").and_then(|v| v.to_str().ok()).unwrap_or("");
    if origin != ALLOWED_ORIGIN {
        return (StatusCode::FORBIDDEN, Json(serde_json::json!({"error":"Разрешён только официальный сайт PC Booster."})));
    }
    #[cfg(windows)]
    {
        let approved = tokio::task::spawn_blocking(|| {
            use windows_sys::Win32::UI::WindowsAndMessaging::{IDYES, MB_DEFBUTTON2, MB_ICONQUESTION, MB_SETFOREGROUND, MB_YESNO};
            show_notice(
                "Сайт PC Booster запрашивает доступ к диагностике этого компьютера.\\n\\nБудут прочитаны характеристики Windows, процессор, память, диски и список работающих программ. Файлы и личные документы не открываются.\\n\\nРазрешить проверку?",
                "Разрешить доступ к ПК — PC Booster",
                MB_YESNO | MB_ICONQUESTION | MB_SETFOREGROUND | MB_DEFBUTTON2,
            ) == IDYES
        }).await.unwrap_or(false);
        if !approved {
            return (StatusCode::FORBIDDEN, Json(serde_json::json!({"error":"Проверка отменена пользователем."})));
        }
        return (StatusCode::OK, Json(serde_json::json!({"ok":true,"token":*state.pairing_token})));
    }
    #[cfg(not(windows))]
    {
        let _ = state;
        (StatusCode::NOT_IMPLEMENTED, Json(serde_json::json!({"error":"Helper доступен только для Windows."})))
    }
}

async fn health() -> impl IntoResponse {
    (StatusCode::OK, Json(serde_json::json!({
        "ok": true,
        "service": "pc-booster-helper",
        "engine": "rust",
        "version": "0.2.0"
    })))
}

async fn diagnostics(State(state): State<AppState>, headers: HeaderMap) -> impl IntoResponse {
    if !authorized(&headers, &state) {
        return (StatusCode::UNAUTHORIZED, Json(serde_json::json!({"error":"Invalid pairing token"})));
    }

    let mut system = System::new_all();
    system.refresh_all();
    let total_memory = system.total_memory();
    let used_memory = system.used_memory();
    let disks = Disks::new_with_refreshed_list();
    let disk_list: Vec<_> = disks.list().iter().map(|disk| serde_json::json!({
        "name": disk.name().to_string_lossy(),
        "mount": disk.mount_point().to_string_lossy(),
        "totalBytes": disk.total_space(),
        "freeBytes": disk.available_space(),
        "usedPercent": if disk.total_space()==0 {0.0} else {((disk.total_space()-disk.available_space()) as f64 / disk.total_space() as f64 * 100.0).round()}
    })).collect();
    let system_disk = disks.list().iter()
        .find(|disk| disk.mount_point().to_string_lossy().eq_ignore_ascii_case("C:\\"))
        .or_else(|| disks.list().iter().find(|disk| disk.mount_point().to_string_lossy().starts_with("C:")));
    let (total_disk, free_disk, disk_name) = system_disk
        .map(|disk| (disk.total_space(), disk.available_space(), disk.mount_point().to_string_lossy().to_string()))
        .unwrap_or((0, 0, "C:\\".to_string()));
    let mut top_processes: Vec<_> = system.processes().values().map(|p| serde_json::json!({
        "name": p.name().to_string(),
        "memoryBytes": p.memory(),
        "cpuPercent": (p.cpu_usage() * 10.0).round() / 10.0
    })).collect();
    top_processes.sort_by(|a,b| b["memoryBytes"].as_u64().cmp(&a["memoryBytes"].as_u64()));
    top_processes.truncate(8);
    let cpu_name = system.cpus().first().map(|cpu| cpu.brand().to_string()).unwrap_or_else(|| "Не определён".into());
    let cpu_usage = system.global_cpu_info().cpu_usage();

    (StatusCode::OK, Json(serde_json::json!({
        "machine": std::env::var("COMPUTERNAME").unwrap_or_else(|_| "Windows PC".into()),
        "os": System::long_os_version().unwrap_or_else(|| "Windows".into()),
        "cpu": { "name": cpu_name, "logicalProcessors": system.cpus().len(), "usedPercent": (cpu_usage * 10.0).round() / 10.0 },
        "memory": {
            "totalBytes": total_memory,
            "availableBytes": total_memory.saturating_sub(used_memory),
            "usedPercent": if total_memory == 0 { 0.0 } else { (used_memory as f64 / total_memory as f64 * 100.0).round() }
        },
        "systemDrive": {
            "name": disk_name,
            "totalBytes": total_disk,
            "freeBytes": free_disk,
            "usedPercent": if total_disk == 0 { 0.0 } else { ((total_disk - free_disk) as f64 / total_disk as f64 * 100.0).round() }
        },
        "disks": disk_list,
        "topProcesses": top_processes,
        "processCount": system.processes().len(),
        "collectedAt": chrono_now()
    })))
}

fn chrono_now() -> String {
    // Timestamp is informational; no external clock or service is used.
    format!("{:?}", std::time::SystemTime::now())
}

async fn tweaks(State(state): State<AppState>, headers: HeaderMap) -> impl IntoResponse {
    if !authorized(&headers, &state) {
        return (StatusCode::UNAUTHORIZED, Json(serde_json::json!({"error":"Invalid pairing token"})));
    }
    let journal = state.journal.lock().unwrap();
    let items = [
        ("show_file_extensions", "Показывать расширения файлов", "Проводник", "Показывает .exe, .txt и другие расширения."),
        ("show_hidden_files", "Показывать скрытые файлы", "Проводник", "Открывает отображение скрытых файлов и папок."),
        ("disable_transparency", "Отключить прозрачность", "Внешний вид", "Уменьшает визуальные эффекты интерфейса Windows."),
        ("performance_visual_effects", "Режим визуальных эффектов: производительность", "Производительность", "Выбирает настройку Windows «Наилучшее быстродействие». Внешний вид станет проще."),
        ("disable_game_dvr", "Отключить фоновую запись игр", "Игры", "Отключает захват игровых клипов в текущем профиле пользователя."),
        ("disable_mouse_acceleration", "Отключить ускорение мыши", "Управление", "Делает движение курсора более предсказуемым; исходные параметры сохраняются."),
        ("faster_menu_delay", "Ускорить открытие меню", "Интерфейс", "Уменьшает задержку открытия меню Windows; исходное значение сохраняется.")
    ];
    let result: Vec<_> = items.iter().map(|(id, name, category, description)| serde_json::json!({
        "id": id,
        "name": name,
        "category": category,
        "description": description,
        "applied": journal.applied.contains_key(*id),
        "reversible": true
    })).collect();
    (StatusCode::OK, Json(serde_json::json!({"items": result})))
}

fn tweak_settings(id: &str) -> Option<Vec<(&'static str, &'static str, &'static str, &'static str)>> {
    let explorer = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced";
    let personalize = "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize";
    let visual = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\VisualEffects";
    let game_store = "System\\GameConfigStore";
    let game_dvr = "Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR";
    let mouse = "Control Panel\\Mouse";
    let desktop = "Control Panel\\Desktop";
    match id {
        "show_file_extensions" => Some(vec![(explorer, "HideFileExt", "dword", "0")]),
        "show_hidden_files" => Some(vec![(explorer, "Hidden", "dword", "1")]),
        "disable_transparency" => Some(vec![(personalize, "EnableTransparency", "dword", "0")]),
        "performance_visual_effects" => Some(vec![(visual, "VisualFXSetting", "dword", "2")]),
        "disable_game_dvr" => Some(vec![
            (game_store, "GameDVR_Enabled", "dword", "0"),
            (game_dvr, "AppCaptureEnabled", "dword", "0")
        ]),
        "disable_mouse_acceleration" => Some(vec![
            (mouse, "MouseSpeed", "string", "0"),
            (mouse, "MouseThreshold1", "string", "0"),
            (mouse, "MouseThreshold2", "string", "0")
        ]),
        "faster_menu_delay" => Some(vec![(desktop, "MenuShowDelay", "string", "100")]),
        _ => None,
    }
}

fn read_original(key_path: &str, name: &str, kind: &str) -> OriginalValue {
    let root = RegKey::predef(HKEY_CURRENT_USER);
    let value = root.open_subkey_with_flags(key_path, KEY_READ).ok();
    let (existed, dword, string) = match (value, kind) {
        (Some(key), "dword") => match key.get_value::<u32, _>(name) {
            Ok(v) => (true, Some(v), None),
            Err(_) => (false, None, None),
        },
        (Some(key), "string") => match key.get_value::<String, _>(name) {
            Ok(v) => (true, None, Some(v)),
            Err(_) => (false, None, None),
        },
        _ => (false, None, None),
    };
    OriginalValue { key: key_path.into(), name: name.into(), kind: kind.into(), existed, dword, string }
}

fn write_setting(key_path: &str, name: &str, kind: &str, value: &str) -> Result<(), String> {
    let root = RegKey::predef(HKEY_CURRENT_USER);
    let (key, _) = root.create_subkey_with_flags(key_path, KEY_READ | KEY_WRITE)
        .map_err(|e| format!("Не удалось открыть раздел реестра: {e}"))?;
    match kind {
        "dword" => {
            let parsed = value.parse::<u32>().map_err(|_| "Некорректное значение DWORD".to_string())?;
            key.set_value(name, &parsed).map_err(|e| format!("Не удалось изменить параметр: {e}"))
        }
        "string" => key.set_value(name, &value.to_string()).map_err(|e| format!("Не удалось изменить параметр: {e}")),
        _ => Err("Тип параметра не разрешён".into()),
    }
}

fn restore_value(value: &OriginalValue) -> Result<(), String> {
    let root = RegKey::predef(HKEY_CURRENT_USER);
    let key = root.create_subkey_with_flags(&value.key, KEY_READ | KEY_WRITE)
        .map_err(|e| format!("Не удалось открыть раздел для отката: {e}"))?.0;
    if !value.existed {
        match key.delete_value(&value.name) {
            Ok(()) => Ok(()),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
            Err(e) => Err(format!("Не удалось удалить новый параметр: {e}")),
        }
    } else {
        match value.kind.as_str() {
            "dword" => key.set_value(&value.name, &value.dword.ok_or_else(|| "Нет сохранённого DWORD".to_string())?)
                .map_err(|e| format!("Не удалось восстановить DWORD: {e}")),
            "string" => {
                let restored = value.string.as_ref()
                    .ok_or_else(|| "Нет сохранённой строки".to_string())?
                    .clone();
                key.set_value(&value.name, &restored)
                    .map_err(|e| format!("Не удалось восстановить строку: {e}"))
            },
            _ => Err("Неизвестный тип сохранённого параметра".into()),
        }
    }
}

fn save_journal(state: &AppState, journal: &Journal) -> Result<(), String> {
    let bytes = serde_json::to_vec_pretty(journal).map_err(|e| e.to_string())?;
    fs::write(state.journal_path.as_path(), bytes)
        .map_err(|e| format!("Не удалось сохранить журнал: {e}"))
}

async fn apply_tweak(State(state): State<AppState>, headers: HeaderMap, Json(body): Json<ApplyRequest>) -> impl IntoResponse {
    if !authorized(&headers, &state) {
        return (StatusCode::UNAUTHORIZED, Json(serde_json::json!({"error":"Invalid pairing token"})));
    }
    let settings = match tweak_settings(&body.id) {
        Some(settings) => settings,
        None => return (StatusCode::BAD_REQUEST, Json(serde_json::json!({"error":"Этот твик отсутствует в разрешённом списке"}))),
    };
    let mut journal = state.journal.lock().unwrap();
    if journal.applied.contains_key(&body.id) {
        return (StatusCode::CONFLICT, Json(serde_json::json!({"error":"Твик уже применён. Сначала выполни откат."})));
    }
    let originals: Vec<_> = settings.iter().map(|(key, name, kind, _)| read_original(key, name, kind)).collect();
    for (key, name, kind, value) in &settings {
        if let Err(error) = write_setting(key, name, kind, value) {
            for original in &originals { let _ = restore_value(original); }
            return (StatusCode::INTERNAL_SERVER_ERROR, Json(serde_json::json!({"error":error})));
        }
    }
    journal.applied.insert(body.id.clone(), originals);
    if let Err(error) = save_journal(&state, &journal) {
        if let Some(originals) = journal.applied.remove(&body.id) {
            for original in &originals { let _ = restore_value(original); }
        }
        return (StatusCode::INTERNAL_SERVER_ERROR, Json(serde_json::json!({"error":error})));
    }
    (StatusCode::OK, Json(serde_json::json!({"ok":true,"id":body.id,"message":"Твик применён. Исходные значения сохранены для отката."})))
}

async fn rollback_tweak(State(state): State<AppState>, headers: HeaderMap, Json(body): Json<RollbackRequest>) -> impl IntoResponse {
    if !authorized(&headers, &state) {
        return (StatusCode::UNAUTHORIZED, Json(serde_json::json!({"error":"Invalid pairing token"})));
    }
    let mut journal = state.journal.lock().unwrap();
    let originals = match journal.applied.get(&body.id).cloned() {
        Some(values) => values,
        None => return (StatusCode::NOT_FOUND, Json(serde_json::json!({"error":"Для этого твика нет сохранённого изменения"}))),
    };
    for original in &originals {
        if let Err(error) = restore_value(original) {
            return (StatusCode::INTERNAL_SERVER_ERROR, Json(serde_json::json!({"error":error})));
        }
    }
    journal.applied.remove(&body.id);
    if let Err(error) = save_journal(&state, &journal) {
        return (StatusCode::INTERNAL_SERVER_ERROR, Json(serde_json::json!({"error":error})));
    }
    (StatusCode::OK, Json(serde_json::json!({"ok":true,"id":body.id,"message":"Исходные настройки восстановлены."})))
}


#[cfg(test)]
mod tests {
    use super::tweak_settings;

    #[test]
    fn only_known_tweaks_are_executable() {
        for id in [
            "show_file_extensions",
            "show_hidden_files",
            "disable_transparency",
            "performance_visual_effects",
            "disable_game_dvr",
            "disable_mouse_acceleration",
            "faster_menu_delay",
        ] {
            assert!(tweak_settings(id).is_some(), "missing allowlisted tweak: {id}");
        }
        assert!(tweak_settings("run_powershell").is_none());
        assert!(tweak_settings("arbitrary_registry_write").is_none());
    }
}
