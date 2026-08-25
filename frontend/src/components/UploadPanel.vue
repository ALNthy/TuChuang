<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useCategories } from '../composables/useCategories'
import { uploadFilesSmart } from '../api'

/** 上传完成事件：父组件监听后刷新分类与图片列表 */
const emit = defineEmits<{ (e: 'uploaded'): void }>()

const { categories, refresh: refreshCategories } = useCategories()

const dragOver = ref(false)
const inputEl = ref<HTMLInputElement | null>(null)
const errorMsg = ref('')

/** 上传状态：uploading 占位 + progress 百分比（0-100） + 当前文件信息 */
const uploading = ref(false)
const progress = ref(0)
const currentFile = ref('')

/** 分片上传阈值（超过此大小会切片上传） */
const CHUNK_THRESHOLD_MB = 5

/** select 当前选中的值；当选中 "custom" 时使用 customText */
const selectedCategory = ref<string>('其他')
const customText = ref<string>('')

const CUSTOM_OPTION = '__custom__'

const MAX_BYTES = 100 * 1024 * 1024
/** 后端白名单：与 ImagesController.AllowedExtensions 一致 */
const ALLOWED_EXT = ['.png', '.jpg', '.jpeg', '.gif', '.webp', '.bmp', '.raw', '.arw']

/** 当 categories 加载完成后，如果默认 "其他" 存在则保持，否则取第一个 */
watch(
  categories,
  list => {
    if (!list.length) return
    if (!list.some(c => c.name === selectedCategory.value)) {
      if (list.some(c => c.name === '其他')) selectedCategory.value = '其他'
      else selectedCategory.value = list[0].name
    }
  },
  { immediate: true }
)

const resolvedCategory = computed<string>(() => {
  if (selectedCategory.value === CUSTOM_OPTION) {
    const t = customText.value.trim()
    return t || '其他'
  }
  return selectedCategory.value
})

function extOf(name: string): string {
  const idx = name.lastIndexOf('.')
  return idx < 0 ? '' : name.slice(idx).toLowerCase()
}

function isExtAllowed(name: string): boolean {
  return ALLOWED_EXT.includes(extOf(name))
}

/** 前置校验：体积 + 扩展名；返回合格的 File[] 或通过 errorMsg 提示 */
function filterAndValidate(files: File[]): File[] {
  errorMsg.value = ''
  if (!files.length) return []
  const ok: File[] = []
  const bad: string[] = []
  const oversized: string[] = []
  for (const f of files) {
    if (f.size > MAX_BYTES) { oversized.push(`${f.name}(${f.size})`); continue }
    // RAW/ARW 在浏览器 mime 中可能为 ''；所以允许 mime=image/* 或扩展名白名单命中
    const mimeOk = f.type && f.type.startsWith('image/')
    if (!mimeOk && !isExtAllowed(f.name)) { bad.push(f.name); continue }
    if (mimeOk || isExtAllowed(f.name)) ok.push(f)
  }
  if (bad.length) errorMsg.value = `以下文件类型不支持：${bad.slice(0, 5).join('、')}${bad.length > 5 ? '…' : ''}`
  if (oversized.length) {
    const s = oversized.slice(0, 5).map(s => s).join('、')
    errorMsg.value = (errorMsg.value ? errorMsg.value + '；' : '') +
      `以下文件超过 100MB 限制：${s}${oversized.length > 5 ? '…' : ''}`
  }
  return ok
}

async function submit(files: File[]) {
  const ok = filterAndValidate(files)
  if (!ok.length) return
  uploading.value = true
  progress.value = 0
  errorMsg.value = ''
  currentFile.value = ok.length === 1 ? ok[0].name : `${ok.length} 个文件`
  try {
    await uploadFilesSmart(ok, resolvedCategory.value, p => {
      progress.value = p
    })
    // 上传可能新建了自定义分类，刷新分类列表
    await refreshCategories()
    // 完成后通知父组件刷新图片列表
    emit('uploaded')
    progress.value = 100
  } catch (e) {
    errorMsg.value = (e as Error).message
  } finally {
    uploading.value = false
    currentFile.value = ''
    // 进度条收尾：留一点时间让用户看到 100% 再隐藏
    setTimeout(() => { progress.value = 0 }, 600)
  }
}

function onDrop(e: DragEvent) {
  if (uploading.value) return
  dragOver.value = false
  const files = Array.from(e.dataTransfer?.files ?? [])
  void submit(files)
}

function onPick(e: Event) {
  if (uploading.value) return
  const target = e.target as HTMLInputElement
  const files = Array.from(target.files ?? [])
  void submit(files)
  target.value = ''
}

/** 进度条样式宽度 */
const progressWidth = computed(() => `${progress.value}%`)
</script>

