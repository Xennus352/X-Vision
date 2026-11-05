using System;
using System.Collections.Generic;
using CardboardXR.Stereo;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.Hands;

namespace CardboardXR.Hands
{
    // Gaze-assisted pinch, the Vision Pro indirect way: look at a planet, pinch, and it comes to your
    // gaze; drag where you look, open the fingers, and it flies.
    //   Each frame a ray from the head (HeadPoseFusion's forward, or the assigned m_Head) finds the
    //   nearest collider — the solar system's planets, which is why this sits next to the controller.
    //   A pinch on the hovered object grabs it: it is carried at the distance it was hit at, offset by
    //   where on it was hit (no pop), moved by moving your head, with an eased follow. Releasing the
    //   pinch throws it with the velocity of the smoothed carry path over the last m_ThrowWindow
    //   seconds (clamped to m_MaxThrowSpeed); a Rigidbody on the object receives it as linear velocity
    //   (gravity only with m_ThrowUnderGravity; planets drift), anything else is glided along that
    //   velocity until it slows under m_GlideStopSpeed, then stops.
    //   The hand is only the pinch button (either hand works); the drag is gaze-driven and works even
    //   while the hand is lost, but requires the hand to have been pinched to start — on losing the
    //   hand mid-grab the carry ends without a throw, rather than flinging a planet from stale data.
    //   Hovering highlights the object softly; carrying holds it with no highlight. Do not run this on
    //   the same planets as a direct-hand component that also listens to pinches (e.g.
    //   SolarSystemTwoHandController) unless you want both to react; pick one as the demo's input.
    //   Logs [Gaze]. Put it on an empty or on the solar-system root; assign m_GrabbableRoots to
    //   restrict what can be grabbed, or leave it empty to grab any collider.
    public class GazePinchGrabber : MonoBehaviour
    {
        // The < 2.5 cm pinch threshold, with the same hysteresis shape as elsewhere in the app.
        const float k_PinchDistance = 0.025f;
        const float k_ReleaseDistance = 0.045f;
        const int k_History = 12;

        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int k_Color = Shader.PropertyToID("_Color");

        [Tooltip("Where the gaze ray comes from; found (HeadPoseFusion) if unset, then the camera.")]
        [SerializeField] Transform m_Head;
        [Tooltip("Ray reach, in meters.")]
        [SerializeField] float m_MaxDistance = 20f;
        [Tooltip("Only objects under one of these roots can be grabbed; empty grabs anything with a collider.")]
        [SerializeField] List<Transform> m_GrabbableRoots = new();
        [Tooltip("Layers the gaze can hit.")]
        [SerializeField] LayerMask m_PhysicsMask = Physics.DefaultRaycastLayers;
        [Tooltip("Time constant, in seconds, for the held object easing toward the gaze.")]
        [SerializeField] float m_EaseSeconds = 0.1f;
        [Tooltip("How much of the carry, in seconds, is averaged for the release velocity.")]
        [SerializeField] float m_ThrowWindow = 0.12f;
        [Tooltip("Throws are clamped to this speed, in meters per second.")]
        [SerializeField] float m_MaxThrowSpeed = 8f;
        [Tooltip("Apply gravity to thrown objects with a Rigidbody (CubePhysics-style); off keeps planets drifting in space.")]
        [SerializeField] bool m_ThrowUnderGravity;
        [Tooltip("A tossed object with no Rigidbody glides with its throw until slower than this, then stops.")]
        [SerializeField] float m_GlideStopSpeed = 0.25f;
        [Tooltip("Throw damping, in seconds, for the glide: the glide loses about half its speed per two of these.")]
        [SerializeField] float m_GlideDampSeconds = 2f;
        [Tooltip("Soft tint while the gaze rests on a grabbable target.")]
        [SerializeField] Color m_HoverTint = new(0.55f, 0.9f, 1f, 1f);
        [Tooltip("How much of the hover tint goes into the base color.")]
        [SerializeField, Range(0f, 1f)] float m_HoverTintAmount = 0.4f;
        [Tooltip("Called with the object on grab begin, e.g. to select it in the demo.")]
        [SerializeField] UnityEvent<Transform> m_Grabbed;
        [Tooltip("Called with the object on release, e.g. to deselect it in the demo.")]
        [SerializeField] UnityEvent<Transform> m_Released;

