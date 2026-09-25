import {defineConfig} from 'vite';
import {resolve} from 'node:path';
import react from '@vitejs/plugin-react';
export default defineConfig({base:'./',plugins:[react()],build:{chunkSizeWarningLimit:1000,rollupOptions:{input:{main:resolve(process.cwd(),'index.html'),login:resolve(process.cwd(),'login.html')}}}});