<template>
  <div
    class="dropzone"
    :class="{ active: dragOver, busy: uploading }"
    @dragover.prevent="dragOver = true"
    @dragleave.prevent="dragOver = false"
    @drop.prevent="onDrop"
    @click="!uploading && inputEl?.click()"
  >
    <input
      ref="inputEl"
      type="file"
      accept="image/*,.raw,.arw,.RAW,.ARW"
      multiple
      hidden
      @change="onPick"
    />
    <div class="inner">
      <div class="icon">⬆️</div>
      <p v-if="!uploading" class="title">点击或拖拽文件到此处上传</p>
      <div v-else class="upload-status">
        <p class="title">上传中… {{ progress }}%</p>
        <p v-if="currentFile" class="current-file">{{ currentFile }}</p>
      </div>
      <p class="hint">
        支持 PNG / JPG / GIF / WebP / BMP / RAW / ARW，单张最大 <strong>100MB</strong>
        <br />大于 {{ CHUNK_THRESHOLD_MB }}MB 自动<span class="chunk-badge">分片上传</span>
      </p>

      <!-- 上传进度条 -->
      <div v-if="uploading || progress > 0" class="progress" @click.stop>
        <div class="progress-bar" :style="{ width: progressWidth }">
          <span class="progress-pct">{{ progress }}%</span>
        </div>
      </div>

      <p v-if="errorMsg" class="err" @click.stop>{{ errorMsg }}</p>

      <div class="cat-block" @click.stop>
        <label class="cat-row">
          <span>分类：</span>
          <select class="cat-select" v-model="selectedCategory" :disabled="uploading">
            <option v-for="c in categories" :key="c.id" :value="c.name">{{ c.name }}</option>
            <option :value="CUSTOM_OPTION">＋ 自定义分类…</option>
          </select>
        </label>
        <label v-if="selectedCategory === CUSTOM_OPTION" class="cat-row">
          <span>分类名：</span>
          <input
            class="cat-input"
            v-model="customText"
            maxlength="64"
            placeholder="输入自定义分类名称"
            :disabled="uploading"
          />
        </label>
        <p class="selected" v-if="selectedCategory !== CUSTOM_OPTION">
          上传到分类：<strong>{{ selectedCategory }}</strong>
        </p>
        <p class="selected" v-else>
          上传到分类：<strong>{{ resolvedCategory }}</strong>
        </p>
      </div>
    </div>
  </div>
</template>

<style scoped>
.dropzone {
  position: relative;
  border-radius: var(--r-xl);
  padding: 38px 32px;
  text-align: center;
  cursor: pointer;
  transition: transform var(--t-base) var(--ease), box-shadow var(--t-base) var(--ease), border-color var(--t-base) var(--ease), background var(--t-base) var(--ease);
  background: var(--glass-bg);
  border: 2px dashed rgba(165, 180, 252, 0.75);
  backdrop-filter: var(--glass-blur);
  -webkit-backdrop-filter: var(--glass-blur);
  box-shadow: var(--shadow-sm);
  overflow: hidden;
}
.dropzone::before {
  content: '';
  position: absolute;
  inset: -40%;
  background:
    radial-gradient(circle at 20% 20%, rgba(99,102,241,0.12), transparent 40%),
    radial-gradient(circle at 80% 80%, rgba(236,72,153,0.10), transparent 40%);
  pointer-events: none;
  opacity: 0;
  transition: opacity var(--t-base) var(--ease);
}
.dropzone:hover,
.dropzone.active {
  border-color: var(--accent);
  background: linear-gradient(180deg, rgba(224, 231, 255, 0.7), rgba(238, 242, 255, 0.72));
  transform: translateY(-1px);
  box-shadow: var(--shadow-md);
}
.dropzone:hover::before,
.dropzone.active::before {
  opacity: 1;
}
.dropzone.busy {
  opacity: 0.65;
  cursor: progress;
  transform: none !important;
}
.inner {
  position: relative;
  z-index: 1;
}
.inner .icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 62px;
  height: 62px;
  font-size: 28px;
  border-radius: 50%;
  background: linear-gradient(135deg, rgba(99,102,241,0.14), rgba(236,72,153,0.12));
  border: 1px solid rgba(196,181,253,0.5);
  margin-bottom: 10px;
  box-shadow: inset 0 1px 0 rgba(255,255,255,0.7);
}
.inner .title {
  font-size: 16px;
  font-weight: 700;
  letter-spacing: 0.01em;
  margin: 0 0 4px;
  color: var(--text);
}
.inner .hint {
  font-size: 13px;
  color: var(--muted);
  margin: 0 auto 10px;
  max-width: 560px;
}
.inner .hint strong { color: var(--accent); }

