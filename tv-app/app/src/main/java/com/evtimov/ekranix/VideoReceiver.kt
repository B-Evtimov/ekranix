package com.evtimov.ekranix

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.view.Surface
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.io.BufferedInputStream
import java.io.DataInputStream
import java.io.IOException
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket

class VideoReceiver(
    private val scope: CoroutineScope,
    private val isAllowed: (InetAddress) -> Boolean,
    private val listener: Listener,
) {
    interface Listener {
        fun onStreamStarted()
        fun onStreamEnded()
    }

    @Volatile var surface: Surface? = null

    private var serverSocket: ServerSocket? = null
    private var acceptJob: Job? = null
    @Volatile private var current: Socket? = null
    @Volatile private var streaming = false

    fun start() {
        acceptJob = scope.launch(Dispatchers.IO) {
            val ss = try {
                ServerSocket().apply {
                    reuseAddress = true
                    bind(InetSocketAddress(Protocol.VIDEO_PORT))
                }
            } catch (e: IOException) {
                return@launch
            }
            serverSocket = ss
            while (isActive) {
                val s = try { ss.accept() } catch (e: IOException) { break }
                if (current != null || !isAllowed(s.inetAddress)) {
                    try { s.close() } catch (e: IOException) { }
                    continue
                }
                current = s
                streaming = true
                launch { runStream(s) }
            }
        }
    }

    fun stop() {
        try { serverSocket?.close() } catch (e: IOException) { }
        closeStream()
        acceptJob?.cancel()
    }

    fun closeStream() {
        streaming = false
        try { current?.close() } catch (e: IOException) { }
    }

    private fun runStream(socket: Socket) {
        var codec: MediaCodec? = null
        var drain: Thread? = null
        try {
            socket.tcpNoDelay = true
            val input = DataInputStream(BufferedInputStream(socket.getInputStream(), 256 * 1024))
            val surf = waitForSurface() ?: return
            scope.launch(Dispatchers.Main) { listener.onStreamStarted() }

            var buf = ByteArray(1 shl 20)
            while (streaming) {
                val len = input.readInt()
                val flags = input.readUnsignedByte()
                if (len <= 0 || len > 16 * 1024 * 1024) throw IOException("Невалидна дължина $len")
                if (buf.size < len) buf = ByteArray(len)
                input.readFully(buf, 0, len)

                if (codec == null) {
                    // Без SPS/PPS декодерът не знае какво декодира: чакаме ги
                    if (flags and Protocol.VIDEO_FLAG_CONFIG == 0) continue
                    codec = createDecoder(surf)
                    drain = startDrain(codec)
                }
                queue(codec, buf, len, flags)
            }
        } catch (e: IOException) {
        } catch (e: IllegalStateException) {
        } finally {
            streaming = false
            try { socket.close() } catch (e: IOException) { }
            drain?.join(500)
            codec?.let {
                try { it.stop() } catch (e: Exception) { }
                it.release()
            }
            current = null
            scope.launch(Dispatchers.Main) { listener.onStreamEnded() }
        }
    }

    private fun createDecoder(surface: Surface): MediaCodec {
        val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, 1920, 1080).apply {
            setInteger(MediaFormat.KEY_MAX_WIDTH, 3840)
            setInteger(MediaFormat.KEY_MAX_HEIGHT, 2160)
            setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 4 * 1024 * 1024)
            setInteger(MediaFormat.KEY_PRIORITY, 0) // 0 = реално време
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            }
        }
        return MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC).apply {
            configure(format, surface, null, 0)
            start()
        }
    }

    private fun queue(codec: MediaCodec, data: ByteArray, len: Int, flags: Int) {
        var index: Int
        do {
            index = codec.dequeueInputBuffer(10_000)
        } while (index < 0 && streaming)
        if (index < 0) return

        val ib = codec.getInputBuffer(index)!!
        ib.clear()
        ib.put(data, 0, len)

        var f = 0
        if (flags and Protocol.VIDEO_FLAG_CONFIG != 0) f = f or MediaCodec.BUFFER_FLAG_CODEC_CONFIG
        if (flags and Protocol.VIDEO_FLAG_KEY != 0) f = f or MediaCodec.BUFFER_FLAG_KEY_FRAME
        codec.queueInputBuffer(index, 0, len, System.nanoTime() / 1000, f)
    }

    // Отделна нишка вади декодираните кадри и ги показва ВЕДНАГА (render = true)
    private fun startDrain(codec: MediaCodec): Thread = Thread({
        val info = MediaCodec.BufferInfo()
        try {
            while (streaming) {
                val idx = codec.dequeueOutputBuffer(info, 10_000)
                if (idx >= 0) codec.releaseOutputBuffer(idx, true)
            }
        } catch (e: IllegalStateException) { }
    }, "ekranix-decoder-out").apply {
        priority = Thread.MAX_PRIORITY
        start()
    }

    private fun waitForSurface(): Surface? {
        repeat(60) {
            surface?.let { if (it.isValid) return it }
            Thread.sleep(50)
        }
        return null
    }
}