# 📷 图床 Tuchuang

全栈图床网站，支持图片分类管理、RAW/ARW 预览、大文件分片上传、玻璃态 UI + 夜间模式。

## 技术栈

| 层 | 技术 |
|---|---|
| 后端 | ASP.NET Core 10 Web API + EF Core 10 SQLite |
| 前端 | Vite 6 + Vue 3.5 + TypeScript + Vue Router 4 |
| 图片处理 | Magick.NET-Q16-AnyCPU（RAW/ARW 转 JPEG 预览 + 缩略图 + EXIF） |
| 鉴权 | JWT Bearer Token |
| 部署 | Docker Compose / 一键脚本（Windows + Linux） |

## 功能特性

### 图片管理
- **分类管理**：上传时选择分类，支持动态增删/重命名分类，删除非空分类自动阻止
- **图片搜索**：按文件名模糊搜索，可与分类筛选组合
- **批量操作**：复选框多选 → 批量删除 / 批量改分类
- **图片编辑**：单张改分类、改文件名
- **EXIF 信息**：点击查看相机型号、拍摄时间、光圈、快门等

### 上传
- **单张最大 100MB**，支持 PNG / JPG / GIF / WebP / BMP / RAW / ARW
- **分片上传**：大于 5MB 自动切片 5MB/片逐片上传，网络中断只需重传当前片
- **上传进度条**：百分比 + 渐变动画，按字节加权算多文件总进度
- **拖拽上传**：拖拽文件到上传面板即可

### 浏览体验
- **Lightbox 全屏预览**：缩放（滚轮/双击/按钮）、拖拽平移、键盘导航（←→/Esc）、旋转
- **原图保护**：列表只暴露缩略图（480px JPEG），点击查看原图才返回原文件
- **分页显示**：每页 10/20/40/80/100 张可选，默认 20 张
- **URL 状态持久化**：搜索关键词、分类、页码、每页条数同步到 URL，刷新不丢失，可分享带筛选条件的链接
- **懒加载**：IntersectionObserver + 并发上限 8，只加载可视区图片
- **缩略图生成**：480px 卡片缩略图 + 1600px Lightbox RAW 预览，缓存到磁盘

### 安全
- **JWT 鉴权**：写操作（上传/删除/编辑）需登录，密钥从环境变量读取
- **原图不直接暴露**：`/uploads` 静态目录关闭，必须通过 `/api/images/{publicId}/raw` 按不可预测的随机标识获取（`publicId` 为 StoredName 的 GUID 部分，无法遍历下载）
- **文件名安全化**：`Path.GetFileName` 去除路径分隔符
- **文件类型白名单**：后端校验扩展名

### UI / UX
- **玻璃态设计**：backdrop-filter blur + rgba 半透明 + 渐变背景
- **夜间模式**：CSS 变量切换，localStorage 持久化，首次跟随系统偏好
- **移动端响应式**：768px / 480px 断点，顶栏/网格/Lightbox/搜索框自适应
- **空状态引导**：无图片时显示上传引导
- **全局错误边界**：onErrorCaptured + app.config.errorHandler 兜底

### 性能
- **后端分页**：API 支持 page/pageSize，返回 hasMore
- **RAW 负缓存**：转码失败写 `.fail` 标记，25 分钟内不重试
- **预览缓存启动清理**：每次启动扫描 `preview-cache/`，删除超过 30 天未修改的缓存文件（含 `.fail` 残留），按需重新生成
- **content-visibility: auto**：离屏卡片跳过布局/绘制
- **decoding="async"**：图片解码不阻塞首帧

## 快速开始

### 方式一：一键运行（开发模式）

#### Windows
```bash
# 双击 run.bat，或命令行执行
run.bat
```

#### Linux / macOS
```bash
chmod +x run.sh
./run.sh
```

脚本会自动检查环境、安装依赖、启动后端（5000）和前端（5173）。

### 方式二：Docker 部署

```bash
# 需要 Docker Desktop 或 Docker Engine + docker compose
docker compose up --build -d
```

访问 http://localhost:8080

### 方式三：手动运行

```bash
# 后端
cd backend
dotnet restore
dotnet run --urls "http://0.0.0.0:5000"

# 前端（另一个终端）
cd frontend
npm install
npm run dev
```

访问 http://localhost:5173

## 访问地址

| 页面 | 地址 |
|---|---|
| 浏览页 | http://localhost:5173/ （Docker: :8080） |
| 管理页 | http://localhost:5173/manage |
| 后端 API | http://localhost:5000/api |

**默认管理员账号**：`admin` / `admin123`

> 生产环境务必修改管理员密码和 JWT 密钥。