/* ========== 上传进度条 ========== */
.progress {
  margin: 6px auto 10px;
  max-width: 560px;
  height: 18px;
  border-radius: var(--r-pill);
  background: var(--surface-strong);
  border: 1px solid var(--glass-border);
  overflow: hidden;
  box-shadow: var(--shadow-xs);
  position: relative;
}
.progress-bar {
  height: 100%;
  width: 0%;
  border-radius: var(--r-pill);
  background: linear-gradient(90deg, var(--accent) 0%, #8b5cf6 50%, #ec4899 100%);
  background-size: 200% 100%;
  animation: progressShine 1.6s linear infinite;
  transition: width 0.25s var(--ease);
  display: flex;
  align-items: center;
  justify-content: flex-end;
  box-shadow: 0 0 12px rgba(99, 102, 241, 0.45);
  position: relative;
  overflow: hidden;
}
.progress-bar::after {
  content: '';
  position: absolute;
  inset: 0;
  background: linear-gradient(90deg, transparent 0%, rgba(255, 255, 255, 0.25) 50%, transparent 100%);
  background-size: 200% 100%;
  animation: progressSweep 1.2s linear infinite;
  pointer-events: none;
}
.progress-pct {
  font-size: 11.5px;
  font-weight: 700;
  color: #fff;
  padding-right: 8px;
  font-variant-numeric: tabular-nums;
  text-shadow: 0 1px 2px rgba(0, 0, 0, 0.35);
  letter-spacing: 0.02em;
}
@keyframes progressShine {
  0% { background-position: 0% 0; }
  100% { background-position: -200% 0; }
}
@keyframes progressSweep {
  0% { background-position: -100% 0; }
  100% { background-position: 200% 0; }
}

.err {
  margin: 0 auto 10px;
  max-width: 560px;
  padding: 9px 14px;
  background: linear-gradient(180deg, rgba(254, 226, 226, 0.9), rgba(254, 242, 242, 0.7));
  color: var(--error);
  border: 1px solid rgba(254, 202, 202, 0.9);
  border-radius: var(--r-md);
  font-size: 13px;
  font-weight: 500;
  text-align: left;
  box-shadow: var(--shadow-xs);
}
.cat-block {
  display: inline-flex;
  flex-direction: column;
  align-items: stretch;
  gap: 8px;
  text-align: left;
  margin-top: 10px;
  padding: 14px 16px;
  background: var(--surface-strong);
  border: 1px solid var(--glass-border);
  border-radius: var(--r-lg);
  backdrop-filter: saturate(180%) blur(6px);
  -webkit-backdrop-filter: saturate(180%) blur(6px);
  min-width: 360px;
  max-width: 100%;
}
.cat-row {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  font-size: 13.5px;
  color: var(--text);
  font-weight: 500;
}
.cat-row > span:first-child {
  min-width: 66px;
  color: var(--muted);
}
.cat-select,
.cat-input {
  padding: 7px 34px 7px 12px;
  border: 1px solid var(--border);
  background: var(--card-solid);
  color: var(--text);
  border-radius: var(--r-md);
  font-size: 13.5px;
  font-weight: 500;
  transition: border-color var(--t-fast) var(--ease), box-shadow var(--t-fast) var(--ease);
  flex: 1;
  min-width: 240px;
  cursor: pointer;
}
.cat-select {
  appearance: none;
  background-image:
    linear-gradient(45deg, transparent 50%, var(--text-muted) 50%),
    linear-gradient(135deg, var(--text-muted) 50%, transparent 50%);
  background-position:
    calc(100% - 16px) 50%,
    calc(100% - 11px) 50%;
  background-size: 5px 5px, 5px 5px;
  background-repeat: no-repeat;
}
.cat-input { cursor: text; padding-right: 12px; }
.cat-select:hover:not(:disabled),
.cat-input:hover:not(:disabled) { border-color: #c7d2fe; }
.cat-select:focus,
.cat-input:focus {
  outline: none;
  border-color: var(--accent);
  box-shadow: var(--shadow-ring);
}
.cat-select:disabled,
.cat-input:disabled {
  opacity: 0.55;
  cursor: not-allowed;
}
.selected {
  margin: 2px 0 0;
  padding: 6px 12px;
  background: linear-gradient(90deg, rgba(99,102,241,0.08), rgba(236,72,153,0.07));
  border-radius: var(--r-md);
  font-size: 13px;
  color: var(--muted);
  border: 1px solid rgba(199, 210, 254, 0.55);
}
.selected strong {
  color: var(--accent);
  font-weight: 700;
}

@media (max-width: 480px) {
  .cat-block { min-width: unset; width: 100%; }
  .cat-row { flex-wrap: wrap; gap: 6px; font-size: 12.5px; }
  .cat-row select { min-width: 120px; }
  .upload-progress { max-width: 100%; height: 14px; font-size: 11px; }
}

/* ========== 分片上传相关样式 ========== */
.upload-status .current-file {
  font-size: 12px;
  color: var(--text-muted);
  margin: 2px 0 0;
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.chunk-badge {
  display: inline-block;
  padding: 1px 6px;
  font-size: 11px;
  font-weight: 600;
  color: var(--accent);
  background: rgba(99, 102, 241, 0.12);
  border: 1px solid rgba(99, 102, 241, 0.25);
  border-radius: var(--r-pill);
  vertical-align: 1px;
}
</style>
