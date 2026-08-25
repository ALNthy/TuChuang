import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// 开发环境：前端跑在 5173，通过代理转发到 ASP.NET Core 后端（5000）
export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:5000', changeOrigin: true },
      '/uploads': { target: 'http://localhost:5000', changeOrigin: true }
    }
  }
})
