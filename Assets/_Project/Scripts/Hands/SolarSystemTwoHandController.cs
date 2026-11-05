using System;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Hands;

namespace CardboardXR.Hands
{
    // Apple Vision Pro-style direct interactions for a 3D solar system, from Unity's XR Hands
    // subsystem: the joints each hand is tracked with, which both the OpenXR provider and this
    // project's camera-tracked hand feed (HandSubsystemFeed) report through XRHandSubsystem.
    //   - Two hands pinched pull the active object apart: the distance between the two pinch points
    //     scales it (pinch out to grow, in to shrink), and the turn of the line between them pitches,
    //     yaws and rolls it, following both hands in 3D.
    //   - One hand pinched drags the object, carried at the point between the thumb and index
    //     fingertips, held at the same offset it was grabbed with.
    //   - An index fingertip within m_TouchDistance of a planet's surface highlights it (a warm glow
    //     and tint, eased in and out), which is also how a planet is "touched".
    // The active object is the selected planet while one is selected, otherwise the solar system root
    // (m_Target). A one-hand grab that starts within m_GrabRadius of a planet grabs and selects it;
    // a grab that starts anywhere else drags (and deselects) the whole system. Two-hand gestures act
    // on the active object. Touching only highlights; it never selects on its own.
    // Joint poses come in the subsystem's tracking space; assign the XR Origin's camera floor offset
    // as m_TrackingSpace (auto-found when left empty) to use them in world space. Put this on an
    // empty in the scene, or on the solar system root itself to drive it directly. Planets are the
    // children of m_Target whose colliders (else their renderers) approximate their surfaces, or an
    // explicit list in m_Planets. Logs [SolarSystem].
    public class SolarSystemTwoHandController : MonoBehaviour
    {
        // How often to look for a running hand subsystem while there isn't one.
        const float k_RequerySeconds = 1f;
        // A chord that barely exists when a two-hand gesture starts would divide by it.
        const float k_MinChordLength = 0.01f;
        // How far the active object's rotation may jump per frame before it's held still for that one
        // frame: one bad tracking frame shouldn't snap a planet around.
        const float k_MaxRotationStepDegrees = 60f;

        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int k_Color = Shader.PropertyToID("_Color");
        static readonly int k_EmissionColor = Shader.PropertyToID("_EmissionColor");

        enum GrabMode { Idle, OneHand, TwoHand }

        [Header("Target")]
        [Tooltip("The solar system root to move, rotate and scale. If unset, this object.")]
        [SerializeField] Transform m_Target;
        [Tooltip("The space XR Hands' joint poses are relative to: the XR Origin's camera floor " +
                 "offset. Auto-found if unset; the joints are then world poses.")]
        [SerializeField] Transform m_TrackingSpace;
        [Tooltip("The planets to touch, select and scale individually. If empty, collected under " +
                 "m_Target: children with colliders, or renderers when there are none.")]
        [SerializeField] List<Transform> m_Planets;

        [Header("Touch")]
        [Tooltip("An index fingertip this close to a planet's surface, in meters, highlights it.")]
        [SerializeField, Range(0.01f, 0.1f)] float m_TouchDistance = 0.03f;
        [Tooltip("Planet tint while touched, blended in by m_TintAmount.")]
        [SerializeField] Color m_TouchTint = new(1f, 0.85f, 0.45f, 1f);
        [Tooltip("Glow added to the emission color while touched.")]
        [SerializeField] Color m_TouchEmission = new(1f, 0.55f, 0.1f, 1f);
        [Tooltip("Multiplies the emission glow; URP Lit goes past 1 for a real bloom.")]
        [SerializeField, Range(0f, 8f)] float m_GlowStrength = 2.2f;
        [Tooltip("How much of the touch tint goes into a planet's base color at full touch.")]
        [SerializeField, Range(0f, 1f)] float m_TintAmount = 0.35f;
        [Tooltip("Time constant, in seconds, for the glow fading in and out.")]
        [SerializeField] float m_TouchEase = 0.12f;

        [Header("Pinch")]
        [Tooltip("Fingertips closer than this, in meters, start a pinch.")]
        [SerializeField, Range(0.02f, 0.06f)] float m_PinchDistance = 0.035f;
        [Tooltip("Fingertips farther apart than this, in meters, end a pinch. Between the two the " +
                 "pinch holds, so it doesn't flicker near the threshold.")]
        [SerializeField, Range(0.03f, 0.09f)] float m_ReleaseDistance = 0.055f;

