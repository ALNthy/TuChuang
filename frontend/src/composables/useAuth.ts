import { computed, ref } from 'vue'
import router from '../router'

const TOKEN_KEY = 'tuchuang_token'
const USER_KEY = 'tuchuang_username'

// 模块级单例状态：同一页面内所有组件共享
const token = ref<string>(localStorage.getItem(TOKEN_KEY) || '')
const username = ref<string>(localStorage.getItem(USER_KEY) || '')

export function useAuth() {
  const isLoggedIn = computed(() => !!token.value)

  function setAuth(newToken: string, newUser: string) {
    token.value = newToken
    username.value = newUser
    localStorage.setItem(TOKEN_KEY, newToken)
    localStorage.setItem(USER_KEY, newUser)
  }

  function logout(redirect = true) {
    token.value = ''
    username.value = ''
    localStorage.removeItem(TOKEN_KEY)
    localStorage.removeItem(USER_KEY)
    if (redirect) {
      // 清理完再用 router.replace 跳到登录页（避免history 栈残留管理页）
      router.replace({ path: '/login' })
    }
  }

  function getAuthHeaders(): Record<string, string> {
    const t = token.value || localStorage.getItem(TOKEN_KEY)
    return t ? { Authorization: `Bearer ${t}` } : {}
  }

  return { token, username, isLoggedIn, setAuth, logout, getAuthHeaders }
}

// 给纯 fetch 封装用（不进 vue 上下文）
export function readAuthToken(): string | null {
  return localStorage.getItem(TOKEN_KEY)
}

export function clearAuthLocal(): void {
  localStorage.removeItem(TOKEN_KEY)
  localStorage.removeItem(USER_KEY)
}
