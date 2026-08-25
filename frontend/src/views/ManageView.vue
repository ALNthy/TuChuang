<script setup lang="ts">
import { ref, onMounted, watch } from 'vue'
import UploadPanel from '../components/UploadPanel.vue'
import ImageGrid from '../components/ImageGrid.vue'
import Pagination from '../components/Pagination.vue'
import CategoryManager from '../components/CategoryManager.vue'
import { listImages, deleteImage, deleteImageBatch, updateImage, updateImageBatch } from '../api'
import { useCategories } from '../composables/useCategories'
import { useUrlState } from '../composables/useUrlState'
import { ALL_CATEGORY } from '../types'
import type { ImageDto, ImageUpdateRequest } from '../types'

// 点击空状态"选择文件上传"按钮：滚动到上传面板并触发文件选择
function focusUpload() {
  const fileInput = document.querySelector('.upload-panel input[type=file]') as HTMLInputElement | null
  if (fileInput) {
    fileInput.scrollIntoView({ behavior: 'smooth', block: 'center' })
    setTimeout(() => fileInput.click(), 400)
  }
}

const { categories, refresh: refreshCategories } = useCategories()

const images = ref<ImageDto[]>([])
const loading = ref(false)
const error = ref('')
const activeCategory = ref<string>(ALL_CATEGORY)
const keyword = ref('')

const page = ref(1)
const pageSize = ref(20)
const pageSizeOptions = [10, 20, 40, 80, 100]
const total = ref(0)

// URL 状态持久化：必须在 watch 注册之前还原，避免初始化触发 watch 造成重复刷新
const { restoreFromUrl, syncToUrl } = useUrlState({ keyword, activeCategory, page, pageSize })
const initialPage = restoreFromUrl()

async function refresh(targetPage = 1) {
  loading.value = true
  error.value = ''
  window.scrollTo({ top: 0, behavior: 'smooth' })
  try {
    const r = await listImages(activeCategory.value, targetPage, pageSize.value, keyword.value)
    images.value = r.items
    total.value = r.total
    page.value = targetPage
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    loading.value = false
  }
  // 把当前状态写回 URL，便于刷新 / 分享链接时还原上下文
  syncToUrl()
}

function changePage(p: number) {
  if (p === page.value || loading.value) return
  refresh(p).catch(() => {})
}

function changePageSize(size: number) {
  if (size === pageSize.value) return
  pageSize.value = size
  refresh(1).catch(() => {})
}

// 搜索：400ms 防抖，回车立即搜
let searchTimer: ReturnType<typeof setTimeout> | null = null
function onSearchInput() {
  if (searchTimer) clearTimeout(searchTimer)
  searchTimer = setTimeout(() => refresh(1).catch(() => {}), 400)
}
function onSearchEnter() {
  if (searchTimer) { clearTimeout(searchTimer); searchTimer = null }
  refresh(1).catch(() => {})
}
function clearSearch() {
  keyword.value = ''
  refresh(1).catch(() => {})
}

/** UploadPanel 完成上传后回调：刷新列表 */
function handleUploaded() {
  refreshCategories()
  // 上传后回到第 1 页让新图片可见
  refresh(1).catch(() => {})
}

async function handleDelete(id: number) {
  if (!confirm('确定删除这张图片吗？')) return
  try {
    await deleteImage(id)
    // 删除后重新加载当前页（可能补齐被删的项）
    await refresh(page.value)
  } catch (e) {
    error.value = (e as Error).message
  }
}

/** 批量删除：传入选中的 id 列表 */
async function handleBatchDelete(ids: number[]) {
  if (!ids.length) return
  if (!confirm(`确定删除选中的 ${ids.length} 张图片吗？`)) return
  try {
    await deleteImageBatch(ids)
    await refresh(page.value)
  } catch (e) {
    error.value = (e as Error).message
  }
}

/** 批量改分类 */
async function handleBatchUpdateCategory(ids: number[], category: string) {
  if (!ids.length || !category) return
  try {
    await updateImageBatch(ids, category)
    await refresh(page.value)
  } catch (e) {
    error.value = (e as Error).message
  }
}

/** 单张图片元信息编辑（分类 / 文件名） */
async function handleUpdateImage(id: number, data: ImageUpdateRequest) {
  try {
    await updateImage(id, data)
    await refresh(page.value)
  } catch (e) {
    error.value = (e as Error).message
  }
}

watch(activeCategory, () => refresh(1))

onMounted(() => {
  refresh(initialPage).catch(() => {})
})
</script>