        [Header("Grab")]
        [Tooltip("A grab starting this close to a planet's surface, in meters, grabs and selects " +
                 "the planet instead of the whole system.")]
        [SerializeField, Range(0.02f, 0.15f)] float m_GrabRadius = 0.06f;
        [Tooltip("Grabbing a planet selects it; grabbing empty space clears the selection.")]
        [SerializeField] bool m_GrabSelectsPlanet = true;
        [Tooltip("Also carry the active object with the midpoint of the two pinch points while " +
                 "zooming/rotating, as Vision Pro does.")]
        [SerializeField] bool m_MoveWhileTwoHanded = true;

        [Header("Zoom")]
        [Tooltip("Smallest scale factor of a gesture (pinch all the way in).")]
        [SerializeField, Range(0.05f, 1f)] float m_MinZoom = 0.25f;
        [Tooltip("Largest scale factor of a gesture (pinch all the way out).")]
        [SerializeField, Range(1f, 8f)] float m_MaxZoom = 4f;

        [Header("Smoothing")]
        [Tooltip("Time constant, in seconds, for position following the hands.")]
        [SerializeField] float m_PositionEase = 0.12f;
        [Tooltip("Time constant, in seconds, for rotation following the hands.")]
        [SerializeField] float m_RotationEase = 0.12f;
        [Tooltip("Time constant, in seconds, for scale following the hands.")]
        [SerializeField] float m_ScaleEase = 0.12f;

        // The planets, each with its surface approximation and glow state.
        readonly List<PlanetVisual> m_Visuals = new();

        readonly List<XRHandSubsystem> m_Subsystems = new();
        XRHandSubsystem m_Subsystem;
        float m_NextQuery;
        bool m_LoggedNoSubsystem;

        // One pinch gate per hand, so the pinch holds between the close and release distances.
        PinchGate m_LeftPinch;
        PinchGate m_RightPinch;

        GrabMode m_Mode = GrabMode.Idle;
        // The object under the current gesture (a grabbed planet or the system), its transform on
        // the gesture's first frame, and what the hands were doing then.
        Transform m_Active;
        Vector3 m_StartPosition;
        Quaternion m_StartRotation;
        Vector3 m_StartLocalScale;
        Vector3 m_StartMidpoint;
        float m_StartChordLength;
        Quaternion m_StartChordFrame;
        // One-hand drag: which hand, and where the object sat relative to the pinch point at grab.
        bool m_OneHandIsLeft;
        Vector3 m_GrabOffset;
        // The rotation last applied, to hold rotation still for a frame on an implausible jump.
        Quaternion m_LastDesiredRotation;
        bool m_HasLastDesiredRotation;

        /// <summary>The selected planet two-hand gestures act on, or null while the system is selected.</summary>
        public Transform SelectedPlanet { get; private set; }

        /// <summary>The planet an index fingertip is on right now (the closest one), or null.</summary>
        public Transform TouchedPlanet { get; private set; }

        /// <summary>True while one hand is dragging the active object.</summary>
        public bool IsDragging => m_Mode == GrabMode.OneHand;

        /// <summary>True while both hands are pinched and zooming/rotating the active object.</summary>
        public bool IsTwoHanding => m_Mode == GrabMode.TwoHand;

        /// <summary>Raised when the selection changes: the new planet, or null when it's cleared.</summary>
        public event Action<Transform> SelectionChanged;

        /// <summary>Raised when a planet's touch begins or ends: the planet and whether it's touched now.</summary>
        public event Action<Transform, bool> TouchChanged;

        void Awake()
        {
            if (m_Target == null)
                m_Target = transform;
            m_LeftPinch = new PinchGate(m_PinchDistance, m_ReleaseDistance);
            m_RightPinch = new PinchGate(m_PinchDistance, m_ReleaseDistance);
            if (m_TrackingSpace == null)
            {
                var origin = FindAnyObjectByType<XROrigin>();
                // XR Hands' poses are relative to the tracking origin; the camera floor offset is
                // where this project's feed puts them, and identity elsewhere.
                m_TrackingSpace = origin != null && origin.CameraFloorOffsetObject != null
                    ? origin.CameraFloorOffsetObject.transform
                    : null;
            }
        }

        void Start()
        {
            CollectPlanets();
        }

        void OnEnable()
        {
            m_NextQuery = 0f;
            m_LoggedNoSubsystem = false;
        }

