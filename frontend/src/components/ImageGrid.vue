<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { getImageExif } from '../api'
import type { Category, ExifResponse, ImageDto, ImageUpdateRequest } from '../types'

const props = withDefaults(
  defineProps<{
    images: ImageDto[]
    showDelete?: boolean
    /** 分类列表，用于批量改分类下拉和单张编辑弹窗的"改分类"选择 */
    categories?: Category[]
  }>(),
  { showDelete: true, categories: () => [] }
)
const emit = defineEmits<{
  (e: 'delete', id: number): void
  (e: 'batch-delete', ids: number[]): void
  (e: 'batch-update-category', ids: number[], category: string): void
  (e: 'update-image', id: number, data: ImageUpdateRequest): void
}>()

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1024 / 1024).toFixed(2)} MB`
}

function formatDate(s: string): string {
  return new Date(s).toLocaleString('zh-CN')
}

async function copyUrl(publicId: string, fileName: string) {
  // 原图通过不可预测的 publicId 暴露，避免自增 id 被遍历下载
  const full = `${window.location.origin}/api/images/${publicId}/raw`
  try {
    await navigator.clipboard.writeText(full)
    if (fileName) void fileName
  } catch {
    /* 忽略剪贴板权限失败 */
  }
}

const WEB_EXTS = new Set(['.png', '.jpg', '.jpeg', '.gif', '.webp', '.bmp'])
const RAW_EXTS = new Set(['.raw', '.arw'])

function ext(name: string): string {
  const i = name.lastIndexOf('.')
  return i < 0 ? '' : name.slice(i).toLowerCase()
}

/** 缩略图 src：一律走后端 `/api/images/{id}/preview` 接口
 *  所有格式统一由后端转码成 480px JPEG 缩略图返回（缓存命中时直接吐文件）
 *  目的：列表阶段绝不把真实原图字节流或静态路径暴露给前端
 */
function thumbSrc(img: ImageDto): string {
  return `/api/images/${img.id}/preview`
}

/** Lightbox 展示图 src（"点击查看原图"之后才去拉）：
 *  - 通用可渲染格式：`/api/images/{publicId}/raw` 返回原图字节流（inline + 正确 MIME，可直接渲染或下载）
 *  - RAW/ARW：浏览器不能直接渲染 RAW 二进制，走 `?size=medium` 拿 1600px JPEG 预览
 *    （顶部"下载原图"按钮还是走 raw 接口，把源 RAW 文件下载给用户）
 */
function viewerSrc(img: ImageDto): string {
  if (RAW_EXTS.has(ext(img.fileName))) {
    return `/api/images/${img.id}/preview?size=medium`
  }
  return `/api/images/${img.publicId}/raw`
}

function isRaw(img: ImageDto): boolean {
  return RAW_EXTS.has(ext(img.fileName))
}

function extLabel(filename: string): string {
  const e = ext(filename)
  return e ? e.slice(1).toUpperCase() : 'FILE'
}

// ================================================================================
// 缩略图卡片加载状态 + 懒加载门禁（IntersectionObserver + 并发上限 8）
// 目的：离屏 img 不发请求；同屏同时最多 8 个请求在飞；失败不重试；避免请求洪泛+解码阻塞
// ================================================================================
type ThumbState = {
  failed: boolean
  loading: boolean
  decoded: boolean // 是否真正挂载 src（门禁后）
  queued: boolean  // 是否入过队
}
const cardState = reactive<Record<number, ThumbState>>({})

function ensureState(id: number): ThumbState {
  let s = cardState[id]
  if (!s) {
    s = { failed: false, loading: true, decoded: false, queued: false }
    cardState[id] = s
  }
  return s
}

// IO：当卡片进入视口（+3 屏提前量）→ 标记为可见，推入加载队列
let thumbIo: IntersectionObserver | null = null
const visibleIds = new Set<number>()
const MAX_CONCURRENCY = 8
const pendingQueue: number[] = []
let activeCount = 0
const imgElByImgId = new Map<number, HTMLImageElement>()
const pendingSrcByImgId = new Map<number, string>()

function runQueue() {
  while (activeCount < MAX_CONCURRENCY && pendingQueue.length) {
    const id = pendingQueue.shift()!
    const st = cardState[id]
    const src = pendingSrcByImgId.get(id)
    const el = imgElByImgId.get(id)
    if (!st || !src || !el) continue
    if (st.failed || st.decoded) continue
    activeCount++
    st.decoded = true
    st.loading = true
    // 启动真实请求
    el.src = src
  }
}

function tryQueue(id: number, src: string, el: HTMLImageElement) {
  const st = ensureState(id)
  if (st.failed || st.decoded) return
  pendingSrcByImgId.set(id, src)
  imgElByImgId.set(id, el)
  if (st.queued) return
  st.queued = true
  pendingQueue.push(id)
  runQueue()
}

function onThumbLoaded(id: number) {
  const st = cardState[id]
  if (st) {
    st.loading = false
  }
  activeCount = Math.max(0, activeCount - 1)
  pendingSrcByImgId.delete(id)
  imgElByImgId.delete(id)
  runQueue()
}
function onThumbErrored(id: number) {
  const st = cardState[id]
  if (st) {
    st.loading = false
    st.failed = true
  }
  activeCount = Math.max(0, activeCount - 1)
  pendingSrcByImgId.delete(id)
  imgElByImgId.delete(id)
  runQueue()
}

function onCardVisible(img: ImageDto, el: HTMLImageElement) {
  // 进入 IO 可见范围 → 入队并发加载
  if (!visibleIds.has(img.id)) {
    visibleIds.add(img.id)
    tryQueue(img.id, thumbSrc(img), el)
  }
}

let cardRefFn: ((el: Element | null, img: ImageDto) => void) | null = null

function getCardRefFn() {
  if (cardRefFn) return cardRefFn
  const map = new WeakMap<Element, ImageDto>()
  const io = ('IntersectionObserver' in window)
    ? new IntersectionObserver(
        entries => {
          for (const e of entries) {
            const img = map.get(e.target as Element)
            const imgEl = (e.target as HTMLElement).querySelector('img[data-thumb-id]') as HTMLImageElement | null
            if (!img || !imgEl) continue
            if (e.isIntersecting) {
              onCardVisible(img, imgEl)
            }
          }
        },
        { rootMargin: '1200px 0px' }
      )
    : null
  thumbIo = io

  cardRefFn = function (el: Element | null, img: ImageDto) {
    if (!el) return
    map.set(el, img)
    io?.observe(el)
    // 兜底：如果不支持 IO，直接挂 src（数量少不影响）
    if (!io) {
      const imgEl = (el as HTMLElement).querySelector('img[data-thumb-id]') as HTMLImageElement | null
      if (imgEl) {
        ensureState(img.id).decoded = true
        imgEl.src = thumbSrc(img)
      }
    }
  }
  return cardRefFn
}

// ================================================================================
// Lightbox 状态
// ================================================================================
const openIndex = ref<number | null>(null)
const viewerState = reactive<{ loading: boolean; failed: boolean }>({ loading: false, failed: false })

const currentImage = computed<ImageDto | null>(() => {
  if (openIndex.value === null) return null
  return props.images[openIndex.value] ?? null
})

const hasPrev = computed(() => openIndex.value !== null && openIndex.value > 0)
const hasNext = computed(() =>
  openIndex.value !== null && openIndex.value < props.images.length - 1
)

function openViewer(index: number, event?: Event) {
  event?.stopPropagation()
  openIndex.value = index
  viewerState.loading = true
  viewerState.failed = false
  document.body.style.overflow = 'hidden'
}

function closeViewer() {
  if (openIndex.value === null) return
  openIndex.value = null
  document.body.style.overflow = ''
}

function goPrev() {
  if (!hasPrev.value) return
  openIndex.value!--
  viewerState.loading = true
  viewerState.failed = false
}
function goNext() {
  if (!hasNext.value) return
  openIndex.value!++
  viewerState.loading = true
  viewerState.failed = false
}
function onKey(e: KeyboardEvent) {
  if (openIndex.value === null) return
  if (e.key === 'Escape') closeViewer()
  else if (e.key === 'ArrowLeft') goPrev()
  else if (e.key === 'ArrowRight') goNext()
  else if (e.key === '+' || e.key === '=') zoomIn()
  else if (e.key === '-' || e.key === '_') zoomOut()
  else if (e.key === '0') zoomFit()
  else if (e.key === 'r' || e.key === 'R') {
    // Shift+R 左旋，普通 R 右旋
    if (e.shiftKey) rotateLeft()
    else rotateRight()
  }
}

// ---------------- Lightbox 缩放、旋转、拖拽 ----------------
const MIN_SCALE = 0.1
const MAX_SCALE = 8
const scale = ref(1)
const offsetX = ref(0)
const offsetY = ref(0)
const rotation = ref(0)          // 旋转角度（度），每次 ±90°
const fitScale = ref(1)
const stageEl = ref<HTMLDivElement | null>(null)
const vImgEl = ref<HTMLImageElement | null>(null)
const dragging = ref(false)
let lastClientX = 0
let lastClientY = 0

const scalePct = computed(() => Math.round(scale.value * 100))

function clampScale(s: number): number {
  return Math.min(MAX_SCALE, Math.max(MIN_SCALE, s))
}

function resetView(newScale?: number) {
  offsetX.value = 0
  offsetY.value = 0
  scale.value = newScale ?? fitScale.value ?? 1
}

function recalcFitScale() {
  const stage = stageEl.value
  const img = vImgEl.value
  if (!stage || !img) {
    fitScale.value = 1
    return 1
  }
  const paddingX = 32
  const paddingY = 32
  const stageW = Math.max(100, stage.clientWidth - paddingX)
  const stageH = Math.max(100, stage.clientHeight - paddingY)
  const imgW = img.naturalWidth || img.clientWidth || stageW
  const imgH = img.naturalHeight || img.clientHeight || stageH
  const s = Math.min(1, stageW / imgW, stageH / imgH)
  fitScale.value = s > 0 ? s : 1
  return fitScale.value
}

function zoomBy(factor: number, stageX?: number, stageY?: number) {
  const stage = stageEl.value
  const oldScale = scale.value
  const newScale = clampScale(oldScale * factor)
  if (newScale === oldScale) return

  let anchorX: number
  let anchorY: number
  let stageW = 0
  let stageH = 0
  if (stage) {
    stageW = stage.clientWidth
    stageH = stage.clientHeight
  }
  if (stage && stageX !== undefined && stageY !== undefined) {
    anchorX = stageX
    anchorY = stageY
  } else if (stage) {
    anchorX = stageW / 2
    anchorY = stageH / 2
  } else {
    anchorX = 0
    anchorY = 0
  }
  const ratio = newScale / oldScale
  const oldCenterX = stageW / 2 + offsetX.value
  const oldCenterY = stageH / 2 + offsetY.value
  const newCenterX = anchorX - (anchorX - oldCenterX) * ratio
  const newCenterY = anchorY - (anchorY - oldCenterY) * ratio
  offsetX.value = newCenterX - stageW / 2
  offsetY.value = newCenterY - stageH / 2
  scale.value = newScale
}

function zoomIn() { zoomBy(1.25) }
function zoomOut() { zoomBy(1 / 1.25) }
function zoomActual() { resetView(1) }
function zoomFit() {
  recalcFitScale()
  resetView(fitScale.value)
}

// ---------------- 旋转 ----------------
function rotateRight() { rotation.value = (rotation.value + 90) % 360 }
function rotateLeft() { rotation.value = (rotation.value - 90 + 360) % 360 }
function rotateReset() { rotation.value = 0 }

function onStageWheel(e: WheelEvent) {
  const stage = stageEl.value
  if (!stage) return
  const rect = stage.getBoundingClientRect()
  const sx = e.clientX - rect.left
  const sy = e.clientY - rect.top
  const factor = e.deltaY < 0 ? 1.15 : 1 / 1.15
  zoomBy(factor, sx, sy)
}

function onStageMouseDown(e: MouseEvent) {
  if (e.button !== 0) return
  dragging.value = true
  lastClientX = e.clientX
  lastClientY = e.clientY
}
function onStageMouseMove(e: MouseEvent) {
  if (!dragging.value) return
  const dx = e.clientX - lastClientX
  const dy = e.clientY - lastClientY
  offsetX.value += dx
  offsetY.value += dy
  lastClientX = e.clientX
  lastClientY = e.clientY
}
function onStageMouseUp() { dragging.value = false }

function onImgDblClick() {
  if (Math.abs(scale.value - fitScale.value) < 0.01) {
    zoomActual()
  } else {
    zoomFit()
  }
}

function onViewerImgLoad() {
  viewerState.loading = false
  nextTick(() => {
    recalcFitScale()
    resetView(fitScale.value)
  })
}
function onViewerImgError() {
  viewerState.failed = true
  viewerState.loading = false
}

watch(() => openIndex.value, () => {
  resetView(1)
  rotation.value = 0
})

// images 变化（切页 / 切 pageSize / 切分类）→ 重置可见性记录，并兜底加载所有 src 为空的 img
// 目的：新挂载的 article 若不在 IO rootMargin 范围内，IO 不会触发，缩略图永不加载
// 这里直接设置 el.src 绕过依赖 @load 递减的 activeCount 队列，避免队列卡死
// 注意：切回上一页时 img 元素是新挂载的（src 空），但同 id 的 cardState.decoded 可能=true（之前加载过），
// 所以不能用 st.decoded 跳过——只要 imgEl.src 为空就必须重新设置 src
watch(() => props.images, () => {
  visibleIds.clear()
  nextTick(() => {
    const cards = document.querySelectorAll<HTMLElement>('.grid .card')
    cards.forEach(article => {
      const imgEl = article.querySelector<HTMLImageElement>('img[data-thumb-id]')
      if (!imgEl) return
      // 已有 src（元素复用 / 已加载），跳过
      if (imgEl.src) return
      const id = Number(imgEl.dataset.thumbId)
      if (!id) return
      const img = props.images.find(i => i.id === id)
      if (!img) return
      const st = ensureState(id)
      // 只跳过失败过的图片，不跳过 decoded=true 的（新挂载的 img 元素 src 仍为空）
      if (st.failed) return
      // 直接连 src，浏览器自身 6 并发限流；不依赖 activeCount 队列
      st.decoded = true
      st.loading = true
      visibleIds.add(id)
      imgEl.src = thumbSrc(img)
    })
  })
}, { flush: 'post' })

onMounted(() => {
  window.addEventListener('keydown', onKey)
})

onBeforeUnmount(() => {
  window.removeEventListener('keydown', onKey)
  document.body.style.overflow = ''
  if (thumbIo) thumbIo.disconnect()
  thumbIo = null
})

// ================================================================================
// 批量选择（仅在 showDelete=true 即管理页生效）
// ================================================================================
const selectedIds = ref<Set<number>>(new Set())

const selectionCount = computed(() => selectedIds.value.size)
const hasSelection = computed(() => selectedIds.value.size > 0)
const isAllSelected = computed(() => {
  if (!props.images.length) return false
  return props.images.every(img => selectedIds.value.has(img.id))
})

function isSelected(id: number): boolean {
  return selectedIds.value.has(id)
}

function toggleSelect(id: number, event?: Event) {
  event?.stopPropagation()
  if (selectedIds.value.has(id)) {
    selectedIds.value.delete(id)
  } else {
    selectedIds.value.add(id)
  }
  // 触发响应式更新（Set 内部变化需要重新赋值）
  selectedIds.value = new Set(selectedIds.value)
}

function toggleSelectAll() {
  if (isAllSelected.value) {
    // 取消全选当前页
    const ids = new Set(props.images.map(i => i.id))
    const next = new Set<number>()
    for (const id of selectedIds.value) if (!ids.has(id)) next.add(id)
    selectedIds.value = next
  } else {
    const next = new Set(selectedIds.value)
    for (const img of props.images) next.add(img.id)
    selectedIds.value = next
  }
}

function clearSelection() {
  selectedIds.value = new Set()
}

function selectedIdsArray(): number[] {
  return Array.from(selectedIds.value)
}

// 批量改分类下拉
const batchCategoryTarget = ref<string>('')

function applyBatchCategory() {
  if (!selectedIds.value.size || !batchCategoryTarget.value) return
  emit('batch-update-category', selectedIdsArray(), batchCategoryTarget.value)
  batchCategoryTarget.value = ''
  clearSelection()
}

function applyBatchDelete() {
  if (!selectedIds.value.size) return
  emit('batch-delete', selectedIdsArray())
  clearSelection()
}

// 切页 / 切分类 / images 变化时清空选中，避免操作到已不存在的 id
watch(() => props.images, () => {
  clearSelection()
}, { flush: 'post' })

// ================================================================================
// 单张图片编辑弹窗（改分类 / 文件名）
// ================================================================================
const editingId = ref<number | null>(null)
const editForm = reactive<{ category: string; fileName: string }>({
  category: '',
  fileName: ''
})
const editError = ref('')
const editBusy = ref(false)

const editingCurrent = computed<ImageDto | null>(() => {
  if (editingId.value === null) return null
  return props.images.find(i => i.id === editingId.value) ?? null
})

function openEdit(img: ImageDto, event?: Event) {
  event?.stopPropagation()
  editingId.value = img.id
  editForm.category = img.category
  editForm.fileName = img.fileName
  editError.value = ''
  editBusy.value = false
}

function closeEdit() {
  editingId.value = null
  editError.value = ''
  editBusy.value = false
}

function saveEdit() {
  if (editingId.value === null) return
  const cat = editForm.category.trim()
  const name = editForm.fileName.trim()
  if (!cat || !name) {
    editError.value = '分类与文件名都不能为空'
    return
  }
  const img = editingCurrent.value
  if (!img) {
    editError.value = '图片不存在'
    return
  }
  const data: ImageUpdateRequest = {}
  if (cat !== img.category) data.category = cat
  if (name !== img.fileName) data.fileName = name
  if (!data.category && !data.fileName) {
    closeEdit()
    return
  }
  editBusy.value = true
  emit('update-image', editingId.value, data)
  // 等父组件刷新后再关闭：用 nextTick 兜底，错误提示交给父组件
  // 简化处理：直接关闭弹窗，父组件失败时图片列表不变（PATCH 失败会回滚状态）
  nextTick(() => closeEdit())
}

function onEditKeydown(e: KeyboardEvent) {
  if (e.key === 'Enter') {
    e.preventDefault()
    saveEdit()
  } else if (e.key === 'Escape') {
    e.preventDefault()
    closeEdit()
  }
}

// ================================================================================
// Lightbox EXIF 侧边面板
// ================================================================================
const showExif = ref(false)
const exifData = ref<ExifResponse | null>(null)
const exifLoading = ref(false)
const exifError = ref('')

/** 把 exif 对象按 key 排序转为 [key, value] 数组（保证渲染顺序稳定） */
const exifEntries = computed<Array<[string, string]>>(() => {
  if (!exifData.value || !exifData.value.exif) return []
  return Object.entries(exifData.value.exif).sort(([a], [b]) => a.localeCompare(b))
})

async function loadExifForCurrent() {
  if (!currentImage.value) return
  exifLoading.value = true
  exifError.value = ''
  exifData.value = null
  try {
    exifData.value = await getImageExif(currentImage.value.id)
  } catch (e) {
    exifError.value = (e as Error).message
  } finally {
    exifLoading.value = false
  }
}

async function toggleExifPanel() {
  if (showExif.value) {
    showExif.value = false
    return
  }
  showExif.value = true
  await loadExifForCurrent()
}

function closeExif() {
  showExif.value = false
}

// 切换图片 / 关闭 Lightbox 时重置 EXIF 状态
watch(
  () => openIndex.value,
  () => {
    showExif.value = false
    exifData.value = null
    exifLoading.value = false
    exifError.value = ''
  }
)
</script>

<template>
  <!-- 批量操作栏：仅在管理页（showDelete=true）显示，且仅在有图片时显示 -->
  <div v-if="showDelete && images.length" class="batch-bar">
    <label class="batch-check" @click.stop>
      <input
        type="checkbox"
        :checked="isAllSelected"
        @change="toggleSelectAll"
      />
      <span>{{ isAllSelected ? '取消全选' : '全选' }}</span>
    </label>
    <span class="batch-count" v-if="hasSelection">已选 {{ selectionCount }} 张</span>
    <span class="batch-count" v-else>未选中</span>
    <div class="batch-ops" @click.stop>
      <select
        v-model="batchCategoryTarget"
        class="batch-select"
        :disabled="!hasSelection"
        title="批量改分类"
      >
        <option value="">批量改分类…</option>
        <option v-for="c in categories" :key="c.id" :value="c.name">{{ c.name }}</option>
      </select>
      <button
        class="batch-btn"
        :disabled="!hasSelection || !batchCategoryTarget"
        @click="applyBatchCategory"
      >应用</button>
      <button
        class="batch-btn danger"
        :disabled="!hasSelection"
        @click="applyBatchDelete"
      >批量删除</button>
      <button
        v-if="hasSelection"
        class="batch-btn ghost"
        @click="clearSelection"
      >取消</button>
    </div>
  </div>

  <section v-if="images.length" class="grid">
    <article
      v-for="(img, i) in images"
      :key="img.id"
      class="card"
      :class="{ selected: showDelete && isSelected(img.id) }"
      :ref="(el) => getCardRefFn()(el as Element | null, img)"
    >
      <div
        class="thumb"
        @click="openViewer(i, $event)"
        :title="`点击查看原图：${img.fileName}`"
      >
        <!-- loading 遮罩 -->
        <div v-if="ensureState(img.id).loading && !ensureState(img.id).failed" class="loading">
          <span class="spinner" />
        </div>

        <!-- 选择框：仅在管理页显示，左上角 -->
        <label
          v-if="showDelete"
          class="card-check"
          :class="{ checked: isSelected(img.id) }"
          @click.stop
          :title="isSelected(img.id) ? '取消选择' : '选中此图片'"
        >
          <input
            type="checkbox"
            :checked="isSelected(img.id)"
            @change="toggleSelect(img.id, $event)"
          />
          <span class="check-mark">✓</span>
        </label>

        <!-- 缩略图（懒加载：data-src 存源地址，IO 入队后再把 src 挂上） -->
        <img
          v-if="!ensureState(img.id).failed"
          :data-thumb-id="img.id"
          :alt="img.fileName"
          loading="lazy"
          decoding="async"
          class="thumb-img"
          :class="{ 'is-raw': isRaw(img) }"
          @click.stop="openViewer(i)"
          @load="onThumbLoaded(img.id)"
          @error="onThumbErrored(img.id)"
        />

        <!-- 失败兜底占位块 -->
        <div v-else class="placeholder" :title="img.fileName" @click.stop>
          <span class="ph-icon">🗂️</span>
          <span class="ph-ext">{{ extLabel(img.fileName) }}</span>
          <span class="ph-hint">
            {{ WEB_EXTS.has(ext(img.fileName)) ? '图片加载失败' : 'RAW/ARW 预览生成失败' }}
          </span>
        </div>

        <!-- hover 编辑按钮：管理页可见 -->
        <button
          v-if="showDelete"
          class="card-edit"
          title="编辑分类 / 文件名"
          @click.stop="openEdit(img, $event)"
        >编辑</button>

        <span class="badge" @click.stop>{{ img.category }}</span>
        <span
          v-if="isRaw(img)"
          class="raw-badge"
          title="RAW/ARW 格式，已自动转码为 JPEG 预览"
          @click.stop
          >RAW</span
        >
      </div>
      <div class="meta">
        <p class="name" :title="img.fileName">{{ img.fileName }}</p>
        <p class="info">{{ formatSize(img.fileSize) }} · {{ formatDate(img.uploadedAt) }}</p>
      </div>
      <div class="actions">
        <button v-if="showDelete" class="danger" @click="emit('delete', img.id)">删除</button>
      </div>
    </article>
  </section>
  <div v-else class="empty">
    <p>还没有图片，上传一张吧 ✨</p>
  </div>

  <!-- 单张图片编辑弹窗 -->
  <div
    v-if="editingId !== null"
    class="modal-overlay"
    @click.self="closeEdit"
  >
    <div class="modal" @click.stop>
      <header class="modal-head">
        <strong>编辑图片</strong>
        <button class="modal-close" title="关闭 (Esc)" @click="closeEdit">×</button>
      </header>
      <div class="modal-body">
        <label class="modal-row">
          <span class="modal-label">分类</span>
          <select v-model="editForm.category" class="modal-input">
            <option v-for="c in categories" :key="c.id" :value="c.name">{{ c.name }}</option>
          </select>
        </label>
        <label class="modal-row">
          <span class="modal-label">文件名</span>
          <input
            v-model="editForm.fileName"
            class="modal-input"
            maxlength="255"
            placeholder="文件名"
            @keydown="onEditKeydown"
          />
        </label>
        <p v-if="editError" class="modal-error">{{ editError }}</p>
      </div>
      <footer class="modal-foot">
        <button class="modal-btn ghost" @click="closeEdit">取消</button>
        <button class="modal-btn primary" :disabled="editBusy" @click="saveEdit">保存</button>
      </footer>
    </div>
  </div>

  <!-- 全屏 Lightbox：点击查看原图（支持缩放 / 拖拽 / 适应 / 1:1 / 滚轮 / 双击 / EXIF） -->
  <div v-if="currentImage" class="viewer" role="dialog" aria-modal="true" @click.self="closeViewer">
    <button class="v-close" title="关闭 (Esc)" @click="closeViewer">×</button>
    <button v-if="hasPrev" class="v-nav v-prev" title="上一张 (←)" @click.stop="goPrev">‹</button>
    <button v-if="hasNext" class="v-nav v-next" title="下一张 (→)" @click.stop="goNext">›</button>

    <div class="v-body" :class="{ 'exif-on': showExif }" @click.stop>
      <header class="v-head">
        <div class="v-title">
          <strong>{{ currentImage.fileName }}</strong>
          <span class="v-meta"
            >{{ currentImage.category }} · {{ formatSize(currentImage.fileSize) }} ·
            {{ formatDate(currentImage.uploadedAt) }}</span
          >
        </div>
        <div class="v-zoom" @click.stop>
          <button class="v-btn" title="缩小 (-)" @click="zoomOut">－</button>
          <button class="v-btn v-pct" title="适应窗口 (0)" @click="zoomFit">{{ scalePct }}%</button>
          <button class="v-btn" title="放大 (+)" @click="zoomIn">＋</button>
          <button class="v-btn" title="实际像素 (1:1)" @click="zoomActual">1:1</button>
          <button class="v-btn" title="适应窗口" @click="zoomFit">适应</button>
          <span class="v-sep" aria-hidden="true" />
          <button class="v-btn" title="左旋 90° (Shift+R)" @click="rotateLeft">⟲</button>
          <button class="v-btn" title="右旋 90° (R)" @click="rotateRight">⟳</button>
          <button class="v-btn" title="重置旋转" @click="rotateReset">↺</button>
        </div>
        <div class="v-actions">
          <button class="v-btn" :class="{ active: showExif }" title="查看 EXIF 元数据" @click="toggleExifPanel">EXIF</button>
          <button class="v-btn" @click="copyUrl(currentImage.publicId, currentImage.fileName)">复制链接</button>
          <a
            class="v-btn primary"
            :href="`/api/images/${currentImage.publicId}/raw`"
            :download="currentImage.fileName"
            :title="isRaw(currentImage) ? 'RAW/ARW 浏览器无法直接渲染，下载源文件查看' : '下载原图二进制源文件'"
            >{{ isRaw(currentImage) ? '下载源文件' : '下载原图' }}</a
          >
        </div>
      </header>

      <div class="v-stage-wrap" :class="{ 'exif-on': showExif }">
        <div
          ref="stageEl"
          class="v-stage"
          :class="{ dragging: dragging }"
          @wheel.prevent="onStageWheel"
          @mousedown="onStageMouseDown"
          @mousemove="onStageMouseMove"
          @mouseup="onStageMouseUp"
          @mouseleave="onStageMouseUp"
          @dblclick="onImgDblClick"
        >
          <div v-if="viewerState.loading && !viewerState.failed" class="v-loading">
            <span class="spinner lg" />
            <p>加载中…</p>
          </div>

          <div v-show="!viewerState.failed" class="v-img-wrap">
            <img
              ref="vImgEl"
              class="v-img"
              :class="{ 'is-raw': isRaw(currentImage) }"
              :src="viewerSrc(currentImage)"
              :alt="currentImage.fileName"
              :draggable="false"
              decoding="async"
              :style="{
                transform: `translate(-50%, -50%) translate(${offsetX}px, ${offsetY}px) rotate(${rotation}deg) scale(${scale})`,
                transformOrigin: '50% 50%',
                transition: dragging ? 'none' : 'transform 0.18s ease'
              }"
              @load="onViewerImgLoad"
              @error="onViewerImgError"
            />
          </div>

          <div v-if="viewerState.failed" class="v-error">
            <span class="ph-icon">⚠️</span>
            <p>
              {{
                isRaw(currentImage)
                  ? 'RAW/ARW 预览生成失败，可点击右侧"下载源文件"获取原始数据'
                  : '原图加载失败，请稍后重试或下载本地'
              }}
            </p>
            <a class="v-btn primary" :href="`/api/images/${currentImage.publicId}/raw`" :download="currentImage.fileName"
              >下载源文件</a
            >
          </div>
        </div>

        <!-- EXIF 侧边面板 -->
        <aside v-show="showExif" class="exif-panel" @click.stop>
          <header class="exif-head">
            <strong>EXIF 元数据</strong>
            <button class="exif-close" title="关闭 EXIF 面板" @click="closeExif">×</button>
          </header>
          <div class="exif-body">
            <div v-if="exifLoading" class="exif-loading">
              <span class="spinner" />
              <p>加载 EXIF 中…</p>
            </div>
            <p v-else-if="exifError" class="exif-error">{{ exifError }}</p>
            <div v-else-if="exifData && exifData.exif && exifEntries.length" class="exif-table">
              <dl>
                <template v-for="[k, v] in exifEntries" :key="k">
                  <dt>{{ k }}</dt>
                  <dd>{{ v }}</dd>
                </template>
              </dl>
            </div>
            <div v-else class="exif-empty">
              <span class="ph-icon">📷</span>
              <p>该图片没有 EXIF 数据</p>
              <p v-if="exifData?.note" class="exif-note">{{ exifData.note }}</p>
            </div>
          </div>
        </aside>
      </div>
    </div>
  </div>
</template>

<style scoped>
/* ========== 卡片网格（CSS Grid auto-fill，响应式列数） ========== */
.grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
  gap: 18px;
  margin-top: 24px;
  align-items: start;
}

.card {
  background: var(--card);
  border: 1px solid var(--glass-border);
  border-radius: var(--r-lg);
  overflow: hidden;
  display: flex;
  flex-direction: column;
  backdrop-filter: saturate(180%) blur(10px);
  -webkit-backdrop-filter: saturate(180%) blur(10px);
  box-shadow: var(--shadow-sm);
  transition: transform var(--t-base) var(--ease), box-shadow var(--t-base) var(--ease), border-color var(--t-base) var(--ease);
  /* 离屏跳过布局/绘制，滚动更顺 */
  content-visibility: auto;
  contain-intrinsic-size: 360px;
  contain: layout paint style;
  position: relative;
}
.card::before {
  content: '';
  position: absolute;
  inset: 0;
  border-radius: inherit;
  padding: 1px;
  background: linear-gradient(135deg, rgba(99,102,241,0.22), rgba(236,72,153,0.08) 50%, rgba(6,182,212,0.22));
  -webkit-mask: linear-gradient(#000 0 0) content-box, linear-gradient(#000 0 0);
  -webkit-mask-composite: xor;
  mask-composite: exclude;
  pointer-events: none;
  opacity: 0;
  transition: opacity var(--t-fast) var(--ease);
  z-index: 1;
}
.card:hover {
  transform: translateY(-4px) scale(1.012);
  box-shadow: var(--shadow-lg);
  border-color: rgba(224, 231, 255, 1);
}
.card:hover::before { opacity: 1; }

.thumb {
  position: relative;
  width: 100%;
  aspect-ratio: 4 / 3;
  background: var(--thumb-bg);
  overflow: hidden;
  cursor: zoom-in;
}
.thumb::after {
  content: '';
  position: absolute;
  inset: 0;
  background: linear-gradient(180deg, rgba(255,255,255,0.22), transparent 30%, rgba(15,23,42,0.18));
  pointer-events: none;
  opacity: 0;
  transition: opacity var(--t-base) var(--ease);
}
.card:hover .thumb::after { opacity: 1; }
.thumb-img {
  width: 100%;
  height: 100%;
  object-fit: cover;
  display: block;
  transition: transform var(--t-slow) var(--ease), filter var(--t-base) var(--ease);
  content-visibility: auto;
}
.card:hover .thumb-img {
  transform: scale(1.05);
  filter: saturate(1.08);
}
.thumb-img.is-raw {
  background: var(--raw-thumb-bg);
}

.loading {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--surface-strong);
  backdrop-filter: blur(4px);
  -webkit-backdrop-filter: blur(4px);
  z-index: 2;
}
.spinner {
  width: 28px;
  height: 28px;
  border-radius: 50%;
  border: 3px solid rgba(99, 102, 241, 0.22);
  border-top-color: var(--accent);
  animation: spin 0.85s linear infinite;
}
.spinner.lg {
  width: 44px;
  height: 44px;
  border: 3px solid rgba(255,255,255,0.2);
  border-top-color: #c7d2fe;
}
@keyframes spin {
  to { transform: rotate(360deg); }
}

.placeholder {
  position: absolute;
  inset: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 4px;
  background:
    radial-gradient(60% 60% at 50% 30%, rgba(129, 140, 248, 0.18), transparent 60%),
    var(--placeholder-bg);
  color: var(--accent);
  cursor: default;
  z-index: 1;
}
.ph-icon { font-size: 34px; }
.ph-ext {
  font-size: 18px;
  font-weight: 700;
  letter-spacing: 0.06em;
  background: linear-gradient(135deg, var(--accent), #ec4899);
  -webkit-background-clip: text;
  background-clip: text;
  color: transparent;
}
.ph-hint {
  font-size: 11.5px;
  color: var(--muted);
}

.badge {
  position: absolute;
  top: 10px;
  left: 10px;
  padding: 4px 10px;
  font-size: 12px;
  font-weight: 600;
  color: #fff;
  background: linear-gradient(135deg, rgba(99,102,241,0.92), rgba(139,92,246,0.92));
  border-radius: var(--r-pill);
  border: 1px solid rgba(255,255,255,0.28);
  backdrop-filter: blur(8px);
  -webkit-backdrop-filter: blur(8px);
  z-index: 3;
  cursor: default;
  box-shadow: 0 4px 12px rgba(99, 102, 241, 0.3);
}
.raw-badge {
  position: absolute;
  top: 10px;
  right: 10px;
  padding: 4px 10px;
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.08em;
  color: #fff;
  background: linear-gradient(135deg, rgba(13,148,136,0.92), rgba(6,182,212,0.92));
  border-radius: var(--r-pill);
  border: 1px solid rgba(255,255,255,0.28);
  backdrop-filter: blur(8px);
  -webkit-backdrop-filter: blur(8px);
  z-index: 3;
  cursor: default;
  box-shadow: 0 4px 12px rgba(13, 148, 136, 0.28);
}

.meta {
  padding: 12px 14px 6px;
  position: relative;
}
.name {
  margin: 0;
  font-size: 13.5px;
  font-weight: 600;
  color: var(--text);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.info {
  margin: 4px 0 0;
  font-size: 12px;
  color: var(--muted);
  font-variant-numeric: tabular-nums;
}

.actions {
  display: flex;
  gap: 8px;
  padding: 10px 14px 14px;
}
.actions button {
  flex: 1;
  padding: 7px 12px;
  font-size: 12.5px;
  font-weight: 600;
  border: 1px solid var(--border);
  background: var(--surface-strong);
  border-radius: var(--r-md);
  cursor: pointer;
  color: var(--text);
  transition: transform var(--t-fast) var(--ease), background var(--t-fast) var(--ease), border-color var(--t-fast) var(--ease), color var(--t-fast) var(--ease), box-shadow var(--t-fast) var(--ease);
}
.actions button:hover {
  transform: translateY(-1px);
  background: var(--card-solid);
  border-color: #c7d2fe;
  color: var(--accent);
  box-shadow: var(--shadow-xs);
}
.actions button.danger { color: var(--error); }
.actions button.danger:hover {
  border-color: #fecaca;
  color: var(--error);
  background: linear-gradient(180deg, #fff, #fef2f2);
}

.empty {
  text-align: center;
  padding: 56px 24px;
  margin-top: 24px;
  background: var(--glass-bg);
  border: 1px dashed rgba(148, 163, 184, 0.35);
  border-radius: var(--r-lg);
  color: var(--muted);
  font-size: 15px;
  backdrop-filter: var(--glass-blur);
  -webkit-backdrop-filter: var(--glass-blur);
}

/* ========== Lightbox ========== */
.viewer {
  position: fixed;
  inset: 0;
  z-index: 100;
  background:
    radial-gradient(80% 60% at 50% 40%, rgba(15, 23, 42, 0.68), rgba(15, 23, 42, 0.92));
  backdrop-filter: blur(8px) saturate(140%);
  -webkit-backdrop-filter: blur(8px) saturate(140%);
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 28px;
  animation: fadeIn 0.18s var(--ease);
}
@keyframes fadeIn {
  from { opacity: 0; }
  to { opacity: 1; }
}

.v-close {
  position: absolute;
  top: 18px;
  right: 22px;
  width: 42px;
  height: 42px;
  border-radius: 50%;
  background: rgba(255, 255, 255, 0.14);
  color: #fff;
  border: 1px solid rgba(255, 255, 255, 0.22);
  font-size: 22px;
  line-height: 1;
  cursor: pointer;
  backdrop-filter: blur(10px);
  -webkit-backdrop-filter: blur(10px);
  transition: background var(--t-fast) var(--ease), transform var(--t-fast) var(--ease), box-shadow var(--t-fast) var(--ease);
}
.v-close:hover {
  background: rgba(255, 255, 255, 0.26);
  transform: scale(1.05);
  box-shadow: 0 6px 20px rgba(255, 255, 255, 0.12);
}

.v-nav {
  position: absolute;
  top: 50%;
  transform: translateY(-50%);
  width: 46px;
  height: 46px;
  border-radius: 50%;
  background: rgba(255, 255, 255, 0.14);
  color: #fff;
  border: 1px solid rgba(255, 255, 255, 0.22);
  font-size: 30px;
  line-height: 1;
  cursor: pointer;
  user-select: none;
  backdrop-filter: blur(10px);
  -webkit-backdrop-filter: blur(10px);
  transition: background var(--t-fast) var(--ease), transform var(--t-fast) var(--ease), box-shadow var(--t-fast) var(--ease);
}
.v-nav:hover {
  background: rgba(255, 255, 255, 0.26);
  transform: translateY(-50%) scale(1.05);
  box-shadow: 0 6px 20px rgba(255, 255, 255, 0.12);
}
.v-prev { left: 26px; }
.v-next { right: 26px; }

.v-body {
  width: min(1160px, 100%);
  height: calc(100vh - 60px);
  max-height: calc(100vh - 60px);
  background: var(--card-solid);
  border-radius: var(--r-xl);
  overflow: hidden;
  display: flex;
  flex-direction: column;
  box-shadow: 0 32px 80px rgba(15, 23, 42, 0.6), 0 8px 20px rgba(15, 23, 42, 0.35);
  border: 1px solid rgba(255,255,255,0.5);
  transform: translateZ(0);
}

.v-head {
  display: grid;
  grid-template-columns: 1fr auto 1fr;
  align-items: center;
  gap: 16px;
  padding: 12px 18px;
  background: linear-gradient(90deg, rgba(99,102,241,0.06), rgba(236,72,153,0.05) 50%, rgba(6,182,212,0.06));
  border-bottom: 1px solid rgba(224, 231, 255, 0.9);
  backdrop-filter: saturate(180%) blur(8px);
  -webkit-backdrop-filter: saturate(180%) blur(8px);
}
.v-title {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}
.v-title strong {
  font-size: 14px;
  color: var(--text);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  letter-spacing: 0.01em;
}
.v-meta {
  font-size: 12px;
  color: var(--muted);
  font-variant-numeric: tabular-nums;
}

.v-zoom {
  display: flex;
  align-items: center;
  gap: 6px;
  justify-self: center;
  padding: 4px;
  background: var(--surface-strong);
  border: 1px solid var(--surface-border);
  border-radius: var(--r-pill);
  backdrop-filter: saturate(180%) blur(6px);
  -webkit-backdrop-filter: saturate(180%) blur(6px);
  box-shadow: var(--shadow-xs);
}
.v-zoom .v-btn {
  padding: 5px 12px;
  font-size: 12.5px;
  min-width: 38px;
  justify-content: center;
  border-radius: var(--r-pill);
  border-color: transparent;
  background: transparent;
}
.v-zoom .v-btn:hover {
  background: rgba(99, 102, 241, 0.08);
  color: var(--accent);
  transform: translateY(-0.5px);
}
.v-zoom .v-sep {
  width: 1px;
  height: 18px;
  background: rgba(99, 102, 241, 0.18);
  margin: 0 4px;
  display: inline-block;
}
.v-btn.v-pct {
  font-weight: 700;
  font-variant-numeric: tabular-nums;
  min-width: 66px;
  background: linear-gradient(135deg, var(--accent) 0%, #8b5cf6 100%) !important;
  color: #fff !important;
  border-color: transparent !important;
  box-shadow: 0 4px 12px rgba(99, 102, 241, 0.28);
}
.v-btn.v-pct:hover {
  color: #fff !important;
  filter: brightness(1.05);
}

.v-actions {
  display: flex;
  gap: 8px;
  flex-shrink: 0;
  justify-self: end;
}
.v-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 7px 14px;
  font-size: 13px;
  font-weight: 600;
  border: 1px solid var(--border);
  background: var(--card-solid);
  color: var(--text);
  border-radius: var(--r-md);
  cursor: pointer;
  text-decoration: none;
  transition: transform var(--t-fast) var(--ease), background var(--t-fast) var(--ease), border-color var(--t-fast) var(--ease), color var(--t-fast) var(--ease), box-shadow var(--t-fast) var(--ease), filter var(--t-fast) var(--ease);
}
.v-btn:hover {
  transform: translateY(-0.5px);
  border-color: #c7d2fe;
  color: var(--accent);
  box-shadow: var(--shadow-xs);
}
.v-btn.primary {
  background: linear-gradient(135deg, var(--accent) 0%, #8b5cf6 100%);
  border-color: transparent;
  color: #fff;
  box-shadow: 0 6px 16px rgba(99, 102, 241, 0.32);
}
.v-btn.primary:hover {
  transform: translateY(-1px);
  color: #fff;
  filter: brightness(1.05);
  box-shadow: 0 8px 22px rgba(99, 102, 241, 0.38);
}

.v-stage-wrap {
  position: relative;
  flex: 1 1 auto;
  display: flex;
  min-height: 0;
  overflow: hidden;
}
.v-stage-wrap.exif-on {
  /* 横向布局：左 stage + 右 EXIF 面板 */
}
.v-stage {
  position: relative;
  flex: 1 1 0;
  min-height: 120px;
  background:
    radial-gradient(80% 60% at 50% 30%, rgba(30,41,59,0.9), rgba(15,23,42,1));
  padding: 16px;
  overflow: hidden;
  cursor: zoom-in;
  user-select: none;
}
.v-stage::before {
  content: '';
  position: absolute;
  inset: 0;
  background-image:
    linear-gradient(rgba(255,255,255,0.04) 1px, transparent 1px),
    linear-gradient(90deg, rgba(255,255,255,0.04) 1px, transparent 1px);
  background-size: 32px 32px;
  mask-image: radial-gradient(ellipse at center, rgba(0,0,0,0.8), transparent 70%);
  -webkit-mask-image: radial-gradient(ellipse at center, rgba(0,0,0,0.8), transparent 70%);
  pointer-events: none;
}
.v-stage.dragging { cursor: grabbing; }

.v-img-wrap {
  position: absolute;
  inset: 0;
  overflow: visible;
  pointer-events: none;
}
.v-img {
  position: absolute;
  left: 50%;
  top: 50%;
  display: block;
  pointer-events: auto;
  border-radius: 6px;
  box-shadow: 0 22px 60px rgba(0,0,0,0.55), 0 6px 16px rgba(0,0,0,0.35);
  cursor: grab;
  will-change: transform;
  -webkit-user-drag: none;
  user-select: none;
  max-width: none;
  max-height: none;
  transform-origin: 50% 50%;
}
.v-stage:not(.dragging) .v-img:hover { cursor: grab; }

.v-loading,
.v-error {
  position: relative;
  height: 100%;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 12px;
  color: #cbd5e1;
  text-align: center;
  z-index: 1;
}
.v-loading p {
  margin: 0;
  font-size: 14px;
}
.v-error {
  color: #e2e8f0;
  max-width: 440px;
}

.load-more {
  padding: 32px 0 40px;
  text-align: center;
}
.load-more .muted {
  margin: 0;
  color: var(--muted);
  font-size: 13px;
  letter-spacing: 0.04em;
}

/* ========== 批量操作栏 ========== */
.batch-bar {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
  padding: 10px 16px;
  margin-top: 18px;
  background: var(--glass-bg);
  border: 1px solid var(--glass-border);
  border-radius: var(--r-lg);
  backdrop-filter: var(--glass-blur);
  -webkit-backdrop-filter: var(--glass-blur);
  box-shadow: var(--shadow-sm);
}
.batch-check {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  font-size: 13.5px;
  font-weight: 600;
  color: var(--text);
  cursor: pointer;
  user-select: none;
}
.batch-check input[type="checkbox"] {
  width: 16px;
  height: 16px;
  cursor: pointer;
  accent-color: var(--accent);
}
.batch-count {
  font-size: 12.5px;
  color: var(--muted);
  padding: 4px 10px;
  background: var(--surface-strong);
  border: 1px solid var(--border);
  border-radius: var(--r-pill);
  font-variant-numeric: tabular-nums;
}
.batch-ops {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  margin-left: auto;
  flex-wrap: wrap;
}
.batch-select {
  padding: 7px 30px 7px 12px;
  border: 1px solid var(--border);
  background: var(--card-solid);
  color: var(--text);
  border-radius: var(--r-md);
  font-size: 13px;
  font-weight: 500;
  cursor: pointer;
  appearance: none;
  background-image:
    linear-gradient(45deg, transparent 50%, var(--text-muted) 50%),
    linear-gradient(135deg, var(--text-muted) 50%, transparent 50%);
  background-position:
    calc(100% - 16px) 50%,
    calc(100% - 11px) 50%;
  background-size: 5px 5px, 5px 5px;
  background-repeat: no-repeat;
  transition: border-color var(--t-fast) var(--ease), box-shadow var(--t-fast) var(--ease);
}
.batch-select:hover:not(:disabled) { border-color: #c7d2fe; }
.batch-select:focus {
  outline: none;
  border-color: var(--accent);
  box-shadow: var(--shadow-ring);
}
.batch-select:disabled {
  opacity: 0.55;
  cursor: not-allowed;
}
.batch-btn {
  padding: 7px 14px;
  font-size: 13px;
  font-weight: 600;
  border: 1px solid var(--border);
  background: var(--surface-strong);
  color: var(--text);
  border-radius: var(--r-md);
  cursor: pointer;
  transition: transform var(--t-fast) var(--ease), background var(--t-fast) var(--ease), border-color var(--t-fast) var(--ease), color var(--t-fast) var(--ease), box-shadow var(--t-fast) var(--ease), opacity var(--t-fast) var(--ease);
}
.batch-btn:hover:not(:disabled) {
  transform: translateY(-1px);
  background: var(--card-solid);
  border-color: #c7d2fe;
  color: var(--accent);
  box-shadow: var(--shadow-xs);
}
.batch-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
.batch-btn.danger { color: var(--error); }
.batch-btn.danger:hover:not(:disabled) {
  border-color: #fecaca;
  color: var(--error);
  background: linear-gradient(180deg, var(--card-solid), rgba(254, 242, 242, 0.7));
}
.batch-btn.ghost {
  background: transparent;
  border-color: transparent;
  color: var(--muted);
}
.batch-btn.ghost:hover:not(:disabled) {
  color: var(--text);
  background: var(--surface-strong);
  border-color: var(--border);
}

/* ========== 卡片选择框 / 编辑按钮 ========== */
.card.selected {
  border-color: var(--accent);
  box-shadow: 0 0 0 2px var(--accent-soft), var(--shadow-md);
}
.card-check {
  position: absolute;
  top: 10px;
  left: 10px;
  width: 24px;
  height: 24px;
  border-radius: 6px;
  background: var(--card-solid);
  border: 1.5px solid var(--border);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
  z-index: 5;
  transition: background var(--t-fast) var(--ease), border-color var(--t-fast) var(--ease), transform var(--t-fast) var(--ease), opacity var(--t-base) var(--ease);
  box-shadow: var(--shadow-xs);
}
.card-check input[type="checkbox"] {
  position: absolute;
  inset: 0;
  opacity: 0;
  cursor: pointer;
  margin: 0;
}
.card-check .check-mark {
  font-size: 14px;
  color: #fff;
  font-weight: 700;
  line-height: 1;
  opacity: 0;
  transform: scale(0.5);
  transition: opacity var(--t-fast) var(--ease), transform var(--t-fast) var(--ease);
}
.card-check:hover {
  border-color: var(--accent);
  transform: scale(1.06);
}
.card-check.checked {
  background: linear-gradient(135deg, var(--accent), #8b5cf6);
  border-color: transparent;
  box-shadow: 0 4px 10px rgba(99, 102, 241, 0.35);
}
.card-check.checked .check-mark {
  opacity: 1;
  transform: scale(1);
}

.card-edit {
  position: absolute;
  bottom: 10px;
  right: 10px;
  padding: 5px 11px;
  font-size: 12px;
  font-weight: 600;
  color: var(--text);
  background: var(--card-solid);
  border: 1px solid var(--border);
  border-radius: var(--r-md);
  cursor: pointer;
  z-index: 5;
  opacity: 0;
  transform: translateY(6px);
  box-shadow: var(--shadow-xs);
  transition: opacity var(--t-base) var(--ease), transform var(--t-base) var(--ease), background var(--t-fast) var(--ease), border-color var(--t-fast) var(--ease), color var(--t-fast) var(--ease);
}
.card:hover .card-edit {
  opacity: 1;
  transform: translateY(0);
}
.card-edit:hover {
  background: var(--card-solid);
  border-color: #c7d2fe;
  color: var(--accent);
  box-shadow: var(--shadow-xs);
}

/* ========== 编辑弹窗 ========== */
.modal-overlay {
  position: fixed;
  inset: 0;
  z-index: 200;
  background: radial-gradient(80% 60% at 50% 40%, rgba(15, 23, 42, 0.65), rgba(15, 23, 42, 0.9));
  backdrop-filter: blur(6px) saturate(140%);
  -webkit-backdrop-filter: blur(6px) saturate(140%);
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 24px;
  animation: fadeIn 0.16s var(--ease);
}
.modal {
  width: min(440px, 100%);
  background: var(--card-solid);
  border: 1px solid var(--glass-border);
  border-radius: var(--r-xl);
  box-shadow: 0 24px 60px rgba(15, 23, 42, 0.5), 0 8px 18px rgba(15, 23, 42, 0.3);
  overflow: hidden;
  display: flex;
  flex-direction: column;
}
.modal-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 14px 18px;
  background: linear-gradient(90deg, rgba(99,102,241,0.08), rgba(236,72,153,0.06));
  border-bottom: 1px solid var(--border);
}
.modal-head strong {
  font-size: 14.5px;
  color: var(--text);
  letter-spacing: 0.01em;
}
.modal-close {
  width: 28px;
  height: 28px;
  border-radius: 50%;
  border: none;
  background: var(--surface-strong);
  color: var(--text);
  font-size: 18px;
  line-height: 1;
  cursor: pointer;
  transition: background var(--t-fast) var(--ease), transform var(--t-fast) var(--ease);
}
.modal-close:hover {
  background: var(--error);
  color: #fff;
  transform: scale(1.05);
}
.modal-body {
  padding: 16px 18px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.modal-row {
  display: flex;
  align-items: center;
  gap: 10px;
}
.modal-label {
  min-width: 56px;
  font-size: 13px;
  color: var(--muted);
  font-weight: 500;
}
.modal-input {
  flex: 1;
  padding: 8px 12px;
  font-size: 13.5px;
  color: var(--text);
  background: var(--card-solid);
  border: 1px solid var(--border);
  border-radius: var(--r-md);
  font-family: inherit;
  min-width: 0;
  transition: border-color var(--t-fast) var(--ease), box-shadow var(--t-fast) var(--ease);
}
select.modal-input {
  cursor: pointer;
}
.modal-input:hover { border-color: #c7d2fe; }
.modal-input:focus {
  outline: none;
  border-color: var(--accent);
  box-shadow: var(--shadow-ring);
}
.modal-error {
  margin: 0;
  padding: 8px 12px;
  font-size: 12.5px;
  font-weight: 500;
  color: var(--error);
  background: linear-gradient(180deg, rgba(254, 226, 226, 0.85), rgba(254, 242, 242, 0.7));
  border: 1px solid rgba(254, 202, 202, 0.9);
  border-radius: var(--r-md);
}
.modal-foot {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  padding: 12px 18px;
  background: var(--surface-strong);
  border-top: 1px solid var(--border);
}
.modal-btn {
  padding: 8px 18px;
  font-size: 13px;
  font-weight: 600;
  border: 1px solid var(--border);
  background: var(--card-solid);
  color: var(--text);
  border-radius: var(--r-md);
  cursor: pointer;
  transition: transform var(--t-fast) var(--ease), background var(--t-fast) var(--ease), border-color var(--t-fast) var(--ease), color var(--t-fast) var(--ease), box-shadow var(--t-fast) var(--ease), filter var(--t-fast) var(--ease);
}
.modal-btn:hover:not(:disabled) {
  transform: translateY(-1px);
  border-color: #c7d2fe;
  color: var(--accent);
  box-shadow: var(--shadow-xs);
}
.modal-btn.primary {
  background: linear-gradient(135deg, var(--accent) 0%, #8b5cf6 100%);
  border-color: transparent;
  color: #fff;
  box-shadow: 0 6px 16px rgba(99, 102, 241, 0.32);
}
.modal-btn.primary:hover:not(:disabled) {
  color: #fff;
  filter: brightness(1.05);
  box-shadow: 0 8px 22px rgba(99, 102, 241, 0.38);
}
.modal-btn.ghost {
  background: transparent;
  border-color: transparent;
  color: var(--muted);
}
.modal-btn:disabled {
  opacity: 0.55;
  cursor: not-allowed;
}

/* ========== EXIF 侧边面板 ========== */
.v-btn.active {
  background: linear-gradient(135deg, var(--accent) 0%, #8b5cf6 100%);
  color: #fff !important;
  border-color: transparent !important;
  box-shadow: 0 6px 16px rgba(99, 102, 241, 0.32);
}
.exif-panel {
  flex: 0 0 320px;
  width: 320px;
  background: var(--card-solid);
  border-left: 1px solid var(--border);
  display: flex;
  flex-direction: column;
  overflow: hidden;
  box-shadow: -8px 0 24px rgba(15, 23, 42, 0.18);
}
.exif-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 12px 16px;
  background: linear-gradient(90deg, rgba(99,102,241,0.06), rgba(236,72,153,0.04));
  border-bottom: 1px solid var(--border);
  flex-shrink: 0;
}
.exif-head strong {
  font-size: 13.5px;
  color: var(--text);
  letter-spacing: 0.01em;
}
.exif-close {
  width: 26px;
  height: 26px;
  border-radius: 50%;
  border: none;
  background: var(--surface-strong);
  color: var(--text);
  font-size: 16px;
  line-height: 1;
  cursor: pointer;
  transition: background var(--t-fast) var(--ease), transform var(--t-fast) var(--ease);
}
.exif-close:hover {
  background: var(--error);
  color: #fff;
  transform: scale(1.05);
}
.exif-body {
  flex: 1 1 auto;
  overflow-y: auto;
  padding: 8px 16px 16px;
}
.exif-loading {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  padding: 32px 12px;
  color: var(--muted);
  font-size: 13px;
}
.exif-loading .spinner {
  width: 26px;
  height: 26px;
  border: 2.5px solid rgba(99, 102, 241, 0.22);
  border-top-color: var(--accent);
}
.exif-error {
  margin: 12px 0 0;
  padding: 10px 12px;
  font-size: 12.5px;
  color: var(--error);
  background: linear-gradient(180deg, rgba(254, 226, 226, 0.85), rgba(254, 242, 242, 0.7));
  border: 1px solid rgba(254, 202, 202, 0.9);
  border-radius: var(--r-md);
  font-weight: 500;
}
.exif-table dl {
  margin: 8px 0 0;
  display: grid;
  grid-template-columns: 110px 1fr;
  gap: 1px 8px;
  font-size: 12.5px;
  line-height: 1.5;
}
.exif-table dt {
  color: var(--muted);
  font-weight: 500;
  padding: 5px 0;
  word-break: break-all;
  border-bottom: 1px solid var(--border);
}
.exif-table dd {
  margin: 0;
  color: var(--text);
  font-weight: 500;
  padding: 5px 0;
  word-break: break-word;
  font-variant-numeric: tabular-nums;
  border-bottom: 1px solid var(--border);
}
.exif-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 6px;
  padding: 40px 12px;
  text-align: center;
  color: var(--muted);
}
.exif-empty .ph-icon {
  font-size: 28px;
  opacity: 0.7;
}
.exif-empty p {
  margin: 0;
  font-size: 13px;
}
.exif-note {
  margin-top: 4px !important;
  font-size: 12px !important;
  color: var(--text-muted);
  font-style: italic;
}

/* 响应式：窄屏把 EXIF 面板压缩 */
@media (max-width: 720px) {
  .exif-panel {
    flex: 0 0 240px;
    width: 240px;
  }
  .exif-table dl {
    grid-template-columns: 88px 1fr;
  }
}

/* ============ 移动端响应式 ============ */
@media (max-width: 768px) {
  .grid {
    grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
    gap: 12px;
    margin-top: 16px;
  }
  .card { border-radius: var(--r-md); }
  .batch-bar {
    flex-wrap: wrap;
    gap: 8px;
    padding: 8px 10px;
  }
  .batch-count { font-size: 12px; }
  .v-head {
    flex-wrap: wrap;
    gap: 6px;
    padding: 8px 10px;
  }
  .v-head button,
  .v-actions button {
    min-width: 32px;
    height: 32px;
    font-size: 12px;
  }
  .v-zoom-display { font-size: 11px; min-width: 40px; }
  .exif-panel { width: 100%; max-width: 100%; }
  .exif-table { width: 100%; }
  .exif-table dl { grid-template-columns: 80px 1fr; }
}

@media (max-width: 480px) {
  .grid {
    grid-template-columns: repeat(auto-fill, minmax(140px, 1fr));
    gap: 10px;
  }
  .card .actions button {
    padding: 4px 6px;
    font-size: 11px;
  }
  .card .actions .btn-icon {
    width: 28px;
    height: 28px;
  }
  .v-head { padding: 6px 8px; }
  .v-head button,
  .v-actions button {
    min-width: 30px;
    height: 30px;
    font-size: 11px;
  }
  .exif-panel { width: 100%; }
}
</style>
