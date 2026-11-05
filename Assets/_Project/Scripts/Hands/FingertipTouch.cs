using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.Hands;

namespace CardboardXR.Hands
{
    // A fingertip as a direct pointer: when an index fingertip gets within m_HoverDistance of a
    // target's surface it glows softly (hover); within m_TouchDistance it's a touch — full glow, and
    // events fire. "Surface" means a sphere collider exactly (otherwise the closest point of the
    // collider's bounds, or of the renderers when there's no collider at all — good enough for
    // planets and buttons authored as primitives). Haptic feedback is a hook, not built in: hook
    // m_TouchChanged to a sound or (on iPhone) a CoreHaptics pulse; visionOS has no hand haptics, so
    // sound plus the glow is the feedback there. Hover never selects; touch does whatever you wire.
    // Targets are transforms whose colliders (else renderers) are the touchable surfaces. If the
    // list is empty, every child with a collider — or with a renderer when there are none — is one.
    // The index fingertip is read from XR Hands, same as the glove; poses come in the subsystem's
    // tracking space and m_TrackingSpace maps them to world (the XR Origin's camera floor offset,
    // auto-found if unset). Logs [Touch]. Put it on the object whose children are touchable.
    public class FingertipTouch : MonoBehaviour
    {
        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int k_Color = Shader.PropertyToID("_Color");
        static readonly int k_EmissionColor = Shader.PropertyToID("_EmissionColor");

        [Tooltip("Touchable surfaces: their colliders (else renderers). Empty: every child with a collider, else a renderer.")]
        [SerializeField] List<Transform> m_Targets;
        [Tooltip("The space XR Hands' poses are relative to: the XR Origin's camera floor offset. Auto-found if unset; the poses are then world poses.")]
        [SerializeField] Transform m_TrackingSpace;
        [Tooltip("An index fingertip within this distance of the surface, in meters, counts as a touch.")]
        [SerializeField, Range(0.005f, 0.1f)] float m_TouchDistance = 0.03f;
        [Tooltip("A soft hover begins at this distance, in meters.")]
        [SerializeField, Range(0.02f, 0.3f)] float m_HoverDistance = 0.08f;
        [Tooltip("Tint while hovered, blended by m_TintAmount.")]
        [SerializeField] Color m_HoverTint = new(1f, 0.9f, 0.6f, 1f);
        [Tooltip("Tint while touched, blended by m_TintAmount.")]
        [SerializeField] Color m_TouchTint = new(1f, 0.8f, 0.4f, 1f);
        [Tooltip("Glow added to the emission color while touched.")]
        [SerializeField] Color m_TouchEmission = new(1f, 0.55f, 0.1f, 1f);
        [Tooltip("Multiplies the emission glow; URP Lit goes past 1 for a real bloom.")]
        [SerializeField, Range(0f, 8f)] float m_GlowStrength = 2.5f;
        [Tooltip("How much of the tint goes into the base color at full intensity.")]
        [SerializeField, Range(0f, 1f)] float m_TintAmount = 0.3f;
        [Tooltip("Time constant, in seconds, for the glow fading in and out.")]
        [SerializeField] float m_EaseSeconds = 0.12f;
        [Tooltip("Called with true on touch begin and false on end, e.g. to play a haptic or a sound cue.")]
        [SerializeField] UnityEvent<Transform, bool> m_TouchChanged;

        readonly List<TargetVisual> m_Visuals = new();

        /// <summary>True while any target is touched.</summary>
        public bool IsTouching { get; private set; }

        /// <summary>The closest target an index fingertip is on right now, or null.</summary>
        public Transform Touched { get; private set; }

        /// <summary>Raised on every touch begin/end: the target and whether it's touched.</summary>
        public event Action<Transform, bool> TouchChanged;

        /// <summary>Raised on every hover enter/exit (beyond the touch band): the target and whether it's hovered.</summary>
        public event Action<Transform, bool> HoverChanged;

        void Awake()
        {
            if (m_TrackingSpace == null)
            {
                var origin = FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
                m_TrackingSpace = origin != null && origin.CameraFloorOffsetObject != null
                    ? origin.CameraFloorOffsetObject.transform
                    : null;
            }
        }