## 配置

### 环境变量

| 变量 | 说明 | 默认值 |
|---|---|---|
| `JWT_SIGNING_KEY` | JWT 签名密钥（≥32 字符） | appsettings.json 中的默认值 |
| `ADMIN_USER` | 管理员用户名 | admin |
| `ADMIN_PASSWORD` | 管理员密码 | admin123 |

### Docker 环境变量示例

```bash
JWT_SIGNING_KEY=your-random-secret-key-at-least-32-chars-long
ADMIN_PASSWORD=YourStrongPassword123
docker compose up --build -d
```

## 项目结构

```
tuchuang/
├── backend/
│   ├── Controllers/
│   │   ├── ImagesController.cs      # 图片 CRUD + 分页 + 搜索 + 分片上传 + EXIF
│   │   ├── CategoriesController.cs   # 分类增删改 + 重命名 + 删除保护
│   │   └── AccountController.cs      # 登录 + JWT 签发
│   ├── Models/                       # EF Core 实体
│   ├── Program.cs                    # 中间件 + 鉴权 + CORS + 请求日志
│   ├── appsettings.json              # 配置（JWT 密钥 + 管理员账号）
│   └── Dockerfile
├── frontend/
│   ├── src/
│   │   ├── api/index.ts              # API 封装（含分片上传）
│   │   ├── components/
│   │   │   ├── ImageGrid.vue         # 图片网格 + Lightbox + 批量操作 + EXIF 面板
│   │   │   ├── UploadPanel.vue       # 上传面板 + 进度条 + 分片上传
│   │   │   ├── CategoryManager.vue   # 分类管理 + 重命名
│   │   │   └── Pagination.vue        # 分页组件
│   │   ├── views/
│   │   │   ├── GalleryView.vue       # 浏览页
│   │   │   ├── ManageView.vue        # 管理页
│   │   │   └── LoginView.vue         # 登录页
│   │   ├── composables/             # useAuth / useCategories / useUrlState
│   │   ├── style.css                 # 全局样式 + 夜间模式 + 移动端响应式
│   │   └── main.ts
│   ├── nginx.conf                    # Docker nginx 配置
│   └── Dockerfile
├── tests/
│   └── TuchuangApi.Tests/            # xUnit 集成测试（23 个）
├── docker-compose.yml
├── run.bat / run.sh                  # 一键运行脚本
└── stop.bat                          # 停止服务（Windows）
```

## API 接口

| 方法 | 路径 | 鉴权 | 说明 |
|---|---|---|---|
| GET | `/api/images` | - | 图片列表（分页 + 搜索 + 分类筛选） |
| POST | `/api/images` | ✅ | 上传图片（FormData） |
| POST | `/api/images/upload-chunk` | ✅ | 分片上传：接收单个分片 |
| POST | `/api/images/merge` | ✅ | 分片上传：合并所有分片 |
| GET | `/api/images/{id}/preview` | - | 获取缩略图（480px / 1600px） |
| GET | `/api/images/{publicId}/raw` | - | 获取原图（点击查看原图时调用，publicId 为随机标识） |
| GET | `/api/images/{id}/exif` | - | 获取 EXIF 信息 |
| PATCH | `/api/images/{id}` | ✅ | 修改图片分类 / 文件名 |
| PATCH | `/api/images/batch` | ✅ | 批量改分类 |
| DELETE | `/api/images/batch` | ✅ | 批量删除 |
| DELETE | `/api/images/{id}` | ✅ | 删除单张 |
| GET | `/api/categories` | - | 分类列表 |
| POST | `/api/categories` | ✅ | 新建分类 |
| PATCH | `/api/categories/{id}` | ✅ | 重命名分类（同步更新图片） |
| DELETE | `/api/categories/{id}` | ✅ | 删除分类（非空阻止） |
| POST | `/api/account/login` | - | 登录获取 JWT |
| GET | `/api/account/me` | ✅ | 验证 token |

## 测试

```bash
cd tests/TuchuangApi.Tests
dotnet test
```

覆盖：分页参数、搜索过滤、分类筛选、上传文件类型校验、分类删除保护。

## 数据持久化

### 开发模式
- SQLite：`backend/data/tuchuang.db`
- 上传文件：`backend/uploads/`
- 缩略图缓存：`backend/uploads/preview-cache/`

### Docker 模式
- `tuchuang-db` 卷 → `/app/data/tuchuang.db`
- `tuchuang-uploads` 卷 → `/app/uploads/`

容器重建后数据不丢失。

## 浏览器支持

- Chrome / Edge / Firefox / Safari 最新版
- 移动端响应式（iPhone SE 375px 起）
