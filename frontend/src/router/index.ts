import { createRouter, createWebHistory } from 'vue-router'
import GalleryView from '../views/GalleryView.vue'
import ManageView from '../views/ManageView.vue'
import LoginView from '../views/LoginView.vue'
import { readAuthToken } from '../composables/useAuth'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', name: 'gallery', component: GalleryView, meta: { title: '浏览', requiresAuth: false } },
    { path: '/login', name: 'login', component: LoginView, meta: { title: '登录', requiresAuth: false } },
    { path: '/manage', name: 'manage', component: ManageView, meta: { title: '管理', requiresAuth: true } }
  ]
})

router.beforeEach((to) => {
  // 统一修改文档标题
  const t = to.meta?.title
  if (typeof t === 'string') {
    document.title = `${t} · 图床`
  }

  if (to.meta.requiresAuth && !readAuthToken()) {
    // 未登录，访问需要鉴权的页面 → 跳到登录页，附带回跳参数
    return { path: '/login', query: to.fullPath === '/login' ? undefined : { redirect: to.fullPath } }
  }
  // 已登录访问登录页 → 直接去管理
  if (to.path === '/login' && readAuthToken()) {
    return { path: '/manage' }
  }
})

export default router
