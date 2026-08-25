export interface ImageDto {
  id: number
  fileName: string
  fileSize: number
  contentType: string
  category: string
  uploadedAt: string
}

export interface Category {
  id: number
  name: string
}

export const ALL_CATEGORY = '全部'

export interface LoginRequest {
  username: string
  password: string
}

export interface LoginResponse {
  token: string
  username: string
  expiresIn: number
}

export interface MeResponse {
  isAuthenticated: boolean
  username: string
}

export interface PagedResponse<T> {
  items: T[]
  total: number
  hasMore: boolean
  page: number
  pageSize: number
}

/** 图片元数据更新请求体：两个字段都可选，传哪个改哪个 */
export interface ImageUpdateRequest {
  category?: string
  fileName?: string
}

/** EXIF 查询响应：exif 为 null 表示无 EXIF 或解析失败，note 是补充说明 */
export interface ExifResponse {
  exif: Record<string, string> | null
  note?: string
}

/** 批量操作结果：删除返回 deleted，更新返回 updated */
export interface BatchResult {
  deleted?: number
  updated?: number
}
