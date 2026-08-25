import router from '../router'
import type {
  BatchResult,
  Category,
  ExifResponse,
  ImageDto,
  ImageUpdateRequest,
  LoginResponse,
  MeResponse,
  PagedResponse
} from '../types'
import { ALL_CATEGORY } from '../types'
import { clearAuthLocal, readAuthToken } from '../composables/useAuth'

const BASE = '/api'

function authHeaders(): Record<string, string> {
  const t = readAuthToken()
  return t ? { Authorization: `Bearer ${t}` } : {}
}

/**
 * 统一请求封装：自动带鉴权头；401 自动清空 token 并跳到登录页。
 */
async function request<T = unknown>(
  path: string,
  options: RequestInit = {}
): Promise<T> {
  const headers = new Headers(options.headers || {})
  for (const [k, v] of Object.entries(authHeaders())) {
    if (!headers.has(k)) headers.set(k, v)
  }
  const r = await fetch(`${BASE}${path}`, { ...options, headers })

  if (r.status === 401) {
    clearAuthLocal()
    // 已经在登录页就不再跳转，避免死循环
    if (router.currentRoute.value.path !== '/login') {
      router.replace({ path: '/login', query: { redirect: router.currentRoute.value.fullPath } })
    }
    throw new Error('未登录或登录已过期 (401)')
  }
  if (!r.ok && r.status !== 204) {
    let text = ''
    try {
      text = await r.text()
    } catch {
      /* ignore */
    }
    // 尝试解析 JSON 提取 error 字段，让错误信息更友好
    let msg = text || r.statusText
    try {
      const j = JSON.parse(text)
      if (j.error) msg = j.error
    } catch {
      /* not json, use raw text */
    }
    throw new Error(`请求失败 (${r.status}): ${msg}`)
  }
  // 204 No Content 返回空对象给 T 不报错
  if (r.status === 204 || r.headers.get('content-length') === '0') {
    return undefined as unknown as T
  }
  return r.json() as Promise<T>
}

export async function listImages(
  category: string = ALL_CATEGORY,
  page: number = 1,
  pageSize: number = 40,
  keyword?: string
): Promise<PagedResponse<ImageDto>> {
  const params = new URLSearchParams()
  if (category && category !== ALL_CATEGORY) params.set('category', category)
  if (keyword && keyword.trim()) params.set('keyword', keyword.trim())
  params.set('page', String(page))
  params.set('pageSize', String(pageSize))
  return request<PagedResponse<ImageDto>>(`/images?${params.toString()}`, { method: 'GET' })
}

export async function uploadImages(files: File[], category: string): Promise<ImageDto[]> {
  const fd = new FormData()
  for (const f of files) fd.append('files', f)
  fd.append('category', category)
  return request<ImageDto[]>('/images', { method: 'POST', body: fd })
}

export async function deleteImage(id: number): Promise<void> {
  return request<void>(`/images/${id}`, { method: 'DELETE' })
}

export async function getCategories(): Promise<Category[]> {
  return request<Category[]>('/categories', { method: 'GET' })
}

export async function createCategory(name: string): Promise<Category> {
  return request<Category>('/categories', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name })
  })
}

export async function deleteCategory(id: number): Promise<void> {
  return request<void>(`/categories/${id}`, { method: 'DELETE' })
}

export async function login(username: string, password: string): Promise<LoginResponse> {
  return request<LoginResponse>('/account/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password })
  })
}

export async function getMe(): Promise<MeResponse> {
  return request<MeResponse>('/account/me', { method: 'GET' })
}

// ================================================================================
// 图片元信息 / 批量操作 / EXIF
// ================================================================================

/** 更新单张图片的元信息（分类 / 文件名），传哪个字段改哪个 */
export async function updateImage(id: number, data: ImageUpdateRequest): Promise<ImageDto> {
  return request<ImageDto>(`/images/${id}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(data)
  })
}

/** 批量更新图片分类：把 ids 中的图片全部归入 category */
export async function updateImageBatch(ids: number[], category: string): Promise<BatchResult> {
  return request<BatchResult>('/images/batch', {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ ids, category })
  })
}

/** 批量删除图片 */
export async function deleteImageBatch(ids: number[]): Promise<BatchResult> {
  return request<BatchResult>('/images/batch', {
    method: 'DELETE',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ ids })
  })
}

/** 查询单张图片的 EXIF 元数据；exif 为 null 表示无 EXIF 或解析失败 */
export async function getImageExif(id: number): Promise<ExifResponse> {
  return request<ExifResponse>(`/images/${id}/exif`, { method: 'GET' })
}

// ================================================================================
// 分类重命名
// ================================================================================

/** 重命名分类：返回更新后的 Category */
export async function renameCategory(id: number, name: string): Promise<Category> {
  return request<Category>(`/categories/${id}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name })
  })
}

