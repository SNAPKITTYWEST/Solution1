import XCTest
import Foundation
@testable import SovereignRuntime
final class RuntimeTests: XCTestCase {
    func runtime() throws -> Runtime {
        let path=ProcessInfo.processInfo.environment["SOVEREIGN_WASM"] ?? "../public/core.wasm"
        return try Runtime(module:Data(contentsOf:URL(fileURLWithPath:path)))
    }
    func testKnownVectorAndZeroCopy() async throws {
        let r=try runtime();let hash=try await r.withOutput(.hash,input:Data("hello".utf8)){try $0.readU32(at:0)}
        XCTAssertEqual(hash,0x4f9f2cab);let tick=try await r.tick();XCTAssertEqual(tick,1)
    }
    func testByteForByteDeterminism() async throws {
        let a=try runtime(),b=try runtime()
        for i in 0..<100 { let data=Data("deterministic \(i)".utf8);_ = await a.execute(.embed,input:data);_ = await b.execute(.embed,input:data) }
        let first=try await a.snapshot(),second=try await b.snapshot();XCTAssertEqual(first,second)
    }
    func testBoundsAndOverflow() async throws {
        let r=try runtime()
        do { try await r.validateRange(offset:UInt32.max,length:4);XCTFail("Expected bounds error") } catch { XCTAssertEqual(error as? CoreError,.bounds) }
        do { try await r.validateRange(offset:0,length:Int.max);XCTFail("Expected bounds error") } catch { XCTAssertEqual(error as? CoreError,.bounds) }
        let bad=await r.execute(.hash,input:Data(repeating:1,count:65537));XCTAssertEqual(bad,.failure(.invalidSize))
        let tick=try await r.tick();XCTAssertEqual(tick,0)
    }
    func testMaximumInputAndNormalization() async throws {
        let r=try runtime();let output=try await r.execute(.normalize,input:Data(repeating:65,count:65536)).get()
        XCTAssertEqual(output,Data(repeating:97,count:65536))
    }
    func testPointerEncoding() throws {
        var storage=[UInt8](repeating:0,count:8)
        try storage.withUnsafeMutableBytes { p in try p.writeU32(0x12345678,at:1);XCTAssertEqual(try UnsafeRawBufferPointer(p).readU32(at:1),0x12345678);XCTAssertThrowsError(try p.writeU32(1,at:Int.max)) }
    }
    func testInvalidModule() { XCTAssertThrowsError(try Runtime(module:Data([0,1,2,3]))) }
}
