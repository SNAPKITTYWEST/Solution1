import {readFile} from 'node:fs/promises';
import {Core} from '../shared/engine.mjs';
const core=await Core.create(await readFile('public/core.wasm'));
const text='Deterministic local execution, bounded memory, no cloud account. '.repeat(64);
for(let i=0;i<100;i++)core.run('embed',text);
const count=10000,start=performance.now();for(let i=0;i<count;i++)core.run('embed',text);
const ms=performance.now()-start;
console.log(JSON.stringify({operation:'64-bin histogram',iterations:count,bytesPerInput:new TextEncoder().encode(text).length,totalMs:ms,meanMicroseconds:ms*1000/count,runtime:process.version},null,2));