// ================================================================================
// 带进度的上传（XMLHttpRequest 实现）
//  - fetch 不支持上传进度，所以单独用 XHR 封装
//  - 401 时按统一逻辑清 token 跳登录页
//  - onProgress 回调返回 0-100 百分比整数
// ================================================================================
export function uploadImagesWithProgress(
  files: File[],
  category: string,
  onProgress?: (percent: number) => void
): Promise<ImageDto[]> {
  return new Promise((resolve, reject) => {
    const fd = new FormData()
    for (const f of files) fd.append('files', f)
    fd.append('category', category)

    const xhr = new XMLHttpRequest()
    xhr.open('POST', `${BASE}/images`)

    // 鉴权头：和 request() 保持一致
    const auth = authHeaders()
    for (const [k, v] of Object.entries(auth)) {
      xhr.setRequestHeader(k, v)
    }

    xhr.upload.onprogress = (e: ProgressEvent) => {
      if (!e.lengthComputable || !onProgress) return
      const pct = Math.min(100, Math.max(0, Math.round((e.loaded / e.total) * 100)))
      onProgress(pct)
    }

    xhr.onload = () => {
      if (xhr.status === 401) {
        clearAuthLocal()
        if (router.currentRoute.value.path !== '/login') {
          router.replace({
            path: '/login',
            query: { redirect: router.currentRoute.value.fullPath }
          })
        }
        reject(new Error('未登录或登录已过期 (401)'))
        return
      }
      if (xhr.status >= 200 && xhr.status < 300) {
        // 204 / 空响应兜底
        if (xhr.status === 204 || xhr.responseText === '') {
          resolve(undefined as unknown as ImageDto[])
          return
        }
        try {
          resolve(JSON.parse(xhr.responseText) as ImageDto[])
        } catch {
          reject(new Error('上传响应解析失败：' + xhr.responseText.slice(0, 200)))
        }
        return
      }
      // 错误：尝试从 JSON 中提取 error 字段
      let msg = xhr.responseText || xhr.statusText
      try {
        const j = JSON.parse(xhr.responseText)
        if (j.error) msg = j.error
      } catch {
        /* not json, use raw text */
      }
      reject(new Error(`请求失败 (${xhr.status}): ${msg}`))
    }

    xhr.onerror = () => {
      reject(new Error('网络错误：上传请求失败'))
    }
    xhr.onabort = () => {
      reject(new Error('上传已取消'))
    }

    onProgress?.(0)
    xhr.send(fd)
  })
}

// ================================================================================
// 分片上传（大文件 > 5MB 切片逐片上传，小文件走 XHR 单次传）
// 后端接口：POST /api/images/upload-chunk + POST /api/images/merge
// ================================================================================
const CHUNK_SIZE = 5 * 1024 * 1024 // 每片 5MB

/** 上传单个分片到后端 */
function uploadChunk(
  chunk: Blob,
  uploadId: string,
  chunkIndex: number,
  totalChunks: number,
  fileName: string,
  onChunkProgress?: (loaded: number, total: number) => void
): Promise<void> {
  return new Promise((resolve, reject) => {
    const fd = new FormData()
    fd.append('file', chunk, fileName)
    fd.append('uploadId', uploadId)
    fd.append('chunkIndex', String(chunkIndex))
    fd.append('totalChunks', String(totalChunks))
    fd.append('fileName', fileName)

    const xhr = new XMLHttpRequest()
    xhr.open('POST', `${BASE}/images/upload-chunk`)
    const auth = authHeaders()
    for (const [k, v] of Object.entries(auth)) xhr.setRequestHeader(k, v)

    xhr.upload.onprogress = (e) => {
      if (e.lengthComputable && onChunkProgress) onChunkProgress(e.loaded, e.total)
    }

    xhr.onload = () => {
      if (xhr.status === 401) {
        clearAuthLocal()
        if (router.currentRoute.value.path !== '/login') {
          router.replace({ path: '/login', query: { redirect: router.currentRoute.value.fullPath } })
        }
        reject(new Error('未登录或登录已过期 (401)'))
        return
      }
      if (xhr.status >= 200 && xhr.status < 300) { resolve(); return }
      let msg = xhr.responseText || xhr.statusText
      try { const j = JSON.parse(xhr.responseText); if (j.error) msg = j.error } catch { /* */ }
      reject(new Error(`分片上传失败 (${xhr.status}): ${msg}`))
    }
    xhr.onerror = () => reject(new Error('网络错误：分片上传失败'))
    xhr.send(fd)
  })
}

