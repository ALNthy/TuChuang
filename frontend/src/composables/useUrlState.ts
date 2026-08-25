import { useRoute, useRouter } from 'vue-router'
import type { Ref } from 'vue'
import { ALL_CATEGORY } from '../types'

interface UrlStateRefs {
  keyword: Ref<string>
  activeCategory: Ref<string>
  page: Ref<number>
  pageSize: Ref<number>
}

/**
 * 把搜索/分页状态（关键词、分类、页码、每页条数）同步到 URL query，
 * 刷新页面或分享链接时可还原上下文。
 *
 * 用法：
 *   const { restoreFromUrl, syncToUrl } = useUrlState({ keyword, activeCategory, page, pageSize })
 *   // 必须在 watch 注册之前调用 restoreFromUrl，避免初始化时触发 watch 造成重复刷新
 *   const initialPage = restoreFromUrl()
 *   // 在 refresh() 末尾调用 syncToUrl() 把当前状态写回 URL
 */
export function useUrlState(refs: UrlStateRefs) {
  const route = useRoute()
  const router = useRouter()

  /** 默认每页条数：与视图中保持一致 */
  const DEFAULT_PAGE_SIZE = 20

  /** 从 URL query 还原到 ref，返回应跳转的页码 */
  function restoreFromUrl(): number {
    const q = route.query
    if (typeof q.keyword === 'string' && q.keyword) refs.keyword.value = q.keyword
    if (typeof q.category === 'string' && q.category && q.category !== ALL_CATEGORY) {
      refs.activeCategory.value = q.category
    }
    if (typeof q.pageSize === 'string') {
      const n = Number(q.pageSize)
      if (Number.isFinite(n) && n > 0) refs.pageSize.value = n
    }
    let targetPage = 1
    if (typeof q.page === 'string') {
      const n = Number(q.page)
      if (Number.isFinite(n) && n > 0) targetPage = n
    }
    return targetPage
  }

  /** 把当前状态写回 URL（replace：不污染历史栈，避免每次搜索都新增一条历史） */
  function syncToUrl() {
    const query: Record<string, string> = {}
    if (refs.keyword.value) query.keyword = refs.keyword.value
    if (refs.activeCategory.value && refs.activeCategory.value !== ALL_CATEGORY) {
      query.category = refs.activeCategory.value
    }
    if (refs.page.value > 1) query.page = String(refs.page.value)
    if (refs.pageSize.value !== DEFAULT_PAGE_SIZE) query.pageSize = String(refs.pageSize.value)

    // 仅在 query 真正变化时才 replace，避免无意义的导航触发响应式更新
    const current = route.query
    const sameKeys =
      Object.keys(query).length === Object.keys(current).length &&
      Object.entries(query).every(([k, v]) => current[k] === v)
    if (!sameKeys) {
      router.replace({ query }).catch(() => {})
    }
  }

  return { restoreFromUrl, syncToUrl }
}
