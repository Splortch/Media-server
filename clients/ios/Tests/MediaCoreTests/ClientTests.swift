import XCTest
@testable import MediaCore

final class MockProtocol: URLProtocol {
    static var handler: ((URLRequest) throws -> (Int, String))?
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        do {
            let (status, body) = try Self.handler!(request)
            client?.urlProtocol(self, didReceive: HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: Data(body.utf8))
            client?.urlProtocolDidFinishLoading(self)
        } catch { client?.urlProtocol(self, didFailWithError: error) }
    }
    override func stopLoading() {}
}

final class ClientTests: XCTestCase {
    func testOriginPolicy() throws {
        for text in ["https://media.example", "https://media.example:8443/", "http://127.0.0.1:5000", "http://[::1]:5000"] {
            XCTAssertNoThrow(try ServerOrigin.validate(text))
        }
        for text in ["http://192.168.1.10", "http://example.com", "https://a:b@example.com", "https://example.com/prefix", "https://example.com?x=1", "https://example.com#x", "file:///etc/passwd", "https://example.com:0"] {
            XCTAssertThrowsError(try ServerOrigin.validate(text), text)
        }
    }
    @MainActor private func client() throws -> APIClient {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [MockProtocol.self]
        return APIClient(origin: try ServerOrigin.validate("https://media.example"), configuration: config)
    }
    @MainActor func testPaginationQueryIsNotPartOfEncodedPath() throws {
        let url = try client().url("media?offset=100&limit=100")
        XCTAssertEqual(url.path, "/api/v1/media")
        XCTAssertEqual(url.query, "offset=100&limit=100")
    }
    @MainActor func testIncompatibleServerNeverReceivesPassword() async throws {
        var paths = [String]()
        MockProtocol.handler = { request in
            paths.append(request.url!.path)
            return (200, #"{"name":"Server","serverVersion":"1","apiVersion":2,"minimumClientApiVersion":2,"setupRequired":false,"features":[]}"#)
        }
        do { _ = try await client().login(name: "user", password: "secret"); XCTFail("Accepted incompatible server") }
        catch ClientError.incompatible {} catch { XCTFail("\(error)") }
        XCTAssertEqual(paths, ["/api/v1/server-info"])
    }
    @MainActor func testWritesRefreshCsrfAndAdoptRevision() async throws {
        let api = try client()
        var revisions = [Int64]()
        var tokens = 0
        MockProtocol.handler = { request in
            if request.url!.path.hasSuffix("csrf") { tokens += 1; return (200, #"{"token":"csrf-value"}"#) }
            XCTAssertEqual(request.value(forHTTPHeaderField: "X-CSRF-TOKEN"), "csrf-value")
            XCTAssertEqual(request.httpMethod, "PUT")
            // URLSession may supply a body stream rather than httpBody to URLProtocol.
            if let body = request.httpBody { revisions.append(try JSONDecoder().decode(ProgressUpdate.self, from: body).expectedRevision) }
            return (200, "{\"mediaItemId\":1,\"seconds\":12,\"completed\":false,\"revision\":\(tokens + 4),\"updatedAt\":null}")
        }
        let writer = ProgressWriter(api: api, state: ProgressState(mediaItemId: 1, seconds: 0, completed: false, revision: 4, updatedAt: nil))
        try await writer.save(seconds: 12)
        XCTAssertEqual(writer.state.revision, 5)
        try await writer.save(seconds: 13)
        XCTAssertEqual(writer.state.revision, 6)
        XCTAssertEqual(tokens, 2)
        if !revisions.isEmpty { XCTAssertEqual(revisions, [4, 5]) }
    }
    @MainActor func testConflictAndUncertainFailuresStopFutureWrites() async throws {
        for status in [409, 503] {
            let api = try client()
            var writes = 0
            MockProtocol.handler = { request in
                if request.url!.path.hasSuffix("csrf") { return (200, #"{"token":"csrf"}"#) }
                writes += 1
                return (status, "{}")
            }
            let writer = ProgressWriter(api: api, state: ProgressState(mediaItemId: 1, seconds: 10, completed: false, revision: 2, updatedAt: nil))
            do { try await writer.save(seconds: 20); XCTFail("Accepted failed write") } catch {}
            XCTAssertTrue(writer.stopped)
            try await writer.save(seconds: 30)
            XCTAssertEqual(writes, 1)
            XCTAssertEqual(writer.state.revision, 2)
        }
    }
    @MainActor func testExpiredSessionClearsCookies() async throws {
        let api = try client()
        let cookie = HTTPCookie(properties: [.domain: "media.example", .path: "/", .name: "session", .value: "revocable", .secure: "TRUE"])!
        api.cookies.setCookie(cookie)
        var expired = false
        api.onExpired = { expired = true }
        MockProtocol.handler = { _ in (401, "{}") }
        do { let _: UserIdentity = try await api.get("auth/me"); XCTFail("Accepted expired session") } catch ClientError.expired {} catch { XCTFail("\(error)") }
        XCTAssertTrue(expired)
        XCTAssertTrue(api.cookies.cookies?.isEmpty ?? true)
    }
    func testAdditiveContractFieldsAndOptionalMetadata() throws {
        let json = #"{"id":1,"title":"Film","duration":60,"videoCodec":"h264","audioCodec":"aac","container":"mp4","size":100,"playback":{"mode":"direct","mimeType":"video/mp4","warning":null},"futureField":true}"#
        let item = try JSONDecoder().decode(MediaDetails.self, from: Data(json.utf8))
        XCTAssertEqual(item.id, 1)
        XCTAssertNil(item.metadata)
    }
}
