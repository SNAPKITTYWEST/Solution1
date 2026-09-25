import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {Core,Workspace,cosine} from '../shared/engine.mjs';
const bytes=await readFile(new URL('../public/core.wasm',import.meta.url));
test('module has zero host imports',async()=>assert.deepEqual(WebAssembly.Module.imports(await WebAssembly.compile(bytes)),[]));
test('FNV known vector, normalization, secret rules and vectors',async()=>{
  const c=await Core.create(bytes);
  assert.equal(c.run('hash','hello').value,0x4f9f2cab);
  assert.equal(c.run('normalize','ABC 世界').value,'abc 世界');
  assert.equal(c.run('guard','PASSWORD secret api_key').value,7);
  assert.equal(c.run('guard','ordinary text').value,0);
  const a=c.run('embed','cat cat dog').value,b=c.run('embed','DOG cat cat').value;
  assert.deepEqual(a,b);assert.equal(a.reduce((x,y)=>x+y),3);assert.ok(cosine(a,b)>0.99999);
});
test('500 deterministic property cases match entire memory bit for bit',async()=>{
  const a=await Core.create(bytes),b=await Core.create(bytes);let seed=7;
  for(let i=0;i<500;i++){
    seed=(Math.imul(seed,1664525)+1013904223)>>>0;
    const text=`case ${seed} ${'A'.repeat(seed%100)}`,op=['hash','embed','guard','normalize'][i%4];
    assert.deepEqual(a.run(op,text),b.run(op,text));
    assert.deepEqual(new Uint8Array(a.exports.memory.buffer),new Uint8Array(b.exports.memory.buffer));
  }
});
test('pointer, capacity, overlap, integer overflow and tick overflow fail without advancing',async()=>{
  const {exports:e}=await Core.create(bytes),p=e.alloc(16),o=e.alloc(256);
  assert.equal(e.execute_core_step(1,0xffffffff,8,o,4),1);
  assert.equal(e.execute_core_step(1,p,65537,o,4),2);
  assert.equal(e.execute_core_step(2,p,8,o,4),2);
  assert.equal(e.execute_core_step(9,p,8,o,4),3);
  assert.equal(e.execute_core_step(1,p,8,p,8),1);
  assert.equal(e.alloc(0xffffffff),0);
  const m=new DataView(e.memory.buffer,e.metadata_ptr(),24);assert.equal(m.getUint32(8,true),0);
  m.setUint32(8,0xffffffff,true);assert.equal(e.execute_core_step(1,p,8,o,4),6);
  assert.equal(m.getUint32(8,true),0xffffffff);
});
test('LIFO free validates sizes, rejects stale pointers and zeroes released memory',async()=>{
  const {exports:e}=await Core.create(bytes),a=e.alloc(8),b=e.alloc(8);
  assert.equal(e.dealloc(a,8),5);assert.equal(e.dealloc(b,7),5);
  new Uint8Array(e.memory.buffer,b,8).fill(255);assert.equal(e.dealloc(b,8),0);
  assert.equal(e.dealloc(b,8),5);assert.equal(e.execute_core_step(1,b,8,a,8),1);
  assert.equal(e.alloc(8),b);assert.deepEqual([...new Uint8Array(e.memory.buffer,b,8)],Array(8).fill(0));
});
test('memory grows in pages, maximum enforced, maximum input accepted',async()=>{
  const c=await Core.create(bytes),e=c.exports;
  assert.equal(c.run('normalize','a'.repeat(65536)).value.length,65536);
  assert.throws(()=>c.run('hash','x'.repeat(65537)),/exceeds/);
  const p=e.alloc(200000);assert.ok(p);assert.equal(e.memory.buffer.byteLength%65536,0);
  assert.equal(e.alloc(16777216),0);assert.equal(e.dealloc(p,200000),0);
});
test('retrieval ranks sources; guard blocks; flow stops at a denial',async()=>{
  const w=new Workspace(await Core.create(bytes));w.ingest('cats','cat cat feline');w.ingest('ships','ocean boat');
  assert.equal(w.retrieve('cat')[0].title,'cats');assert.ok(w.answer('cat').text.includes('[1]'));
  assert.equal(w.answer('password').blocked,true);
  assert.equal(w.flow('secret',['normalize','guard','hash']).length,2);
  assert.throws(()=>w.flow('x',['unknown']),/Unknown/);
  assert.throws(()=>w.ingest('',''),/required/);
});
