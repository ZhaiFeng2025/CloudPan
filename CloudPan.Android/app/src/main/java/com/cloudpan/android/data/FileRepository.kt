package com.cloudpan.android.data

import android.util.Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaTypeOrNull
import okhttp3.MultipartBody
import okhttp3.RequestBody.Companion.asRequestBody
import okhttp3.ResponseBody
import java.io.File
import java.io.FileOutputStream
import java.time.Instant

/**
 * 文件树单页大小。服务端按 Path 排序 + cursor 游标翻页（T-059），
 * 客户端配合 FileTreeResponse.nextCursor/hasMore 增量加载。
 */
private const val FILE_TREE_PAGE_SIZE = 200

/** 版本解析用整目录拉取上限（服务端 Take 上限为 10000，见 FilesController.GetTree）。 */
private const val VERSION_LOOKUP_LIMIT = 10000

/**
 * 服务端 409 冲突异常——目标文件已被其他设备修改（客户端 baseVersion 过期）。
 * 删除/上传携带 baseVersion 后服务端返回 409（T-089），由 UI 给出白话提示 + 覆盖/跳过选项，不静默。
 */
class FileConflictException(message: String) : Exception(message)

/**
 * 文件操作仓库——封装 API 调用和错误处理。
 */
class FileRepository(private val settings: SettingsStore) {
    private var _api: CloudPanApi? = null

    private fun api(): CloudPanApi {
        if (_api == null || needsRefresh) {
            _api = ApiClientFactory.create(
                settings.serverUrl,
                settings.token,
                settings.deviceId
            )
        }
        return _api!!
    }

    private var needsRefresh = true

    fun invalidateClient() {
        needsRefresh = true
        _api = null
    }

    suspend fun getFileTree(sinceVersion: Int = 0, cursor: String? = null, subPath: String? = null): Result<FileTreeResponse> {
        // Retrofit 的 @Query 支持 null 参数自动忽略；HTTP 非 2xx 时抛异常，由 safeCall 转 Result.failure
        return safeCall {
            val r = api().getFileTree(sinceVersion, FILE_TREE_PAGE_SIZE, cursor)
            if (!r.isSuccessful) throw Exception("获取文件列表失败: ${r.code()} ${r.message()}")
            r.body()!!
        }
        // 注：CloudPanApi.getFileTree 暂不支持 subPath，需要直接拼接 URL
    }

    /** 指定文件夹子树的分页请求（T-059：cursor 增量翻页，nextCursor 由调用方拼接）。 */
    suspend fun getFileTreeInFolder(folderPath: String, cursor: String? = null): Result<FileTreeResponse> {
        return safeCall {
            val r = api().getFileTreeInFolder(folderPath, FILE_TREE_PAGE_SIZE, cursor)
            if (!r.isSuccessful) throw Exception("获取文件夹列表失败: ${r.code()} ${r.message()}")
            r.body()!!
        }
    }

    suspend fun downloadFile(remotePath: String, localDir: File): Result<File> {
        return withContext(Dispatchers.IO) {
            val fileName = remotePath.substringAfterLast('/')
            val tmpFile = File(localDir, ".${fileName}.tmp")
            try {
                val response = api().downloadFile(remotePath)
                if (!response.isSuccessful) {
                    return@withContext Result.failure(
                        Exception("下载失败: ${response.code()} ${response.message()}")
                    )
                }
                val body: ResponseBody = response.body()
                    ?: return@withContext Result.failure(Exception("空响应体"))

                FileOutputStream(tmpFile).use { out ->
                    body.byteStream().use { input -> input.copyTo(out) }
                }
                val localFile = File(localDir, fileName)
                tmpFile.renameTo(localFile)
                Result.success(localFile)
            } catch (e: Exception) {
                try { tmpFile.delete() } catch (_: Exception) {}
                if (e is kotlinx.coroutines.CancellationException) throw e
                Result.failure(e)
            }
        }
    }

