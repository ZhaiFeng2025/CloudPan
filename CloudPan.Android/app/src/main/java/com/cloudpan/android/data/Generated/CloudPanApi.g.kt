// AUTO-GENERATED from shared-spec.json
// 版本: 1.11.0  日期: 2026-08-04
// 源: shared-spec.json → api.endpoints[].clientMethod（Retrofit interface，与 C# ClientApi.g.cs 同源）
// 请勿手工编辑 — 重新生成: dotnet run --project CloudPan.CodeGen

package com.cloudpan.android.data

import okhttp3.MultipartBody
import okhttp3.RequestBody
import retrofit2.Response
import retrofit2.http.*

/**
 * Retrofit HTTP 接口——方法签名由 shared-spec.json → api.endpoints[].clientMethod 生成（T-086）。
 * 路由注解引用 SpecRoutes 常量，返回类型引用 Dtos.g.kt；
 * 改 spec 端点后重跑 CodeGen --verify 强制 C#/Kotlin 两端接口签名一致，禁止手工翻译回归。
 */
interface CloudPanApi {
    /**
     * 健康检查（GET /api/health）
     */
    @GET(SpecRoutes.Health)
    suspend fun healthCheck(): Response<HealthResponse>

    /**
     * 文件树（支持 sinceVersion/limit/cursor 分页）（GET /api/files/tree）
     */
    @GET(SpecRoutes.FilesTree)
    suspend fun getFileTree(@Query("sinceVersion") sinceVersion: Int, @Query("limit") limit: Int = 5000, @Query("cursor") cursor: String? = null): Response<FileTreeResponse>

    /**
     * 文件树（支持 sinceVersion/limit/cursor 分页）（GET /api/files/tree）
     */
    @GET(SpecRoutes.FilesTree)
    suspend fun getFileTreeInFolder(@Query("path") folderPath: String, @Query("limit") limit: Int = 5000, @Query("cursor") cursor: String? = null): Response<FileTreeResponse>

    /**
     * 上传文件（multipart）（POST /api/files/upload）
     */
    @Multipart
    @POST(SpecRoutes.FilesUpload)
    suspend fun uploadFile(@Part file: MultipartBody.Part, @Part("path") path: RequestBody, @Part("baseVersion") baseVersion: RequestBody, @Part("lastModified") lastModified: RequestBody): Response<UploadResponse>

    /**
     * 下载文件（stream）（GET /api/files/download）
     */
    @GET(SpecRoutes.FilesDownload)
    suspend fun downloadFile(@Query("path") path: String): Response<okhttp3.ResponseBody>

    /**
     * 删除文件/文件夹（递归）（POST /api/files/delete）
     */
    @POST(SpecRoutes.FilesDelete)
    suspend fun deleteFile(@Body request: DeleteRequestDto): Response<DeleteResponse>

    /**
     * 移动/重命名（POST /api/files/move）
     */
    @POST(SpecRoutes.FilesMove)
    suspend fun moveFile(@Body request: MoveRequestDto): Response<MoveResponse>

    /**
     * 创建文件夹（POST /api/files/mkdir）
     */
    @POST(SpecRoutes.FilesMkdir)
    suspend fun createFolder(@Body request: MkdirRequestDto): Response<MkdirResponse>

    /**
     * 分块上传（POST /api/files/upload/chunk）
     */
    @Multipart
    @POST(SpecRoutes.FilesUploadChunk)
    suspend fun uploadChunk(@Part chunk: MultipartBody.Part, @Part("path") path: RequestBody, @Part("chunkIndex") chunkIndex: RequestBody, @Part("totalChunks") totalChunks: RequestBody, @Part("fileHash") fileHash: RequestBody, @Part("baseVersion") baseVersion: RequestBody, @Part("lastModified") lastModified: RequestBody): Response<UploadResponse>

    /**
     * 查询分块上传进度（GET /api/files/upload/chunk/status）
     */
    @GET(SpecRoutes.FilesUploadChunkStatus)
    suspend fun getChunkStatus(@Query("path") path: String, @Query("fileHash") fileHash: String? = null): Response<ChunkStatusResponse>

    /**
     * 文件名搜索（GET /api/files/search）
     */
    @GET(SpecRoutes.FilesSearch)
    suspend fun searchFiles(@Query("q") query: String, @Query("limit") limit: Int = 50): Response<SearchResponse>

    /**
     * 图片缩略图（GET /api/thumbnails）
     */
    @GET(SpecRoutes.Thumbnails)
    suspend fun getThumbnail(@Query("path") path: String, @Query("width") width: Int): Response<okhttp3.ResponseBody>

    /**
     * 设备列表（GET /api/devices）
     */
    @GET(SpecRoutes.Devices)
    suspend fun getDevices(): Response<DevicesResponse>

    /**
     * 创建分享链接（POST /api/shares）
     */
    @POST(SpecRoutes.Shares)
    suspend fun createShare(@Body request: CreateShareRequestDto): Response<ShareCreateResponse>

    /**
     * 分享链接列表（当前设备创建，不含 token 等敏感字段）（GET /api/shares）
     */
    @GET(SpecRoutes.Shares)
    suspend fun getShares(): Response<ShareListResponse>

    /**
     * 撤销分享链接（DELETE /api/shares/{shareId}）
     */
    @DELETE(SpecRoutes.SharesByShareId)
    suspend fun revokeShare(@Path("shareId") shareId: String): Response<Unit>

    /**
     * 回收站文件列表（GET /api/trash）
     */
    @GET(SpecRoutes.Trash)
    suspend fun getTrash(): Response<TrashListResponse>

    /**
     * 恢复回收站文件（POST /api/trash/restore）
     */
    @POST(SpecRoutes.TrashRestore)
    suspend fun restoreTrash(@Body request: RestoreTrashRequestDto): Response<TrashRestoreResponse>

    /**
     * 清空回收站（DELETE /api/trash/empty）
     */
    @DELETE(SpecRoutes.TrashEmpty)
    suspend fun emptyTrash(): Response<TrashEmptyResponse>

}
