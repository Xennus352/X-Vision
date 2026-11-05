using UnityEngine;

namespace CardboardXR.Stereo
{
    // Holds the frame rate at a steady 60 fps ceiling and moves the eye render scale between
    // 80% and 100% based on the GPU's frame time. The GPU time comes from FrameTimingManager, a
    // few frames late; it needs Frame Timing Stats on (ProjectSetup enables it). A rolling average
    // smooths it. The scale steps down by m_Step once the GPU time stays past m_DownThreshold of
    // the frame budget for m_DropSeconds, and steps up again once it stays under m_UpThreshold for
    // m_RaiseSeconds; between the two watermarks nothing moves, and the limits are 0.8 and 1.0.
    // When GPU timings aren't available (the editor reports them as zero), the frame's CPU time is
    // used as a proxy for the step down only — and never for the step up, since a CPU-bound frame
    // doesn't implicate the GPU. OnDisable does nothing; the eye textures live in StereoRig.
    // Frame-rate ceiling: also re-applied here once per enable, as a backstop to StereoRig's own
    // default (the "fps" launch option may legitimately raise it, so it's not re-forced every frame).
    // Logs [Perf]. Put it on the head with StereoRig; the builder wires it up (StereoBuilder).
    public class DynamicResolutionScaler : MonoBehaviour
    {
        const int k_TimingDelayFrames = 4;
        const float k_Smoothing = 0.1f;

        [Tooltip("The rig whose eye textures get rescaled; on this object if unset.")]
        [SerializeField] StereoRig m_Rig;
        [Tooltip("Never scale below this, relative to the screen.")]
        [SerializeField, Range(0.5f, 1f)] float m_MinRenderScale = 0.8f;
        [Tooltip("How much the scale moves per step.")]
        [SerializeField, Range(0.01f, 0.25f)] float m_Step = 0.05f;
        [Tooltip("GPU frame time above this fraction of the budget (16.7 ms at 60 fps) counts as under pressure.")]
        [SerializeField, Range(0.5f, 0.95f)] float m_DownThreshold = 0.75f;
        [Tooltip("GPU frame time below this fraction of the budget counts as headroom.")]
        [SerializeField, Range(0.2f, 0.7f)] float m_UpThreshold = 0.45f;
        [Tooltip("How long the GPU must stay past the threshold before the scale steps down, in seconds.")]
        [SerializeField] float m_DropSeconds = 2f;
        [Tooltip("How long the GPU must stay below the threshold before the scale steps up, in seconds.")]
        [SerializeField] float m_RaiseSeconds = 10f;
        [Tooltip("The frame-rate ceiling to enforce for this app, in fps.")]
        [SerializeField, Range(24, 120)] int m_FrameRateCeiling = 60;

        readonly FrameTiming[] m_Timings = new FrameTiming[k_TimingDelayFrames];
        float m_SmoothedMs = -1f;
        float m_OverBudgetSince = -1f;
        float m_UnderBudgetSince = -1f;
        bool m_LoggedNoGpuTimings;

        void OnEnable()
        {
            if (Application.targetFrameRate <= 0 || Application.targetFrameRate > m_FrameRateCeiling)
                Application.targetFrameRate = m_FrameRateCeiling;
            if (m_Rig == null)
                m_Rig = GetComponent<StereoRig>() ?? GetComponentInParent<StereoRig>() ?? GetComponentInChildren<StereoRig>();
        }

        void Update()
        {
            FrameTimingManager.CaptureFrameTimings();
            var budgetMs = 1000f / Mathf.Max(1, Application.targetFrameRate);
            var gpuMs = SampleGpuMs();
            var canRaise = gpuMs >= 0f;
            if (gpuMs < 0f && !m_LoggedNoGpuTimings)
            {
                m_LoggedNoGpuTimings = true;
                Debug.Log("[Perf] No GPU frame timings; resolution only steps down, using CPU frame time as the proxy");
            }
            var pressure = gpuMs >= 0f ? gpuMs : Time.deltaTime * 1000f;
            if (m_SmoothedMs < 0f)
                m_SmoothedMs = pressure;
            else
                m_SmoothedMs = Mathf.Lerp(m_SmoothedMs, pressure, k_Smoothing);

            var now = Time.unscaledTime;
            if (m_SmoothedMs > budgetMs * m_DownThreshold)
            {
                if (m_OverBudgetSince < 0f)
                    m_OverBudgetSince = now;
                m_UnderBudgetSince = -1f;
                if (now - m_OverBudgetSince >= m_DropSeconds)
                {
                    Step(-1);
                    m_OverBudgetSince = now;
                }
            }
            else if (canRaise && m_SmoothedMs < budgetMs * m_UpThreshold)
            {
                if (m_UnderBudgetSince < 0f)
                    m_UnderBudgetSince = now;
                m_OverBudgetSince = -1f;
                if (now - m_UnderBudgetSince >= m_RaiseSeconds)
                {
                    Step(+1);
                    m_UnderBudgetSince = now;
                }
            }
            else
            {
                m_OverBudgetSince = -1f;
                m_UnderBudgetSince = -1f;
            }
        }

        void Step(int direction)
        {
            if (m_Rig == null)
                return;
            var current = m_Rig.RenderScale;
            var next = Mathf.Clamp(Mathf.Round((current + direction * m_Step) / m_Step) * m_Step, m_MinRenderScale, 1f);
            if (Mathf.Approximately(next, current))
                return;
            m_Rig.SetRenderScale(next);
            Debug.Log($"[Perf] GPU frametime {m_SmoothedMs:F1} ms; render scale {current:F2} -> {next:F2}");
        }

        // The GPU's average frame time over the latest timing batch, or -1 when the platform reports
        // none (the editor, or Frame Timing Stats off).
        float SampleGpuMs()
        {
            var count = (int)FrameTimingManager.GetLatestTimings((uint)m_Timings.Length, m_Timings);
            double sum = 0;
            var samples = 0;
            for (var i = 0; i < count; i++)
            {
                if (m_Timings[i].gpuFrameTime > 0)
                {
                    sum += m_Timings[i].gpuFrameTime;
                    samples++;
                }
            }
            return samples > 0 ? (float)(sum / samples) : -1f;
        }
    }
}
