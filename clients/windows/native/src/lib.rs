use axum::{
    body::Body,
    extract::{Path, State},
    http::{HeaderMap, Method, StatusCode},
    response::Response,
    routing::get,
    Router,
};
use futures_util::{StreamExt, TryStreamExt};
use reqwest::cookie::{CookieStore, Jar};
use serde::Serialize;
use serde_json::{json, Value};
#[cfg(any(windows, test))]
use sha2::{Digest, Sha256};
use std::{sync::Arc, time::Duration};
use tokio::sync::Mutex;
use tokio_util::sync::CancellationToken;
use url::Url;
use zeroize::Zeroize;

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ClientError {
    pub code: &'static str,
    pub message: &'static str,
    pub status: Option<u16>,
    pub current: Option<Value>,
}
impl ClientError {
    fn new(code: &'static str, message: &'static str) -> Self {
        Self {
            code,
            message,
            status: None,
            current: None,
        }
    }
}
type Result<T> = std::result::Result<T, ClientError>;
pub trait CredentialStore: Send + Sync {
    fn load(&self, server: &str) -> Result<Option<String>>;
    fn save(&self, server: &str, cookies: &str) -> Result<()>;
    fn delete(&self, server: &str) -> Result<()>;
}
pub struct WindowsVault;
#[cfg(any(windows, test))]
fn credential_key(server: &str) -> String {
    // Format bytes, not the digest array: sha2 0.11 no longer implements LowerHex
    // on its output type. Keep the key identical so existing saved sessions survive.
    Sha256::digest(server.as_bytes())
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect()
}
#[cfg(windows)]
fn entry(server: &str) -> Result<keyring::Entry> {
    let key = credential_key(server);
    keyring::Entry::new("org.splortch.media-server.windows", &key)
        .map_err(|_| ClientError::new("VAULT", "Windows Credential Manager is unavailable."))
}
impl CredentialStore for WindowsVault {
    fn load(&self, server: &str) -> Result<Option<String>> {
        #[cfg(windows)]
        {
            match entry(server)?.get_password() {
                Ok(s) => Ok(Some(s)),
                Err(keyring::Error::NoEntry) => Ok(None),
                Err(_) => Err(ClientError::new(
                    "VAULT",
                    "Saved session cannot be read from Windows Credential Manager.",
                )),
            }
        }
        #[cfg(not(windows))]
        {
            let _ = server;
            Ok(None)
        }
    }
    fn save(&self, server: &str, cookies: &str) -> Result<()> {
        #[cfg(windows)]
        {
            entry(server)?.set_password(cookies).map_err(|_| {
                ClientError::new(
                    "VAULT",
                    "Session could not be protected by Windows Credential Manager.",
                )
            })
        }
        #[cfg(not(windows))]
        {
            let _ = (server, cookies);
            Err(ClientError::new(
                "VAULT",
                "Persistent sessions are supported only on Windows.",
            ))
        }
    }
    fn delete(&self, server: &str) -> Result<()> {
        #[cfg(windows)]
        {
            match entry(server)?.delete_credential() {
                Ok(()) | Err(keyring::Error::NoEntry) => Ok(()),
                Err(_) => Err(ClientError::new(
                    "VAULT",
                    "Saved credentials could not be removed.",
                )),
            }
        }
        #[cfg(not(windows))]
        {
            let _ = server;
            Ok(())
        }
    }
}
pub fn validate_server(input: &str) -> Result<Url> {
    if input.len() > 2048 {
        return Err(ClientError::new("URL", "Server address is too long."));
    }
    let mut url = Url::parse(input.trim())
        .map_err(|_| ClientError::new("URL", "Enter an absolute HTTPS server address."))?;
    if url.host_str().is_none()
        || !url.username().is_empty()
        || url.password().is_some()
        || url.query().is_some()
        || url.fragment().is_some()
        || url.path() != "/"
    {
        return Err(ClientError::new(
            "URL",
            "Use a server origin without credentials, path, query or fragment.",
        ));
    }
    let local = match url.host() {
        Some(url::Host::Domain("localhost")) => true,
        Some(url::Host::Ipv4(ip)) => ip.is_loopback(),
        Some(url::Host::Ipv6(ip)) => ip.is_loopback(),
        _ => false,
    };
    if url.scheme() != "https" && !(url.scheme() == "http" && local) {
        return Err(ClientError::new("INSECURE","Remote servers require HTTPS. HTTP is allowed only on this computer's loopback address."));
    }
    url.set_path("/");
    Ok(url)
}
#[derive(Clone)]
struct Connection {
    base: Url,
    jar: Arc<Jar>,
    http: reqwest::Client,
    remember: bool,
    cancel: CancellationToken,
    proxy_secret: Arc<str>,
}
impl Connection {
    fn new(base: Url, remember: bool) -> Result<Self> {
        let jar = Arc::new(Jar::default());
        let http = reqwest::Client::builder()
            .cookie_provider(jar.clone())
            .redirect(reqwest::redirect::Policy::none())
            .connect_timeout(Duration::from_secs(10))
            .read_timeout(Duration::from_secs(45))
            .no_proxy()
            .build()
            .map_err(|_| ClientError::new("NETWORK", "Cannot initialize secure networking."))?;
        Ok(Self {
            base,
            jar,
            http,
            remember,
            cancel: CancellationToken::new(),
            proxy_secret: uuid::Uuid::new_v4().simple().to_string().into(),
        })
    }
    async fn json(&self, method: Method, path: &str, body: Option<Value>) -> Result<Value> {
        let url = self
            .base
            .join(path)
            .map_err(|_| ClientError::new("URL", "Invalid API path."))?;
        let mut request = self
            .http
            .request(method.clone(), url)
            .timeout(Duration::from_secs(15));
        if method != Method::GET && method != Method::HEAD {
            let response = self
                .http
                .get(self.base.join("/api/v1/auth/csrf").unwrap())
                .timeout(Duration::from_secs(15))
                .send()
                .await
                .map_err(|_| {
                    ClientError::new(
                        "NETWORK",
                        "Server unavailable while establishing a secure session.",
                    )
                })?;
            let token = bounded_json(response)
                .await?
                .get("token")
                .and_then(Value::as_str)
                .filter(|s| s.len() < 8192)
                .ok_or_else(|| ClientError::new("PROTOCOL", "Invalid antiforgery response."))?
                .to_owned();
            request = request.header("X-CSRF-TOKEN", token);
        }
        if let Some(body) = body {
            request = request.json(&body);
        }
        let response = request.send().await.map_err(|_| {
            ClientError::new(
                "NETWORK",
                "Cannot reach the server. Check the connection and TLS certificate.",
            )
        })?;
        bounded_json(response).await
    }
}
async fn bounded_json(response: reqwest::Response) -> Result<Value> {
    let status = response.status();
    if response
        .content_length()
        .is_some_and(|n| n > 2 * 1024 * 1024)
    {
        return Err(ClientError::new(
            "PROTOCOL",
            "Server response exceeded the client limit.",
        ));
    }
    let mut bytes = Vec::new();
    let mut stream = response.bytes_stream();
    while let Some(chunk) = stream.next().await {
        let chunk =
            chunk.map_err(|_| ClientError::new("NETWORK", "Server response was interrupted."))?;
        if bytes.len() + chunk.len() > 2 * 1024 * 1024 {
            return Err(ClientError::new(
                "PROTOCOL",
                "Server response exceeded the client limit.",
            ));
        }
        bytes.extend_from_slice(&chunk);
    }
    let value = if bytes.is_empty() {
        Value::Null
    } else {
        serde_json::from_slice(&bytes).map_err(|_| {
            ClientError::new("PROTOCOL", "Server returned an invalid JSON response.")
        })?
    };
    if status.is_success() {
        return Ok(value);
    }
    let (code, message) = match status.as_u16() {
        401 => ("AUTH", "Your session expired or sign-in was rejected."),
        403 => ("FORBIDDEN", "This account does not have permission."),
        409 => (
            "CONFLICT",
            "Progress changed on another device. Reopen this video before saving again.",
        ),
        429 => (
            "RATE_LIMIT",
            "Too many requests. Wait a minute before trying again.",
        ),
        _ => ("SERVER", "The server could not complete the request."),
    };
    Err(ClientError {
        code,
        message,
        status: Some(status.as_u16()),
        current: if status.as_u16() == 409 {
            Some(value)
        } else {
            None
        },
    })
}
#[derive(Clone)]
pub struct DesktopClient {
    connection: Arc<Mutex<Option<Connection>>>,
    vault: Arc<dyn CredentialStore>,
    proxy: String,
}
impl DesktopClient {
    pub async fn start(vault: Arc<dyn CredentialStore>) -> Result<Self> {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0")
            .await
            .map_err(|_| ClientError::new("PROXY", "Cannot start the local video bridge."))?;
        let proxy = format!(
            "http://127.0.0.1:{}",
            listener
                .local_addr()
                .map_err(|_| ClientError::new("PROXY", "No local bridge address."))?
                .port()
        );
        let client = Self {
            connection: Arc::new(Mutex::new(None)),
            vault,
            proxy,
        };
        let router = Router::new()
            .route("/{secret}/media/{id}", get(stream_media))
            .route(
                "/{secret}/playback/{session}/files/{file}",
                get(stream_playback),
            )
            .with_state(client.clone());
        tokio::spawn(async move {
            let _ = axum::serve(listener, router).await;
        });
        Ok(client)
    }
    pub async fn connect(&self, address: &str, remember: bool) -> Result<Value> {
        let base = validate_server(address)?;
        let connection = Connection::new(base, remember)?;
        let info = connection
            .json(Method::GET, "/api/v1/server-info", None)
            .await
            .map_err(|e| {
                if matches!(e.status, Some(401 | 404)) {
                    ClientError::new(
                        "INCOMPATIBLE",
                        "This server does not offer client API v1. Update it before connecting.",
                    )
                } else {
                    e
                }
            })?;
        if info.get("apiVersion").and_then(Value::as_u64) != Some(1)
            || info
                .get("minimumClientApiVersion")
                .and_then(Value::as_u64)
                .is_none_or(|n| n > 1)
        {
            return Err(ClientError::new(
                "INCOMPATIBLE",
                "This server uses an incompatible client API. Update the server or client.",
            ));
        }
        if remember {
            if let Some(mut cookies) = self.vault.load(connection.base.as_str())? {
                for cookie in cookies.split(';') {
                    let flags = if connection.base.scheme() == "https" {
                        "; Secure; HttpOnly; Path=/"
                    } else {
                        "; HttpOnly; Path=/"
                    };
                    connection
                        .jar
                        .add_cookie_str(&format!("{}{flags}", cookie.trim()), &connection.base);
                }
                cookies.zeroize();
            }
        }
        if let Some(old) = self.connection.lock().await.replace(connection) {
            old.cancel.cancel();
        }
        Ok(info)
    }
    async fn current(&self) -> Result<Connection> {
        self.connection
            .lock()
            .await
            .clone()
            .ok_or_else(|| ClientError::new("DISCONNECTED", "Connect to a server first."))
    }
    pub async fn login(&self, name: String, mut password: String) -> Result<Value> {
        let c = self.current().await?;
        let result = c
            .json(
                Method::POST,
                "/api/v1/auth/login",
                Some(json!({"name":name,"password":password,"deviceName":format!("{} desktop client", std::env::consts::OS)})),
            )
            .await;
        password.zeroize();
        let result = result?;
        if c.remember {
            let mut cookies = c
                .jar
                .cookies(&c.base)
                .and_then(|h| h.to_str().ok().map(str::to_owned))
                .ok_or_else(|| {
                    ClientError::new("PROTOCOL", "Server did not establish a session.")
                })?;
            let save = self.vault.save(c.base.as_str(), &cookies);
            cookies.zeroize();
            if let Err(e) = save {
                let _ = c.json(Method::POST, "/api/v1/auth/logout", None).await;
                self.clear(&c).await?;
                return Err(e);
            }
        }
        let mut active = self.connection.lock().await;
        if active
            .as_ref()
            .is_some_and(|current| current.proxy_secret == c.proxy_secret)
        {
            let mut authenticated = c.clone();
            c.cancel.cancel();
            authenticated.cancel = CancellationToken::new();
            authenticated.proxy_secret = uuid::Uuid::new_v4().simple().to_string().into();
            *active = Some(authenticated);
        }
        Ok(result)
    }
    async fn clear(&self, c: &Connection) -> Result<()> {
        let mut active = self.connection.lock().await;
        if !active
            .as_ref()
            .is_some_and(|current| current.proxy_secret == c.proxy_secret)
        {
            return Ok(());
        }
        c.cancel.cancel();
        let deletion = self.vault.delete(c.base.as_str());
        let replacement = Connection::new(c.base.clone(), c.remember)?;
        *active = Some(replacement);
        deletion
    }
    pub fn forget_saved_session(&self, address: &str) -> Result<()> {
        let base = validate_server(address)?;
        self.vault.delete(base.as_str())
    }
    pub async fn logout(&self) -> Result<Value> {
        let c = self.current().await?;
        let result = c.json(Method::POST, "/api/v1/auth/logout", None).await;
        self.clear(&c).await?;
        result
    }
    pub async fn request(&self, method: &str, path: &str, body: Option<Value>) -> Result<Value> {
        if !allowed_request(method, path) {
            return Err(ClientError::new(
                "PATH",
                "This API operation is not available to the desktop renderer.",
            ));
        }
        let c = self.current().await?;
        let method = Method::from_bytes(method.as_bytes())
            .map_err(|_| ClientError::new("PATH", "Invalid operation."))?;
        let result = c.json(method, path, body).await;
        if result.as_ref().is_err_and(|e| e.status == Some(401)) {
            self.clear(&c).await?;
        }
        result
    }
    pub async fn stream_url(&self, id: u64) -> Result<String> {
        if id == 0 {
            return Err(ClientError::new("PATH", "Invalid media ID."));
        }
        let c = self.current().await?;
        Ok(format!("{}/{}/media/{id}", self.proxy, c.proxy_secret))
    }
    pub async fn playback_url(&self, session: &str, file: &str) -> Result<String> {
        if !valid_session(session) || !playback_file(file) {
            return Err(ClientError::new("PATH", "Invalid playback resource."));
        }
        let c = self.current().await?;
        Ok(format!(
            "{}/{}/playback/{session}/files/{file}",
            self.proxy, c.proxy_secret
        ))
    }
}
fn allowed_request(method: &str, path: &str) -> bool {
    if path.len() > 1024 || path.contains('#') || path.contains('\\') {
        return false;
    }
    let Ok(url) = Url::parse(&format!("http://local.invalid{path}")) else {
        return false;
    };
    if url.host_str() != Some("local.invalid") || !path.starts_with("/api/v1/") {
        return false;
    }
    let p = url.path().strip_prefix("/api/v1/").unwrap_or("");
    let parts = p.split('/').collect::<Vec<_>>();
    if method == "GET"
        && matches!(
            p,
            "auth/me" | "media" | "progress" | "continue-watching" | "sessions" | "status"
        )
    {
        return url.query().is_none() || p == "media";
    }
    if url.query().is_some() {
        return false;
    }
    if method == "PUT" && p == "capabilities" {
        return true;
    }
    if method == "DELETE" && parts.len() == 2 && parts[0] == "sessions" {
        return parts[1].len() == 32 && parts[1].chars().all(|c| c.is_ascii_hexdigit());
    }
    if parts.len() == 2 && parts[0] == "playback-sessions" && valid_session(parts[1]) {
        return matches!(method, "GET" | "DELETE");
    }
    if parts.len() >= 2 && parts[0] == "media" && parts[1].parse::<u64>().is_ok_and(|n| n > 0) {
        if parts.get(2) == Some(&"bookmarks") {
            return match (method, parts.len()) {
                ("GET" | "POST", 3) => true,
                ("GET", 4) => parts[3] == "export",
                ("POST", 4) => parts[3] == "import",
                ("PUT", 4) => valid_session(parts[3]),
                ("DELETE", 5) => valid_session(parts[3]) && parts[4].parse::<u64>().is_ok(),
                _ => false,
            };
        }
        return matches!(
            (method, parts.len(), parts.get(2).copied()),
            ("GET", 2, _)
                | ("GET", 3, Some("progress" | "tracks" | "playback-options"))
                | ("PUT", 3, Some("progress-sync"))
                | ("POST", 3, Some("playback-errors" | "playback-sessions"))
        );
    }
    false
}
fn valid_session(value: &str) -> bool {
    value.len() == 32 && value.bytes().all(|c| c.is_ascii_hexdigit())
}
fn playback_file(value: &str) -> bool {
    if matches!(
        value,
        "master.m3u8" | "captions.vtt" | "360p.m3u8" | "480p.m3u8" | "720p.m3u8"
    ) {
        return true;
    }
    ["360p_", "480p_", "720p_"].iter().any(|prefix| {
        value
            .strip_prefix(prefix)
            .and_then(|v| v.strip_suffix(".ts"))
            .is_some_and(|v| (5..=10).contains(&v.len()) && v.bytes().all(|c| c.is_ascii_digit()))
    })
}
fn bridge_origin(headers: &HeaderMap) -> Option<&str> {
    headers
        .get("origin")
        .and_then(|v| v.to_str().ok())
        .filter(|origin| {
            matches!(
                *origin,
                "http://tauri.localhost" | "tauri://localhost" | "http://127.0.0.1:1420"
            )
        })
}
fn valid_playlist(text: &str) -> bool {
    text.starts_with("#EXTM3U")
        && text.lines().all(|line| {
            let line = line.trim();
            line.is_empty()
                || (line.starts_with('#') && !line.contains("URI="))
                || playback_file(line)
        })
}
async fn stream_playback(
    State(client): State<DesktopClient>,
    Path((secret, session, file)): Path<(String, String, String)>,
    method: Method,
    headers: HeaderMap,
) -> Response {
    let empty = |status| {
        Response::builder()
            .status(status)
            .body(Body::empty())
            .unwrap()
    };
    if !valid_session(&session)
        || !playback_file(&file)
        || (headers.contains_key("origin") && bridge_origin(&headers).is_none())
    {
        return empty(StatusCode::NOT_FOUND);
    }
    let Ok(c) = client.current().await else {
        return empty(StatusCode::NOT_FOUND);
    };
    if secret != c.proxy_secret.as_ref() {
        return empty(StatusCode::NOT_FOUND);
    }
    let path = format!("/api/v1/playback-sessions/{session}/files/{file}");
    let mut request = c.http.request(method.clone(), c.base.join(&path).unwrap());
    if let Some(range) = headers.get("range") {
        request = request.header("range", range);
    }
    let Ok(response) = request.send().await else {
        return empty(StatusCode::BAD_GATEWAY);
    };
    let status = response.status();
    let mut builder = Response::builder()
        .status(status)
        .header("Cache-Control", "no-store")
        .header("X-Content-Type-Options", "nosniff");
    if let Some(origin) = bridge_origin(&headers) {
        builder = builder
            .header("Access-Control-Allow-Origin", origin)
            .header("Access-Control-Allow-Credentials", "true")
            .header("Vary", "Origin");
    }
    if !status.is_success() {
        return builder.body(Body::empty()).unwrap();
    }
    let kind = response
        .headers()
        .get("content-type")
        .and_then(|v| v.to_str().ok())
        .unwrap_or("");
    let valid_type = if file.ends_with(".m3u8") {
        matches!(
            kind.split(';').next().unwrap_or(""),
            "application/vnd.apple.mpegurl" | "application/x-mpegURL"
        )
    } else if file.ends_with(".vtt") {
        kind.starts_with("text/vtt")
    } else {
        kind == "video/mp2t"
    };
    if !valid_type {
        return empty(StatusCode::BAD_GATEWAY);
    }
    for name in ["content-type", "content-range", "accept-ranges"] {
        if let Some(value) = response.headers().get(name) {
            builder = builder.header(name, value);
        }
    }
    if file.ends_with(".m3u8") && method != Method::HEAD {
        let mut data = Vec::new();
        let mut stream = response.bytes_stream();
        while let Some(chunk) = stream.next().await {
            let Ok(chunk) = chunk else {
                return empty(StatusCode::BAD_GATEWAY);
            };
            if data.len() + chunk.len() > 1024 * 1024 {
                return empty(StatusCode::BAD_GATEWAY);
            }
            data.extend_from_slice(&chunk);
        }
        if !std::str::from_utf8(&data).is_ok_and(valid_playlist) {
            return empty(StatusCode::BAD_GATEWAY);
        }
        return builder.body(Body::from(data)).unwrap();
    }
    if let Some(length) = response.headers().get("content-length") {
        builder = builder.header("content-length", length);
    }
    let body = response
        .bytes_stream()
        .take_until(c.cancel.cancelled_owned())
        .map_err(std::io::Error::other);
    builder.body(Body::from_stream(body)).unwrap()
}
async fn stream_media(
    State(client): State<DesktopClient>,
    Path((secret, id)): Path<(String, u64)>,
    method: Method,
    headers: HeaderMap,
) -> Response {
    let denied = || {
        Response::builder()
            .status(StatusCode::NOT_FOUND)
            .body(Body::empty())
            .unwrap()
    };
    if id == 0 {
        return denied();
    }
    if let Some(origin) = headers.get("origin").and_then(|v| v.to_str().ok()) {
        if !matches!(
            origin,
            "http://tauri.localhost" | "tauri://localhost" | "http://127.0.0.1:1420"
        ) {
            return denied();
        }
    }
    let Ok(c) = client.current().await else {
        return denied();
    };
    if secret.as_str() != c.proxy_secret.as_ref() {
        return denied();
    }
    let mut request = c.http.request(
        method,
        c.base.join(&format!("/api/v1/media/{id}/stream")).unwrap(),
    );
    for name in ["range", "if-range"] {
        if let Some(value) = headers.get(name) {
            request = request.header(name, value);
        }
    }
    let Ok(response) = request.send().await else {
        return Response::builder().status(502).body(Body::empty()).unwrap();
    };
    let mut builder = Response::builder()
        .status(response.status())
        .header("Cache-Control", "no-store")
        .header("X-Content-Type-Options", "nosniff");
    if let Some(origin) = bridge_origin(&headers) {
        builder = builder
            .header("Access-Control-Allow-Origin", origin)
            .header("Access-Control-Allow-Credentials", "true")
            .header("Vary", "Origin");
    }
    if let Some(kind) = response
        .headers()
        .get("content-type")
        .and_then(|v| v.to_str().ok())
    {
        if !kind.starts_with("video/")
            && !kind.starts_with("audio/")
            && kind != "application/octet-stream"
        {
            return Response::builder()
                .status(response.status())
                .body(Body::empty())
                .unwrap();
        }
    }
    for name in [
        "content-type",
        "content-length",
        "content-range",
        "accept-ranges",
        "etag",
        "last-modified",
    ] {
        if let Some(value) = response.headers().get(name) {
            builder = builder.header(name, value);
        }
    }
    let body = response
        .bytes_stream()
        .take_until(c.cancel.cancelled_owned())
        .map_err(std::io::Error::other);
    builder.body(Body::from_stream(body)).unwrap()
}
#[cfg(test)]
mod tests {
    #[test]
    fn scoped_hls_paths_and_manifests() {
        assert!(super::allowed_request(
            "GET",
            "/api/v1/media/1/playback-options"
        ));
        assert!(super::allowed_request(
            "POST",
            "/api/v1/media/1/playback-sessions"
        ));
        assert!(super::allowed_request(
            "GET",
            "/api/v1/playback-sessions/abcdef0123456789abcdef0123456789"
        ));
        assert!(!super::allowed_request(
            "GET",
            "/api/v1/playback-sessions/../../media/1"
        ));
        assert!(super::valid_playlist(
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=960000\n360p.m3u8\n"
        ));
        for value in [
            "#EXTM3U\nhttps://evil.invalid/video.ts",
            "#EXTM3U\n../source.bin",
            "#EXTM3U\n#EXT-X-KEY:URI=\"https://evil.invalid\"",
            "#EXTM3U\n//evil.invalid/360p.m3u8",
        ] {
            assert!(!super::valid_playlist(value));
        }
        assert!(!super::playback_file("source.bin"));
        assert!(super::playback_file("720p_00001.ts"));
        assert!(!super::playback_file("720p_../../secret.ts"));
    }

