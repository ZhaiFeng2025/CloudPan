// AUTO-GENERATED from shared-spec.json
// 版本: 1.6.0  日期: 2026-08-03
// 源: shared-spec.json → enums + entities.apiMapping + api.responses + api.errorResponse
// 请勿手工编辑 — 重新生成: dotnet run --project CloudPan.CodeGen

package com.cloudpan.android.data

import com.google.gson.annotations.SerializedName

// ===================== 枚举（显式数值对齐 shared-spec.json enums） =====================

// 文件同步状态。服务端使用 0-3,7；客户端瞬态 4-5 仅客户端本地使用。值 6 保留（原设计为 Locked，P3 文件锁定功能启用后占用）
enum class FileState(val value: Int)
{
    Synced(0),
    Modified(1),
    Deleting(2),
    CloudOnly(3),
    Downloading(4), // 仅客户端本地瞬态
    Uploading(5), // 仅客户端本地瞬态
    Conflict(7)
}

// 文件类型
enum class FileType(val value: Int)
{
    File(0),
    Directory(1)
}

// 同步操作类型
enum class SyncOperation(val value: Int)
{
    Upload(0),
    Download(1),
    Delete(2),
    Rename(3),
    Restore(4)
}

// 传输队列优先级。小文件优先传输以提升感知速度
enum class QueuePriority(val value: Int)
{
    Normal(0),
    // 文件字节数 < queuePriorityThreshold（独立于分块/续传阈值）
    High(1)
}

// 同步日志结果
enum class LogResult(val value: Int)
{
    Success(0),
    Conflict(1),
    Error(2)
}

// Android 照片备份状态
enum class BackupStatus(val value: Int)
{
    Pending(0),
    Uploading(1),
    Done(2),
    Failed(3)
}

// 字符串常量：WebSocketEvent 各事件名。
object WebSocketEvent
{
    const val Auth = "auth"
    const val AuthOk = "auth_ok"
    const val AuthError = "auth_error"
    const val Ping = "ping"
    const val Pong = "pong"
    const val FileChanged = "file_changed"
    const val FileDeleted = "file_deleted"
    const val FileRenamed = "file_renamed"
    const val Conflict = "conflict"
}

// HTTP 错误码元数据。
data class ErrorCode(
    val httpStatus: Int,
    val code: String,
    val retry: Boolean
)

// HttpErrorCode——所有错误响应必须引用此枚举，禁止手写错误码字符串。
object HttpErrorCode
{
    // HTTP 400 — BAD_REQUEST
    val BAD_REQUEST = ErrorCode(400, "BAD_REQUEST", false)
    // HTTP 400 — INVALID_DEVICE_ID
    val INVALID_DEVICE_ID = ErrorCode(400, "INVALID_DEVICE_ID", false)
    // HTTP 401 — UNAUTHORIZED
    val UNAUTHORIZED = ErrorCode(401, "UNAUTHORIZED", false)
    // HTTP 404 — NOT_FOUND
    val NOT_FOUND = ErrorCode(404, "NOT_FOUND", false)
    // HTTP 409 — CONFLICT（可重试）
    val CONFLICT = ErrorCode(409, "CONFLICT", true)
    // HTTP 413 — PAYLOAD_TOO_LARGE（可重试）
    val PAYLOAD_TOO_LARGE = ErrorCode(413, "PAYLOAD_TOO_LARGE", true)
    // HTTP 429 — RATE_LIMITED（可重试）
    val RATE_LIMITED = ErrorCode(429, "RATE_LIMITED", true)
    // HTTP 500 — INTERNAL_ERROR（可重试）
    val INTERNAL_ERROR = ErrorCode(500, "INTERNAL_ERROR", true)
    // HTTP 503 — SERVICE_UNAVAILABLE（可重试）
    val SERVICE_UNAVAILABLE = ErrorCode(503, "SERVICE_UNAVAILABLE", true)
}

// ===================== 实体 DTO（entities → apiMapping） =====================

// 服务端文件索引。每行一个文件或目录。Path 为主键。
data class FileEntryDto(
    @SerializedName("path") val path: String,
    @SerializedName("type") val type: Int,
    @SerializedName("hash") val hash: String?,
    @SerializedName("size") val size: Long,
    @SerializedName("version") val version: Int,
    @SerializedName("lastModified") val lastModified: String,
    @SerializedName("state") val state: Int
)

