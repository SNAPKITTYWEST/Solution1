#!/usr/bin/env node
import {readFile} from 'node:fs/promises';
import {Core,Workspace} from '../shared/engine.mjs';
const [command='help',...args]=process.argv.slice(2);
if(command==='help'){console.log('sovereign <hash|embed|guard|normalize|flow> "text" [normalize,guard,hash]\nRuns the same Wasm module as the Pages playground.');}
else {
  try {
    const core=await Core.create(await readFile(new URL('../public/core.wasm',import.meta.url)));
    const workspace=new Workspace(core);
    const result=command==='flow'?workspace.flow(args[0]??'',(args[1]??'normalize,guard,hash').split(',')):workspace.run(command,args.join(' '));
    console.log(JSON.stringify(result,null,2));
  }catch(error){console.error(error.message);process.exitCode=1;}
}
