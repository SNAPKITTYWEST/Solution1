import test from 'node:test';
import assert from 'node:assert/strict';
import {spawn,spawnSync} from 'node:child_process';
import {randomBytes,createHmac} from 'node:crypto';
import {existsSync,readFileSync} from 'node:fs';
import {Core} from '../shared/engine.mjs';
const dotnet=process.env.DOTNET || (process.platform==='win32'&&existsSync('C:/Users/jessi/.dotnet/dotnet.exe')?'C:/Users/jessi/.dotnet/dotnet.exe':'dotnet');
test('JWT HTTP boundary, isolated retrieval, Wasm/.NET parity and provider failure',async()=>{
 const dll='Sovereign.Host/bin/Release/net8.0/Sovereign.Host.dll';
 if(!existsSync(dll)) throw Error('Build Solution1.sln -c Release before service integration tests');
 const secret=randomBytes(32).toString('hex');const env={...process.env,SOVEREIGN_JWT_SECRET:secret,SOVEREIGN_MODEL:'',SOVEREIGN_ALLOWED_ORIGIN:'http://127.0.0.1:4173'};
 const issue=spawnSync(dotnet,[dll,'token'],{env,encoding:'utf8'});assert.equal(issue.status,0,issue.stderr);const token=issue.stdout.trim();
 const server=spawn(dotnet,[dll,'serve'],{env,stdio:'pipe'});let logs='';server.stdout.on('data',d=>logs+=d);server.stderr.on('data',d=>logs+=d);
 const base='http://127.0.0.1:5080';
 const call=(path,body,auth=token)=>fetch(base+path,{method:body?'POST':'GET',headers:{Authorization:`Bearer ${auth}`,'Content-Type':'application/json'},body:body?JSON.stringify(body):undefined});
 try{
  let ready=false;for(let i=0;i<80;i++){try{const r=await call('/api/status');if(r.status===200){ready=true;break}}catch{}await new Promise(r=>setTimeout(r,100));}assert.ok(ready,logs);
  assert.equal((await call('/api/status',null,'invalid')).status,401);
  assert.equal((await call('/api/status',null,token.slice(0,-8)+'AAAAAAAA')).status,401);
  const preflight=await fetch(base+'/api/tools',{method:'OPTIONS',headers:{Origin:env.SOVEREIGN_ALLOWED_ORIGIN,'Access-Control-Request-Method':'POST','Access-Control-Request-Headers':'authorization,content-type'}});
  assert.equal(preflight.headers.get('access-control-allow-origin'),env.SOVEREIGN_ALLOWED_ORIGIN);
  const core=await Core.create(readFileSync('public/core.wasm'));
  for(const operation of ['hash','embed','guard','normalize'])for(const text of ['hello','CAT dog secret','世界 API_KEY','']){
   const r=await call('/api/tools',{operation,text});assert.equal(r.status,200);assert.deepEqual((await r.json()).value,core.run(operation,text).value);
  }
  assert.equal((await call('/api/documents',{title:'cats',text:'cat feline'})).status,200);
  const found=await (await call('/api/retrieve',{prompt:'cat'})).json();assert.equal(found[0].title,'cats');
  const now=Math.floor(Date.now()/1000);const h=Buffer.from(JSON.stringify({alg:'HS256',typ:'JWT'})).toString('base64url');
  const p=Buffer.from(JSON.stringify({iss:'sovereign',aud:'sovereign-api',scope:'playground',sub:'different-user',iat:now,nbf:now,exp:now+900})).toString('base64url');
  const other=`${h}.${p}.${createHmac('sha256',secret).update(`${h}.${p}`).digest('base64url')}`;
  assert.deepEqual(await (await call('/api/retrieve',{prompt:'cat'},other)).json(),[]);
  assert.equal((await call('/api/invoke',{prompt:'hello'})).status,503);
  assert.equal((await call('/api/invoke',{prompt:'password'})).status,400);
  assert.equal((await call('/api/tools',{operation:'hash',text:null})).status,400);
  assert.equal((await call('/api/batch',{operation:'hash',texts:[]})).status,400);
 }finally{server.kill();await new Promise(resolve=>{if(server.exitCode!==null)resolve();else server.once('exit',resolve)});}
});
