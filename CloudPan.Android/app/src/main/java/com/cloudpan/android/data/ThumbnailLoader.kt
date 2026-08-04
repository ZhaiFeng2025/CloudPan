package com.cloudpan.android.data

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.Log
import android.util.LruCache
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.withContext
import java.io.File
import java.security.MessageDigest

/**
 * 缩略图加载器（T-113）——服务端 GET /api/thumbnails 已生成 JPEG 小图（含 heic/heif，见 ThumbnailsController），
 * 客户端负责三级缓存与并发控制，避免相册滚动 OOM / 卡顿 / 拉满带宽：
 * 1) 内存缓存：LruCache 按 bitmap.byteCount 计数，超上限 LRU 淘汰（防 OOM）；
 * 2) 磁盘缓存：cacheDir/thumbnails 存服务端返回的 JPEG 字节，断网可复用；
 * 3) 网络：经 FileRepository.fetchThumbnail 拉取，并发经 kotlinx.coroutines.Semaphore 限流（可取消）。
 * 加载失败返回 null，由 UI 降级为类型图标，不抛异常不崩溃。
 */
class ThumbnailLoader(
    private val repository: FileRepository,
    private val cacheDir: File,
    private val maxMemoryBytes: Int = DEFAULT_MEMORY_CACHE_BYTES,
    private val maxConcurrent: Int = DEFAULT_MAX_CONCURRENT
) {
    companion object {
        /** 内存缓存上限（字节）——按 bitmap.byteCount 计数，相册滚动时超限 LRU 淘汰，防 OOM。 */
        private const val DEFAULT_MEMORY_CACHE_BYTES = 48 * 1024 * 1024
        /** 并发加载上限——同时解码/下载的缩略图数，避免滚动时解码与网络拉满。 */
        private const val DEFAULT_MAX_CONCURRENT = 4
    }

    private val memoryCache = object : LruCache<String, Bitmap>(maxMemoryBytes) {
        override fun sizeOf(key: String, value: Bitmap): Int = value.byteCount
    }
    private val semaphore = Semaphore(maxConcurrent)

    init {
        cacheDir.mkdirs()
    }

    /**
     * 加载缩略图（按 path + width 缓存）。内存命中直接返回；未命中进入 IO 上下文，
     * 经并发门（可取消）依次查磁盘缓存 → 网络。失败返回 null。
     */
    suspend fun load(path: String, width: Int): Bitmap? {
        val key = thumbnailKey(path, width)
        // 内存命中：主线程直接返回，滚动快速回看不卡顿
        memoryCache.get(key)?.let { return it }
        return withContext(Dispatchers.IO) {
            semaphore.withPermit {
                // 双检：等待并发门期间该图可能已被其他请求加载写入
                memoryCache.get(key)?.let { return@withPermit it }
                val disk = diskFile(key)
                if (disk.exists()) {
                    val fromDisk = BitmapFactory.decodeFile(disk.absolutePath)
                    if (fromDisk != null) {
                        memoryCache.put(key, fromDisk)
                        return@withPermit fromDisk
                    }
                }
                val bytes = repository.fetchThumbnail(path, width) ?: return@withPermit null
                val decoded = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                    ?: return@withPermit null
                // 服务端已按 width 缩放，但客户端再按目标宽度兜底降采样，控制内存缓存占用
                val scaled = scaleToWidth(decoded, width)
                if (scaled !== decoded) decoded.recycle()
                memoryCache.put(key, scaled)
                try {
                    disk.writeBytes(bytes)
                } catch (e: Exception) {
                    Log.e("CloudPan", "缩略图磁盘缓存写入失败", e)
                }
                scaled
            }
        }
    }

    /** 等比例缩到目标宽度；目标宽度 ≥ 原宽时原样返回。 */
    private fun scaleToWidth(bmp: Bitmap, width: Int): Bitmap {
        if (width <= 0 || bmp.width <= width) return bmp
        val height = (bmp.height.toFloat() * width / bmp.width).toInt().coerceAtLeast(1)
        return Bitmap.createScaledBitmap(bmp, width, height, true)
    }

    private fun thumbnailKey(path: String, width: Int): String = "$width|$path"

    /** 磁盘缓存文件名（SHA-256 前 32 hex）——同 key 落盘不冲突。 */
    private fun diskFile(key: String): File = File(cacheDir, hashKey(key) + ".jpg")

    private fun hashKey(key: String): String {
        val md = MessageDigest.getInstance("SHA-256")
        return md.digest(key.toByteArray())
            .joinToString("") { b -> Integer.toHexString(b.toInt() and 0xff).padStart(2, '0') }
            .take(32)
    }
}
