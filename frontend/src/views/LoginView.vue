<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { login as apiLogin } from '../api'
import { useAuth } from '../composables/useAuth'

const username = ref('admin')
const password = ref('admin123')
const loading = ref(false)
const error = ref('')

const { setAuth, isLoggedIn } = useAuth()
const router = useRouter()
const route = useRoute()

onMounted(() => {
  // 已经登录过 → 直接去管理页
  if (isLoggedIn.value) {
    const redirect = (route.query.redirect as string | undefined) || '/manage'
    router.replace(redirect)
  }
})

async function submit(e: Event) {
  e.preventDefault()
  error.value = ''
  if (!username.value.trim() || !password.value) {
    error.value = '请输入账号和密码'
    return
  }
  loading.value = true
  try {
    const resp = await apiLogin(username.value.trim(), password.value)
    setAuth(resp.token, resp.username)
    const redirect = (route.query.redirect as string | undefined) || '/manage'
    router.replace(redirect)
  } catch (err) {
    const msg = err instanceof Error ? err.message : '登录失败'
    error.value = msg.replace(/^请求失败 \(\d+\): /, '')
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <main class="login-page">
    <form class="login-card" @submit="submit" novalidate>
      <h2>🔐 管理员登录</h2>
      <p class="hint">上传、删除图片和管理分类都需要管理员权限</p>

      <label class="field">
        <span>账号</span>
        <input v-model="username" type="text" autocomplete="username" placeholder="请输入账号" />
      </label>

      <label class="field">
        <span>密码</span>
        <input v-model="password" type="password" autocomplete="current-password" placeholder="请输入密码" />
      </label>

      <div v-if="error" class="error">{{ error }}</div>

      <button class="btn-primary" type="submit" :disabled="loading">
        {{ loading ? '登录中…' : '登录' }}
      </button>

      <p class="default-tip">默认账号：<strong>admin</strong> / <strong>admin123</strong><br/>（可在后端 <code>appsettings.json</code> 中修改）</p>
    </form>
  </main>
</template>

<style scoped>
.login-page {
  position: relative;
  min-height: calc(100vh - 200px);
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 32px 20px;
  overflow: hidden;
}
.login-page::before,
.login-page::after {
  content: '';
  position: absolute;
  border-radius: 50%;
  filter: blur(60px);
  pointer-events: none;
  z-index: 0;
}
.login-page::before {
  width: 360px; height: 360px;
  left: -80px; top: -60px;
  background: radial-gradient(circle, rgba(99, 102, 241, 0.32), transparent 65%);
}
.login-page::after {
  width: 420px; height: 420px;
  right: -120px; bottom: -100px;
  background: radial-gradient(circle, rgba(236, 72, 153, 0.26), transparent 65%);
}

.login-card {
  position: relative;
  z-index: 1;
  width: 100%;
  max-width: 400px;
  background: var(--glass-bg);
  border: 1px solid var(--glass-border);
  border-radius: calc(var(--r-xl) + 4px);
  padding: 32px 28px;
  backdrop-filter: saturate(180%) blur(18px);
  -webkit-backdrop-filter: saturate(180%) blur(18px);
  box-shadow: var(--shadow-md);
  display: flex;
  flex-direction: column;
  gap: 16px;
}
.login-card h2 {
  margin: 0 0 -2px;
  font-size: 22px;
  font-weight: 800;
  letter-spacing: -0.01em;
  background: linear-gradient(135deg, var(--accent) 0%, #ec4899 50%, #06b6d4 100%);
  -webkit-background-clip: text;
  background-clip: text;
  color: transparent;
}

.hint {
  margin: 0;
  color: var(--muted);
  font-size: 13.5px;
  line-height: 1.6;
}

.field {
  display: flex;
  flex-direction: column;
  gap: 6px;
  font-size: 13px;
}

.field > span {
  color: var(--muted);
  font-weight: 600;
  font-size: 12.5px;
  letter-spacing: 0.02em;
  text-transform: uppercase;
}

.field input {
  border: 1px solid var(--border);
  background: var(--input-bg);
  color: var(--text);
  border-radius: var(--r-md);
  padding: 11px 14px;
  font-size: 14px;
  outline: none;
  transition: border-color var(--t-fast) var(--ease), box-shadow var(--t-fast) var(--ease), background var(--t-fast) var(--ease);
}
.field input:hover { border-color: #c7d2fe; }
.field input:focus {
  border-color: var(--accent);
  background: #fff;
  box-shadow: var(--shadow-ring);
}

.error {
  margin: 0;
  padding: 10px 14px;
  background: linear-gradient(180deg, rgba(254, 226, 226, 0.92), rgba(254, 242, 242, 0.75));
  color: var(--error);
  border: 1px solid rgba(254, 202, 202, 0.92);
  border-radius: var(--r-md);
  font-size: 13px;
  font-weight: 500;
  box-shadow: var(--shadow-xs);
}

.btn-primary {
  margin-top: 4px;
  background: linear-gradient(135deg, var(--accent) 0%, #8b5cf6 50%, #ec4899 100%);
  color: #fff;
  border: none;
  border-radius: var(--r-md);
  padding: 11px 16px;
  font-size: 15px;
  font-weight: 700;
  letter-spacing: 0.02em;
  cursor: pointer;
  box-shadow: 0 10px 24px rgba(99, 102, 241, 0.32);
  transition: transform var(--t-fast) var(--ease), filter var(--t-fast) var(--ease), box-shadow var(--t-fast) var(--ease), opacity var(--t-fast) var(--ease);
}
.btn-primary:hover:not(:disabled) {
  transform: translateY(-1px);
  filter: brightness(1.05);
  box-shadow: 0 14px 32px rgba(139, 92, 246, 0.38);
}
.btn-primary:active:not(:disabled) { transform: translateY(0); }
.btn-primary:disabled {
  opacity: 0.6;
  cursor: not-allowed;
  filter: none;
  box-shadow: none;
  transform: none;
}

.default-tip {
  margin: 0;
  color: var(--muted);
  font-size: 12.5px;
  line-height: 1.7;
  border-top: 1px dashed rgba(148, 163, 184, 0.35);
  padding-top: 14px;
}
.default-tip strong {
  color: var(--text);
  font-weight: 700;
  padding: 1px 6px;
  background: rgba(99,102,241,0.08);
  border-radius: 6px;
}
.default-tip code {
  background: var(--input-bg);
  padding: 1px 6px;
  border-radius: 6px;
  font-size: 12px;
  border: 1px solid var(--border);
  color: var(--text);
}

@media (max-width: 480px) {
  .login-page { padding: 16px; }
  .login-card { padding: 24px 18px; }
  .login-card h2 { font-size: 20px; }
  .field input { font-size: 15px; }
}
</style>
