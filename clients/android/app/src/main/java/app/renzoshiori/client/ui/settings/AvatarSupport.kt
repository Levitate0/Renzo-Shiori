package app.renzoshiori.client.ui.settings

import android.content.Context
import android.net.Uri
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest

/**
 * Avatar plumbing shared by the Account page and the Users → Edit dialog.
 *
 * Mirrors RenzoFrontend/src/lib/gravatar.ts and the web's file-input guards:
 * ≤2MB, PNG/JPEG/GIF/WebP only, raw base64 with no `data:` prefix (the backend
 * rejects the prefix). The Gravatar email is hashed and sent to gravatar.com
 * only — it never reaches the Renzo server, exactly as on the web.
 */

private const val MAX_AVATAR_BYTES = 2 * 1024 * 1024

val ALLOWED_AVATAR_TYPES = listOf("image/png", "image/jpeg", "image/gif", "image/webp")

/** (base64, contentType) or an error message describing why it was rejected. */
sealed interface AvatarLoad {
    data class Ok(val base64: String, val contentType: String) : AvatarLoad
    data class Failed(val message: String) : AvatarLoad
}

suspend fun readPickedAvatar(context: Context, uri: Uri): AvatarLoad = withContext(Dispatchers.IO) {
    val type = context.contentResolver.getType(uri) ?: "image/png"
    if (type !in ALLOWED_AVATAR_TYPES) {
        return@withContext AvatarLoad.Failed("Only PNG, JPEG, GIF, and WebP images are allowed")
    }
    val bytes = runCatching {
        context.contentResolver.openInputStream(uri)?.use { it.readBytes() }
    }.getOrNull() ?: return@withContext AvatarLoad.Failed("Couldn't read that image")
    if (bytes.size > MAX_AVATAR_BYTES) {
        return@withContext AvatarLoad.Failed("Image must be less than 2MB")
    }
    AvatarLoad.Ok(android.util.Base64.encodeToString(bytes, android.util.Base64.NO_WRAP), type)
}

/**
 * Looks up an address's Gravatar as a *preview* — nothing is saved until the
 * user presses Save, matching the web copy under the field.
 */
suspend fun fetchGravatarBase64(email: String): AvatarLoad = withContext(Dispatchers.IO) {
    val hash = md5Hex(email.trim().lowercase())
    val connection = runCatching {
        (URL("https://www.gravatar.com/avatar/$hash?s=128&d=mp&r=g").openConnection() as HttpURLConnection).apply {
            connectTimeout = 15_000
            readTimeout = 15_000
        }
    }.getOrNull() ?: return@withContext AvatarLoad.Failed("Failed to load Gravatar image")

    try {
        if (connection.responseCode !in 200..299) {
            return@withContext AvatarLoad.Failed("Failed to load Gravatar image")
        }
        val bytes = connection.inputStream.use { it.readBytes() }
        if (bytes.isEmpty()) return@withContext AvatarLoad.Failed("Failed to load Gravatar image")
        if (bytes.size > MAX_AVATAR_BYTES) return@withContext AvatarLoad.Failed("Image must be less than 2MB")
        val contentType = connection.contentType?.substringBefore(';')?.trim()
            ?.takeIf { it in ALLOWED_AVATAR_TYPES } ?: "image/png"
        AvatarLoad.Ok(android.util.Base64.encodeToString(bytes, android.util.Base64.NO_WRAP), contentType)
    } catch (e: Exception) {
        AvatarLoad.Failed(e.message ?: "Failed to load Gravatar image")
    } finally {
        connection.disconnect()
    }
}

private fun md5Hex(value: String): String =
    MessageDigest.getInstance("MD5")
        .digest(value.toByteArray(Charsets.UTF_8))
        .joinToString("") { byte -> "%02x".format(byte) }