        void OnDisable()
        {
            EndGesture();
            m_LeftPinch?.Reset();
            m_RightPinch?.Reset();
            // Don't leave a planet glowing after the component is off.
            foreach (var visual in m_Visuals)
            {
                visual.Intensity = 0f;
                if (visual.WasApplied)
                    ApplyGlow(visual, 0f);
                visual.WasTouching = false;
                visual.WasApplied = false;
            }
            if (TouchedPlanet != null)
            {
                TouchChanged?.Invoke(TouchedPlanet, false);
                TouchedPlanet = null;
            }
        }

        void Update()
        {
            var dt = Mathf.Clamp(Time.deltaTime, 1e-5f, 0.1f);
            ResolveSubsystem();
            var left = ReadHand(Handedness.Left, m_LeftPinch);
            var right = ReadHand(Handedness.Right, m_RightPinch);
            UpdateTouches(left, right, dt);
            UpdateGestures(left, right, dt);
        }

        void OnDrawGizmosSelected()
        {
            if (!Application.isPlaying)
                return;
            DrawHandGizmo(Handedness.Left, m_LeftPinch);
            DrawHandGizmo(Handedness.Right, m_RightPinch);
        }

        // Selects a planet for two-hand gestures to act on (null selects the solar system).
        public void SelectPlanet(Transform planet)
        {
            if (SelectedPlanet == planet)
                return;
            SelectedPlanet = planet;
            SelectionChanged?.Invoke(planet);
            Debug.Log(planet != null ? $"[SolarSystem] Selected {planet.name}" : "[SolarSystem] Selection cleared");
        }

        // Back to acting on the whole solar system.
        public void ClearSelection() => SelectPlanet(null);

        // The object gestures act on: the selected planet while it's still around, else the system.
        Transform ActiveObject => SelectedPlanet != null ? SelectedPlanet : m_Target;

        void ResolveSubsystem()
        {
            if (m_Subsystem != null && m_Subsystem.running)
                return;
            m_Subsystem = null;
            if (Time.unscaledTime < m_NextQuery)
                return;
            m_NextQuery = Time.unscaledTime + k_RequerySeconds;
            m_Subsystems.Clear();
            SubsystemManager.GetSubsystems(m_Subsystems);
            foreach (var subsystem in m_Subsystems)
            {
                if (!subsystem.running)
                    continue;
                m_Subsystem = subsystem;
                m_LoggedNoSubsystem = false;
                Debug.Log("[SolarSystem] Found a running XR Hands subsystem");
                return;
            }
            if (!m_LoggedNoSubsystem)
            {
                m_LoggedNoSubsystem = true;
                Debug.LogWarning("[SolarSystem] No running XR Hands subsystem; the solar system won't respond to hands");
            }
        }

        // Where the hand is this frame: both tips in world space, their midpoint, how far apart
        // they are, and whether the pinch gate says they're pinched. Not tracked reads all-false.
        HandState ReadHand(Handedness handedness, PinchGate gate)
        {
            var state = new HandState();
            if (m_Subsystem == null || !m_Subsystem.running)
            {
                gate.Reset();
                return state;
            }
            var hand = handedness == Handedness.Left ? m_Subsystem.leftHand : m_Subsystem.rightHand;
            if (!hand.isTracked ||
                !TryJointTip(hand, XRHandJointID.ThumbTip, out var thumb) ||
                !TryJointTip(hand, XRHandJointID.IndexTip, out var index))
            {
                gate.Reset();
                return state;
            }
            state.Tracked = true;
            state.IndexTip = index;
            state.PinchPoint = (thumb + index) * 0.5f;
            state.TipDistance = Vector3.Distance(thumb, index);
            state.Pinched = gate.Update(state.TipDistance);
            return state;
        }

        bool TryJointTip(XRHand hand, XRHandJointID id, out Vector3 worldPoint)
        {
            worldPoint = default;
            if (!hand.GetJoint(id).TryGetPose(out var pose))
                return false;
            worldPoint = m_TrackingSpace != null ? m_TrackingSpace.TransformPoint(pose.position) : pose.position;
            return true;
        }

        // An orthonormal frame for the two pinch points: x along left-to-right, as close to the
        // hint's plane as it can be. Roll, pitch and yaw all come out of the turn of this frame.
        static Quaternion ChordFrame(Vector3 left, Vector3 right, Vector3 upHint)
        {
            var x = right - left;
            if (x.sqrMagnitude < 1e-8f)
                return Quaternion.identity;
            x.Normalize();
            var z = Vector3.Cross(x, upHint);
            if (z.sqrMagnitude < 1e-8f)
                z = Vector3.Cross(x, Vector3.forward);
            z.Normalize();
            var y = Vector3.Cross(z, x);
            return Quaternion.LookRotation(z, y);
        }

