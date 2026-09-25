import Foundation
import CSovereign

public enum CoreError: Error, Equatable, Sendable { case initialization, invalidSize, bounds, allocation, host(Int32), core(UInt32) }
public enum Operation: UInt32, Sendable { case hash=1, embed=2, guardText=3, normalize=4 }

public extension UnsafeRawBufferPointer {
    /// Unaligned-safe little-endian load with overflow-safe range checking.
    func readU32(at offset: Int) throws -> UInt32 {
        guard offset >= 0, offset <= count, count-offset >= 4 else { throw CoreError.bounds }
        return UInt32(self[offset]) | UInt32(self[offset+1])<<8 | UInt32(self[offset+2])<<16 | UInt32(self[offset+3])<<24
    }
}
public extension UnsafeMutableRawBufferPointer {
    func writeU32(_ value: UInt32, at offset: Int) throws {
        guard offset >= 0, offset <= count, count-offset >= 4 else { throw CoreError.bounds }
        for i in 0..<4 { self[offset+i]=UInt8(truncatingIfNeeded: value >> (8*i)) }
    }
}

/// One store per actor. Borrowed pointers never survive a Wasm call, await, or closure return.
public actor Runtime {
    private let handle: OpaquePointer
    public init(module: Data) throws {
        guard let instance=module.withUnsafeBytes({ raw in sb_create(raw.bindMemory(to: UInt8.self).baseAddress, raw.count) }) else { throw CoreError.initialization }
        handle=instance
    }
    deinit { sb_destroy(handle) }
    private func call(_ name: String,_ arguments: [UInt32]=[]) throws -> UInt32 {
        var result: UInt32=0
        let status=arguments.withUnsafeBufferPointer { args in name.withCString { sb_call(handle,$0,args.baseAddress,args.count,&result) } }
        guard status==0 else { throw CoreError.host(status) };return result
    }
    private func memory(offset: UInt32,length: Int) throws -> UnsafeMutableRawBufferPointer {
        let total=sb_memory_size(handle),start=Int(offset)
        guard length>=0,start<=total,length<=total-start,let base=sb_memory(handle) else { throw CoreError.bounds }
        return UnsafeMutableRawBufferPointer(start:base.advanced(by:start),count:length)
    }
    /// Zero-copy result access. Do not return or retain pointers from this synchronous closure.
    public func withOutput<T: Sendable>(_ operation: Operation,input: Data,_ body: @Sendable (UnsafeRawBufferPointer) throws -> T) throws -> T {
        guard input.count<=65536 else { throw CoreError.invalidSize }
        let size=UInt32(max(1,input.count));let capacity:UInt32=operation == .embed ? 256 : operation == .normalize ? size : 4
        let a=try call("alloc",[size]);guard a != 0 else { throw CoreError.allocation }
        defer { _ = try? call("dealloc",[a,size]) }
        let b=try call("alloc",[capacity]);guard b != 0 else { throw CoreError.allocation }
        defer { _ = try? call("dealloc",[b,capacity]) }
        try input.withUnsafeBytes { src in try memory(offset:a,length:input.count).copyMemory(from:src) }
        let status=try call("execute_core_step",[operation.rawValue,a,UInt32(input.count),b,capacity]);guard status==0 else { throw CoreError.core(status) }
        return try body(UnsafeRawBufferPointer(try memory(offset:b,length:operation == .normalize ? input.count : Int(capacity))))
    }
    /// Convenience API copies only when owned output is requested.
    public func execute(_ operation: Operation,input: Data) -> Result<Data,CoreError> {
        do { return .success(try withOutput(operation,input:input) { Data($0) }) }
        catch let error as CoreError { return .failure(error) }
        catch { return .failure(.initialization) }
    }
    public func snapshot() throws -> Data { Data(try memory(offset:0,length:sb_memory_size(handle))) }
    public func tick() throws -> UInt32 { let offset=try call("metadata_ptr");return try UnsafeRawBufferPointer(memory(offset:offset,length:24)).readU32(at:8) }
    public func validateRange(offset: UInt32,length: Int) throws { _=try memory(offset:offset,length:length) }
}
