// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "redline",
    platforms: [.macOS(.v14)],
    targets: [
        // Pure parsing and aggregation, split out from the UI so it can be unit tested.
        // Every on-disk format it reads is undocumented, so tests pin the shapes.
        .target(name: "RedlineCore"),
        .executableTarget(name: "redline", dependencies: ["RedlineCore"]),
        .testTarget(name: "RedlineCoreTests", dependencies: ["RedlineCore"]),
    ]
)