        // Which gesture, if any, the hands are making, and how the active object follows it.
        void UpdateGestures(in HandState left, in HandState right, float dt)
        {
            var leftPinching = left.Tracked && left.Pinched;
            var rightPinching = right.Tracked && right.Pinched;

            if (m_Active == null && m_Mode != GrabMode.Idle)
                EndGesture();

            if (leftPinching && rightPinching)
            {
                if (m_Mode == GrabMode.TwoHand && m_Active != null)
                    HandleTwoHand(left, right, dt);
                else
                    BeginTwoHand(left, right);
                return;
            }
            if (leftPinching || rightPinching)
            {
                var hand = leftPinching ? left : right;
                var isLeft = leftPinching;
                // A drag already in progress: keep the object being carried, following this hand —
                // re-anchor if it isn't the one that started the drag, so nothing jumps.
                if (m_Mode == GrabMode.OneHand && m_Active != null)
                {
                    if (m_OneHandIsLeft != isLeft)
                    {
                        m_OneHandIsLeft = isLeft;
                        m_GrabOffset = m_Active.position - hand.PinchPoint;
                        return;
                    }
                    HandleOneHand(hand, dt);
                    return;
                }
                // One of two hands let go: carry on with the one still pinched, re-anchored to it,
                // so the object doesn't jump.
                if (m_Mode == GrabMode.TwoHand && m_Active != null)
                {
                    m_Mode = GrabMode.OneHand;
                    m_OneHandIsLeft = isLeft;
                    m_GrabOffset = m_Active.position - hand.PinchPoint;
                    return;
                }
                BeginOneHand(hand, isLeft);
                return;
            }
            if (m_Mode != GrabMode.Idle)
                EndGesture();
        }

        void BeginOneHand(in HandState hand, bool isLeft)
        {
            var planet = PickGrabTarget(hand.PinchPoint, out var grabbedPlanet);
            m_Active = planet;
            m_Mode = GrabMode.OneHand;
            m_OneHandIsLeft = isLeft;
            m_GrabOffset = m_Active.position - hand.PinchPoint;
            m_HasLastDesiredRotation = false;
            if (m_GrabSelectsPlanet)
                SelectPlanet(grabbedPlanet ? m_Active : null);
        }

        // Keep dragging with the grab offset captured at the pinch, eased toward the hand's point.
        void HandleOneHand(in HandState hand, float dt)
        {
            var t = Damp(m_PositionEase, dt);
            var desired = hand.PinchPoint + m_GrabOffset;
            m_Active.position = Vector3.Lerp(m_Active.position, desired, t);
        }

        void BeginTwoHand(in HandState left, in HandState right)
        {
            // Carrying something already (a one-hand grab)? The two-hand gesture keeps carrying the
            // same object. Otherwise it acts on the selected planet, or the system.
            m_Active = m_Mode == GrabMode.OneHand && m_Active != null ? m_Active : ActiveObject;
            m_Mode = GrabMode.TwoHand;
            m_StartPosition = m_Active.position;
            m_StartRotation = m_Active.rotation;
            m_StartLocalScale = m_Active.localScale;
            m_StartMidpoint = (left.PinchPoint + right.PinchPoint) * 0.5f;
            m_StartChordLength = Mathf.Max((right.PinchPoint - left.PinchPoint).magnitude, k_MinChordLength);
            m_StartChordFrame = ChordFrame(left.PinchPoint, right.PinchPoint, UpHint);
            m_HasLastDesiredRotation = false;
        }

