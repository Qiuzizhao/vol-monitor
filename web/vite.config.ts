import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
export default defineConfig({
  plugins: [vue()],
  build: { outDir: '../src/Noise.Server/wwwroot', emptyOutDir: true },
  server: { proxy: { '/api': 'http://127.0.0.1:5080', '/ws': { target: 'ws://127.0.0.1:5080', ws: true } } }
});
