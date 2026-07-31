package com.cloudpan.android.data

import android.content.Context
import android.content.SharedPreferences
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey

/**
 * 本地设置持久化（EncryptedSharedPreferences）。
 * 存储服务端地址、Token、设备 ID。
 * Token 使用 AES256-GCM 加密存储。
 */
class SettingsStore(context: Context) {
    private val prefs: SharedPreferences = run {
        val masterKey = MasterKey.Builder(context)
            .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
            .build()
        EncryptedSharedPreferences.create(
            context,
            "cloudpan_settings",
            masterKey,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
            EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM
        )
    }

    var serverUrl: String
        get() = prefs.getString("server_url", "http://10.0.2.2:8443") ?: "http://10.0.2.2:8443"
        set(value) = prefs.edit().putString("server_url", value).apply()

    var token: String
        get() = prefs.getString("token", "") ?: ""
        set(value) = prefs.edit().putString("token", value).apply()

    val deviceId: String
        get() {
            var id = prefs.getString("device_id", null)
            if (id == null) {
                id = java.util.UUID.randomUUID().toString().replace("-", "")
                prefs.edit().putString("device_id", id).apply()
            }
            return id
        }

    var wifiOnly: Boolean
        get() = prefs.getBoolean("wifi_only", true)
        set(v) = prefs.edit().putBoolean("wifi_only", v).apply()

    var chargingOnly: Boolean
        get() = prefs.getBoolean("charging_only", true)
        set(v) = prefs.edit().putBoolean("charging_only", v).apply()
}