    suspend fun downloadFileWithProgress(
        remotePath: String,
        localDir: File,
        onProgress: (downloaded: Long, total: Long) -> Unit = { _, _ -> }
    ): Result<File> {
        return withContext(Dispatchers.IO) {
            val fileName = remotePath.substringAfterLast('/')
            val tmpFile = File(localDir, ".${fileName}.tmp")
            try {
                val response = api().downloadFile(remotePath)
                if (!response.isSuccessful) {
                    return@withContext Result.failure(
                        Exception("下载失败: ${response.code()} ${response.message()}")
                    )
                }
                val body = response.body()
                    ?: return@withContext Result.failure(Exception("空响应体"))

                val totalBytes = body.contentLength()
                FileOutputStream(tmpFile).use { out ->
                    body.byteStream().use { input ->
                        val buffer = ByteArray(8192)
                        var downloaded = 0L
                        var bytesRead: Int
                        while (input.read(buffer).also { bytesRead = it } != -1) {
                            // 下载循环内协程取消检查（等价 kotlinx.coroutines.ensureActive()）
                            if (kotlin.coroutines.coroutineContext[kotlinx.coroutines.Job]?.isActive != true) {
                                throw kotlinx.coroutines.CancellationException()
                            }
                            out.write(buffer, 0, bytesRead)
                            downloaded += bytesRead
                            onProgress(downloaded, totalBytes)
                        }
                    }
                }
                val localFile = File(localDir, fileName)
                tmpFile.renameTo(localFile)
                Result.success(localFile)
            } catch (e: Exception) {
                try { tmpFile.delete() } catch (_: Exception) {}
                if (e is kotlinx.coroutines.CancellationException) throw e
                Result.failure(e)
            }
        }
    }

    suspend fun createFolder(path: String): Result<Unit> {
        return safeCall {
            api().createFolder(MkdirRequestDto(path))
            Unit
        }
    }

    /**
     * 删除文件/文件夹（T-050：软删进回收站，可恢复）。
     * baseVersion 为乐观并发基准版本（T-089：取自已拉取文件列表的 fileEntry.version）；
     * 服务端当前版本高于 baseVersion 时返回 409（FileConflictException），由 UI 决定强制删除或跳过，
     * 不再恒传 0（0 表示不校验，静默覆盖其他设备改动）。
     * 调 POST /api/files/delete（服务端移入回收站 + 墓碑），成功后查回收站列表返回对应条目供撤销；
     * 无匹配条目返回 null（删除成功但不可撤销）。禁止物理删除——回收站是唯一可恢复路径。
     */
    suspend fun deleteFile(path: String, baseVersion: Int): Result<TrashItem?> {
        return safeCall {
            val r = api().deleteFile(DeleteRequestDto(path, baseVersion))
            if (r.code() == 409) {
                throw FileConflictException("文件已被其他设备修改")
            }
            if (!r.isSuccessful) {
                throw Exception("删除失败: ${r.code()} ${r.message()}")
            }
            // 查回收站刚删条目（供 5 秒内撤销），失败不影响删除结果
            try {
                val trash = api().getTrash()
                if (trash.isSuccessful) {
                    trash.body()?.data?.firstOrNull { it.originalPath == path }
                } else null
            } catch (_: Exception) {
                null
            }
        }
    }

    /**
     * 查询目标路径当前版本（上传 baseVersion 用，T-089）。
     * 拉取目标所在文件夹子树查找该路径；文件不存在或查询失败返回 0（baseVersion=0 表示不校验）。
     */
    suspend fun resolveBaseVersion(path: String): Int {
        val folder = path.substringBeforeLast('/').ifEmpty { "/" }
        return try {
            val r = api().getFileTreeInFolder(folder, VERSION_LOOKUP_LIMIT, null)
            if (r.isSuccessful) {
                r.body()?.data?.firstOrNull { it.path == path }?.version ?: 0
            } else 0
        } catch (_: Exception) {
            0
        }
    }

    /** 回收站文件列表（GET /api/trash）。 */
    suspend fun getTrash(): Result<List<TrashItem>> {
        return safeCall {
            val r = api().getTrash()
            if (!r.isSuccessful) {
                throw Exception("获取回收站失败: ${r.code()} ${r.message()}")
            }
            r.body()?.data ?: emptyList()
        }
    }