        // Pinch gates for the two hands, mirroring the rest of the app (hysteresis, one per hand).
        readonly PinchGate m_LeftPinch = new(k_PinchDistance, k_ReleaseDistance);
        readonly PinchGate m_RightPinch = new(k_PinchDistance, k_ReleaseDistance);
        readonly RaycastHit[] m_Hits = new RaycastHit[16];
        readonly Vector3[] m_Positions = new Vector3[k_History];
        readonly float[] m_SampleTimes = new float[k_History];
        int m_SampleCursor;
        int m_SampleCount;

        Transform m_Hovered;
        TargetGlow m_HoverGlow;
        Transform m_CarriedTransform;
        Rigidbody m_CarriedBody;
        bool m_CarriedBodyWasKinematic;
        bool m_CarriedBodyUsedGravity;
        Vector3 m_GrabOffset;
        float m_GrabDepth;
        bool m_Holding;
        Vector3 m_Glide;
        Transform m_GlideTransform;

        /// <summary>The object the gaze is on right now, or null.</summary>
        public Transform Hovered => m_Hovered;

        /// <summary>The object being carried, or null.</summary>
        public Transform Carried => m_CarriedTransform;

        /// <summary>True between a pinch that starts on the hovered object and the release.</summary>
        public bool IsDragging => m_Holding;

        /// <summary>Raised on grab begin and on release: the carried object.</summary>
        public event Action<Transform> Grabbed;
        public event Action<Transform> Released;

        void Awake()
        {
            if (m_Head == null)
            {
                var fusion = FindAnyObjectByType<HeadPoseFusion>();
                m_Head = fusion != null ? fusion.transform : Camera.main != null ? Camera.main.transform : transform;
            }
        }

        void OnDisable()
        {
            EndGrab(throwIt: false);
            ClearHover();
            m_LeftPinch.Reset();
            m_RightPinch.Reset();
        }

        void Update()
        {
            var dt = Mathf.Clamp(Time.deltaTime, 1e-5f, 0.1f);
            var subsystem = HandSubsystemLocator.Get();
            var left = ReadPinch(Handedness.Left, m_LeftPinch, subsystem);
            var right = ReadPinch(Handedness.Right, m_RightPinch, subsystem);
            var anyPinch = left.Pinched || right.Pinched;
            // Without a tracked hand there is no pinch button; treat it as a cancelled grab, not a throw.
            var anyTracked = left.Tracked || right.Tracked;

            if (m_Holding)
            {
                HandleCarry(dt);
                if (!anyPinch)
                    EndGrab(throwIt: anyTracked);
                return;
            }
            if (m_GlideTransform != null)
            {
                if (m_Glide.sqrMagnitude > m_GlideStopSpeed * m_GlideStopSpeed)
                {
                    m_GlideTransform.position += m_Glide * dt;
                    m_Glide *= Mathf.Exp(-dt / m_GlideDampSeconds);
                }
                else
                {
                    m_Glide = Vector3.zero;
                    m_GlideTransform = null;
                }
                return;
            }

            var target = FindGazeTarget(out var hit);
            UpdateHover(target);
            if (anyPinch && m_Hovered != null && target != null)
                BeginGrab(target, in hit);
        }

        // The hand's pinch state this frame, and whether the hand is tracked at all.
        (bool Pinched, bool Tracked) ReadPinch(Handedness handedness, PinchGate gate, XRHandSubsystem subsystem)
        {
            if (subsystem == null)
            {
                gate.Reset();
                return (false, false);
            }
            var hand = handedness == Handedness.Left ? subsystem.leftHand : subsystem.rightHand;
            if (!hand.isTracked)
            {
                gate.Reset();
                return (false, false);
            }
            var thumb = hand.GetJoint(XRHandJointID.ThumbTip);
            var index = hand.GetJoint(XRHandJointID.IndexTip);
            if (!thumb.TryGetPose(out var a) || !index.TryGetPose(out var b))
            {
                gate.Reset();
                return (false, true);
            }
            return (gate.Update(Vector3.Distance(a.position, b.position)), true);
        }