    use super::*;
    #[test]
    fn credential_keys_preserve_saved_session_addresses() {
        assert_eq!(
            credential_key("https://example.org:8920/"),
            "f2a7c8b9cd4a5fbb9535af7d3643e946c4f693939a8294c01381846373dc1165"
        );
        assert_ne!(
            credential_key("https://example.org:8920/"),
            credential_key("https://other.example.org:8920/")
        );
    }
    #[test]
    fn validates_origins() {
        for address in [
            "https://example.org:8920",
            "http://127.0.0.1:5050",
            "http://[::1]:5050",
        ] {
            assert!(validate_server(address).is_ok());
        }
        for address in [
            "http://192.168.1.2:5050",
            "http://example.org",
            "file:///etc/passwd",
            "https://user:pass@example.org",
            "https://example.org/path",
            "https://example.org/?token=x",
            "javascript:alert(1)",
        ] {
            assert!(validate_server(address).is_err(), "{address}");
        }
    }
    #[test]
    fn renderer_requests_are_scoped() {
        for (method, path) in [
            ("GET", "/api/v1/media?search=test"),
            ("PUT", "/api/v1/media/4/progress-sync"),
            ("GET", "/api/v1/media/4/bookmarks"),
            ("POST", "/api/v1/media/4/bookmarks/import"),
            (
                "PUT",
                "/api/v1/media/4/bookmarks/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            ),
            (
                "DELETE",
                "/api/v1/media/4/bookmarks/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/1",
            ),
        ] {
            assert!(allowed_request(method, path));
        }
        for (method, path) in [
            ("GET", "//evil.example/api/v1/media"),
            ("POST", "/api/v1/setup"),
            ("GET", "/api/v1/media/../../auth/csrf"),
            ("GET", "/api/v1/media/4/stream"),
            ("GET", "/api/v1/media#x"),
            ("PUT", "/api/v1/media/4/bookmarks/../../users"),
            (
                "DELETE",
                "/api/v1/media/4/bookmarks/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/bad",
            ),
            ("GET", "/api/v1/media/4/bookmarks/not-a-route"),
        ] {
            assert!(!allowed_request(method, path));
        }
    }
}
