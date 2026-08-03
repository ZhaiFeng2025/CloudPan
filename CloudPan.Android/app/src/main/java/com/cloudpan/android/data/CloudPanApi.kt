// Retrofit interface 方法签名（@Query/@Body/@Part 参数绑定）由手工维护——
// shared-spec.json 暂未提供结构化参数段，全量生成标注为渐进项（T-061 note）。
// 路由注解引用 Generated/SpecRoutes.g.kt 常量，返回类型引用 Generated/Dtos.g.kt（均由 shared-spec.json 生成）。
package com.cloudpan.android.data

import okhttp3.MultipartBody
import okhttp3.RequestBody
import retrofit2.Response
import retrofit2.http.*

interface CloudPanApi {

    @GET(SpecRoutes.Health)
    suspend fun healthCheck(): Response<HealthResponse>

    @GET(SpecRoutes.FilesTree)
    suspend fun getFileTree(
        @Query("sinceVersion") sinceVersion: Int = 0,
        @Query("limit") limit: Int = 5000,
        @Query("cursor") cursor: String? = null
    ): Response<FileTreeResponse>

    /** 获取指定文件夹下的文件列表（T-059：支持 cursor 游标分页）。 */
    @GET(SpecRoutes.FilesTree)
    suspend fun getFileTreeInFolder(
        @Query("path") folderPath: String,
        @Query("limit") limit: Int = 5000,
        @Query("cursor") cursor: String? = null
    ): Response<FileTreeResponse>

    @Multipart
    @POST(SpecRoutes.FilesUpload)
    suspend fun uploadFile(
        @Part file: MultipartBody.Part,
        @Part("path") path: RequestBody,
        @Part("baseVersion") baseVersion: RequestBody,
        @Part("lastModified") lastModified: RequestBody
    ): Response<UploadResponse>

    @GET(SpecRoutes.FilesDownload)
    suspend fun downloadFile(
        @Query("path") path: String
    ): Response<okhttp3.ResponseBody>

    @POST(SpecRoutes.FilesDelete)
    suspend fun deleteFile(
        @Body request: Map<String, @JvmSuppressWildcards Any>
    ): Response<DeleteResponse>

    @POST(SpecRoutes.FilesMove)
    suspend fun moveFile(
        @Body request: Map<String, @JvmSuppressWildcards Any>
    ): Response<MoveResponse>

    @POST(SpecRoutes.FilesMkdir)
    suspend fun createFolder(
        @Body request: Map<String, @JvmSuppressWildcards Any>
    ): Response<MkdirResponse>

    @GET(SpecRoutes.FilesSearch)
    suspend fun searchFiles(
        @Query("q") query: String,
        @Query("limit") limit: Int = 50
    ): Response<SearchResponse>

    @POST(SpecRoutes.Shares)
    suspend fun createShare(
        @Body request: Map<String, @JvmSuppressWildcards Any>
    ): Response<ShareCreateResponse>

    @GET(SpecRoutes.Devices)
    suspend fun getDevices(): Response<DevicesResponse>

    // ---- 回收站（T-050，对齐 Windows T-014：删除走软删进回收站，可恢复/清空）----

    /** 回收站文件列表（GET /api/trash）。 */
    @GET(SpecRoutes.Trash)
    suspend fun getTrash(): Response<TrashListResponse>

    /** 恢复回收站文件（POST /api/trash/restore，metaFileName 为回收站元数据文件名）。 */
    @POST(SpecRoutes.TrashRestore)
    suspend fun restoreTrash(
        @Body request: Map<String, @JvmSuppressWildcards Any>
    ): Response<TrashRestoreResponse>

    /** 清空回收站（DELETE /api/trash/empty）。 */
    @DELETE(SpecRoutes.TrashEmpty)
    suspend fun emptyTrash(): Response<TrashEmptyResponse>
}
