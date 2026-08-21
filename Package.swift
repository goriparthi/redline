// swift-tools-version: 5.9
import PackageDescription

// RedlineCore is Foundation only and builds on macOS, Linux and Windows. Everything that
// needs AppKit or SwiftUI is a macOS-only target, added below only on a macOS host.
//
// SQLite comes from the SDK on macOS and from the vendored amalgamation everywhere else;
// see Sources/CSQLite/README.md for why it is vendored rather than linked from the system.
var coreDependencies: [Target.Dependency] = []
var targets: [Target] = []

#if !os(macOS)
coreDependencies.append("CSQLite")
targets.append(
    .target(name: "CSQLite",
            cSettings: [
                // Matches what Apple's own build enables, so a query behaves the same on
                // every platform rather than only on the one it was written against.
                .define("SQLITE_ENABLE_COLUMN_METADATA"),
                .define("SQLITE_ENABLE_FTS5"),
                .define("SQLITE_ENABLE_RTREE"),
                .define("SQLITE_THREADSAFE", to: "1"),
            ])
)
#endif

targets += [
    // Pure parsing and aggregation, split out from the UI so it can be unit tested.
    // Every on-disk format it reads is undocumented, so tests pin the shapes.
    .target(name: "RedlineCore", dependencies: coreDependencies),
    .testTarget(name: "RedlineCoreTests", dependencies: ["RedlineCore"]),
]

#if os(macOS)
targets += [
    // The shared SwiftUI component set. Used by both the app and the widget, which is why it
    // is a library rather than part of the app target.
    .target(name: "RedlineUI", dependencies: ["RedlineCore"]),
    .executableTarget(name: "redline", dependencies: ["RedlineCore", "RedlineUI"]),
    .testTarget(name: "RedlineUITests", dependencies: ["RedlineUI", "RedlineCore"]),
    // The app owns the name "redline" here, so the standalone tool is built under a second
    // name. It is built at all so the entry point cannot rot while only CI compiles it.
    .executableTarget(name: "redline-cli", dependencies: ["RedlineCore"],
                      path: "Sources/RedlineCLI"),
]
#else
// Off macOS there is no app yet, so the command line tool is what "redline" means.
targets.append(
    .executableTarget(name: "redline", dependencies: ["RedlineCore"],
                      path: "Sources/RedlineCLI")
)
#endif

let package = Package(
    name: "redline",
    platforms: [.macOS(.v14)],
    targets: targets
)
