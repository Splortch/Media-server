#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
use media_client_core::{ClientError, DesktopClient, WindowsVault};
use serde_json::Value;
use std::sync::Arc;
use tauri::Manager;
#[tauri::command]
async fn connect(
    address: String,
    remember: bool,
    client: tauri::State<'_, DesktopClient>,
) -> Result<Value, ClientError> {
    client.connect(&address, remember).await
}
#[tauri::command]
async fn login(
    name: String,
    password: String,
    client: tauri::State<'_, DesktopClient>,
) -> Result<Value, ClientError> {
    client.login(name, password).await
}
#[tauri::command]
async fn logout(client: tauri::State<'_, DesktopClient>) -> Result<Value, ClientError> {
    client.logout().await
}
#[tauri::command]
async fn api_request(
    method: String,
    path: String,
    body: Option<Value>,
    client: tauri::State<'_, DesktopClient>,
) -> Result<Value, ClientError> {
    client.request(&method, &path, body).await
}
#[tauri::command]
async fn stream_url(
    id: u64,
    client: tauri::State<'_, DesktopClient>,
) -> Result<String, ClientError> {
    client.stream_url(id).await
}
#[tauri::command]
fn preferences(app: tauri::AppHandle) -> Result<Value, String> {
    let p = app
        .path()
        .app_config_dir()
        .map_err(|_| "Preferences unavailable")?
        .join("connection.json");
    match std::fs::read(p) {
        Ok(bytes) if bytes.len() < 4096 => {
            serde_json::from_slice(&bytes).map_err(|_| "Invalid preferences".into())
        }
        _ => Ok(serde_json::json!({})),
    }
}
#[tauri::command]
fn save_preferences(address: String, theme: String, app: tauri::AppHandle) -> Result<(), String> {
    if !address.is_empty() {
        media_client_core::validate_server(&address).map_err(|e| e.message.to_owned())?;
    }
    if !matches!(theme.as_str(), "system" | "dark" | "light") {
        return Err("Invalid theme".into());
    }
    let directory = app
        .path()
        .app_config_dir()
        .map_err(|_| "Preferences unavailable")?;
    std::fs::create_dir_all(&directory).map_err(|_| "Cannot save preferences")?;
    std::fs::write(
        directory.join("connection.json"),
        serde_json::to_vec(&serde_json::json!({"address":address,"theme":theme})).unwrap(),
    )
    .map_err(|_| "Cannot save preferences".into())
}
fn main() {
    tauri::Builder::default()
        .setup(|app| {
            let client =
                tauri::async_runtime::block_on(DesktopClient::start(Arc::new(WindowsVault)))
                    .map_err(|_| "Cannot initialize the local playback bridge")?;
            app.manage(client);
            Ok(())
        })
        .on_page_load(|webview, _| {
            let url = webview.url();
            if let Ok(url) = url {
                let allowed = url.scheme() == "tauri"
                    || (url.scheme() == "http" && url.host_str() == Some("tauri.localhost"))
                    || (cfg!(debug_assertions)
                        && url.as_str().starts_with("http://127.0.0.1:1420/"));
                if !allowed {
                    let _ = webview.close();
                }
            }
        })
        .invoke_handler(tauri::generate_handler![
            connect,
            login,
            logout,
            api_request,
            stream_url,
            preferences,
            save_preferences
        ])
        .run(tauri::generate_context!())
        .expect("Desktop application failed to start");
}
