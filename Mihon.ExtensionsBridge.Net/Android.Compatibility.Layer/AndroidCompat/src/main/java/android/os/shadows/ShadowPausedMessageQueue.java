package android.os.shadows;
// package org.robolectric.shadows;
//  and badly gutted

import android.os.Looper;
import android.os.Message;
import android.os.MessageQueue;
import android.os.MessageQueue.IdleHandler;
import android.os.SystemClock;
import android.util.Log;
import java.time.Duration;
import java.util.ArrayList;

/**
 * The shadow {@link} MessageQueue} for {@link LooperMode.Mode.PAUSED}
 *
 * <p>This class should not be referenced directly. Use {@link ShadowMessageQueue} instead.
 */
@SuppressWarnings("SynchronizeOnNonFinalField")
public class ShadowPausedMessageQueue {

  // just use this class as the native object
  private static NativeObjRegistry<ShadowPausedMessageQueue> nativeQueueRegistry =
      new NativeObjRegistry<ShadowPausedMessageQueue>(ShadowPausedMessageQueue.class);
  private boolean isPolling = false;
  /**
   * A wake that arrived while the looper was not waiting. Android's native
   * wake writes an eventfd, so a wake sent just before the looper parks is
   * still there when it does; a bare notifyAll() is lost. The lock-free
   * MessageQueue only wakes a parked looper once (later posts see the stack
   * already marked woken), so one lost wake left the main looper asleep for
   * good: every WebView after that timed out ("WebView could not be created",
   * auto-buy failing from 2026-11-06 on).
   */
  private boolean wakePending = false;
  private Exception uncaughtException = null;

  // shadow constructor instead of nativeInit because nativeInit signature has changed across SDK
  // versions
  public static long nativeInit() {
    return nativeQueueRegistry.register(new ShadowPausedMessageQueue());
  }

  public static void nativeDestroy(long ptr) {
    nativeQueueRegistry.unregister(ptr);
  }

  public static void nativePollOnce(long ptr, int timeoutMillis) {
    ShadowPausedMessageQueue obj = nativeQueueRegistry.getNativeObject(ptr);
    obj.nativePollOnce(timeoutMillis);
  }

  public void nativePollOnce(int timeoutMillis) {
    synchronized (this) {
      if (wakePending || timeoutMillis == 0) {
        wakePending = false;
        return;
      }
      isPolling = true;
      try {
        if (timeoutMillis < 0) {
          this.wait();
        } else {
          this.wait(timeoutMillis);
        }
      } catch (InterruptedException e) {
        // ignore
      }
      isPolling = false;
      wakePending = false;
    }
  }

  public static void nativeWake(long ptr) {
    ShadowPausedMessageQueue obj = nativeQueueRegistry.getNativeObject(ptr);
    synchronized (obj) {
      obj.wakePending = true;
      obj.notifyAll();
    }
  }

  public static boolean nativeIsPolling(long ptr) {
    return nativeQueueRegistry.getNativeObject(ptr).isPolling;
  }
}

