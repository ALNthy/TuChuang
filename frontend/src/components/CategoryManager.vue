<script setup lang="ts">
import { nextTick, ref } from 'vue'
import { useCategories } from '../composables/useCategories'
import { createCategory, deleteCategory, renameCategory } from '../api'
import type { Category } from '../types'

const { categories, refresh } = useCategories()
const newName = ref('')
const error = ref('')
const busy = ref(false)

async function add() {
  const name = newName.value.trim()
  if (!name) return
  busy.value = true
  error.value = ''
  try {
    await createCategory(name)
    newName.value = ''
    await refresh()
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    busy.value = false
  }
}

async function remove(id: number, name: string) {
  if (!confirm(`删除分类"${name}"？如果分类下还有图片将无法删除。`)) return
  error.value = ''
  try {
    await deleteCategory(id)
    // 如果正在编辑的就是被删的，退出编辑
    if (editingId.value === id) cancelEdit()
    await refresh()
  } catch (e) {
    error.value = (e as Error).message
  }
}

// ================================================================================
// 重命名：双击 chip 进入编辑模式，input 替换 chip；Enter 保存，Esc 取消
// ================================================================================
const editingId = ref<number | null>(null)
const editingName = ref('')
const editingError = ref('')
const editingBusy = ref(false)
const editInputEl = ref<HTMLInputElement | null>(null)

function isEditing(c: Category): boolean {
  return editingId.value === c.id
}

/** 模板里 :ref 绑定的回调：v-for 上下文用函数 ref 比字符串 ref 类型更稳 */
function setEditInputRef(el: Element | unknown | null): void {
  if (el instanceof HTMLInputElement) {
    editInputEl.value = el
  } else {
    editInputEl.value = null
  }
}

async function startEdit(c: Category, event?: MouseEvent) {
  // 单击 × 按钮不触发编辑（事件冒泡到 chip），通过判断 target 排除
  if (event) {
    const target = event.target as HTMLElement
    if (target.closest('.x')) return
  }
  if (editingBusy.value) return
  editingId.value = c.id
  editingName.value = c.name
  editingError.value = ''
  await nextTick()
  editInputEl.value?.focus()
  editInputEl.value?.select()
}

function cancelEdit() {
  editingId.value = null
  editingName.value = ''
  editingError.value = ''
  editingBusy.value = false
}

async function saveEdit(c: Category) {
  const name = editingName.value.trim()
  if (!name) {
    editingError.value = '分类名不能为空'
    return
  }
  if (name === c.name) {
    cancelEdit()
    return
  }
  editingBusy.value = true
  editingError.value = ''
  try {
    await renameCategory(c.id, name)
    await refresh()
    cancelEdit()
  } catch (e) {
    editingError.value = (e as Error).message
    // 失败时保持编辑态，让用户改
    editingBusy.value = false
    await nextTick()
    editInputEl.value?.focus()
    editInputEl.value?.select()
  }
}

function onEditKeydown(c: Category, e: KeyboardEvent) {
  if (e.key === 'Enter') {
    e.preventDefault()
    void saveEdit(c)
  } else if (e.key === 'Escape') {
    e.preventDefault()
    cancelEdit()
  }
}

function onEditBlur(c: Category) {
  // 失焦时若值有效则保存，空则取消
  if (editingBusy.value) return
  const name = editingName.value.trim()
  if (!name || name === c.name) {
    cancelEdit()
  } else {
    void saveEdit(c)
  }
}
</script>

<template>
  <div class="cm">
    <div class="cm-title">分类管理</div>
    <div class="cm-head">
      <input
        v-model="newName"
        class="cm-input"
        placeholder="输入新分类名称"
        maxlength="64"
        @keyup.enter="add"
      />
      <button class="cm-add" :disabled="busy || !newName.trim()" @click="add">添加分类</button>
    </div>
    <p v-if="error" class="cm-error">{{ error }}</p>
    <div class="chips">
      <template v-for="c in categories" :key="c.id">
        <!-- 编辑态：input 替换 chip -->
        <span v-if="isEditing(c)" class="chip-editing">
          <input
            :ref="setEditInputRef"
            v-model="editingName"
            class="chip-input"
            maxlength="64"
            @keydown="onEditKeydown(c, $event)"
            @blur="onEditBlur(c)"
            @click.stop
          />
          <span v-if="editingError" class="chip-edit-error" :title="editingError">⚠</span>
        </span>
        <!-- 显示态：双击进入编辑 -->
        <span
          v-else
          class="chip"
          :class="{ 'has-error': editingError && false }"
          @dblclick="startEdit(c, $event)"
          :title="'双击重命名分类：' + c.name"
        >
          <span class="chip-name">{{ c.name }}</span>
          <button class="x" @click="remove(c.id, c.name)" title="删除分类" type="button">×</button>
        </span>
      </template>
      <span v-if="!categories.length" class="cm-empty">暂无分类</span>
    </div>
    <!-- 编辑错误内联提示 -->
    <p v-if="editingError" class="cm-error cm-edit-error-inline">{{ editingError }}</p>
    <p v-else-if="categories.length" class="cm-hint">提示：双击分类名可重命名</p>
  </div>
</template>

