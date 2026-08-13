// Manual update check against the GitHub releases API.
//
// Deliberately not automatic: the shipped promise is that Redline makes no network requests
// unless you ask for Claude limits, and a background poll would quietly break that. This runs
// only when the user picks Check for Updates.
import Foundation
import RedlineCore

enum Updates {
    static let releasesURL = URL(string:
        "https://api.github.com/repos/goriparthi/redline/releases/latest")!

    enum Result {
        case upToDate
        case available(version: String, url: URL)
        case failed(String)
    }

    static var bundleVersion: String {
        (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String) ?? "0.0.0"
    }

    static func check(currentVersion: String, completion: @escaping (Result) -> Void) {
        var req = URLRequest(url: releasesURL)
        req.timeoutInterval = 12
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        URLSession.shared.dataTask(with: req) { data, resp, err in
            if let err {
                completion(.failed("Update check failed: \(err.localizedDescription)"))
                return
            }
            let status = (resp as? HTTPURLResponse)?.statusCode ?? 0
            guard let data,
                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  (200..<300).contains(status) else {
                // A private repo answers 404 to an unauthenticated request
                completion(.failed(status == 404 ? "No public releases found"
                                                 : "Update check failed (HTTP \(status))"))
                return
            }
            guard let tag = json["tag_name"] as? String else {
                completion(.failed("Update check failed: unexpected response"))
                return
            }
            let latest = tag.hasPrefix("v") ? String(tag.dropFirst()) : tag
            let page = (json["html_url"] as? String).flatMap(URL.init(string:))
            if isNewer(latest, than: currentVersion), let page {
                completion(.available(version: latest, url: page))
            } else {
                completion(.upToDate)
            }
        }.resume()
    }

    /// Numeric component comparison, so 0.10.0 is correctly newer than 0.9.0.
    static func isNewer(_ candidate: String, than current: String) -> Bool {
        let a = candidate.split(separator: ".").map { Int($0) ?? 0 }
        let b = current.split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(a.count, b.count) {
            let x = i < a.count ? a[i] : 0
            let y = i < b.count ? b[i] : 0
            if x != y { return x > y }
        }
        return false
    }
}
