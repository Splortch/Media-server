fn main() {
    tauri_build::try_build(tauri_build::Attributes::new().app_manifest(
        tauri_build::AppManifest::new().commands(&[
            "connect",
            "login",
            "logout",
            "api_request",
            "stream_url",
            "preferences",
            "save_preferences",
        ]),
    ))
    .expect("Tauri build configuration failed");
}