        // Zoom by how far the chord has stretched since the grab, rotate by how far it has turned,
        // and (optionally) carry the pair's midpoint, all eased toward where the hands are now.
        void HandleTwoHand(in HandState left, in HandState right, float dt)
        {
            if (m_Active == null)
            {
                EndGesture();
                return;
            }
            var upHint = UpHint;
            var midpoint = (left.PinchPoint + right.PinchPoint) * 0.5f;
            var chord = right.PinchPoint - left.PinchPoint;
            var chordLength = Mathf.Max(chord.magnitude, k_MinChordLength);

            var zoom = Mathf.Clamp(chordLength / m_StartChordLength, m_MinZoom, m_MaxZoom);
            var desiredScale = m_StartLocalScale * zoom;

            var frame = ChordFrame(left.PinchPoint, right.PinchPoint, upHint);
            var desiredRotation = frame * Quaternion.Inverse(m_StartChordFrame) * m_StartRotation;
            // One implausible frame (a tracking glitch) holds rotation still rather than snapping.
            if (m_HasLastDesiredRotation && Quaternion.Angle(m_LastDesiredRotation, desiredRotation) > k_MaxRotationStepDegrees)
                desiredRotation = m_LastDesiredRotation;
            m_LastDesiredRotation = desiredRotation;
            m_HasLastDesiredRotation = true;

            var desiredPosition = m_MoveWhileTwoHanded
                ? m_StartPosition + (midpoint - m_StartMidpoint)
                : m_StartPosition;

            // Width on the chord line says nothing about the wrist's twist, so roll is applied in
            // full; there's nothing to dead-zone against.
            var positionT = Damp(m_PositionEase, dt);
            var rotationT = Damp(m_RotationEase, dt);
            var scaleT = Damp(m_ScaleEase, dt);
            m_Active.position = Vector3.Lerp(m_Active.position, desiredPosition, positionT);
            m_Active.rotation = Quaternion.Slerp(m_Active.rotation, desiredRotation, rotationT);
            m_Active.localScale = Vector3.Lerp(m_Active.localScale, desiredScale, scaleT);
        }

        void EndGesture()
        {
            m_Mode = GrabMode.Idle;
            m_Active = null;
            m_HasLastDesiredRotation = false;
        }

        // The grab that starts nearest a planet (its surface within m_GrabRadius) grabs the planet;
        // anything else grabs the solar system.
        Transform PickGrabTarget(Vector3 pinchPoint, out bool grabbedPlanet)
        {
            var best = m_GrabRadius;
            Transform bestRoot = null;
            foreach (var visual in m_Visuals)
            {
                var distance = SurfaceDistance(visual, pinchPoint);
                if (distance < best)
                {
                    best = distance;
                    bestRoot = visual.Root;
                }
            }
            grabbedPlanet = bestRoot != null;
            return bestRoot != null ? bestRoot : m_Target;
        }

        // Touch: any tracked index fingertip within m_TouchDistance of a planet's surface lights it
        // up, eased, and reports it. The closest touched planet is TouchedPlanet.
        void UpdateTouches(in HandState left, in HandState right, float dt)
        {
            var fade = Damp(m_TouchEase, dt);
            Transform closestTouched = null;
            var closestDistance = m_TouchDistance;
            foreach (var visual in m_Visuals)
            {
                var distance = float.PositiveInfinity;
                if (left.Tracked)
                    distance = Mathf.Min(distance, SurfaceDistance(visual, left.IndexTip));
                if (right.Tracked)
                    distance = Mathf.Min(distance, SurfaceDistance(visual, right.IndexTip));

                var touching = distance <= m_TouchDistance;
                if (touching != visual.WasTouching)
                {
                    visual.WasTouching = touching;
                    TouchChanged?.Invoke(visual.Root, touching);
                }
                visual.Intensity = Mathf.Lerp(visual.Intensity, touching ? 1f : 0f, fade);
                if (visual.Intensity > 0.001f || visual.WasApplied)
                    ApplyGlow(visual, visual.Intensity);
                if (touching && distance < closestDistance)
                {
                    closestDistance = distance;
                    closestTouched = visual.Root;
                }
            }
            TouchedPlanet = closestTouched;
        }

        // Distance from a point to the planet's surface: exact to a sphere collider, the closest
        // point of any other collider (or of the renderers, when the planet has no colliders).
        // Zero or less is on it; infinity when nothing approximates the planet.
        static float SurfaceDistance(PlanetVisual visual, Vector3 point)
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

        // Eases 1-normalized exponential approach: small ease = snappier follow.
        static float Damp(float easeSeconds, float dt) => 1f - Mathf.Exp(-dt / Mathf.Max(1e-4f, easeSeconds));

        Vector3 UpHint => m_TrackingSpace != null ? m_TrackingSpace.up : Vector3.up;