<template>
  <section>
    <UploadPanel @uploaded="handleUploaded" />

    <CategoryManager />

    <div class="toolbar">
      <div class="search-box">
        <span class="search-icon">🔍</span>
        <input
          v-model="keyword"
          type="text"
          class="search-input"
          placeholder="搜索图片名…"
          @input="onSearchInput"
          @keydown.enter="onSearchEnter"
        />
        <button v-if="keyword" class="search-clear" title="清除搜索" @click="clearSearch" type="button">✕</button>
      </div>
      <label class="filter">
        分类筛选：
        <select v-model="activeCategory">
          <option :value="ALL_CATEGORY">{{ ALL_CATEGORY }}</option>
          <option v-for="c in categories" :key="c.id" :value="c.name">{{ c.name }}</option>
        </select>
      </label>
      <label class="filter">
        每页：
        <select :value="pageSize" @change="changePageSize(Number(($event.target as HTMLSelectElement).value))">
          <option v-for="n in pageSizeOptions" :key="n" :value="n">{{ n }} 张</option>
        </select>
      </label>
      <span class="count">{{ total }} 张</span>
    </div>

    <p v-if="error" class="error">{{ error }}</p>
    <p v-if="loading && !images.length" class="muted">加载中…</p>

    <ImageGrid
      :images="images"
      :show-delete="true"
      :categories="categories"
      @delete="handleDelete"
      @batch-delete="handleBatchDelete"
      @batch-update-category="handleBatchUpdateCategory"
      @update-image="handleUpdateImage"
    />

    <p v-if="!loading && !images.length && keyword" class="muted empty-hint">
      没有找到匹配「{{ keyword }}」的图片
    </p>

    <!-- 空状态引导：没有任何图片时 -->
    <div v-if="!loading && !images.length && !keyword && !error" class="empty-state">
      <div class="empty-icon">📸</div>
      <h3>还没有图片</h3>
      <p>使用上方的上传面板，上传第一张图片吧</p>
      <a href="javascript:void(0)" class="empty-action" @click="focusUpload">选择文件上传</a>
    </div>

    <p v-if="loading && images.length" class="muted loading-overlay">加载中…</p>

    <Pagination :current="page" :total="total" :page-size="pageSize" @change="changePage" />
  </section>
</template>

<style scoped>
.loading-overlay {
  text-align: center;
  padding: 16px;
  color: var(--muted);
  font-size: 13px;
}

.search-box {
  position: relative;
  display: flex;
  align-items: center;
  flex: 1;
  max-width: 320px;
}

.search-icon {
  position: absolute;
  left: 12px;
  font-size: 14px;
  opacity: 0.6;
  pointer-events: none;
}

.search-input {
  width: 100%;
  padding: 8px 32px 8px 34px;
  font-size: 13px;
  color: var(--text);
  background: var(--input-bg);
  border: 1px solid var(--border);
  border-radius: var(--r-pill);
  outline: none;
  transition: border-color var(--t-fast) var(--ease), box-shadow var(--t-fast) var(--ease);
}
.search-input:focus {
  border-color: var(--accent);
  box-shadow: 0 0 0 3px var(--accent-soft);
}
.search-input::placeholder { color: var(--text-muted); opacity: 0.7; }

.search-clear {
  position: absolute;
  right: 8px;
  width: 20px;
  height: 20px;
  border: none;
  border-radius: 50%;
  background: var(--border);
  color: var(--text-muted);
  font-size: 11px;
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
  line-height: 1;
  transition: background var(--t-fast) var(--ease), color var(--t-fast) var(--ease);
}
.search-clear:hover {
  background: var(--error);
  color: #fff;
}

@media (max-width: 768px) {
  .toolbar { flex-direction: column; align-items: stretch; gap: 10px; }
  .search-box { max-width: 100%; }
}

@media (max-width: 480px) {
  .search-box { width: 100%; }
  .search-input { font-size: 13px; }
}

/* ========== 空状态引导 ========== */
.empty-state {
  text-align: center;
  padding: 48px 20px;
}
.empty-icon {
  font-size: 56px;
  line-height: 1;
  margin-bottom: 16px;
  opacity: 0.6;
}
.empty-state h3 {
  margin: 0 0 8px;
  font-size: 18px;
  font-weight: 700;
  color: var(--text);
}
.empty-state p {
  margin: 0 0 20px;
  font-size: 13px;
  color: var(--text-muted);
}
.empty-action {
  display: inline-flex;
  align-items: center;
  padding: 9px 22px;
  font-size: 13px;
  font-weight: 600;
  text-decoration: none;
  color: #fff;
  background: linear-gradient(135deg, var(--accent) 0%, #8b5cf6 100%);
  border-radius: var(--r-pill);
  box-shadow: 0 6px 16px rgba(99, 102, 241, 0.30);
  transition: transform var(--t-fast) var(--ease), box-shadow var(--t-fast) var(--ease);
  cursor: pointer;
}
.empty-action:hover {
  transform: translateY(-1px);
  box-shadow: 0 8px 20px rgba(99, 102, 241, 0.38);
}
</style>
