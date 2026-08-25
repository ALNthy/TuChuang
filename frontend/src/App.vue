<script setup lang="ts">
import { ref, onErrorCaptured, onMounted, watch } from 'vue'
import { RouterLink, useRouter } from 'vue-router'
import { useAuth } from './composables/useAuth'

const router = useRouter()
const { isLoggedIn, username, logout } = useAuth()

function onLoginClick() { router.push('/login') }
function onLogoutClick() { logout(true) }

// ========== 全局错误边界 ==========
const error = ref<Error | null>(null)

onErrorCaptured((err) => {
  error.value = err instanceof Error ? err : new Error(String(err))
  console.error('[ErrorBoundary]', err)
  return false // 阻止错误继续向上冒泡
})

function retry() {
  error.value = null
  // 强制重新加载当前路由组件
  router.replace({ path: router.currentRoute.value.path, query: { _t: Date.now() } })
}

// ========== 主题切换（日间 / 夜间） ==========
type Theme = 'light' | 'dark'
const theme = ref<Theme>('light')
const THEME_KEY = 'tuchuang-theme'

function applyTheme(t: Theme) {
  document.documentElement.setAttribute('data-theme', t)
}

function toggleTheme() {
  theme.value = theme.value === 'dark' ? 'light' : 'dark'
}

watch(theme, (t) => {
  applyTheme(t)
  localStorage.setItem(THEME_KEY, t)
})

onMounted(() => {
  // 读取持久化主题；未设置时跟随系统偏好
  const saved = localStorage.getItem(THEME_KEY) as Theme | null
  if (saved === 'light' || saved === 'dark') {
    theme.value = saved
  } else if (window.matchMedia('(prefers-color-scheme: dark)').matches) {
    theme.value = 'dark'
  }
  applyTheme(theme.value)
})
</script>

<template>
  <div class="app">
    <header class="header">
      <div class="brand">
        <h1>📷 图床</h1>
        <p class="subtitle">ASP.NET Core + Vue 3 + TypeScript + SQLite</p>
      </div>
      <div class="header-right">
        <nav class="nav">
          <RouterLink to="/" exact-active-class="active">浏览</RouterLink>
          <RouterLink to="/manage" active-class="active">管理</RouterLink>
        </nav>
        <button
          class="theme-toggle"
          :title="theme === 'dark' ? '切换到日间模式' : '切换到夜间模式'"
          :aria-label="theme === 'dark' ? '切换到日间模式' : '切换到夜间模式'"
          @click="toggleTheme"
          type="button"
        >
          <span v-if="theme === 'dark'">☀️</span>
          <span v-else>🌙</span>
        </button>
        <div class="auth-area">
          <template v-if="isLoggedIn">
            <span class="auth-user">{{ username }}</span>
            <button class="btn btn-ghost" @click="onLogoutClick" type="button">退出</button>
          </template>
          <template v-else>
            <button class="btn btn-primary-sm" @click="onLoginClick" type="button">登录</button>
          </template>
        </div>
      </div>
    </header>

    <!-- 全局错误边界兜底 UI -->
    <div v-if="error" class="error-boundary">
      <div class="error-card">
        <div class="error-icon">⚠️</div>
        <h2>页面出错了</h2>
        <p class="error-message">{{ error.message || '发生了未知错误' }}</p>
        <p class="error-hint">可以尝试刷新页面，或返回浏览页继续使用</p>
        <div class="error-actions">
          <button class="btn btn-primary-sm" @click="retry" type="button">刷新重试</button>
          <RouterLink to="/" class="btn btn-ghost">返回浏览页</RouterLink>
        </div>
      </div>
    </div>

    <RouterView v-else />

    <footer class="footer">
      <span>默认按上传时间倒序排列</span>
    </footer>
  </div>
</template>

<style scoped>
.header-right {
  display: flex;
  align-items: center;
  gap: 18px;
}

.auth-area {
  display: flex;
  align-items: center;
  gap: 10px;
}

.auth-user {
  color: var(--text);
  font-size: 13px;
  font-weight: 600;
  padding: 6px 14px;
  background: linear-gradient(135deg, var(--surface-strong), var(--surface));
  border: 1px solid var(--surface-border);
  backdrop-filter: saturate(180%) blur(6px);
  -webkit-backdrop-filter: saturate(180%) blur(6px);
  border-radius: var(--r-pill);
  box-shadow: var(--shadow-xs);
}

.theme-toggle {
  width: 38px;
  height: 38px;
  border-radius: 50%;
  border: 1px solid var(--surface-border);
  background: var(--surface);
  backdrop-filter: saturate(180%) blur(6px);
  -webkit-backdrop-filter: saturate(180%) blur(6px);
  cursor: pointer;
  font-size: 18px;
  line-height: 1;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  box-shadow: var(--shadow-xs);
  transition: transform var(--t-fast) var(--ease), background var(--t-fast) var(--ease), box-shadow var(--t-fast) var(--ease);
}
.theme-toggle:hover {
  transform: translateY(-1px) rotate(8deg);
  background: var(--surface-strong);
  box-shadow: var(--shadow-sm);
}
.theme-toggle:active { transform: translateY(0); }

.btn {
  border: none;
  border-radius: var(--r-pill);
  padding: 8px 16px;
  font-size: 13px;
  font-weight: 600;
  cursor: pointer;
  transition: transform var(--t-fast) var(--ease), background var(--t-fast) var(--ease), box-shadow var(--t-fast) var(--ease), opacity var(--t-fast) var(--ease);
}

.btn-primary-sm {
  background: linear-gradient(135deg, var(--accent) 0%, #8b5cf6 100%);
  color: #fff;
  box-shadow: 0 6px 16px rgba(99, 102, 241, 0.30);
}
.btn-primary-sm:hover { transform: translateY(-1px); box-shadow: 0 8px 20px rgba(99, 102, 241, 0.38); }
.btn-primary-sm:active { transform: translateY(0); }

.btn-ghost {
  background: var(--surface-strong);
  color: var(--text-muted);
  border: 1px solid var(--surface-border);
  backdrop-filter: saturate(180%) blur(6px);
  -webkit-backdrop-filter: saturate(180%) blur(6px);
}
.btn-ghost:hover {
  background: var(--card-solid);
  color: var(--text);
  box-shadow: var(--shadow-xs);
  transform: translateY(-0.5px);
}

/* ========== 错误边界兜底 UI ========== */
.error-boundary {
  display: flex;
  justify-content: center;
  align-items: center;
  min-height: 50vh;
  padding: 40px 20px;
}
.error-card {
  text-align: center;
  padding: 40px 32px;
  max-width: 440px;
  background: var(--glass-bg);
  border: 1px solid var(--glass-border);
  border-radius: var(--r-xl);
  backdrop-filter: var(--glass-blur);
  -webkit-backdrop-filter: var(--glass-blur);
  box-shadow: var(--shadow-md);
}
.error-icon {
  font-size: 48px;
  line-height: 1;
  margin-bottom: 16px;
}
.error-card h2 {
  margin: 0 0 12px;
  font-size: 20px;
  font-weight: 700;
  color: var(--text);
}
.error-message {
  margin: 0 0 8px;
  font-size: 14px;
  color: var(--error);
  font-weight: 500;
  word-break: break-word;
}
.error-hint {
  margin: 0 0 24px;
  font-size: 13px;
  color: var(--text-muted);
}
.error-actions {
  display: flex;
  gap: 12px;
  justify-content: center;
  flex-wrap: wrap;
}
.error-actions .btn {
  text-decoration: none;
  display: inline-flex;
  align-items: center;
}
</style>
