export const OPERATIONS={hash:1,embed:2,guard:3,normalize:4};
export class Core {
  constructor(instance) { this.exports=instance.exports; this.exports.reset_core(); }
  static async create(bytes) { const {instance}=await WebAssembly.instantiate(bytes,{}); return new Core(instance); }
  run(operation,text) {
    const op=OPERATIONS[operation]; if(!op) throw new Error('Unknown core operation');
    const input=new TextEncoder().encode(text); if(input.length>65536) throw new Error('Input exceeds 65536 bytes');
    const size=Math.max(input.length,1), capacity=op===2?256:op===4?size:4;
    const a=this.exports.alloc(size); if(!a) throw new Error('Input allocation failed');
    const b=this.exports.alloc(capacity);
    if(!b) { this.exports.dealloc(a,size); throw new Error('Output allocation failed'); }
    try {
      new Uint8Array(this.exports.memory.buffer,a,input.length).set(input);
      const status=this.exports.execute_core_step(op,a,input.length,b,capacity);
      if(status) throw new Error(`Core error ${status}`);
      const view=new DataView(this.exports.memory.buffer,b,capacity);
      const value=op===2?Array.from({length:64},(_,i)=>view.getUint32(i*4,true)):op===4?new TextDecoder().decode(new Uint8Array(this.exports.memory.buffer,b,input.length)):view.getUint32(0,true);
      const metadata=new DataView(this.exports.memory.buffer,this.exports.metadata_ptr(),24);
      return {value,tick:metadata.getUint32(8,true),seal:metadata.getUint32(16,true).toString(16).padStart(8,'0')};
    } finally { this.exports.dealloc(b,capacity); this.exports.dealloc(a,size); }
  }
}
export function cosine(a,b) {
  if(a.length!==b.length) throw new Error('Vector dimensions differ');
  let dot=0,aa=0,bb=0;
  for(let i=0;i<a.length;i++){dot+=a[i]*b[i];aa+=a[i]*a[i];bb+=b[i]*b[i];}
  return aa&&bb?dot/Math.sqrt(aa*bb):0;
}
export class Workspace {
  constructor(core) { this.core=core; this.documents=[]; this.events=[]; }
  record(tool,result) { this.events.push({tool,tick:result.tick,seal:result.seal}); return result; }
  run(tool,text) { return this.record(tool,this.core.run(tool,text)); }
  ingest(title,text) {
    if(!title.trim()||!text.trim()) throw new Error('Document title and text are required');
    if(this.documents.length>=100) throw new Error('Document limit is 100');
    const result=this.run('embed',text);
    const doc={id:this.documents.length+1,title,text,vector:result.value}; this.documents.push(doc); return doc;
  }
  retrieve(query) {
    const vector=this.run('embed',query).value;
    return this.documents.map(d=>({id:d.id,title:d.title,text:d.text,score:cosine(vector,d.vector)})).sort((a,b)=>b.score-a.score||a.id-b.id).slice(0,5);
  }
  answer(query) {
    if(this.run('guard',query).value) return {text:'Blocked by the configured secret-label rule.',blocked:true,sources:[]};
    const sources=this.retrieve(query).filter(d=>d.score>0);
    return {text:sources.length?sources.map(d=>`[${d.id}] ${d.title}\n${d.text}`).join('\n\n'):'No matching documents. Add a document to the knowledge base, or connect a local model in Settings.',sources,blocked:false};
  }
  flow(text,steps) {
    if(!Array.isArray(steps)||steps.length<1||steps.length>16) throw new Error('Flow requires 1–16 steps');
    let current=text; const results=[];
    for(const step of steps) {
      if(!Object.hasOwn(OPERATIONS,step)) throw new Error(`Unknown step: ${step}`);
      const result=this.run(step,current); results.push({step,...result});
      if(step==='guard'&&result.value) break;
      if(step==='normalize') current=result.value;
    }
    return results;
  }
}