        // The planets: an explicit list, else every child of the target with a collider (or, when
        // there are none, every child with a renderer).
        void CollectPlanets()
        {
            m_Visuals.Clear();
            var roots = new List<Transform>();
            if (m_Planets != null)
                foreach (var planet in m_Planets)
                    if (planet != null && planet != m_Target && !roots.Contains(planet))
                        roots.Add(planet);
            if (roots.Count == 0)
            {
                var set = new HashSet<Transform>();
                foreach (var collider in m_Target.GetComponentsInChildren<Collider>(true))
                    if (collider.transform != m_Target)
                        set.Add(collider.transform);
                if (set.Count == 0)
                    foreach (var renderer in m_Target.GetComponentsInChildren<Renderer>(true))
                        if (renderer.transform != m_Target)
                            set.Add(renderer.transform);
                roots.AddRange(set);
            }
            foreach (var root in roots)
                m_Visuals.Add(BuildPlanet(root));
            Debug.Log($"[SolarSystem] {m_Visuals.Count} planets ready for touch and selection");
        }

        // Gathers the surface approximation and the materials to tint, cloning materials is not
        // needed: Unity's renderer.materials already returns instances, not the assets.
        PlanetVisual BuildPlanet(Transform root)
        {
            var visual = new PlanetVisual { Root = root };
            var spheres = new List<PlanetVisual.Sphere>();
            var others = new List<Collider>();
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
            {
                if (collider is SphereCollider sphere)
                    spheres.Add(new PlanetVisual.Sphere { Transform = sphere.transform, Center = sphere.center, Radius = sphere.radius });
                else
                    others.Add(collider);
            }
            visual.Spheres = spheres.ToArray();
            visual.OtherColliders = others.ToArray();
            visual.Renderers = root.GetComponentsInChildren<Renderer>(true);

            foreach (var renderer in visual.Renderers)
            {
                foreach (var material in renderer.materials)
                {
                    var baseId = material.HasProperty(k_BaseColor) ? k_BaseColor
                        : material.HasProperty(k_Color) ? k_Color : -1;
                    var hasEmission = material.HasProperty(k_EmissionColor);
                    // Lit materials only emit with the keyword on; enabling it is a no-op elsewhere.
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

        // Tints and glows the planet's materials by the touch intensity 0..1.
        void ApplyGlow(PlanetVisual visual, float intensity)
        {
            visual.Intensity = intensity;
            for (var i = 0; i < visual.Materials.Count; i++)
            {
                var material = visual.Materials[i];
                var baseId = visual.BaseIds[i];
                if (baseId >= 0)
                    material.SetColor(baseId, Color.Lerp(visual.BaseOriginals[i], m_TouchTint, intensity * m_TintAmount));
                if (visual.HasEmission[i])
                    material.SetColor(k_EmissionColor, visual.EmissionOriginals[i] + m_TouchEmission * (intensity * m_GlowStrength));
            }
            visual.WasApplied = intensity > 0.001f;
        }

        void DrawHandGizmo(Handedness handedness, PinchGate gate)
        {
            if (m_Subsystem == null || !m_Subsystem.running)
                return;
            var hand = handedness == Handedness.Left ? m_Subsystem.leftHand : m_Subsystem.rightHand;
            if (!hand.isTracked || !TryJointTip(hand, XRHandJointID.IndexTip, out var indexTip))
                return;
            Gizmos.color = gate.IsPinched ? Color.green : Color.cyan;
            Gizmos.DrawWireSphere(indexTip, 0.01f);
            Gizmos.DrawWireSphere(indexTip, m_TouchDistance);
        }

        // One hand's pinch with hysteresis, mirroring the tracker's PinchDetector: closer than the
        // close distance pinches, farther than the release distance lets go, and between the two the
        // state holds.
        sealed class PinchGate
        {
            readonly float m_CloseDistance;
            readonly float m_OpenDistance;

            public PinchGate(float closeDistance, float openDistance)
            {
                m_CloseDistance = Mathf.Min(closeDistance, openDistance);
                m_OpenDistance = Mathf.Max(closeDistance, openDistance);
            }

            public bool IsPinched { get; private set; }

            public bool Update(float tipDistance)
            {
                if (IsPinched ? tipDistance > m_OpenDistance : tipDistance < m_CloseDistance)
                    IsPinched = !IsPinched;
                return IsPinched;
            }

            public void Reset() => IsPinched = false;
        }

        struct HandState
        {
            public bool Tracked;
            public Vector3 IndexTip;
            public Vector3 PinchPoint;
            public float TipDistance;
            public bool Pinched;
        }

        // A planet's surface approximation, its material state for the glow, and how lit it is now.
        sealed class PlanetVisual
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
            public float Intensity;
            public bool WasTouching;
            public bool WasApplied;
        }
    }
}
