using System;
using UnityEngine;

namespace CardboardXR.Stereo
{
    // What the device itself says about how hot it is, as one 0-3 level: 0 nominal, 1 fair, 2 serious,
    // 3 critical; -1 where the platform can't say. iOS reads the plugin (DeviceState, otherwise Unity's
    // iOS player would cap the frame rate silently); Android reads PowerManager's thermal status once
    // per process (getCurrentThermalStatus) and maps NONE/LIGHT to 0, MODERATE to 1, SEVERE to 2,
    // CRITICAL/EMERGENCY/SHUTDOWN to 3 — the same source PerformanceLog logs. Headroom is deliberately
    // not folded in: the level names what the OS will do about it, which is what callers act on.
    // Android only reads it when something asks (see HandTrackingThrottler, polled once a second).
    public static class ThermalState
    {
        // -1 when the platform can't say; otherwise 0 nominal, 1 fair, 2 serious, 3 critical.
        public static int Level
        {
            get
            {
                if (DeviceState.Supported)
                    return DeviceState.ThermalState switch
                    {
                        "nominal" => 0,
                        "fair" => 1,
                        "serious" => 2,
                        "critical" => 3,
                        _ => -1,
                    };
                if (Application.platform != RuntimePlatform.Android)
                    return -1;
                return AndroidLevel();
            }
        }

        public static string Name => Level switch
        {
            0 => "nominal",
            1 => "fair",
            2 => "serious",
            3 => "critical",
            _ => "unknown",
        };

#if UNITY_ANDROID && !UNITY_EDITOR
        static AndroidJavaObject s_PowerManager;
        static bool s_Tried;

        static int AndroidLevel()
        {
            if (!s_Tried)
            {
                s_Tried = true;
                try
                {
                    using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                    using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                    s_PowerManager = activity.Call<AndroidJavaObject>("getSystemService", "power");
                }
                catch (Exception e)
                {
                    Debug.Log($"[Perf] No Android power service for the thermal status: {e.Message}");
                }
            }
            if (s_PowerManager == null)
                return -1;
            try
            {
                // Android's PowerManager.THERMAL_STATUS_*, from API 29.
                return s_PowerManager.Call<int>("getCurrentThermalStatus") switch
                {
                    0 or 1 => 0,   // NONE, LIGHT
                    2 => 1,        // MODERATE
                    3 => 2,        // SEVERE
                    >= 4 => 3,     // CRITICAL, EMERGENCY, SHUTDOWN
                    _ => -1,
                };
            }
            catch (Exception e)
            {
                Debug.Log($"[Perf] Android thermal status read failed: {e.Message}");
                return -1;
            }
        }
#else
        static int AndroidLevel() => -1;
#endif
    }
}
