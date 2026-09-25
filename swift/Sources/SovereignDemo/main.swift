import Foundation
import SovereignRuntime
@main struct Demo {
    static func main() async throws {
        let path=CommandLine.arguments.dropFirst().first ?? "../public/core.wasm"
        let runtime=try Runtime(module:Data(contentsOf:URL(fileURLWithPath:path)))
        let hash=try await runtime.withOutput(.hash,input:Data("hello".utf8)) { try $0.readU32(at:0) }
        print("FNV hello: \(String(hash,radix:16)); tick: \(try await runtime.tick())")
    }
}
