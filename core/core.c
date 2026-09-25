/* Freestanding wasm32 core. No imports, libc, WASI, network, clocks or RNG. */
typedef unsigned int u32;
typedef unsigned char u8;
#define META 131072u
#define HEAP 196608u
#define LIMIT 16777216u
#define MAX_INPUT 65536u
#define WORDS ((volatile u32 *)META)
enum { OK=0, BAD_POINTER=1, BAD_SIZE=2, BAD_OP=3, NO_SPACE=4, BAD_FREE=5, OVERFLOW=6 };
static u32 cursor=HEAP;
static u32 fail(u32 code) { WORDS[3]=code; return code; }
static u32 mix(u32 h,u32 b) { return (h^b)*16777619u; }
static u32 lower(u32 b) { return b>='A' && b<='Z' ? b+32 : b; }
static u32 letter(u32 b) { return (b>='a'&&b<='z') || (b>='0'&&b<='9'); }
static u32 span(u32 p,u32 n) {
  if(p<HEAP+8 || p>cursor || n>cursor-p) return 0;
  for(u32 at=HEAP;at<cursor;) {
    u32 size=*(u32 *)at;
    if(p>=at+8 && p<=at+8+size && n<=at+8+size-p) return 1;
    at+=8+((size+7)&~7u);
  }
  return 0;
}
__attribute__((export_name("reset_core"))) u32 reset_core(void) {
  for(u32 p=META;p<cursor;p++) *(u8 *)p=0;
  cursor=HEAP; WORDS[0]=0x534f5631; WORDS[1]=1; WORDS[4]=2166136261u;
  return 0;
}
__attribute__((export_name("metadata_ptr"))) u32 metadata_ptr(void) { return META; }
__attribute__((export_name("alloc"))) u32 alloc(u32 size) {
  if(!size || size>LIMIT-HEAP-8) { fail(BAD_SIZE); return 0; }
  u32 padded=(size+7)&~7u;
  if(cursor>LIMIT-8-padded) { fail(NO_SPACE); return 0; }
  u32 end=cursor+8+padded;
  u32 bytes=__builtin_wasm_memory_size(0)*65536u;
  if(end>bytes && __builtin_wasm_memory_grow(0,(end-bytes+65535u)/65536u)==(unsigned long)-1) { fail(NO_SPACE); return 0; }
  u32 p=cursor; *(u32 *)p=size; *(u32 *)(p+4)=p;
  for(u32 i=p+8;i<end;i++) *(u8 *)i=0;
  cursor=end; WORDS[3]=0; return p+8;
}
__attribute__((export_name("dealloc"))) u32 dealloc(u32 p,u32 size) {
  if(!span(p,size) || p<HEAP+8) return fail(BAD_FREE);
  u32 h=p-8;
  if(*(u32 *)h!=size || *(u32 *)(h+4)!=h || h+8+((size+7)&~7u)!=cursor) return fail(BAD_FREE);
  for(u32 i=h;i<cursor;i++) *(u8 *)i=0;
  cursor=h; return fail(OK);
}
/* op 1: FNV32, 2: 64-bin word histogram, 3: secret-label detector,
   4: ASCII normalization. All words are little endian. */
__attribute__((export_name("execute_core_step"))) u32 execute_core_step(u32 op,u32 input,u32 len,u32 output,u32 cap) {
  if(len>MAX_INPUT) return fail(BAD_SIZE);
  if(op<1 || op>4) return fail(BAD_OP);
  u32 required=op==2?256:op==4?len:4;
  if(!span(input,len) || !span(output,cap)) return fail(BAD_POINTER);
  if(cap<required) return fail(BAD_SIZE);
  if(input<output+cap && output<input+len) return fail(BAD_POINTER);
  if(WORDS[2]==0xffffffffu) return fail(OVERFLOW);
  u8 *src=(u8 *)input,*dst=(u8 *)output;
  for(u32 i=0;i<cap;i++) dst[i]=0;
  u32 hash=2166136261u;
  for(u32 i=0;i<len;i++) hash=mix(hash,src[i]);
  if(op==1) *(u32 *)dst=hash;
  if(op==2) {
    u32 word=2166136261u,active=0;
    for(u32 i=0;i<=len;i++) {
      u32 b=i<len?lower(src[i]):0;
      if(letter(b)) { word=mix(word,b); active=1; }
      else if(active) { ((u32 *)dst)[word%64]++; active=0; word=2166136261u; }
    }
  }
  if(op==3) {
    const char *a="password",*b="secret",*c="api_key";
    for(u32 i=0;i<len;i++) {
      u32 j=0; while(j<8 && i+j<len && lower(src[i+j])==(u32)a[j]) j++; if(j==8) *(u32 *)dst|=1;
      j=0; while(j<6 && i+j<len && lower(src[i+j])==(u32)b[j]) j++; if(j==6) *(u32 *)dst|=2;
      j=0; while(j<7 && i+j<len && lower(src[i+j])==(u32)c[j]) j++; if(j==7) *(u32 *)dst|=4;
    }
  }
  if(op==4) for(u32 i=0;i<len;i++) dst[i]=(u8)lower(src[i]);
  u32 chain=mix(mix(WORDS[4],op),hash);
  for(u32 i=0;i<required;i++) chain=mix(chain,dst[i]);
  WORDS[2]++; WORDS[3]=0; WORDS[4]=chain; WORDS[5]=required;
  return OK;
}
