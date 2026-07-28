// AUTO-GENERATED from shared-spec.json v1.3.0 — DO NOT EDIT
// 源: shared-spec.json → entities → apiMapping
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
enum class FileState { Synced, Modified, Deleting, CloudOnly, Conflict }
enum class QueuePriority { Normal, High }
