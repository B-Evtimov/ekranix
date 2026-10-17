package com.evtimov.ekranix

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import org.json.JSONException
import org.json.JSONObject
import java.io.BufferedReader
import java.io.BufferedWriter
import java.io.IOException
import java.io.InputStreamReader
import java.io.OutputStreamWriter
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.security.MessageDigest
import java.security.SecureRandom
import java.net.InetAddress

class ControlServer(
    private val scope: CoroutineScope,
    private val tvName: String,
    private val listener: Listener,
) {
    interface Listener {
        fun onCodeChanged(code: String)
        fun onPaired(pcName: String)
        fun onDisconnected(reason: String)
    }

    companion object {
        private const val MAX_FAILED_ATTEMPTS = 5
        private const val PAIR_DEADLINE_MS = 5_000L
        private const val PEER_TIMEOUT_MS = 3_000L
        private const val PING_INTERVAL_MS = 1_000L
    }

    private val random = SecureRandom()
    private var serverSocket: ServerSocket? = null
    private var acceptJob: Job? = null
    @Volatile private var session: Session? = null
    @Volatile private var code = ""
    @Volatile private var failedAttempts = 0

    @Volatile var pairedAddress: InetAddress? = null
        private set


    fun start() {
        newCode()
        acceptJob = scope.launch(Dispatchers.IO) {
            val ss = try {
                ServerSocket().apply {
                    reuseAddress = true
                    bind(InetSocketAddress(Protocol.CONTROL_PORT))
                }
            } catch (e: IOException) {
                return@launch
            }
            serverSocket = ss
            while (isActive) {
                val client = try { ss.accept() } catch (e: IOException) { break }
                if (session != null) reject(client) else runSession(Session(client))
            }
        }
    }

    fun stop() {
        try { serverSocket?.close() } catch (e: IOException) { }
        acceptJob?.cancel()
        session?.close()
        session = null
    }

    /** Спиране от страна на телевизора (бутон НАЗАД). */
    fun stopSession() {
        val s = session ?: return
        scope.launch(Dispatchers.IO) {
            s.send(JSONObject().put("type", "stop"))
            s.close()
        }
    }

    private fun reject(client: Socket) {
        scope.launch(Dispatchers.IO) {
            val s = Session(client)
            s.send(JSONObject().put("type", "busy"))
            s.close()
        }
    }

    private fun runSession(s: Session) {
        session = s
        scope.launch(Dispatchers.IO) {
            // Пазач: срок за сдвояване + heartbeat след това
            val watchdog = launch {
                while (isActive) {
                    delay(PING_INTERVAL_MS)
                    val now = System.currentTimeMillis()
                    if (!s.paired && now - s.createdAt > PAIR_DEADLINE_MS) { s.close(); break }
                    if (s.paired) {
                        if (now - s.lastSeen > PEER_TIMEOUT_MS) { s.close(); break }
                        if (!s.send(JSONObject().put("type", "ping"))) break
                    }
                }
            }

            var reason = "Връзката прекъсна"
            try {
                while (isActive && !s.closed) {
                    val line = s.reader.readLine() ?: break
                    s.lastSeen = System.currentTimeMillis()
                    val msg = try { JSONObject(line) } catch (e: JSONException) { continue }
                    when (msg.optString("type")) {
                        "pair" -> handlePair(s, msg)
                        "stop" -> { reason = "Компютърът спря връзката"; break }
                    }
                }
            } catch (e: IOException) { }

            watchdog.cancel()
            endSession(s, reason)
        }
    }

    private fun handlePair(s: Session, msg: JSONObject) {
        if (s.paired) return
        val given = msg.optString("code").toByteArray()
        // Сравнение с постоянно време: не издава колко цифри са верни
        if (MessageDigest.isEqual(given, code.toByteArray())) {
            failedAttempts = 0
            s.paired = true
            pairedAddress = s.address
            s.pcName = msg.optString("pcName", "Компютър")
            s.send(JSONObject().put("type", "pair_ok").put("name", tvName))
            scope.launch(Dispatchers.Main) { listener.onPaired(s.pcName) }
        } else {
            failedAttempts++
            val left = MAX_FAILED_ATTEMPTS - failedAttempts
            if (left <= 0) newCode()
            s.send(JSONObject().put("type", "pair_fail").put("attemptsLeft", maxOf(left, 0)))
            s.close()
        }
    }

    private fun endSession(s: Session, reason: String) {
        s.close()
        if (session === s) session = null
        if (s.paired) {
            pairedAddress = null
            newCode()
            scope.launch(Dispatchers.Main) { listener.onDisconnected(reason) }
        }
    }

    private fun newCode() {
        failedAttempts = 0
        val c = random.nextInt(1_000_000).toString().padStart(6, '0')
        code = c
        scope.launch(Dispatchers.Main) { listener.onCodeChanged(c) }
    }

    private class Session(private val socket: Socket) {
        val reader = BufferedReader(InputStreamReader(socket.getInputStream(), Charsets.UTF_8))
        private val writer = BufferedWriter(OutputStreamWriter(socket.getOutputStream(), Charsets.UTF_8))
        val createdAt = System.currentTimeMillis()
        @Volatile var lastSeen = createdAt
        @Volatile var paired = false
        @Volatile var closed = false
        var pcName = ""
        val address: InetAddress = socket.inetAddress

        init {
            socket.tcpNoDelay = true
        }

        @Synchronized
        fun send(msg: JSONObject): Boolean = try {
            writer.write(msg.toString())
            writer.write("\n")
            writer.flush()
            true
        } catch (e: IOException) {
            close()
            false
        }

        fun close() {
            closed = true
            try { socket.close() } catch (e: IOException) { }
        }
    }
}