// 文件版本历史。保留最近 N 个版本（默认 5）。
data class VersionRecordDto(
    @SerializedName("version") val version: Int,
    @SerializedName("hash") val hash: String,
    @SerializedName("size") val size: Long,
    @SerializedName("timestamp") val timestamp: String,
    @SerializedName("deviceId") val deviceId: String,
    @SerializedName("restoredFromVersion") val restoredFromVersion: Int?
)

// 设备注册。首次连接时创建。
data class DeviceDto(
    @SerializedName("deviceId") val deviceId: String,
    @SerializedName("name") val name: String,
    @SerializedName("person") val person: String?,
    @SerializedName("lastSeen") val lastSeen: String,
    @SerializedName("online") val online: Int
)

// 文件分享链接。
data class ShareDto(
    @SerializedName("shareId") val shareId: String,
    @SerializedName("filePath") val filePath: String,
    @SerializedName("expiresAt") val expiresAt: String?,
    @SerializedName("maxDownloads") val maxDownloads: Int?,
    @SerializedName("usedDownloads") val usedDownloads: Int,
    @SerializedName("createdAt") val createdAt: String
)

// 同步操作审计日志。P2 阶段新增。
data class SyncLogDto(
    @SerializedName("id") val id: Int,
    @SerializedName("filePath") val filePath: String,
    @SerializedName("operation") val operation: Int,
    @SerializedName("deviceId") val deviceId: String,
    @SerializedName("result") val result: Int,
    @SerializedName("details") val details: String?,
    @SerializedName("createdAt") val createdAt: String
)

// POST /api/files/delete 请求体（非持久化 API 请求 DTO，仅 apiMapping 驱动生成，不入库）。BaseVersion 用于乐观并发校验，0 表示不校验。
data class DeleteRequestDto(
    @SerializedName("path") val path: String,
    @SerializedName("baseVersion") val baseVersion: Int
)

// POST /api/files/move 请求体（非持久化 API 请求 DTO，仅 apiMapping 驱动生成，不入库）。BaseVersion 用于乐观并发校验，0 表示不校验。
data class MoveRequestDto(
    @SerializedName("oldPath") val oldPath: String,
    @SerializedName("newPath") val newPath: String,
    @SerializedName("baseVersion") val baseVersion: Int
)

// POST /api/files/mkdir 请求体（非持久化 API 请求 DTO，仅 apiMapping 驱动生成，不入库）。
data class MkdirRequestDto(
    @SerializedName("path") val path: String
)

// POST /api/shares 请求体（非持久化 API 请求 DTO，仅 apiMapping 驱动生成，不入库）。expiresAt 传 ISO 8601 UTC，null 表示永不过期。
data class CreateShareRequestDto(
    @SerializedName("filePath") val filePath: String,
    @SerializedName("password") val password: String?,
    @SerializedName("expiresAt") val expiresAt: String?,
    @SerializedName("maxDownloads") val maxDownloads: Int?
)

// POST /api/trash/restore 请求体（非持久化 API 请求 DTO，仅 apiMapping 驱动生成，不入库）。metaFileName 为回收站元数据文件名。
data class RestoreTrashRequestDto(
    @SerializedName("metaFileName") val metaFileName: String
)

// POST /api/versions/restore 请求体（非持久化 API 请求 DTO，仅 apiMapping 驱动生成，不入库）。将文件恢复至指定历史版本。
data class RestoreRequestDto(
    @SerializedName("filePath") val filePath: String,
    @SerializedName("version") val version: Int
)

// ===================== API 响应 DTO（api.responses） =====================

// GET /api/files/tree 响应包装
data class FileTreeResponse(
    @SerializedName("data") val data: List<FileEntryDto>,
    @SerializedName("nextCursor") val nextCursor: String?,
    @SerializedName("hasMore") val hasMore: Boolean,
    @SerializedName("maxVersion") val maxVersion: Int
)

// POST /api/files/upload 响应
data class UploadResponse(
    @SerializedName("data") val data: UploadData
)

// 上传响应中的 data 字段
data class UploadData(
    @SerializedName("path") val path: String,
    @SerializedName("version") val version: Int,
    @SerializedName("hash") val hash: String,
    @SerializedName("size") val size: Long,
    @SerializedName("conflictResolved") val conflictResolved: Boolean
)

// POST /api/files/upload/chunk 响应（data 为进度或完成信息）
data class ChunkUploadResponse(
    @SerializedName("data") val data: ChunkUploadData
)

