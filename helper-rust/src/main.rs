#![cfg_attr(not(windows), allow(dead_code))]

use axum::{
    extract::{State, Json},
    http::{HeaderMap, StatusCode},
    response::IntoResponse,
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
        .route("/diagnostics", get(diagnostics))
        .route("/tweaks", get(tweaks))
        .route("/tweaks/apply", post(apply_tweak))
        .route("/tweaks/rollback", post(rollback_tweak))
        .with_state(state)
        .layer(cors);

    println!("PC Booster Helper 0.2.0 — Rust system engine");
    println!("Локальный адрес: http://127.0.0.1:{PORT}/");
    println!("Код подключения (не отправляй его никому):");
    println!("{token}");
    println!("Доступны диагностика и ограниченный список обратимых твиков.");
    println!("Для остановки нажми Ctrl+C.");

    let listener = tokio::net::TcpListener::bind(("127.0.0.1", PORT)).await
        .expect("Не удалось открыть локальный порт 47831");
    if let Err(error) = axum::serve(listener, app).await {
        eprintln!("Helper stopped: {error}");
    }
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
    let system_disk = disks.list().iter()
        .find(|disk| disk.mount_point().to_string_lossy().eq_ignore_ascii_case("C:\\"))
        .or_else(|| disks.list().iter().find(|disk| disk.mount_point().to_string_lossy().starts_with("C:")));

    let (total_disk, free_disk, disk_name) = system_disk
        .map(|disk| (disk.total_space(), disk.available_space(), disk.mount_point().to_string_lossy().to_string()))
        .unwrap_or((0, 0, "C:\\".to_string()));

    (StatusCode::OK, Json(serde_json::json!({
        "machine": std::env::var("COMPUTERNAME").unwrap_or_else(|_| "Windows PC".into()),
        "os": System::long_os_version().unwrap_or_else(|| "Windows".into()),
        "cpu": { "logicalProcessors": system.cpus().len() },
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
        ("disable_game_dvr", "Отключить фоновую запись игр", "Игры", "Отключает захват игровых клипов в текущем профиле пользователя.")
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
    match id {
        "show_file_extensions" => Some(vec![(explorer, "HideFileExt", "dword", "0")]),
        "show_hidden_files" => Some(vec![(explorer, "Hidden", "dword", "1")]),
        "disable_transparency" => Some(vec![(personalize, "EnableTransparency", "dword", "0")]),
        "performance_visual_effects" => Some(vec![(visual, "VisualFXSetting", "dword", "2")]),
        "disable_game_dvr" => Some(vec![
            (game_store, "GameDVR_Enabled", "dword", "0"),
            (game_dvr, "AppCaptureEnabled", "dword", "0")
        ]),
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
            "dword" => key.set_value(&value.name, &value.dword.ok_or("Нет сохранённого DWORD")?)
                .map_err(|e| format!("Не удалось восстановить DWORD: {e}")),
            "string" => key.set_value(&value.name, &value.string.as_ref().ok_or("Нет сохранённой строки")?)
                .map_err(|e| format!("Не удалось восстановить строку: {e}")),
            _ => Err("Неизвестный тип сохранённого параметра".into()),
        }
    }
}

fn save_journal(state: &AppState, journal: &Journal) -> Result<(), String> {
    let bytes = serde_json::to_vec_pretty(journal).map_err(|e| e.to_string())?;
    let temp = state.journal_path.with_extension("tmp");
    fs::write(&temp, bytes).map_err(|e| format!("Не удалось сохранить журнал: {e}"))?;
    fs::rename(&temp, state.journal_path.as_path()).map_err(|e| format!("Не удалось обновить журнал: {e}"))
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