        // The nearest collider along the gaze that belongs to a grabbable root (all of them, when the
        // list is empty). Returns null when nothing grabbable is under the ray.
        Transform FindGazeTarget(out RaycastHit hit)
        {
            hit = default;
            var ray = new Ray(m_Head.position, m_Head.forward);
            var count = Physics.RaycastNonAlloc(ray, m_Hits, m_MaxDistance, m_PhysicsMask, QueryTriggerInteraction.Ignore);
            var nearest = -1;
            for (var i = 0; i < count; i++)
            {
                if (GrabbableRoot(m_Hits[i].collider.transform) == null)
                    continue;
                if (nearest < 0 || m_Hits[i].distance < m_Hits[nearest].distance)
                    nearest = i;
            }
            if (nearest < 0)
                return null;
            hit = m_Hits[nearest];
            return GrabbableRoot(hit.collider.transform);
        }

        // The root to grab: the candidate itself, or the nearest ancestor in m_GrabbableRoots, or any
        // collider at all when the list is empty.
        Transform GrabbableRoot(Transform candidate)
        {
            if (m_GrabbableRoots.Count == 0)
                return candidate;
            for (var t = candidate; t != null; t = t.parent)
                if (m_GrabbableRoots.Contains(t))
                    return t;
            return null;
        }

        void BeginGrab(Transform target, in RaycastHit hit)
        {
            m_CarriedTransform = target;
            m_CarriedBody = target.GetComponentInChildren<Rigidbody>();
            if (m_CarriedBody != null)
            {
                // Carrying must not fight gravity or an in-progress throw; it's put back on release.
                m_CarriedBodyWasKinematic = m_CarriedBody.isKinematic;
                m_CarriedBodyUsedGravity = m_CarriedBody.useGravity;
                m_CarriedBody.isKinematic = true;
            }
            m_GrabOffset = m_CarriedTransform.position - hit.point;
            m_GrabDepth = Vector3.Dot(hit.point - m_Head.position, m_Head.forward);
            m_SampleCursor = 0;
            m_SampleCount = 0;
            m_Glide = Vector3.zero;
            m_GlideTransform = null;
            m_Holding = true;
            ClearHover();
            m_Grabbed?.Invoke(m_CarriedTransform);
            Grabbed?.Invoke(m_CarriedTransform);
            Debug.Log($"[Gaze] Grabbed {m_CarriedTransform.name} at {m_GrabDepth:F2} m along the gaze");
        }

        // The held object eases to the gaze ray at the grab depth; its carry path is sampled for the throw.
        void HandleCarry(float dt)
        {
            var t = 1f - Mathf.Exp(-dt / Mathf.Max(1e-4f, m_EaseSeconds));
            var desired = m_Head.position + m_Head.forward * m_GrabDepth + m_GrabOffset;
            m_CarriedTransform.position = Vector3.Lerp(m_CarriedTransform.position, desired, t);
            m_Positions[m_SampleCursor] = m_CarriedTransform.position;
            m_SampleTimes[m_SampleCursor] = Time.unscaledTime;
            m_SampleCursor = (m_SampleCursor + 1) % k_History;
            m_SampleCount = Mathf.Min(m_SampleCount + 1, k_History);
        }

        void EndGrab(bool throwIt)
        {
            if (!m_Holding)
                return;
            m_Holding = false;
            var carried = m_CarriedTransform;
            m_CarriedTransform = null;
            var velocity = CarryVelocity();
            if (m_CarriedBody != null)
            {
                if (throwIt && velocity.sqrMagnitude > 0.01f)
                {
                    m_CarriedBody.isKinematic = false;
                    m_CarriedBody.useGravity = m_ThrowUnderGravity;
                    m_CarriedBody.linearVelocity = velocity;
                }
                else
                {
                    m_CarriedBody.isKinematic = m_CarriedBodyWasKinematic;
                    m_CarriedBody.useGravity = m_CarriedBodyUsedGravity;
                }
            }
            else if (throwIt && velocity.sqrMagnitude > 0.01f)
            {
                m_Glide = velocity;
                m_GlideTransform = carried;
            }
            m_CarriedBody = null;
            var speed = velocity.magnitude;
            Debug.Log(throwIt ? $"[Gaze] Threw {carried.name} at {speed:F2} m/s" : $"[Gaze] Let go of {carried.name}");
            m_Released?.Invoke(carried);
            Released?.Invoke(carried);
        }

