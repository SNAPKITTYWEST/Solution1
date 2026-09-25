import { spawnSync } from 'node:child_process';
import { mkdirSync, existsSync } from 'node:fs';
const clang=process.env.CLANG || (process.platform==='win32' && existsSync('C:/Program Files/LLVM/bin/clang.exe') ? 'C:/Program Files/LLVM/bin/clang.exe':'clang');
mkdirSync('public',{recursive:true});
const args=['--target=wasm32-unknown-unknown','-O2','-nostdlib','-fno-builtin','-Wl,--no-entry','-Wl,--export-memory','-Wl,--stack-first','-Wl,-z,stack-size=65536','-Wl,--initial-memory=262144','-Wl,--max-memory=16777216','-o','public/core.wasm','core/core.c'];
if(process.env.WASM_LD) args.unshift('--ld-path='+process.env.WASM_LD);
const result=spawnSync(clang,args,{stdio:'inherit'});
if(result.error) throw result.error;
process.exit(result.status??1);