// 分块上传响应 data——进度未完成含 chunkIndex/receivedCount/totalChunks/isComplete，完成含 status='complete'/version/hash/size
data class ChunkUploadData(
    @SerializedName("path") val path: String,
    @SerializedName("chunkIndex") val chunkIndex: Int,
    @SerializedName("receivedCount") val receivedCount: Int,
    @SerializedName("totalChunks") val totalChunks: Int,
    @SerializedName("isComplete") val isComplete: Boolean,
    @SerializedName("version") val version: Int,
    @SerializedName("hash") val hash: String?,
    @SerializedName("size") val size: Long,
    @SerializedName("status") val status: String?
)

// GET /api/files/upload/chunk/status 响应包装
data class ChunkStatusResponse(
    @SerializedName("data") val data: ChunkStatusData
)

// 分块上传进度查询 data
data class ChunkStatusData(
    @SerializedName("filePath") val filePath: String?,
    @SerializedName("path") val path: String?,
    @SerializedName("receivedChunks") val receivedChunks: List<Int>,
    @SerializedName("totalChunks") val totalChunks: Int,
    @SerializedName("isComplete") val isComplete: Boolean,
    @SerializedName("version") val version: Int,
    @SerializedName("deviceId") val deviceId: String?,
    @SerializedName("createdAt") val createdAt: String?
)

// 回收站条目（列表展示用）
data class TrashItem(
    @SerializedName("originalPath") val originalPath: String,
    @SerializedName("trashFileName") val trashFileName: String,
    @SerializedName("fileSize") val fileSize: Long,
    @SerializedName("isDirectory") val isDirectory: Boolean,
    @SerializedName("deletedAt") val deletedAt: String,
    @SerializedName("ageDays") val ageDays: Int
)

// GET /api/trash 响应包装
data class TrashListResponse(
    @SerializedName("data") val data: List<TrashItem>
)

// POST /api/shares 响应包装
data class ShareCreateResponse(
    @SerializedName("data") val data: ShareCreateData
)

// 创建分享链接响应 data
data class ShareCreateData(
    @SerializedName("shareId") val shareId: String,
    @SerializedName("url") val url: String,
    @SerializedName("expiresAt") val expiresAt: String?,
    @SerializedName("maxDownloads") val maxDownloads: Int?
)

// DELETE /api/shares/{shareId} 响应包装
data class ShareRevokeResponse(
    @SerializedName("data") val data: ShareRevokeData
)

// 撤销分享链接响应 data
data class ShareRevokeData(
    @SerializedName("revoked") val revoked: String
)

// 历史版本记录（列表展示用）
data class VersionItem(
    @SerializedName("version") val version: Int,
    @SerializedName("hash") val hash: String,
    @SerializedName("size") val size: Long,
    @SerializedName("timestamp") val timestamp: String,
    @SerializedName("deviceId") val deviceId: String,
    @SerializedName("restoredFromVersion") val restoredFromVersion: Int?
)

// GET /api/versions 响应包装
data class VersionListResponse(
    @SerializedName("data") val data: List<VersionItem>
)

// POST /api/versions/restore 响应包装
data class VersionRestoreResponse(
    @SerializedName("data") val data: VersionRestoreData
)

// 版本回滚响应 data
data class VersionRestoreData(
    @SerializedName("path") val path: String,
    @SerializedName("version") val version: Int,
    @SerializedName("hash") val hash: String,
    @SerializedName("size") val size: Long,
    @SerializedName("restoredFromVersion") val restoredFromVersion: Int?
)

// 管理面板文件列表条目（仅 localhost 访问）
data class AdminFileItem(
    @SerializedName("path") val path: String,
    @SerializedName("type") val type: Int,
    @SerializedName("currentHash") val currentHash: String?,
    @SerializedName("currentSize") val currentSize: Long,
    @SerializedName("version") val version: Int,
    @SerializedName("state") val state: Int,
    @SerializedName("lastModified") val lastModified: String
)

// GET /admin/api/files 响应包装
data class AdminFileResponse(
    @SerializedName("data") val data: List<AdminFileItem>
)

// 管理面板设备列表条目（仅 localhost 访问）
data class AdminDeviceItem(
    @SerializedName("id") val id: String,
    @SerializedName("name") val name: String,
    @SerializedName("person") val person: String?,
    @SerializedName("lastSeen") val lastSeen: String,
    @SerializedName("online") val online: Int,
    @SerializedName("registeredAt") val registeredAt: String
)

