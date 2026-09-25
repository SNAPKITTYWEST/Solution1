#ifndef SOVEREIGN_BRIDGE_H
#define SOVEREIGN_BRIDGE_H
#include <stddef.h>
#include <stdint.h>
typedef struct sb_runtime sb_runtime;
sb_runtime *sb_create(const uint8_t *wasm, size_t length);
void sb_destroy(sb_runtime *runtime);
int sb_call(sb_runtime *runtime, const char *name, const uint32_t *args, size_t count, uint32_t *result);
uint8_t *sb_memory(sb_runtime *runtime);
size_t sb_memory_size(sb_runtime *runtime);
#endif
