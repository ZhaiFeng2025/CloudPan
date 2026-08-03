// Hand-maintained（v1.0 Android 原型，非生成产物）
// 说明: CloudPan.CodeGen 当前无 Kotlin 生成器，本接口由手工维护，不声称由标准管线生成。
// 对齐基线: shared-spec.json v1.1.0 api.endpoints 路径（与 C# SpecEndpoints 一致）。
package com.cloudpan.android.data

import okhttp3.MultipartBody
import okhttp3.RequestBody
import retrofit2.Response
import retrofit2.http.*

interface CloudPanApi {

    @GET("api/health")
    suspend fun healthCheck(): Response<Map<String, Any>>

    @GET("api/files/tree")
    suspend fun getFileTree(
        @Query("sinceVersion") sinceVersion: Int = 0,
        @Query("limit") limit: Int = 5000,
        @Query("cursor") cursor: String? = null
    ): Response<FileTreeResponse>

    /** 获取指定文件夹下的文件列表。 */
    @GET("api/files/tree")
    suspend fun getFileTreeInFolder(
        @Query("path") folderPath: String,
        @Query("limit") limit: Int = 5000
    ): Response<FileTreeResponse>

    @Multipart
    @POST("api/files/upload")
    suspend fun uploadFile(
        @Part file: MultipartBody.Part,
        @Part("path") path: RequestBody,
        @Part("baseVersion") baseVersion: RequestBody,
        @Part("lastModified") lastModified: RequestBody
    ): Response<UploadResponse>

    @GET("api/files/download")
    suspend fun downloadFile(
        @Query("path") path: String
    ): Response<okhttp3.ResponseBody>

    @POST("api/files/delete")
    suspend fun deleteFile(
        @Body request: Map<String, @JvmSuppressWildcards Any>
    ): Response<Map<String, Any>>

    @POST("api/files/move")
    suspend fun moveFile(
        @Body request: Map<String, @JvmSuppressWildcards Any>
    ): Response<Map<String, Any>>

    @POST("api/files/mkdir")
    suspend fun createFolder(
        @Body request: Map<String, @JvmSuppressWildcards Any>
    ): Response<Map<String, Any>>

    @GET("api/files/search")
    suspend fun searchFiles(
        @Query("q") query: String,
        @Query("limit") limit: Int = 50
    ): Response<Map<String, Any>>

    @POST("api/shares")
    suspend fun createShare(
        @Body request: Map<String, @JvmSuppressWildcards Any>
    ): Response<ShareResponse>

    @GET("api/devices")
    suspend fun getDevices(): Response<Map<String, Any>>

    // ---- 回收站（T-050，对齐 Windows T-014：删除走软删进回收站，可恢复/清空）----

    /** 回收站文件列表（GET /api/trash）。 */
    @GET("api/trash")
    suspend fun getTrash(): Response<TrashListResponse>

    /** 恢复回收站文件（POST /api/trash/restore，metaFileName 为回收站元数据文件名）。 */
    @POST("api/trash/restore")
    suspend fun restoreTrash(
        @Body request: Map<String, @JvmSuppressWildcards Any>
    ): Response<Map<String, Any>>

    /** 清空回收站（DELETE /api/trash/empty）。 */
    @DELETE("api/trash/empty")
    suspend fun emptyTrash(): Response<Map<String, Any>>
}
