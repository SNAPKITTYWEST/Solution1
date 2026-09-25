#include "sovereign.h"
#include <wasmtime.h>
#include <stdlib.h>
#include <string.h>
struct sb_runtime { wasm_engine_t *engine; wasmtime_store_t *store; wasmtime_module_t *module; wasmtime_instance_t instance; wasmtime_memory_t memory; };
void sb_destroy(sb_runtime *r) { if(!r)return; if(r->store)wasmtime_store_delete(r->store);if(r->module)wasmtime_module_delete(r->module);if(r->engine)wasm_engine_delete(r->engine);free(r); }
int sb_call(sb_runtime *r,const char *name,const uint32_t *args,size_t count,uint32_t *result) {
  if(!r||!result||count>5)return 1;
  wasmtime_context_t *ctx=wasmtime_store_context(r->store);
  wasmtime_extern_t item;
  if(!wasmtime_instance_export_get(ctx,&r->instance,name,strlen(name),&item))return 2;
  if(item.kind!=WASMTIME_EXTERN_FUNC){wasmtime_extern_delete(&item);return 3;}
  wasmtime_val_t in[5],out;
  for(size_t i=0;i<count;i++){in[i].kind=WASMTIME_I32;in[i].of.i32=(int32_t)args[i];}
  wasmtime_error_t *fuel=wasmtime_context_set_fuel(ctx,20000000);
  if(fuel){wasmtime_error_delete(fuel);wasmtime_extern_delete(&item);return 4;}
  wasm_trap_t *trap=NULL;
  wasmtime_error_t *error=wasmtime_func_call(ctx,&item.of.func,in,count,&out,1,&trap);
  wasmtime_extern_delete(&item);
  if(error){wasmtime_error_delete(error);if(trap)wasm_trap_delete(trap);return 4;}
  if(trap){wasm_trap_delete(trap);return 5;}
  if(out.kind!=WASMTIME_I32){wasmtime_val_unroot(&out);return 6;}
  *result=(uint32_t)out.of.i32;return 0;
}
sb_runtime *sb_create(const uint8_t *wasm,size_t length) {
  if(!wasm||length==0)return NULL;
  sb_runtime *r=calloc(1,sizeof(*r));if(!r)return NULL;
  wasm_config_t *config=wasm_config_new();wasmtime_config_consume_fuel_set(config,true);wasmtime_config_max_wasm_stack_set(config,65536);
  r->engine=wasm_engine_new_with_config(config);if(!r->engine){sb_destroy(r);return NULL;}
  wasmtime_error_t *error=wasmtime_module_new(r->engine,wasm,length,&r->module);
  if(error){wasmtime_error_delete(error);sb_destroy(r);return NULL;}
  wasm_importtype_vec_t imports;wasmtime_module_imports(r->module,&imports);size_t import_count=imports.size;wasm_importtype_vec_delete(&imports);
  if(import_count){sb_destroy(r);return NULL;}
  r->store=wasmtime_store_new(r->engine,NULL,NULL);if(!r->store){sb_destroy(r);return NULL;}
  wasmtime_context_t *ctx=wasmtime_store_context(r->store);wasm_trap_t *trap=NULL;
  error=wasmtime_instance_new(ctx,r->module,NULL,0,&r->instance,&trap);
  if(error||trap){if(error)wasmtime_error_delete(error);if(trap)wasm_trap_delete(trap);sb_destroy(r);return NULL;}
  wasmtime_extern_t mem;
  if(!wasmtime_instance_export_get(ctx,&r->instance,"memory",6,&mem)){sb_destroy(r);return NULL;}
  if(mem.kind!=WASMTIME_EXTERN_MEMORY){wasmtime_extern_delete(&mem);sb_destroy(r);return NULL;}
  r->memory=mem.of.memory;wasmtime_extern_delete(&mem);
  uint32_t result;if(sb_call(r,"reset_core",NULL,0,&result)||result){sb_destroy(r);return NULL;}
  return r;
}
uint8_t *sb_memory(sb_runtime *r){return wasmtime_memory_data(wasmtime_store_context(r->store),&r->memory);}
size_t sb_memory_size(sb_runtime *r){return wasmtime_memory_data_size(wasmtime_store_context(r->store),&r->memory);}