        void Start() => CollectTargets();

        void OnDisable()
        {
            foreach (var visual in m_Visuals)
            {
                if (visual.WasApplied)
                    ApplyGlow(visual, 0f, 0f);
                visual.WasTouching = false;
                visual.WasHovered = false;
                visual.WasApplied = false;
            }
            IsTouching = false;
            Touched = null;
        }

        void Update()
        {
            var dt = Mathf.Clamp(Time.deltaTime, 1e-5f, 0.1f);
            var fade = 1f - Mathf.Exp(-dt / Mathf.Max(1e-4f, m_EaseSeconds));
            var subsystem = HandSubsystemLocator.Get();
            var left = subsystem == null ? default : subsystem.leftHand;
            var right = subsystem == null ? default : subsystem.rightHand;
            var leftTip = FingertipWorld(left);
            var rightTip = FingertipWorld(right);
            Transform closestTouched = null;
            var closestTouchedDistance = m_TouchDistance;
            var anyTouched = false;

            foreach (var visual in m_Visuals)
            {
                var distance = float.PositiveInfinity;
                if (leftTip.HasValue)
                    distance = Mathf.Min(distance, SurfaceDistance(visual, leftTip.Value));
                if (rightTip.HasValue)
                    distance = Mathf.Min(distance, SurfaceDistance(visual, rightTip.Value));

                var touching = distance <= m_TouchDistance;
                var hovering = distance <= m_HoverDistance;
                if (touching != visual.WasTouching)
                {
                    visual.WasTouching = touching;
                    TouchChanged?.Invoke(visual.Root, touching);
                    m_TouchChanged?.Invoke(visual.Root, touching);
                }
                if (hovering != visual.WasHovered)
                {
                    visual.WasHovered = hovering;
                    HoverChanged?.Invoke(visual.Root, hovering);
                }
                visual.TouchIntensity = Mathf.Lerp(visual.TouchIntensity, touching ? 1f : 0f, fade);
                visual.HoverIntensity = Mathf.Lerp(visual.HoverIntensity, hovering && !touching ? 1f : 0f, fade);
                if (visual.TouchIntensity > 0.001f || visual.HoverIntensity > 0.001f || visual.WasApplied)
                    ApplyGlow(visual, visual.TouchIntensity, visual.HoverIntensity);
                if (touching)
                {
                    anyTouched = true;
                    if (distance < closestTouchedDistance)
                    {
                        closestTouchedDistance = distance;
                        closestTouched = visual.Root;
                    }
                }
            }
            IsTouching = anyTouched;
            Touched = closestTouched;
        }

        // The index fingertip in world space, or null when the hand isn't tracked.
        Vector3? FingertipWorld(XRHand hand)
        {
            if (!hand.isTracked || !hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var pose))
                return null;
            return m_TrackingSpace != null ? m_TrackingSpace.TransformPoint(pose.position) : pose.position;
        }

        // Distance from a point to the target's surface: exact for sphere colliders, the closest
        // point of the bounds otherwise, of the renderers when there's no collider at all.
        static float SurfaceDistance(TargetVisual visual, Vector3 point)
        {
            var best = float.PositiveInfinity;
            for (var i = 0; i < visual.Spheres.Length; i++)
            {
                var sphere = visual.Spheres[i];
                var center = sphere.Transform.TransformPoint(sphere.Center);
                best = Mathf.Min(best, Vector3.Distance(point, center) - sphere.Radius * MaxAbsComponent(sphere.Transform.lossyScale));
            }
            foreach (var collider in visual.OtherColliders)
                best = Mathf.Min(best, Vector3.Distance(point, collider.bounds.ClosestPoint(point)));
            if (visual.OtherColliders.Length == 0 && visual.Spheres.Length == 0)
                foreach (var renderer in visual.Renderers)
                    best = Mathf.Min(best, Vector3.Distance(point, renderer.bounds.ClosestPoint(point)));
            return best;
        }

