package org.splortch.mediaserver.core

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.AtomicFile
import java.io.IOException
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

interface SessionVault {
    fun read(): String?

    fun write(value: String)

    fun clear()
}

/** Credentials never enter preferences, backups or logs. No plaintext fallback. */
class KeystoreVault(context: Context) : SessionVault {
    private val file = AtomicFile(java.io.File(context.noBackupFilesDir, "session-v1"))

    private fun key(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getKey(ALIAS, null) as? SecretKey)?.let {
            return it
        }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
            .apply {
                init(
                    KeyGenParameterSpec.Builder(
                            ALIAS,
                            KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
                        )
                        .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                        .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                        .setKeySize(256)
                        .build()
                )
            }
            .generateKey()
    }

    @Synchronized
    override fun read(): String? {
        if (!file.baseFile.exists()) return null
        val bytes =
            file.openRead().use { input ->
                val buffer = ByteArray(131073)
                var total = 0
                while (total < buffer.size) {
                    val n = input.read(buffer, total, buffer.size - total)
                    if (n < 0) break
                    total += n
                }
                buffer.copyOf(total)
            }
        if (bytes.size !in 29..131072) throw IOException("Invalid saved session.")
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, bytes.copyOfRange(0, 12)))
        cipher.updateAAD(ALIAS.toByteArray())
        return cipher.doFinal(bytes.copyOfRange(12, bytes.size)).toString(Charsets.UTF_8)
    }

    @Synchronized
    override fun write(value: String) {
        require(value.toByteArray().size <= 65536)
        val cipher =
            Cipher.getInstance("AES/GCM/NoPadding").apply {
                init(Cipher.ENCRYPT_MODE, key())
                updateAAD(ALIAS.toByteArray())
            }
        val output = file.startWrite()
        try {
            output.write(cipher.iv + cipher.doFinal(value.toByteArray()))
            file.finishWrite(output)
        } catch (e: Exception) {
            file.failWrite(output)
            throw e
        }
    }

    @Synchronized
    override fun clear() {
        file.delete()
    }

    companion object {
        const val ALIAS = "org.splortch.mediaserver.session.v1"
    }
}
