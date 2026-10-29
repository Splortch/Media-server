use media_client_core::{ClientError, CredentialStore, DesktopClient};
use serde_json::{json, Value};
use std::{
    collections::HashMap,
    sync::{Arc, Mutex},
    time::Duration,
};
#[derive(Default)]
struct MemoryVault(Mutex<HashMap<String, String>>);
impl CredentialStore for MemoryVault {
    fn load(&self, key: &str) -> Result<Option<String>, ClientError> {
        Ok(self.0.lock().unwrap().get(key).cloned())
    }
    fn save(&self, key: &str, value: &str) -> Result<(), ClientError> {
        self.0.lock().unwrap().insert(key.into(), value.into());
        Ok(())
    }
    fn delete(&self, key: &str) -> Result<(), ClientError> {
        self.0.lock().unwrap().remove(key);
        Ok(())
    }
}
async fn write(http: &reqwest::Client, base: &str, path: &str, body: Value) -> Value {
    let csrf: Value = http
        .get(format!("{base}/api/auth/csrf"))
        .send()
        .await
        .unwrap()
        .json()
        .await
        .unwrap();
    let response = http
        .post(format!("{base}{path}"))
        .header("X-CSRF-TOKEN", csrf["token"].as_str().unwrap())
        .json(&body)
        .send()
        .await
        .unwrap();
    assert!(
        response.status().is_success(),
        "fixture request failed: {}",
        response.status()
    );
    response.json().await.unwrap()
}
#[tokio::test]
#[ignore = "requires fresh real backend; run scripts/test-native-client.py"]
async fn real_server_cookie_login_ranges_resume_conflicts_revocation_and_disconnect() {
    let base = std::env::var("MEDIA_CLIENT_TEST_SERVER").expect("server URL");
    let token = std::env::var("MEDIA_CLIENT_TEST_TOKEN").expect("fixture setup token");
    let root = std::env::var("MEDIA_CLIENT_TEST_ROOT").expect("fixture media");
    let http = reqwest::Client::builder()
        .cookie_store(true)
        .build()
        .unwrap();
    write(
        &http,
        &base,
        "/api/setup",
        json!({"name":"desktop-test","password":"native-test-only-password","token":token}),
    )
    .await;
    write(
        &http,
        &base,
        "/api/auth/login",
        json!({"name":"desktop-test","password":"native-test-only-password"}),
    )
    .await;
    let library = write(
        &http,
        &base,
        "/api/libraries",
        json!({"name":"Synthetic videos","path":root}),
    )
    .await;
    let job = write(
        &http,
        &base,
        &format!("/api/libraries/{}/scan", library["id"]),
        json!(null),
    )
    .await;
    let mut complete = false;
    for _ in 0..100 {
        let status: Value = http
            .get(format!("{base}/api/scans/{}", job["id"]))
            .send()
            .await
            .unwrap()
            .json()
            .await
            .unwrap();
        if status["state"] == "completed" {
            complete = true;
            break;
        }
        tokio::time::sleep(Duration::from_millis(200)).await;
    }
    assert!(complete);
    let vault = Arc::new(MemoryVault::default());
    let first = DesktopClient::start(vault.clone()).await.unwrap();
    first.connect(&base, true).await.unwrap();
    first
        .login("desktop-test".into(), "native-test-only-password".into())
        .await
        .unwrap();
    let list = first
        .request("GET", "/api/v1/media?search=sample", None)
        .await
        .unwrap();
    assert_eq!(list.as_array().unwrap().len(), 1);
    let id = list[0]["id"].as_u64().unwrap();
    let source = first.stream_url(id).await.unwrap();
    let range = http
        .get(&source)
        .header("Range", "bytes=0-15")
        .send()
        .await
        .unwrap();
    assert_eq!(range.status(), 206);
    assert_eq!(range.bytes().await.unwrap().len(), 16);
    let mut bad = url::Url::parse(&source).unwrap();
    bad.set_path(&format!("/wrong/media/{id}"));
    assert_eq!(http.get(bad).send().await.unwrap().status(), 404);
    let options = first
        .request("GET", &format!("/api/v1/media/{id}/playback-options"), None)
        .await
        .unwrap();
    assert!(options["encoders"]
        .as_array()
        .unwrap()
        .iter()
        .any(|e| e == "libx264"));
    let mut conversion = first
        .request(
            "POST",
            &format!("/api/v1/media/{id}/playback-sessions"),
            Some(json!({"encoder":"libx264"})),
        )
        .await
        .unwrap();
    let conversion_id = conversion["id"].as_str().unwrap().to_owned();
    for _ in 0..200 {
        if conversion["state"] != "preparing" {
            break;
        }
        tokio::time::sleep(Duration::from_millis(200)).await;
        conversion = first
            .request(
                "GET",
                &format!("/api/v1/playback-sessions/{conversion_id}"),
                None,
            )
            .await
            .unwrap();
    }
    assert_eq!(conversion["state"], "ready");
    let manifest_url = first
        .playback_url(&conversion_id, "master.m3u8")
        .await
        .unwrap();
    let public_http = reqwest::Client::new();
    let manifest = public_http
        .get(&manifest_url)
        .header("Origin", "http://tauri.localhost")
        .send()
        .await
        .unwrap();
    assert_eq!(manifest.status(), 200);
    assert_eq!(
        manifest.headers()["access-control-allow-origin"],
        "http://tauri.localhost"
    );
    assert!(!manifest.headers().contains_key("set-cookie"));
    assert!(manifest.text().await.unwrap().contains("720p.m3u8"));
    assert_eq!(
        public_http
            .get(&manifest_url)
            .header("Origin", "https://evil.invalid")
            .send()
            .await
            .unwrap()
            .status(),
        404
    );
    let head = public_http
        .head(&manifest_url)
        .header("Origin", "http://tauri.localhost")
        .send()
        .await
        .unwrap();
    assert_eq!(head.status(), 200);
    assert!(
        head.headers()["content-length"]
            .to_str()
            .unwrap()
            .parse::<u64>()
            .unwrap()
            > 0
    );
    let playlist = public_http
        .get(
            first
                .playback_url(&conversion_id, "360p.m3u8")
                .await
                .unwrap(),
        )
        .send()
        .await
        .unwrap()
        .text()
        .await
        .unwrap();
    let segment = playlist.lines().find(|s| s.ends_with(".ts")).unwrap();
    let bytes = public_http
        .get(first.playback_url(&conversion_id, segment).await.unwrap())
        .header("Range", "bytes=0-187")
        .header("Origin", "http://tauri.localhost")
        .send()
        .await
        .unwrap();
    assert_eq!(bytes.status(), 206);
    assert_eq!(bytes.bytes().await.unwrap()[0], 0x47);
    assert!(first
        .playback_url(&conversion_id, "../source.bin")
        .await
        .is_err());
    let second = DesktopClient::start(vault).await.unwrap();
    second.connect(&base, true).await.unwrap();
    assert_eq!(
        second
            .request("GET", "/api/v1/auth/me", None)
            .await
            .unwrap()["name"],
        "desktop-test"
    );
    let path = format!("/api/v1/media/{id}/progress-sync");
    let result = first
        .request(
            "PUT",
            &path,
            Some(json!({"seconds":3,"expectedRevision":0})),
        )
        .await
        .unwrap();
    assert_eq!(result["revision"], 1);
    let conflict = second
        .request(
            "PUT",
            &path,
            Some(json!({"seconds":1,"expectedRevision":0})),
        )
        .await
        .unwrap_err();
    assert_eq!(conflict.status, Some(409));
    let progress = second
        .request("GET", &format!("/api/v1/media/{id}/progress"), None)
        .await
        .unwrap();
    assert_eq!(progress["seconds"], 3.0);
    second
        .login("desktop-test".into(), "native-test-only-password".into())
        .await
        .unwrap();
    let sessions = first
        .request("GET", "/api/v1/sessions", None)
        .await
        .unwrap();
    let other = sessions
        .as_array()
        .unwrap()
        .iter()
        .find(|s| {
            s["deviceName"] == format!("{} desktop client", std::env::consts::OS)
                && s["current"] == false
        })
        .unwrap();
    first
        .request(
            "DELETE",
            &format!("/api/v1/sessions/{}", other["id"].as_str().unwrap()),
            None,
        )
        .await
        .unwrap();
    assert_eq!(
        second
            .request("GET", "/api/v1/auth/me", None)
            .await
            .unwrap_err()
            .status,
        Some(401)
    );
    first.logout().await.unwrap();
    assert_eq!(http.get(&source).send().await.unwrap().status(), 404);
    assert_eq!(
        public_http
            .get(&manifest_url)
            .send()
            .await
            .unwrap()
            .status(),
        404
    );
    assert_eq!(
        first
            .request("GET", "/api/v1/media", None)
            .await
            .unwrap_err()
            .status,
        Some(401)
    );
    let offline = DesktopClient::start(Arc::new(MemoryVault::default()))
        .await
        .unwrap();
    assert_eq!(
        offline
            .connect("http://127.0.0.1:1", false)
            .await
            .unwrap_err()
            .code,
        "NETWORK"
    );
}
#[cfg(windows)]
#[test]
fn windows_credential_manager_roundtrip() {
    use media_client_core::WindowsVault;
    let key = format!("https://vault-test-{}.invalid/", uuid::Uuid::new_v4());
    let store = WindowsVault;
    store.save(&key, "test-only-cookie").unwrap();
    assert_eq!(
        store.load(&key).unwrap().as_deref(),
        Some("test-only-cookie")
    );
    store.delete(&key).unwrap();
    assert_eq!(store.load(&key).unwrap(), None);
}
