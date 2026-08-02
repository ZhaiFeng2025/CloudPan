// AUTO-GENERATED from shared-spec.json v1.3.0 → api.endpoints — DO NOT EDIT
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
}