/** 合并分片：所有分片传完后调用，后端合并成完整文件并入库 */
function mergeChunks(uploadId: string, fileName: string, category: string): Promise<ImageDto> {
  return new Promise((resolve, reject) => {
    const fd = new FormData()
    fd.append('uploadId', uploadId)
    fd.append('fileName', fileName)
    fd.append('category', category)

    const xhr = new XMLHttpRequest()
    xhr.open('POST', `${BASE}/images/merge`)
    const auth = authHeaders()
    for (const [k, v] of Object.entries(auth)) xhr.setRequestHeader(k, v)

    xhr.onload = () => {
      if (xhr.status === 401) {
        clearAuthLocal()
        if (router.currentRoute.value.path !== '/login') {
          router.replace({ path: '/login', query: { redirect: router.currentRoute.value.fullPath } })
        }
        reject(new Error('未登录或登录已过期 (401)'))
        return
      }
      if (xhr.status >= 200 && xhr.status < 300) {
        try { resolve(JSON.parse(xhr.responseText) as ImageDto) }
        catch { reject(new Error('合并响应解析失败：' + xhr.responseText.slice(0, 200))) }
        return
      }
      let msg = xhr.responseText || xhr.statusText
      try { const j = JSON.parse(xhr.responseText); if (j.error) msg = j.error } catch { /* */ }
      reject(new Error(`合并失败 (${xhr.status}): ${msg}`))
    }
    xhr.onerror = () => reject(new Error('网络错误：合并请求失败'))
    xhr.send(fd)
  })
}

/**
 * 分片上传单个文件：
 *  - 小文件（<= 5MB）：走 /api/images XHR 单次上传，带进度
 *  - 大文件（> 5MB）：切片 5MB/片，逐片上传到 /api/images/upload-chunk，最后调 /api/images/merge 合并
 *  - onProgress 返回 0-100 整数，表示该文件的进度
 */
export async function uploadFileChunked(
  file: File,
  category: string,
  onProgress?: (percent: number) => void
): Promise<ImageDto> {
  // 小文件：单次 XHR 上传
  if (file.size <= CHUNK_SIZE) {
    return new Promise((resolve, reject) => {
      const fd = new FormData()
      fd.append('files', file)
      fd.append('category', category)

      const xhr = new XMLHttpRequest()
      xhr.open('POST', `${BASE}/images`)
      const auth = authHeaders()
      for (const [k, v] of Object.entries(auth)) xhr.setRequestHeader(k, v)

      xhr.upload.onprogress = (e) => {
        if (!e.lengthComputable || !onProgress) return
        onProgress(Math.min(100, Math.round((e.loaded / e.total) * 100)))
      }

      xhr.onload = () => {
        if (xhr.status === 401) {
          clearAuthLocal()
          if (router.currentRoute.value.path !== '/login') {
            router.replace({ path: '/login', query: { redirect: router.currentRoute.value.fullPath } })
          }
          reject(new Error('未登录或登录已过期 (401)'))
          return
        }
        if (xhr.status >= 200 && xhr.status < 300) {
          if (xhr.status === 204 || xhr.responseText === '') { resolve(undefined as unknown as ImageDto); return }
          try {
            const arr = JSON.parse(xhr.responseText) as ImageDto[]
            resolve(arr[0])
          } catch { reject(new Error('上传响应解析失败：' + xhr.responseText.slice(0, 200))) }
          return
        }
        let msg = xhr.responseText || xhr.statusText
        try { const j = JSON.parse(xhr.responseText); if (j.error) msg = j.error } catch { /* */ }
        reject(new Error(`上传失败 (${xhr.status}): ${msg}`))
      }
      xhr.onerror = () => reject(new Error('网络错误：上传请求失败'))
      onProgress?.(0)
      xhr.send(fd)
    })
  }

  // 大文件：分片上传
  const uploadId = typeof crypto !== 'undefined' && crypto.randomUUID
    ? crypto.randomUUID()
    : `${Date.now()}-${Math.random().toString(36).slice(2)}`
  const totalChunks = Math.ceil(file.size / CHUNK_SIZE)

  for (let i = 0; i < totalChunks; i++) {
    const start = i * CHUNK_SIZE
    const end = Math.min(start + CHUNK_SIZE, file.size)
    const chunk = file.slice(start, end)

    // 分片进度：当前片传输字节 + 已完成片字节
    await uploadChunk(chunk, uploadId, i, totalChunks, file.name, (loaded, total) => {
      const chunkPct = total > 0 ? loaded / total : 1
      const overallPct = (i + chunkPct) / totalChunks
      onProgress?.(Math.min(100, Math.round(overallPct * 100)))
    })

    // 该片传完
    onProgress?.(Math.round(((i + 1) / totalChunks) * 100))
  }

  // 合并
  return mergeChunks(uploadId, file.name, category)
}

/**
 * 批量上传多个文件（逐个处理，支持分片）：
 *  - 每个文件单独上传，大文件自动分片
 *  - onProgress 返回 0-100 整数，表示所有文件的综合进度（按字节加权）
 */
export async function uploadFilesSmart(
  files: File[],
  category: string,
  onProgress?: (percent: number) => void
): Promise<ImageDto[]> {
  const results: ImageDto[] = []
  const totalSize = files.reduce((s, f) => s + f.size, 0) || 1
  let uploadedSize = 0

  for (const file of files) {
    const fileStart = uploadedSize
    const result = await uploadFileChunked(file, category, (pct) => {
      const fileUploaded = (pct / 100) * file.size
      const total = fileStart + fileUploaded
      onProgress?.(Math.min(100, Math.round((total / totalSize) * 100)))
    })
    if (result) results.push(result)
    uploadedSize += file.size
  }

  onProgress?.(100)
  return results
}
