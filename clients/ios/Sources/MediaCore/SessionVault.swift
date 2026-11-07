import Foundation
import Security

/// Only the origin and revocable cookies are persisted; passwords never enter this type.
public enum SessionVault {
    private static let key: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
        kSecAttrService as String: "org.splortch.mediaserver.ios", kSecAttrAccount as String: "session"]
    private struct Saved: Codable { let origin: String; let cookies: [[String: String]] }
    public static func save(origin: URL, cookies: [HTTPCookie]) throws {
        let saved = Saved(origin: origin.absoluteString, cookies: cookies.map { cookie in
            var result = [String: String]()
            for (k, v) in cookie.properties ?? [:] {
                if let value = v as? String { result[k.rawValue] = value }
                else if let value = v as? Date { result[k.rawValue] = String(value.timeIntervalSince1970) }
            }
            return result
        })
        let bytes = try JSONEncoder().encode(saved)
        clear()
        var item = key
        item[kSecValueData as String] = bytes
        item[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        let status = SecItemAdd(item as CFDictionary, nil)
        guard status == errSecSuccess else { throw NSError(domain: NSOSStatusErrorDomain, code: Int(status)) }
    }
    public static func restore() throws -> (URL, [HTTPCookie])? {
        var query = key
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let bytes = result as? Data else {
            throw NSError(domain: NSOSStatusErrorDomain, code: Int(status))
        }
        let saved = try JSONDecoder().decode(Saved.self, from: bytes)
        let origin = try ServerOrigin.validate(saved.origin)
        let cookies = saved.cookies.compactMap { values -> HTTPCookie? in
            var props = [HTTPCookiePropertyKey: Any]()
            for (k, v) in values {
                if k == HTTPCookiePropertyKey.expires.rawValue, let seconds = Double(v) {
                    props[.expires] = Date(timeIntervalSince1970: seconds)
                } else { props[HTTPCookiePropertyKey(k)] = v }
            }
            guard let cookie = HTTPCookie(properties: props), cookie.expiresDate.map({ $0 > Date() }) ?? true else { return nil }
            return cookie
        }
        return (origin, cookies)
    }
    public static func clear() { SecItemDelete(key as CFDictionary) }
}