        // The carry path's velocity over the last m_ThrowWindow seconds, clamped to m_MaxThrowSpeed.
        Vector3 CarryVelocity()
        {
            var now = Time.unscaledTime;
            Vector3 newest = default, oldest = default;
            var newestAt = 0f;
            var oldestAt = float.PositiveInfinity;
            for (var i = 0; i < m_SampleCount; i++)
            {
                var at = m_SampleTimes[i];
                if (now - at > m_ThrowWindow || at <= 0f)
                    continue;
                if (at > newestAt) { newestAt = at; newest = m_Positions[i]; }
                if (at < oldestAt) { oldestAt = at; oldest = m_Positions[i]; }
            }
            if (newestAt <= oldestAt)
                return Vector3.zero;
            var velocity = (newest - oldest) / (newestAt - oldestAt);
            return velocity.magnitude > m_MaxThrowSpeed
                ? velocity.normalized * m_MaxThrowSpeed
                : velocity;
        }

        // Soft tint for the gaze target, restored when the gaze moves off it.
        void UpdateHover(Transform target)
        {
            if (target == m_Hovered)
                return;
            ClearHover();
            if (target == null)
                return;
            m_Hovered = target;
            m_HoverGlow = new TargetGlow(target);
            m_HoverGlow.Apply(m_HoverTint, m_HoverTintAmount);
        }

        void ClearHover()
        {
            m_HoverGlow?.Restore();
            m_HoverGlow = null;
            m_Hovered = null;
        }

        // A target's materials, tinted while hovered and restored afterwards. Unity's renderer.materials
        // already returns instances, so the assets are never touched.
        sealed class TargetGlow
        {
            readonly Renderer[] m_Renderers;
            readonly int[] m_BaseIds;
            readonly Color[] m_Originals;

            public TargetGlow(Transform root)
            {
                m_Renderers = root.GetComponentsInChildren<Renderer>(true);
                var baseIds = new List<int>();
                var originals = new List<Color>();
                foreach (var renderer in m_Renderers)
                {
                    foreach (var material in renderer.materials)
                    {
                        var baseId = material.HasProperty(k_BaseColor) ? k_BaseColor
                            : material.HasProperty(k_Color) ? k_Color : -1;
                        baseIds.Add(baseId);
                        originals.Add(baseId >= 0 ? material.GetColor(baseId) : Color.white);
                    }
                }
                m_BaseIds = baseIds.ToArray();
                m_Originals = originals.ToArray();
            }

            public void Apply(Color tint, float amount)
            {
                var index = 0;
                foreach (var renderer in m_Renderers)
                {
                    foreach (var material in renderer.materials)
                    {
                        var baseId = m_BaseIds[index];
                        if (baseId >= 0)
                            material.SetColor(baseId, Color.Lerp(m_Originals[index], tint, amount));
                        index++;
                    }
                }
            }

            public void Restore()
            {
                var index = 0;
                foreach (var renderer in m_Renderers)
                {
                    foreach (var material in renderer.materials)
                    {
                        var baseId = m_BaseIds[index];
                        if (baseId >= 0)
                            material.SetColor(baseId, m_Originals[index]);
                        index++;
                    }
                }
            }
        }

        // One hand's pinch with hysteresis; closes under the close distance, opens past the release
        // distance, holds between.
        sealed class PinchGate
        {
            readonly float m_CloseDistance;
            readonly float m_OpenDistance;

            public PinchGate(float closeDistance, float openDistance)
            {
                m_CloseDistance = Mathf.Min(closeDistance, openDistance);
                m_OpenDistance = Mathf.Max(closeDistance, openDistance);
            }

            public bool Update(float tipDistance)
            {
                if (IsPinched)
                {
                    if (tipDistance > m_OpenDistance)
                        IsPinched = false;
                }
                else if (tipDistance < m_CloseDistance)
                    IsPinched = true;
                return IsPinched;
            }

            public bool IsPinched { get; private set; }

            public void Reset() => IsPinched = false;
        }
    }
}