// GET /admin/api/devices 响应包装
data class AdminDeviceResponse(
    @SerializedName("data") val data: List<AdminDeviceItem>
)

// 管理面板同步日志条目（仅 localhost 访问）
data class AdminLogItem(
    @SerializedName("id") val id: Long,
    @SerializedName("filePath") val filePath: String,
    @SerializedName("operation") val operation: Int,
    @SerializedName("deviceId") val deviceId: String,
    @SerializedName("result") val result: Int,
    @SerializedName("details") val details: String?,
    @SerializedName("createdAt") val createdAt: String
)

// GET /admin/api/logs 响应包装
data class AdminLogResponse(
    @SerializedName("data") val data: List<AdminLogItem>
)

// GET /admin/api/stats 响应（扁平对象，无 data 包装）
data class AdminStatsResponse(
    @SerializedName("fileCount") val fileCount: Int,
    @SerializedName("deviceCount") val deviceCount: Int,
    @SerializedName("onlineDeviceCount") val onlineDeviceCount: Int,
    @SerializedName("logCount") val logCount: Int
)

// GET /api/devices 列表条目
data class DeviceItem(
    @SerializedName("deviceId") val deviceId: String,
    @SerializedName("name") val name: String,
    @SerializedName("person") val person: String?,
    @SerializedName("lastSeen") val lastSeen: String,
    @SerializedName("online") val online: Int,
    @SerializedName("registeredAt") val registeredAt: String
)

// GET /api/devices 响应包装
data class DevicesResponse(
    @SerializedName("data") val data: List<DeviceItem>
)

// GET /api/health 响应（扁平对象，无 data 包装）
data class HealthResponse(
    @SerializedName("status") val status: String,
    @SerializedName("version") val version: String,
    @SerializedName("maxVersion") val maxVersion: Int,
    @SerializedName("syncRoot") val syncRoot: String,
    @SerializedName("disk") val disk: String,
    @SerializedName("memoryMb") val memoryMb: Long,
    @SerializedName("memoryStatus") val memoryStatus: String,
    @SerializedName("dbIntegrity") val dbIntegrity: String,
    @SerializedName("uptime") val uptime: String,
    @SerializedName("timestamp") val timestamp: String
)

// GET /api/version 响应（扁平对象，无 data 包装）
data class VersionResponse(
    @SerializedName("version") val version: String,
    @SerializedName("minClientVersion") val minClientVersion: String,
    @SerializedName("releaseNotes") val releaseNotes: String,
    @SerializedName("downloadUrl") val downloadUrl: String
)

// GET /api/cert-fingerprint 响应（扁平对象，无 data 包装）
data class CertFingerprintResponse(
    @SerializedName("fingerprint") val fingerprint: String
)

// POST /api/files/delete 响应包装
data class DeleteResponse(
    @SerializedName("data") val data: DeleteData
)

// 删除文件响应 data
data class DeleteData(
    @SerializedName("path") val path: String,
    @SerializedName("deletedVersion") val deletedVersion: Int?
)

// POST /api/files/move 响应包装
data class MoveResponse(
    @SerializedName("data") val data: MoveData
)

// 移动/重命名响应 data
data class MoveData(
    @SerializedName("oldPath") val oldPath: String,
    @SerializedName("newPath") val newPath: String,
    @SerializedName("version") val version: Int?
)

// POST /api/files/mkdir 响应包装
data class MkdirResponse(
    @SerializedName("data") val data: MkdirData
)

// 创建文件夹响应 data
data class MkdirData(
    @SerializedName("path") val path: String?
)

// GET /api/files/search 响应包装
data class SearchResponse(
    @SerializedName("data") val data: List<FileEntryDto>
)

// POST /api/trash/restore 响应包装
data class TrashRestoreResponse(
    @SerializedName("data") val data: TrashRestoreData
)

// 恢复回收站条目响应 data
data class TrashRestoreData(
    @SerializedName("restored") val restored: String?
)

// DELETE /api/trash/empty 响应包装
data class TrashEmptyResponse(
    @SerializedName("data") val data: String
)

// ===================== 统一错误响应体（api.errorResponse.shape） =====================

// 统一 API 错误响应体——所有错误响应使用此格式。
data class ErrorBody(
    @SerializedName("error") val error: ErrorInfo
)

data class ErrorInfo(
    @SerializedName("code") val code: String,
    @SerializedName("message") val message: String,
    @SerializedName("friendlyMessage") val friendlyMessage: String,
    @SerializedName("detail") val detail: String?
)