<style scoped>
.cm {
  margin-top: 16px;
  padding: 16px 18px;
  background: var(--glass-bg);
  border: 1px solid var(--glass-border);
  border-radius: var(--r-lg);
  backdrop-filter: var(--glass-blur);
  -webkit-backdrop-filter: var(--glass-blur);
  box-shadow: var(--shadow-sm);
}
.cm-title {
  font-size: 14px;
  font-weight: 700;
  letter-spacing: 0.02em;
  margin-bottom: 12px;
  color: var(--text);
  display: inline-flex;
  align-items: center;
  gap: 8px;
}
.cm-title::before {
  content: '';
  width: 6px;
  height: 16px;
  border-radius: 3px;
  background: linear-gradient(180deg, var(--accent), #ec4899);
}
.cm-head {
  display: flex;
  gap: 8px;
}
.cm-input {
  flex: 1;
  padding: 9px 14px;
  border: 1px solid var(--border);
  background: var(--card-solid);
  border-radius: var(--r-md);
  font-size: 14px;
  transition: border-color var(--t-fast) var(--ease), box-shadow var(--t-fast) var(--ease);
}
.cm-input:hover { border-color: #c7d2fe; }
.cm-input:focus {
  outline: none;
  border-color: var(--accent);
  box-shadow: var(--shadow-ring);
}
.cm-add {
  padding: 9px 18px;
  border: 1px solid transparent;
  background: linear-gradient(135deg, var(--accent) 0%, #8b5cf6 100%);
  color: #fff;
  border-radius: var(--r-md);
  font-size: 13.5px;
  font-weight: 600;
  cursor: pointer;
  box-shadow: 0 6px 16px rgba(99, 102, 241, 0.30);
  transition: transform var(--t-fast) var(--ease), filter var(--t-fast) var(--ease), box-shadow var(--t-fast) var(--ease), opacity var(--t-fast) var(--ease);
}
.cm-add:hover:not(:disabled) {
  transform: translateY(-1px);
  filter: brightness(1.05);
  box-shadow: 0 8px 22px rgba(99, 102, 241, 0.36);
}
.cm-add:disabled {
  opacity: 0.55;
  cursor: not-allowed;
  filter: none;
  box-shadow: none;
  transform: none;
}
.cm-error {
  color: var(--error);
  font-size: 13px;
  margin: 10px 0 0;
  padding: 8px 12px;
  background: linear-gradient(180deg, rgba(254, 226, 226, 0.9), rgba(254, 242, 242, 0.7));
  border: 1px solid rgba(254, 202, 202, 0.9);
  border-radius: var(--r-md);
  font-weight: 500;
}
.chips {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-top: 14px;
}
.chip {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  padding: 5px 6px 5px 14px;
  background: linear-gradient(135deg, rgba(238, 242, 255, 0.9), rgba(243, 232, 255, 0.9));
  color: var(--accent);
  border: 1px solid rgba(199, 210, 254, 0.9);
  border-radius: var(--r-pill);
  font-size: 13px;
  font-weight: 600;
  backdrop-filter: blur(6px);
  -webkit-backdrop-filter: blur(6px);
  transition: transform var(--t-fast) var(--ease), box-shadow var(--t-fast) var(--ease);
}
.chip:hover {
  transform: translateY(-1px);
  box-shadow: 0 6px 14px rgba(99, 102, 241, 0.18);
}
.chip .chip-name {
  cursor: inherit;
  user-select: none;
  letter-spacing: 0.01em;
}
.chip .x {
  border: none;
  background: transparent;
  color: var(--accent);
  cursor: pointer;
  font-size: 16px;
  line-height: 1;
  padding: 0 8px;
  border-radius: 999px;
  transition: background var(--t-fast) var(--ease), color var(--t-fast) var(--ease), transform var(--t-fast) var(--ease);
}
.chip .x:hover {
  background: linear-gradient(135deg, var(--accent), #ec4899);
  color: #fff;
  transform: scale(1.08);
}

/* ========== 编辑态 chip ========== */
.chip-editing {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 0;
  background: var(--card-solid);
  border: 1.5px solid var(--accent);
  border-radius: var(--r-pill);
  box-shadow: var(--shadow-ring);
  transition: border-color var(--t-fast) var(--ease), box-shadow var(--t-fast) var(--ease);
}
.chip-input {
  border: none;
  background: transparent;
  color: var(--text);
  font-size: 13px;
  font-weight: 600;
  padding: 4px 12px;
  border-radius: var(--r-pill);
  outline: none;
  width: auto;
  min-width: 80px;
  max-width: 220px;
  font-family: inherit;
  letter-spacing: 0.01em;
}
.chip-edit-error {
  color: var(--error);
  font-size: 13px;
  padding-right: 10px;
  cursor: help;
  user-select: none;
}
.cm-edit-error-inline {
  margin-top: 8px;
  font-size: 12.5px;
  padding: 6px 12px;
}
.cm-hint {
  margin: 10px 0 0;
  font-size: 12px;
  color: var(--text-muted);
  font-style: italic;
  letter-spacing: 0.02em;
  opacity: 0.85;
}
.cm-empty {
  color: var(--muted);
  font-size: 13.5px;
  padding: 4px 2px;
}
</style>
