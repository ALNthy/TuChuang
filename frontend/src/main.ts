import { createApp } from 'vue'
import App from './App.vue'
import router from './router'
import './style.css'

const app = createApp(App)

// 全局错误兜底：处理 onErrorCaptured 未捕获的异常（如异步回调、Promise reject）
app.config.errorHandler = (err) => {
  console.error('[GlobalErrorHandler]', err)
}

app.use(router).mount('#app')
