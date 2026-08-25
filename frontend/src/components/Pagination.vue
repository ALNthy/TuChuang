<script setup lang="ts">
import { computed } from 'vue'

const props = defineProps<{
  current: number
  total: number
  pageSize: number
}>()
const emit = defineEmits<{ (e: 'change', page: number): void }>()

const totalPages = computed(() => Math.max(1, Math.ceil(props.total / props.pageSize)))

/**
 * 生成页码按钮列表：当前页前后各 2 页 + 首尾 + 必要的省略号占位
 * 例：current=5, total=20 → [1, '…', 3, 4, 5, 6, 7, '…', 20]
 * 例：current=2, total=20 → [1, 2, 3, 4, 5, '…', 20]
 */
const pageItems = computed<(number | 'ellipsis')[]>(() => {
  const tp = totalPages.value
  const cur = props.current
  if (tp <= 7) {
    return Array.from({ length: tp }, (_, i) => i + 1)
  }
  const items: (number | 'ellipsis')[] = [1]
  const start = Math.max(2, cur - 2)
  const end = Math.min(tp - 1, cur + 2)
  if (start > 2) items.push('ellipsis')
  for (let i = start; i <= end; i++) items.push(i)
  if (end < tp - 1) items.push('ellipsis')
  items.push(tp)
  return items
})

function go(page: number) {
  const p = Math.min(Math.max(1, page), totalPages.value)
  if (p !== props.current) emit('change', p)
}
</script>

<template>
  <nav v-if="total > pageSize" class="pagination" aria-label="分页">
    <button class="pg-btn pg-side" :disabled="current <= 1" title="上一页" @click="go(current - 1)">‹ 上一页</button>

    <ul class="pg-list">
      <li v-for="(item, i) in pageItems" :key="i">
        <span v-if="item === 'ellipsis'" class="pg-ellipsis">…</span>
        <button
          v-else
          class="pg-num"
          :class="{ active: item === current }"
          :aria-current="item === current ? 'page' : undefined"
          @click="go(item)"
        >{{ item }}</button>
      </li>
    </ul>

    <button class="pg-btn pg-side" :disabled="current >= totalPages" title="下一页" @click="go(current + 1)">下一页 ›</button>

    <span class="pg-total">共 {{ total }} 张 · {{ current }} / {{ totalPages }} 页</span>
  </nav>
</template>

<style scoped>
.pagination {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 12px;
  flex-wrap: wrap;
  margin-top: 32px;
  padding: 16px;
  background: var(--glass-bg);
  border: 1px solid var(--glass-border);
  border-radius: var(--r-lg);
  backdrop-filter: var(--glass-blur);
  -webkit-backdrop-filter: var(--glass-blur);
  box-shadow: var(--shadow-sm);
}

.pg-list {
  display: flex;
  align-items: center;
  gap: 6px;
  list-style: none;
  margin: 0;
  padding: 0;
}

.pg-btn,
.pg-num {
  cursor: pointer;
  border: 1px solid var(--border);
  background: rgba(255, 255, 255, 0.7);
  color: var(--text);
  font-size: 13.5px;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
  border-radius: var(--r-md);
  transition: transform var(--t-fast) var(--ease), background var(--t-fast) var(--ease), border-color var(--t-fast) var(--ease), color var(--t-fast) var(--ease), box-shadow var(--t-fast) var(--ease);
  user-select: none;
}
.pg-btn {
  padding: 8px 14px;
}
.pg-num {
  min-width: 36px;
  height: 36px;
  padding: 0 8px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
}

.pg-btn:hover:not(:disabled),
.pg-num:hover:not(.active) {
  transform: translateY(-1px);
  background: #fff;
  border-color: #c7d2fe;
  color: var(--accent);
  box-shadow: var(--shadow-xs);
}

.pg-num.active {
  background: linear-gradient(135deg, var(--accent) 0%, #8b5cf6 100%);
  border-color: transparent;
  color: #fff;
  box-shadow: 0 6px 16px rgba(99, 102, 241, 0.32);
}

.pg-btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
  pointer-events: none;
}

.pg-ellipsis {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 24px;
  height: 36px;
  color: var(--muted);
  font-size: 14px;
  user-select: none;
}

.pg-total {
  font-size: 12.5px;
  color: var(--muted);
  font-variant-numeric: tabular-nums;
  padding-left: 6px;
  border-left: 1px solid var(--border);
  margin-left: 4px;
}

@media (max-width: 480px) {
  .pagination { gap: 4px; }
  .pagination button { min-width: 32px; height: 32px; font-size: 12.5px; }
  .pg-total { font-size: 11px; padding-left: 4px; margin-left: 2px; }
}
</style>
