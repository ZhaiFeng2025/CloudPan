// Hand-maintained（v1.0 Android 原型，非生成产物）
// 说明: CloudPan.CodeGen 当前无 Kotlin 生成器，本文件由手工维护，不声称由标准管线生成。
// 对齐基线: shared-spec.json v1.1.0（与 CloudPan.Contract/Generated/*.g.cs 生成的 C# 契约一致）。
package com.cloudpan.android.data

import com.google.gson.annotations.SerializedName

// ---- DTO（Kotlin data class，与 API JSON 字段对齐） ----

data class FileEntryDto(
    @SerializedName("path") val path: String,
    @SerializedName("type") val type: Int,
    @SerializedName("hash") val hash: String?,
    @SerializedName("size") val size: Long,
    @SerializedName("version") val version: Int,
    @SerializedName("lastModified") val lastModified: String,
    @SerializedName("state") val state: Int
)

data class DeviceDto(
    @SerializedName("deviceId") val id: String,
    @SerializedName("name") val name: String,
    @SerializedName("person") val person: String?,
    @SerializedName("lastSeen") val lastSeen: String,
    @SerializedName("online") val online: Int
)

data class FileTreeResponse(
    @SerializedName("data") val data: List<FileEntryDto>,
    @SerializedName("nextCursor") val nextCursor: String?,
    @SerializedName("hasMore") val hasMore: Boolean,
    @SerializedName("maxVersion") val maxVersion: Int
)

data class UploadResponse(
    @SerializedName("data") val data: UploadData
)

data class UploadData(
    @SerializedName("path") val path: String,
    @SerializedName("version") val version: Int,
    @SerializedName("hash") val hash: String,
    @SerializedName("size") val size: Long,
    @SerializedName("conflictResolved") val conflictResolved: Boolean
)

data class ShareResponse(
    @SerializedName("data") val data: ShareData
)

data class ShareData(
    @SerializedName("shareId") val shareId: String,
    @SerializedName("url") val url: String,
    @SerializedName("expiresAt") val expiresAt: String?,
    @SerializedName("maxDownloads") val maxDownloads: Int?
)

data class ErrorBody(
    @SerializedName("error") val error: ErrorInfo
)

data class ErrorInfo(
    @SerializedName("code") val code: String,
    @SerializedName("message") val message: String
)

// ---- 枚举 ----

enum class FileType { File, Directory }
enum class SyncOperation { Upload, Download, Delete, Rename, Restore }
/**
 * 文件同步状态。显式数值对齐 C# 契约 CloudPan.Contract/Generated/Enums.g.cs（源 shared-spec v1.1.0）：
 * Synced=0 / Modified=1 / Deleting=2 / CloudOnly=3 / Downloading=4 / Uploading=5；值 6 保留；Conflict=7。
 * 服务端持久化与传输使用 0-3,7；Downloading/Uploading 仅客户端本地瞬态，不落盘/不传输。
 * 防再漂移：数值必须以 C# Enums.g.cs 为准，禁止依赖 Kotlin 序数（原序数 4 曾被 Conflict 占用）。
 */
enum class FileState(val value: Int) {
    Synced(0),
    Modified(1),
    Deleting(2),
    CloudOnly(3),
    Downloading(4),
    Uploading(5),
    Conflict(7)
}
enum class QueuePriority { Normal, High }
