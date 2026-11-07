// swift-tools-version: 5.9
import PackageDescription
let package = Package(name: "MediaCore", platforms: [.iOS(.v17), .macOS(.v13)], products: [
    .library(name: "MediaCore", targets: ["MediaCore"])
], targets: [.target(name: "MediaCore"), .testTarget(name: "MediaCoreTests", dependencies: ["MediaCore"])])