        static float MaxAbsComponent(Vector3 v) => Mathf.Max(Mathf.Max(Mathf.Abs(v.x), Mathf.Abs(v.y)), Mathf.Abs(v.z));

        void CollectTargets()
        {
            m_Visuals.Clear();
            var roots = new List<Transform>();
            if (m_Targets != null)
                foreach (var target in m_Targets)
                    if (target != null && !roots.Contains(target))
                        roots.Add(target);
            if (roots.Count == 0)
            {
                var set = new HashSet<Transform>();
                foreach (var collider in GetComponentsInChildren<Collider>(true))
                    set.Add(collider.transform);
                if (set.Count == 0)
                    foreach (var renderer in GetComponentsInChildren<Renderer>(true))
                        if (renderer.transform != transform)
                            set.Add(renderer.transform);
                roots.AddRange(set);
            }
            foreach (var root in roots)
                m_Visuals.Add(BuildTarget(root));
            Debug.Log($"[Touch] {m_Visuals.Count} touchable targets ready");
        }

        TargetVisual BuildTarget(Transform root)
        {
            var visual = new TargetVisual { Root = root };
            var spheres = new List<TargetVisual.Sphere>();
            var others = new List<Collider>();
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
            {
                if (collider is SphereCollider sphere)
                    spheres.Add(new TargetVisual.Sphere { Transform = sphere.transform, Center = sphere.center, Radius = sphere.radius });
                else
                    others.Add(collider);
            }
            visual.Spheres = spheres.ToArray();
            visual.OtherColliders = others.ToArray();
            visual.Renderers = root.GetComponentsInChildren<Renderer>(true);

            // Unity's renderer.materials already returns instances, so tinting them never touches assets.
            foreach (var renderer in visual.Renderers)
            {
                foreach (var material in renderer.materials)
                {
                    var baseId = material.HasProperty(k_BaseColor) ? k_BaseColor
                        : material.HasProperty(k_Color) ? k_Color : -1;
                    var hasEmission = material.HasProperty(k_EmissionColor);
                    if (hasEmission)
                        material.EnableKeyword("_EMISSION");
                    visual.Materials.Add(material);
                    visual.BaseIds.Add(baseId);
                    visual.BaseOriginals.Add(baseId >= 0 ? material.GetColor(baseId) : Color.white);
                    visual.HasEmission.Add(hasEmission);
                    visual.EmissionOriginals.Add(hasEmission ? material.GetColor(k_EmissionColor) : Color.black);
                }
            }
            return visual;
        }

        // Tints and glows by combined touch/hover intensity; restores the originals at zero.
        void ApplyGlow(TargetVisual visual, float touch, float hover)
        {
            var combined = Mathf.Max(touch, hover * 0.4f);
            for (var i = 0; i < visual.Materials.Count; i++)
            {
                var material = visual.Materials[i];
                var baseId = visual.BaseIds[i];
                var tint = touch >= hover * 0.4f ? m_TouchTint : m_HoverTint;
                if (baseId >= 0)
                    material.SetColor(baseId, Color.Lerp(visual.BaseOriginals[i], tint, combined * m_TintAmount));
                if (visual.HasEmission[i])
                    material.SetColor(k_EmissionColor, visual.EmissionOriginals[i] + m_TouchEmission * (touch * m_GlowStrength));
            }
            visual.WasApplied = combined > 0.001f;
        }

        sealed class TargetVisual
        {
            public struct Sphere
            {
                public Transform Transform;
                public Vector3 Center;
                public float Radius;
            }

            public Transform Root;
            public Sphere[] Spheres = Array.Empty<Sphere>();
            public Collider[] OtherColliders = Array.Empty<Collider>();
            public Renderer[] Renderers = Array.Empty<Renderer>();
            public readonly List<Material> Materials = new();
            public readonly List<int> BaseIds = new();
            public readonly List<Color> BaseOriginals = new();
            public readonly List<Color> EmissionOriginals = new();
            public readonly List<bool> HasEmission = new();
            public float TouchIntensity;
            public float HoverIntensity;
            public bool WasTouching;
            public bool WasHovered;
            public bool WasApplied;
        }
    }
}
