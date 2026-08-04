// AUTO-GENERATED from shared-spec.json
// 版本: 1.11.0  日期: 2026-08-04
// 源: shared-spec.json → api.endpoints（Retrofit 路由常量，与 C# SpecRoutes.g.cs 同源）
// 请勿手工编辑 — 重新生成: dotnet run --project CloudPan.CodeGen

package com.cloudpan.android.data

// Retrofit 路由常量——路径单一事实来源为 shared-spec.json → api.endpoints。
// CloudPanApi.kt 的 @GET/@POST/@DELETE 注解引用本常量，禁止硬编码 "/api/..." 路由字面量；
// 改 spec 端点后重跑 CodeGen 即全链路生效。路径无前导 "/"（Retrofit 相对路径，与 baseUrl 拼接）。
object SpecRoutes
{
    /**
     * 健康检查（GET /api/health）
     */
    const val Health = "api/health"

    /**
     * 服务端版本信息（GET /api/version）
     */
    const val Version = "api/version"

    /**
     * 自签证书 SHA-256 指纹（GET /api/cert-fingerprint）
     */
    const val CertFingerprint = "api/cert-fingerprint"

    /**
     * 设备配对页面（显示 Token，仅本地访问）（GET /pair）
     */
    const val Pair = "pair"

    /**
     * 文件树（支持 sinceVersion/limit/cursor 分页）（GET /api/files/tree）
     */
    const val FilesTree = "api/files/tree"

    /**
     * 上传文件（multipart）（POST /api/files/upload）
     */
    const val FilesUpload = "api/files/upload"

    /**
     * 下载文件（stream）（GET /api/files/download）
     */
    const val FilesDownload = "api/files/download"

    /**
     * 删除文件/文件夹（递归）（POST /api/files/delete）
     */
    const val FilesDelete = "api/files/delete"

    /**
     * 移动/重命名（POST /api/files/move）
     */
    const val FilesMove = "api/files/move"

    /**
     * 创建文件夹（POST /api/files/mkdir）
     */
    const val FilesMkdir = "api/files/mkdir"

    /**
     * 分块上传（POST /api/files/upload/chunk）
     */
    const val FilesUploadChunk = "api/files/upload/chunk"

    /**
     * 查询分块上传进度（GET /api/files/upload/chunk/status）
     */
    const val FilesUploadChunkStatus = "api/files/upload/chunk/status"

    /**
     * 文件名搜索（GET /api/files/search）
     */
    const val FilesSearch = "api/files/search"

    /**
     * 图片缩略图（GET /api/thumbnails）
     */
    const val Thumbnails = "api/thumbnails"

    /**
     * 版本历史列表（GET /api/versions）
     */
    const val Versions = "api/versions"

    /**
     * 回滚到历史版本（POST /api/versions/restore）
     */
    const val VersionsRestore = "api/versions/restore"

    /**
     * 设备列表（GET /api/devices）
     */
    const val Devices = "api/devices"

    /**
     * 创建分享链接（POST /api/shares）
     */
    const val Shares = "api/shares"

    /**
     * 分享链接列表（当前设备创建，不含 token 等敏感字段）（GET /api/shares）
     */
    const val SharesGet = "api/shares"

    /**
     * 撤销分享链接（DELETE /api/shares/{shareId}）
     */
    const val SharesByShareId = "api/shares/{shareId}"

    /**
     * 访问分享页面（无需 Token）（GET /share/{shareId}）
     */
    const val ShareByShareId = "share/{shareId}"

    /**
     * 下载分享文件（?password=xxx）（GET /share/{shareId}/download）
     */
    const val ShareByShareIdDownload = "share/{shareId}/download"

    /**
     * 回收站文件列表（GET /api/trash）
     */
    const val Trash = "api/trash"

    /**
     * 恢复回收站文件（POST /api/trash/restore）
     */
    const val TrashRestore = "api/trash/restore"

    /**
     * 清空回收站（DELETE /api/trash/empty）
     */
    const val TrashEmpty = "api/trash/empty"

    /**
     * 管理面板首页（GET /admin）
     */
    const val Admin = "admin"

    /**
     * 管理面板——文件列表（GET /admin/api/files）
     */
    const val AdminApiFiles = "admin/api/files"

    /**
     * 管理面板——设备列表（GET /admin/api/devices）
     */
    const val AdminApiDevices = "admin/api/devices"

    /**
     * 管理面板——同步日志（GET /admin/api/logs）
     */
    const val AdminApiLogs = "admin/api/logs"

    /**
     * 管理面板——统计信息（GET /admin/api/stats）
     */
    const val AdminApiStats = "admin/api/stats"

    /**
     * WebSocket 实时推送（认证在首条消息）（GET /ws）
     */
    const val WebSocket = "ws"

}
