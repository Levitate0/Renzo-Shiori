package extension.bridge.cef

import extension.bridge.logging.AndroidCompatLogger
import java.util.concurrent.atomic.AtomicBoolean
import org.cef.CefApp

object CefMessageLoopBridge {
    private val logger = AndroidCompatLogger.forClass(CefMessageLoopBridge::class.java)
    private val running = AtomicBoolean(false)
    @Volatile private var loopThread: Thread? = null

    fun start(app: CefApp) {
        if (!running.compareAndSet(false, true)) return

        loopThread =
            Thread({
                logger.info { "Starting CEF message loop" }
                try {
                    while (running.get()) {
                        // Per-iteration guard. This catch used to sit OUTSIDE the
                        // loop, so a single throwable from doMessageLoopWork ended
                        // the pump for the lifetime of the JVM — and nothing ever
                        // restarted it, because `running` stayed true so start()'s
                        // compareAndSet kept returning early.
                        //
                        // That is catastrophic rather than cosmetic: CefClient.dispose()
                        // and CefBrowser.close() are REQUESTS that only complete when
                        // the message loop delivers onBeforeClose. With the pump dead,
                        // every browser opened afterwards can never finish closing, so
                        // its jcef_helper renderer stays alive (and zombies once the
                        // child exits with nobody reaping). A dead pump turns every
                        // subsequent WebView into a permanent process leak.
                        try {
                            app.doMessageLoopWork(0L)
                        } catch (t: Throwable) {
                            logger.warn { "Error inside CEF message loop iteration: ${'$'}t" }
                        }
                        Thread.sleep(10L)
                    }
                } catch (interrupted: InterruptedException) {
                    Thread.currentThread().interrupt()
                } catch (t: Throwable) {
                    logger.warn { "CEF message loop terminated: ${'$'}t" }
                } finally {
                    // Clear the flag so a later start() can actually revive the pump
                    // instead of being swallowed by the compareAndSet guard.
                    running.set(false)
                    loopThread = null
                    logger.info { "CEF message loop stopped" }
                }
            }).apply {
                isDaemon = true
                name = "cef-message-loop"
                start()
            }
    }

    fun stop() {
        if (!running.compareAndSet(true, false)) return

        loopThread?.interrupt()
        try {
            loopThread?.join(1_000L)
        } catch (_: InterruptedException) {
            Thread.currentThread().interrupt()
        } finally {
            loopThread = null
        }
    }
}
