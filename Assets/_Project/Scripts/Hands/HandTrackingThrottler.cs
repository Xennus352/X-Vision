using UnityEngine;
using CardboardXR.Stereo;

namespace CardboardXR.Hands
{
    // Slows the BlazeHand landmarker down as the device heats up, so the app doesn't throttle the
    // CPU (and with it the frame rate) sooner than it must. Runs both hand models constantly while
    // a hand is in view; the landmarker runs on every image by default, which is the largest
    // recurring CPU/heat cost in this project. On a thermal tier rise it moves the interval to
    // m_FairSeconds (1), m_SeriousSeconds (2) or m_CriticalSeconds (3); when the state settles back,
    // it restores whatever the tracker was configured with (the "handHz" launch option or its
    // inspector default), so an explicit rate is never exceeded by this. Rising back needs the
    // state to stay cool for m_CoolDwellSeconds so a borderline phone doesn't oscillate.
    // Reads the device's thermal level once a second (ThermalState); does nothing where the
    // platform can't say. Logs [Hands]. Put it on the XR Origin with the HandTracker; the
    // builder wires it up (StereoBuilder).
    public class HandTrackingThrottler : MonoBehaviour
    {
        const float k_CheckSeconds = 1f;

        [Tooltip("The tracker to throttle; found on this object, its parents and children if unset.")]
        [SerializeField] HandTracker m_Tracker;
        [Tooltip("Landmarker interval at tier 1 (fair / moderate heat), in seconds.")]
        [SerializeField] float m_FairSeconds = 1f / 30f;
        [Tooltip("Landmarker interval at tier 2 (serious / severe heat), in seconds.")]
        [SerializeField] float m_SeriousSeconds = 1f / 24f;
        [Tooltip("Landmarker interval at tier 3 (critical heat), in seconds.")]
        [SerializeField] float m_CriticalSeconds = 1f / 15f;
        [Tooltip("How long the level must stay hot before the rate drops, in seconds.")]
        [SerializeField] float m_HotDwellSeconds = 2f;
        [Tooltip("How long the level must stay cool before the rate rises again, in seconds.")]
        [SerializeField] float m_CoolDwellSeconds = 10f;

        float m_NextCheck;
        float m_Baseline;
        int m_Tier;
        float m_HotSince = -1f;
        float m_CoolSince = -1f;

        void Start()
        {
            if (m_Tracker == null)
                m_Tracker = GetComponent<HandTracker>() ?? GetComponentInParent<HandTracker>() ?? GetComponentInChildren<HandTracker>();
            if (m_Tracker == null)
            {
                Debug.LogError("[Hands] No HandTracker to throttle");
                enabled = false;
                return;
            }
            // Configured in the tracker's OnEnable (launch option) before any Start runs.
            m_Baseline = m_Tracker.TrackInterval;
        }

        void Update()
        {
            if (Time.unscaledTime < m_NextCheck)
                return;
            m_NextCheck = Time.unscaledTime + k_CheckSeconds;
            var tier = ThermalState.Level;
            if (tier < 0 || tier == m_Tier)
            {
                if (tier == m_Tier)
                    m_HotSince = m_CoolSince = -1f;
                return;
            }
            var now = Time.unscaledTime;
            if (tier > m_Tier)
            {
                if (m_HotSince < 0f)
                    m_HotSince = now;
                if (now - m_HotSince < m_HotDwellSeconds)
                    return;
                m_Tier = tier;
                m_HotSince = -1f;
                m_CoolSince = -1f;
            }
            else
            {
                if (m_CoolSince < 0f)
                    m_CoolSince = now;
                if (now - m_CoolSince < m_CoolDwellSeconds)
                    return;
                m_Tier = tier;
                m_CoolSince = -1f;
                m_HotSince = -1f;
            }
            Apply();
        }

        // The seconds for a tier: tier seconds, but never slower than or faster than the baseline
        // configured on the tracker (Max keeps the configured rate as the floor, so a user who
        // chose 10 Hz keeps it, and the thermal states never ask for more than 60 Hz).
        void Apply()
        {
            var tierSeconds = m_Tier switch { 1 => m_FairSeconds, 2 => m_SeriousSeconds, 3 => m_CriticalSeconds, _ => 0f };
            var interval = m_Tier == 0 ? m_Baseline
                : m_Baseline <= 0f ? tierSeconds
                : Mathf.Max(m_Baseline, tierSeconds);
            m_Tracker.SetTrackInterval(interval);
            var rate = interval <= 0f ? "every image" : $"{1f / interval:F0} Hz";
            Debug.Log($"[Hands] Thermal {ThermalState.Name}: BlazeHand landmarker at {rate}");
        }
    }
}
