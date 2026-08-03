package com.cloudpan.android.data

import java.io.IOException
import java.net.ConnectException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import javax.net.ssl.SSLException

/**
 * 错误白话归因（T-091）——把底层异常（连接失败/HTTP 状态码/超时等）映射为家庭用户可读的
 * 『原因 + 下一步』，避免失败提示透出英文异常原文（F-133）。
 * 语义对齐 Windows 端 CloudPan.Client.Core.Services.ErrorAttribution（F-31）。
 *
 * Android 异常形态说明：Retrofit 端点返回 Response<T>，HTTP 非 2xx 由 FileRepository 手动
 * 构造 `Exception("xxx失败: <code> <reason>")`；连接失败由 OkHttp 抛 IOException 子类
 * （UnknownHostException / ConnectException / SocketTimeoutException / SSLException 等）。
 * 据此按「异常类型优先 + message 内嵌状态码兜底」归类。
 */
data class ErrorAttribution(val message: String, val nextStep: String = "") {

    /** 完整展示文案：原因 + 下一步。 */
    fun displayText(): String =
        if (nextStep.isBlank()) message else "$message（下一步：$nextStep）"

    companion object {
        /** 从异常生成白话归因；异常为 null 时返回未知错误。 */
        fun from(exception: Throwable?): ErrorAttribution {
            if (exception == null) return UNKNOWN
            // 按类型归类——子类分支必须排在父类之前（ConnectException/UnknownHostException 均继承 IOException）
            return when (exception) {
                // 409 冲突（T-089 UI 已弹窗分流，此处兜底）
                is FileConflictException -> CONFLICT
                is UnknownHostException -> CONNECTION
                is ConnectException -> CONNECTION
                is SocketTimeoutException -> TIMEOUT
                is SSLException -> SSL_ERROR
                is IOException -> CONNECTION
                else -> classifyByStatus(exception.message)
            }
        }

        /** 按 message 中嵌入的 HTTP 状态码归类（仓库以「xxx失败: <code> <reason>」构造异常）。 */
        private fun classifyByStatus(message: String?): ErrorAttribution {
            val code = STATUS_PATTERN.find(message ?: "")?.groupValues?.get(1)?.toIntOrNull()
            return when (code) {
                401 -> UNAUTHORIZED
                404 -> NOT_FOUND
                null -> UNKNOWN
                else -> ErrorAttribution("云盘服务返回错误（HTTP $code）", "请稍后重试")
            }
        }

        /** 匹配 4xx/5xx 状态码（仓库构造的异常 message 形如「上传失败: 401 Unauthorized」）。 */
        private val STATUS_PATTERN = Regex("(\\b[45]\\d{2}\\b)")

        private val UNAUTHORIZED = ErrorAttribution(
            "登录凭证已失效，无法连接云盘服务",
            "请打开设置，重新配置云盘地址与访问令牌"
        )
        private val NOT_FOUND = ErrorAttribution(
            "找不到该文件或文件夹",
            "文件可能已在其他设备上被删除，请刷新后再试"
        )
        private val CONNECTION = ErrorAttribution(
            "无法连接到云盘服务",
            "请检查台式机是否已开机、云盘服务是否正在运行，且手机与台式机在同一网络"
        )
        private val TIMEOUT = ErrorAttribution(
            "请求超时",
            "请稍后重试，或检查网络连接"
        )
        private val SSL_ERROR = ErrorAttribution(
            "与云盘服务的安全连接失败",
            "家庭局域网请使用 http:// 地址；若确为 https，请检查证书是否有效"
        )
        private val CONFLICT = ErrorAttribution(
            "文件已被其他设备修改",
            "请刷新后查看最新内容，再决定是否覆盖"
        )
        private val UNKNOWN = ErrorAttribution(
            "操作失败（未知错误）",
            "请重试；若持续失败，可查看手机日志确认原因"
        )
    }
}

/** 扩展：异常（含 null）→ 完整白话展示文案（原因 + 下一步）。 */
fun Throwable?.toUserMessage(): String = ErrorAttribution.from(this).displayText()
