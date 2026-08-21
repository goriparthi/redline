// Update check against the GitHub releases API, run daily by default and on demand from
// the menu. The only request RedLine makes without being asked.
//
// Deliberately not automatic: the shipped promise is that RedLine makes no network requests
// unless you ask for Claude limits, and a background poll would quietly break that. This runs
// only when the user picks Check for Updates.
import Foundation
import RedlineCore

enum Updates {
    /// The public repository. Shown in About so the source is reachable from the app
    /// itself rather than only from wherever someone happened to find the download.
    static let repoURL = URL(string: "https://github.com/goriparthi/redline")!
    static let releasesURL = URL(string:
        "https://api.github.com/repos/goriparthi/redline/releases/latest")!
    // Beta channel: the list endpoint is the only one that includes prereleases
    static let allReleasesURL = URL(string:
        "https://api.github.com/repos/goriparthi/redline/releases?per_page=20")!

    enum Result {
        case upToDate
        case available(version: String, url: URL, dmg: URL?)
        case failed(String)
    }

    static var bundleVersion: String {
        (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String) ?? "0.0.0"
    }

    static func check(currentVersion: String, channel: String = "stable",
                      completion: @escaping (Result) -> Void) {
        let beta = channel == "beta"
        var req = URLRequest(url: beta ? allReleasesURL : releasesURL)
        req.timeoutInterval = 12
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        URLSession.shared.dataTask(with: req) { data, resp, err in
            if let err {
                completion(.failed("Update check failed: \(err.localizedDescription)"))
                return
            }
            let status = (resp as? HTTPURLResponse)?.statusCode ?? 0
            guard let data, (200..<300).contains(status) else {
                // A private repo answers 404 to an unauthenticated request
                completion(.failed(status == 404 ? "No public releases found"
                                                 : "Update check failed (HTTP \(status))"))
                return
            }
            let json: [String: Any]?
            if beta {
                // Newest by version, not list order, so a stable hotfix outranks older betas
                let list = ((try? JSONSerialization.jsonObject(with: data))
                    as? [[String: Any]] ?? [])
                    .filter { !(($0["draft"] as? Bool) ?? false) }
                json = list.max { VersionCompare.isNewer(tagVersion(of: $1),
                                                         than: tagVersion(of: $0)) }
            } else {
                json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
            }
            guard let json, let tag = json["tag_name"] as? String else {
                completion(.failed(beta ? "No releases found"
                                        : "Update check failed: unexpected response"))
                return
            }
            let latest = tag.hasPrefix("v") ? String(tag.dropFirst()) : tag
            let page = (json["html_url"] as? String).flatMap(URL.init(string:))
            // The DMG asset enables the in-place install; without one the page still opens
            let dmg = (json["assets"] as? [[String: Any]])?
                .compactMap { $0["browser_download_url"] as? String }
                .first { $0.hasSuffix(".dmg") }
                .flatMap(URL.init(string:))
            if isNewer(latest, than: currentVersion), let page {
                completion(.available(version: latest, url: page, dmg: dmg))
            } else {
                completion(.upToDate)
            }
        }.resume()
    }

    // MARK: - In-place install

    /// Updates are only ever swapped in when they carry this exact Developer ID team, so a
    /// tampered download or a hijacked release can never replace the running app.
    static let expectedTeamID = "QX3NQYWX6F"

    enum StageResult {
        /// Everything verified and staged; calling swap() hands off to a helper that waits
        /// for this process to exit, replaces the bundle, and relaunches it.
        case ready(swap: () -> Void)
        case failed(String)
    }

    /// Downloads the DMG, mounts it, verifies notarization and the pinned team id, and
    /// stages the new bundle next to a swap script. No part of this touches the installed
    /// app yet; that only happens after the caller quits.
    static func stage(dmg: URL, replacing target: URL,
                      status: @escaping (String) -> Void,
                      completion: @escaping (StageResult) -> Void) {
        status("Downloading…")
        URLSession.shared.downloadTask(with: dmg) { tmp, _, err in
            guard let tmp, err == nil else {
                completion(.failed("Download failed: \(err?.localizedDescription ?? "no data")"))
                return
            }
            // downloadTask deletes its temp file when this handler returns, so move it first
            let fm = FileManager.default
            let work = fm.temporaryDirectory
                .appendingPathComponent("redline-update-\(UUID().uuidString)")
            let image = work.appendingPathComponent("update.dmg")
            do {
                try fm.createDirectory(at: work, withIntermediateDirectories: true)
                try fm.moveItem(at: tmp, to: image)
            } catch {
                completion(.failed("Could not stage the download"))
                return
            }
            status("Verifying…")
            DispatchQueue.global(qos: .userInitiated).async {
                completion(verifyAndStage(image: image, work: work, target: target))
            }
        }.resume()
    }

