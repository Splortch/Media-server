package org.splortch.mediaserver.core

import java.io.IOException
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import okhttp3.HttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.ResponseBody
import okio.Buffer
import okio.BufferedSource
import okio.ForwardingSource
import okio.buffer
import retrofit2.Retrofit
import retrofit2.converter.kotlinx.serialization.asConverterFactory
import retrofit2.http.*

// The existing csrf endpoint has no response schema in the OpenAPI snapshot.
@Serializable data class CsrfToken(val token: String)

interface MediaApi {
    @GET("server-info") suspend fun info(): ServerInfo

    @GET("auth/csrf") suspend fun csrf(): CsrfToken

    @POST("auth/login")
    suspend fun login(@Header("X-CSRF-TOKEN") csrf: String, @Body input: LoginRequest): UserIdentity

    @GET("auth/me") suspend fun me(): UserIdentity

    @POST("auth/logout") suspend fun logout(@Header("X-CSRF-TOKEN") csrf: String)

    @GET("media-page")
    suspend fun page(
        @Query("prefix") prefix: String = "",
        @Query("cursor") cursor: String? = null,
        @Query("libraryId") libraryId: Int? = null,
        @Query("limit") limit: Int = 60,
    ): MediaPage

    @GET("browse-libraries") suspend fun libraries(): List<LibraryBrowse>

    @GET("continue-watching-media") suspend fun continuing(): List<MediaSummary>

    @GET("media/{id}") suspend fun details(@Path("id") id: Int): MediaDetails

    @GET("media/{id}/tracks") suspend fun tracks(@Path("id") id: Int): TrackCatalog

    @GET("media/{id}/progress") suspend fun progress(@Path("id") id: Int): ProgressState

    @PUT("media/{id}/progress-sync")
    suspend fun sync(
        @Path("id") id: Int,
        @Header("X-CSRF-TOKEN") csrf: String,
        @Body update: ProgressUpdate,
    ): ProgressState

    @PUT("capabilities")
    suspend fun capabilities(@Header("X-CSRF-TOKEN") csrf: String, @Body input: ClientCapabilities)

    @POST("media/{id}/playback-errors")
    suspend fun report(
        @Path("id") id: Int,
        @Header("X-CSRF-TOKEN") csrf: String,
        @Body error: PlaybackError,
    )

    @GET("media/{id}/playback-options")
    suspend fun playbackOptions(@Path("id") id: Int): PlaybackOptions

    @POST("media/{id}/playback-sessions")
    suspend fun prepare(
        @Path("id") id: Int,
        @Header("X-CSRF-TOKEN") csrf: String,
        @Body input: PlaybackSelection,
    ): ConversionState

    @GET("playback-sessions/{id}") suspend fun conversion(@Path("id") id: String): ConversionState

    @DELETE("playback-sessions/{id}")
    suspend fun release(@Path("id") id: String, @Header("X-CSRF-TOKEN") csrf: String)

    @GET("sessions") suspend fun sessions(): List<SessionInfo>

    @DELETE("sessions/{handle}")
    suspend fun revoke(@Path("handle") handle: String, @Header("X-CSRF-TOKEN") csrf: String)
}

class ServerConnection(
    val origin: HttpUrl,
    vault: SessionVault,
    remember: Boolean,
    restored: Boolean = false,
    expired: () -> Unit = {},
) {
    private val closed = AtomicBoolean(false)
    private val notified = AtomicBoolean(false)
    private val jar = ProtectedCookies(origin, vault, remember).apply { if (restored) restore() }
    private val base =
        OkHttpClient.Builder()
            .cookieJar(jar)
            .followRedirects(false)
            .followSslRedirects(false)
            .connectTimeout(10, TimeUnit.SECONDS)
            .readTimeout(20, TimeUnit.SECONDS)
            .writeTimeout(20, TimeUnit.SECONDS)
            .addInterceptor { chain ->
                if (closed.get() || !jar.sameOrigin(chain.request().url))
                    throw IOException("Connection is closed or server origin changed.")
                val response = chain.proceed(chain.request())
                if (
                    response.code == 401 && chain.request().url.encodedPath != "/api/v1/auth/login"
                ) {
                    jar.close()
                    if (notified.compareAndSet(false, true)) expired()
                }
                response
            }
            .build()
    val mediaClient: OkHttpClient =
        base
            .newBuilder()
            .addInterceptor { chain ->
                if (!chain.request().url.encodedPath.let { PlaybackResources.allowedPath(it) })
                    throw IOException("Invalid media path.")
                chain.proceed(chain.request())
            }
            .build()
    private val apiClient =
        base
            .newBuilder()
            .callTimeout(25, TimeUnit.SECONDS)
            .addInterceptor { chain ->
                val response = chain.proceed(chain.request())
                val body = response.body ?: return@addInterceptor response
                if (body.contentLength() > MAX_BODY) {
                    response.close()
                    throw IOException("Server response is too large.")
                }
                response
                    .newBuilder()
                    .body(
                        object : ResponseBody() {
                            override fun contentType() = body.contentType()

                            override fun contentLength() = body.contentLength()

                            private val limited by lazy {
                                object : ForwardingSource(body.source()) {
                                        var total = 0L

                                        override fun read(sink: Buffer, byteCount: Long): Long {
                                            val n =
                                                super.read(
                                                    sink,
                                                    minOf(byteCount, MAX_BODY - total + 1),
                                                )
                                            if (n > 0) total += n
                                            if (total > MAX_BODY)
                                                throw IOException("Server response is too large.")
                                            return n
                                        }
                                    }
                                    .buffer()
                            }

                            override fun source(): BufferedSource = limited
                        }
                    )
                    .build()
            }
            .build()
    val api: MediaApi =
        Retrofit.Builder()
            .baseUrl(origin.resolve("/api/v1/")!!)
            .client(apiClient)
            .addConverterFactory(
                Json { ignoreUnknownKeys = true }
                    .asConverterFactory("application/json".toMediaType())
            )
            .build()
            .create(MediaApi::class.java)

    suspend fun token(): String =
        api.csrf().token.also {
            require(it.isNotBlank() && it.length <= 8192) { "Invalid CSRF response." }
        }

    suspend fun check(): ServerInfo =
        api.info().also {
            require(
                it.apiVersion == 1 &&
                    it.minimumClientApiVersion <= 1 &&
                    it.features.contains("prefix-search")
            ) {
                "This server API is incompatible. Update the server or Android app."
            }
            require(!it.setupRequired) {
                "Finish first-time server setup in the web interface, then connect again."
            }
            require(it.serverVersion.length <= 64 && it.features.size <= 64) {
                "Invalid server information."
            }
        }

    fun streamUrl(id: Int): String {
        require(id > 0)
        return origin.resolve("/api/v1/media/$id/stream")!!.toString()
    }

    fun playbackUrl(session: String, file: String): String {
        require(PlaybackResources.validSession(session) && PlaybackResources.validFile(file)) {
            "Invalid playback resource."
        }
        return origin.resolve("/api/v1/playback-sessions/$session/files/$file")!!.toString()
    }

    fun close(clearSaved: Boolean = true) {
        closed.set(true)
        jar.close(clearSaved)
        base.dispatcher.cancelAll()
    }

    companion object {
        private const val MAX_BODY = 2L * 1024 * 1024
    }
}
