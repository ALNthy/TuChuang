import { ref } from 'vue'
import { getCategories } from '../api'
import type { Category } from '../types'

// 模块级共享状态：所有组件共用同一份分类列表，增删后统一刷新
const categories = ref<Category[]>([])
const loading = ref(false)
let initialized = false

async function refreshCategories(): Promise<void> {
  loading.value = true
  try {
    categories.value = await getCategories()
  } finally {
    loading.value = false
    initialized = true
  }
}

export function useCategories() {
  if (!initialized && !loading.value) {
    void refreshCategories()
  }
  return { categories, loading, refresh: refreshCategories }
}
