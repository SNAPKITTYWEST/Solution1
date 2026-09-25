// swift-tools-version: 5.9
import PackageDescription
let package = Package(
    name: "SovereignRuntime",
    platforms: [.macOS(.v13)],
    products: [.library(name: "SovereignRuntime", targets: ["SovereignRuntime"]), .executable(name: "sovereign-swift", targets: ["SovereignDemo"])],
    targets: [
        .target(name: "CSovereign", publicHeadersPath: "include", linkerSettings: [.linkedLibrary("wasmtime")]),
        .target(name: "SovereignRuntime", dependencies: ["CSovereign"]),
        .executableTarget(name: "SovereignDemo", dependencies: ["SovereignRuntime"]),
        .testTarget(name: "SovereignRuntimeTests", dependencies: ["SovereignRuntime"])
    ]
)