    private static func verifyAndStage(image: URL, work: URL, target: URL) -> StageResult {
        let fm = FileManager.default
        func fail(_ msg: String) -> StageResult {
            try? fm.removeItem(at: work)
            return .failed(msg)
        }

        // -mountrandom keeps the volume out of /Volumes and away from name collisions
        let attach = run("/usr/bin/hdiutil",
                         ["attach", image.path, "-nobrowse", "-readonly",
                          "-mountrandom", fm.temporaryDirectory.path])
        guard attach.status == 0,
              let mount = attach.output.split(separator: "\n").last?
                  .split(separator: "\t").last.map({ $0.trimmingCharacters(in: .whitespaces) })
        else { return fail("Could not open the downloaded image") }
        defer { _ = run("/usr/bin/hdiutil", ["detach", mount, "-quiet"]) }

        guard let app = (try? fm.contentsOfDirectory(atPath: mount))?
            .first(where: { $0.hasSuffix(".app") })
            .map({ (mount as NSString).appendingPathComponent($0) })
        else { return fail("No app inside the downloaded image") }

        // Both checks must pass: notarized by Apple, and signed by this project's team.
        // The team is enforced as a codesign requirement, so the match is cryptographic;
        // grepping codesign's text output would be spoofable by the app's own filename,
        // which the DMG author chooses and which appears in that output.
        guard run("/usr/sbin/spctl", ["-a", "-t", "install", app]).status == 0 else {
            return fail("Update rejected: not accepted by Gatekeeper")
        }
        let requirement = "anchor apple generic and certificate leaf[subject.OU] = "
            + "\"\(expectedTeamID)\""
        guard run("/usr/bin/codesign",
                  ["--verify", "--deep", "--strict", "-R=\(requirement)", app]).status == 0
        else {
            return fail("Update rejected: unexpected signing identity")
        }

        let staged = work.appendingPathComponent("staged.app")
        guard run("/bin/cp", ["-R", app, staged.path]).status == 0 else {
            return fail("Could not copy the new version")
        }

        // The helper outlives this process: it waits for exit, swaps, relaunches, cleans up
        let script = work.appendingPathComponent("swap.sh")
        let body = """
        #!/bin/bash
        PID=$1; TARGET=$2; STAGED=$3; WORK=$4
        for _ in $(seq 1 150); do kill -0 "$PID" 2>/dev/null || break; sleep 0.2; done
        killall RedlineWidget 2>/dev/null
        rm -rf "$TARGET"
        if mv "$STAGED" "$TARGET"; then
            open "$TARGET"
        else
            osascript -e 'display notification "Update failed; reinstall from the release \
        page." with title "RedLine"' 2>/dev/null
        fi
        rm -rf "$WORK"
        """
        do {
            try body.write(to: script, atomically: true, encoding: .utf8)
            try fm.setAttributes([.posixPermissions: 0o700], ofItemAtPath: script.path)
        } catch { return fail("Could not write the update helper") }

        return .ready(swap: {
            // nohup + & detaches the helper into its own lineage, so booting this app's
            // launchd job out does not take the helper down with it
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/bin/bash")
            p.arguments = ["-c",
                "nohup /bin/bash \(shq(script.path)) \(getpid()) \(shq(target.path)) "
                + "\(shq(staged.path)) \(shq(work.path)) >/dev/null 2>&1 &"]
            try? p.run()
            p.waitUntilExit()
        })
    }

    private static func shq(_ s: String) -> String {
        "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    @discardableResult
    private static func run(_ path: String, _ args: [String]) -> (status: Int32, output: String) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: path)
        p.arguments = args
        let pipe = Pipe()
        p.standardOutput = pipe
        p.standardError = pipe
        do { try p.run() } catch { return (127, "") }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return (p.terminationStatus, String(data: data, encoding: .utf8) ?? "")
    }

    /// Numeric component comparison, so 0.10.0 is correctly newer than 0.9.0.
    static func isNewer(_ candidate: String, than current: String) -> Bool {
        VersionCompare.isNewer(candidate, than: current)
    }

    private static func tagVersion(of release: [String: Any]) -> String {
        let tag = release["tag_name"] as? String ?? "0"
        return tag.hasPrefix("v") ? String(tag.dropFirst()) : tag
    }
}
