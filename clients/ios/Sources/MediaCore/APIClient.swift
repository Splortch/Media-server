import Foundation

public enum ClientError: Error, LocalizedError {
    case invalidOrigin, incompatible, setupRequired, expired, denied, conflict, http(Int), invalidPosition
    public var errorDescription: String? {
        switch self {
        case .invalidOrigin: return "Enter an HTTPS server origin without a path, credentials, query or fragment. HTTP is only allowed on loopback."
        case .incompatible: return "This server does not support client API v1."
        case .setupRequired: return "Complete server setup in the web interface first."
        case .expired: return "Your session expired. Sign in again."
        case .denied: return "Permission denied."
        case .conflict: return "Progress changed on another device. Reopen this title to resume."
        case .http(let status): return "Server request failed (\(status)). Reopen the title before retrying playback."
        case .invalidPosition: return "Invalid playback position."
        }
    }
}

public enum ServerOrigin {
    public static func validate(_ text: String) throws -> URL {
        guard let c = URLComponents(string: text), let host = c.host, !host.isEmpty,
              c.user == nil, c.password == nil, c.query == nil, c.fragment == nil,
              c.path.isEmpty || c.path == "/",
              c.port == nil || (1...65535).contains(c.port!),
              c.scheme == "https" || (c.scheme == "http" && ["localhost", "127.0.0.1", "[::1]", "::1"].contains(host)),
              let url = c.url else { throw ClientError.invalidOrigin }
        return url
    }
}

// Redirects are rejected so cookies and credentials cannot be forwarded to another origin.
final class NoRedirects: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
}

@MainActor
public final class APIClient {
    public let origin: URL
    public let cookies: HTTPCookieStorage
    private let session: URLSession
    private let delegate = NoRedirects()
    public var onExpired: (() -> Void)?

    public init(origin: URL, configuration: URLSessionConfiguration = .ephemeral) {
        self.origin = origin
        // Ephemeral storage never writes session cookies to the filesystem.
        cookies = configuration.httpCookieStorage ?? HTTPCookieStorage.sharedCookieStorage(forGroupContainerIdentifier: UUID().uuidString)
        configuration.httpCookieStorage = cookies
        configuration.httpShouldSetCookies = true
        configuration.urlCache = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.timeoutIntervalForRequest = 30
        session = URLSession(configuration: configuration, delegate: delegate, delegateQueue: nil)
    }

    public func url(_ path: String) -> URL {
        let parts = path.split(separator: "?", maxSplits: 1)
        var components = URLComponents(url: origin.appendingPathComponent("api/v1/" + parts[0]), resolvingAgainstBaseURL: false)!
        if parts.count == 2 { components.percentEncodedQuery = String(parts[1]) }
        return components.url!
    }

    private func request(_ path: String, method: String = "GET", body: Data? = nil) async throws -> Data {
        var req = URLRequest(url: url(path))
        req.httpMethod = method
        req.httpBody = body
        req.setValue("application/json", forHTTPHeaderField: "Accept")
        if method != "GET" {
            struct Token: Decodable { let token: String }
            let token: Token = try await get("auth/csrf")
            req.setValue(token.token, forHTTPHeaderField: "X-CSRF-TOKEN")
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }
        let (bytes, response) = try await session.data(for: req)
        guard let response = response as? HTTPURLResponse else { throw ClientError.http(0) }
        switch response.statusCode {
        case 200..<300: return bytes
        case 401:
            clearCookies()
            onExpired?()
            throw ClientError.expired
        case 403: throw ClientError.denied
        case 409: throw ClientError.conflict
        default: throw ClientError.http(response.statusCode)
        }
    }
    public func get<T: Decodable>(_ path: String) async throws -> T {
        try JSONDecoder().decode(T.self, from: await request(path))
    }
    public func write<T: Decodable, B: Encodable>(_ path: String, method: String, body: B) async throws -> T {
        try JSONDecoder().decode(T.self, from: await request(path, method: method, body: JSONEncoder().encode(body)))
    }
    public func checkVersion() async throws {
        let info: ServerInfo = try await get("server-info")
        guard info.apiVersion == 1, info.minimumClientApiVersion <= 1 else { throw ClientError.incompatible }
        guard !info.setupRequired else { throw ClientError.setupRequired }
    }
    public func login(name: String, password: String) async throws -> UserIdentity {
        try await checkVersion()
        return try await write("auth/login", method: "POST", body: LoginRequest(name: name, password: password, deviceName: "iOS"))
    }
    public func logout() async throws {
        defer { clearCookies() }
        _ = try await request("auth/logout", method: "POST")
    }
    public func clearCookies() {
        for cookie in cookies.cookies ?? [] { cookies.deleteCookie(cookie) }
    }
    public func streamCookies(id: Int) -> [HTTPCookie] {
        cookies.cookies(for: url("media/\(id)/stream")) ?? []
    }
}

// One writer per playback. A failed or uncertain write permanently halts this writer.
@MainActor
public final class ProgressWriter {
    public private(set) var state: ProgressState
    public private(set) var stopped = false
    private var busy = false
    private let api: APIClient
    public init(api: APIClient, state: ProgressState) { self.api = api; self.state = state }
    public func save(seconds: Double, completed: Bool = false) async throws {
        guard !stopped, !busy else { return }
        guard seconds.isFinite, seconds >= 0 else { throw ClientError.invalidPosition }
        busy = true
        defer { busy = false }
        do {
            state = try await api.write("media/\(state.mediaItemId)/progress-sync", method: "PUT",
                body: ProgressUpdate(seconds: seconds, expectedRevision: state.revision, completed: completed))
        } catch { stopped = true; throw error }
    }
}