    /**
     * 恢复回收站条目（POST /api/trash/restore）。
     * metaFileName = 条目 TrashFileName + ".json"（对齐服务端 MoveToTrashAsync 写盘命名）。
     * 目标位置已有同名文件时服务端返回 409（T-078 收敛）→ 抛 FileConflictException，
     * 由 UI 给具体原因与下一步（改名/删除同名文件后重试），不再泛化『恢复失败，请稍后重试』（T-094/F-136）。
     */
    suspend fun restoreTrash(trashFileName: String): Result<Unit> {
        return safeCall {
            val r = api().restoreTrash(RestoreTrashRequestDto("$trashFileName.json"))
            if (r.code() == 409) {
                throw FileConflictException("恢复失败：目标位置已有同名文件")
            }
            if (!r.isSuccessful) {
                throw Exception("恢复失败: ${r.code()} ${r.message()}")
            }
            Unit
        }
    }

    /** 清空回收站（DELETE /api/trash/empty，物理删除，不可恢复）。 */
    suspend fun emptyTrash(): Result<Unit> {
        return safeCall {
            val r = api().emptyTrash()
            if (!r.isSuccessful) {
                throw Exception("清空失败: ${r.code()} ${r.message()}")
            }
            Unit
        }
    }

    suspend fun createShare(filePath: String, password: String? = null): Result<ShareCreateResponse> {
        return safeCall {
            val r = api().createShare(CreateShareRequestDto(filePath, password, null, null))
            if (!r.isSuccessful) throw Exception("创建分享失败: ${r.code()} ${r.message()}")
            r.body()!!
        }
    }

    /**
     * 上传文件。
     * baseVersion 为乐观并发基准版本（T-089：调用方经 resolveBaseVersion 先查目标文件当前版本，
     * 或复用列表 fileEntry.version），不再恒传 0；服务端当前版本更高时返回 409（FileConflictException），
     * 由 UI 给出覆盖/跳过选项，不静默覆盖其他设备改动。
     */
    suspend fun uploadFile(localFile: File, remotePath: String, baseVersion: Int): Result<UploadResponse> {
        return withContext(Dispatchers.IO) {
            try {
                val mimeType = "application/octet-stream".toMediaTypeOrNull()!!
                val requestBody = localFile.asRequestBody(mimeType)
                val filePart = MultipartBody.Part.createFormData(
                    "file", localFile.name, requestBody
                )
                val pathPart = remotePath.toRequestBody(MultipartBody.FORM)
                val versionPart = baseVersion.toString().toRequestBody(MultipartBody.FORM)
                val modifiedPart = Instant.ofEpochMilli(localFile.lastModified())
                    .toString().toRequestBody(MultipartBody.FORM)

                val response = api().uploadFile(filePart, pathPart, versionPart, modifiedPart)
                if (response.code() == 409) {
                    throw FileConflictException("文件已被其他设备修改")
                }
                if (!response.isSuccessful) {
                    return@withContext Result.failure(
                        Exception("上传失败: ${response.code()} ${response.message()}")
                    )
                }
                val body = response.body()
                    ?: return@withContext Result.failure(Exception("空响应"))
                Result.success(body)
            } catch (e: Exception) {
                Result.failure(e)
            }
        }
    }

    suspend fun searchFiles(query: String): Result<List<FileEntryDto>> {
        return safeCall {
            val response = api().searchFiles(query)
            if (response.isSuccessful) {
                // SearchResponse 由 shared-spec.json → api.responses 生成（T-061），消除 Map 松散解析
                response.body()?.data ?: emptyList()
            } else emptyList()
        }
    }

    suspend fun getDevices(): Result<List<DeviceItem>> {
        return safeCall {
            val response = api().getDevices()
            if (response.isSuccessful) {
                // DevicesResponse 由 shared-spec.json → api.responses 生成（T-061），消除 Map 松散解析
                response.body()?.data ?: emptyList()
            } else emptyList()
        }
    }

    suspend fun healthCheck(): Result<Boolean> {
        return safeCall {
            val r = api().healthCheck()
            if (r.isSuccessful) Unit else throw Exception("健康检查失败: ${r.code()}")
            true
        }
    }

    private fun String.toRequestBody(mediaType: okhttp3.MediaType?): okhttp3.RequestBody {
        return okhttp3.RequestBody.create(mediaType, this)
    }

    private suspend fun <T> safeCall(block: suspend () -> T): Result<T> {
        return try {
            Result.success(block())
        } catch (e: Exception) {
            Log.e("CloudPan", "API 错误", e)
            Result.failure(e)
        }
    }
}
