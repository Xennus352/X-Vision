using System;
using System.Collections.Generic;
using CardboardXR.Stereo;
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
    //   - If the pinch point isn't near any planet, the gaze is tried instead: the ray from the head
    //     picks the planet under it (any distance, the reach of m_GazeMaxDistance) and carries it
    //     along the gaze, far or near — so both grab styles work on the same planets.
    //   - Empty space is never grabbable: the whole system isn't draggable. Only the Moon and the
    //     planets are, one at a time, and a grabbed body stays wherever it's put until it's recalled.
    //   - Two-hand gestures scale and turn the selected planet; with no selection they do nothing.
    //   - Releasing a carried planet with any speed sends it gliding with the release velocity
    //     (sampled over the last m_ThrowWindow seconds, clamped to m_MaxThrowSpeed); a thrown planet
    //     drifts without gravity and slows slowly, forever in the air like a hologram.
    //   - A pinch shorter than m_TapMaxSeconds, with the object moving less than m_TapMaxMove, is a
    //     tap: it toggles that planet's selection (tapping it again clears the selection). Empty
    //     space doesn't clear it — a tap on a panel button is not a deselection; the close button
    //     on the panel does that.
    // A grabbed planet leaves its orbit pivot (it would fight the carry) and is reparented to the
    // system root; SolarSystemHologram.RecallPlanets puts it home. The active object is the selected
    // planet while one is selected, otherwise nothing moves. Two-hand gestures act on the active
    // object. Touching only highlights; it never selects on its own.
    // Joint poses come in the subsystem's tracking space; assign the XR Origin's camera floor offset
    // as m_TrackingSpace (auto-found when left empty) to use them in world space. Put this on an
    // empty in the scene, or on the solar system root itself to drive it directly. Planets are the
    // children of m_Target with colliders. Logs [SolarSystem].
    public class SolarSystemTwoHandController : MonoBehaviour
    {
        // How often to look for a running hand subsystem while there isn't one.
        const float k_RequerySeconds = 1f;
        // A chord that barely exists when a two-hand gesture starts would divide by it.
        const float k_MinChordLength = 0.01f;
        // How far the active object's rotation may jump per frame before it's held still for that one
        // frame: one bad tracking frame shouldn't snap a planet around.
        const float k_MaxRotationStepDegrees = 60f;
        // Samples of the carry path kept for the release velocity, and how often they're refreshed.
        const int k_History = 12;

        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int k_Color = Shader.PropertyToID("_Color");
        static readonly int k_EmissionColor = Shader.PropertyToID("_EmissionColor");

        enum GrabMode { Idle, OneHand, TwoHand }

        [Header("Target")]
        [Tooltip("The solar system root: grabbed planets are reparented to it while carried. If unset, this object.")]
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
                 "the planet.")]
        [SerializeField, Range(0.02f, 0.15f)] float m_GrabRadius = 0.06f;
        [Tooltip("Also carry the active object with the midpoint of the two pinch points while " +
                 "zooming/rotating, as Vision Pro does.")]
        [SerializeField] bool m_MoveWhileTwoHanded = true;
        [Tooltip("When the pinch point is nowhere near a planet, the head's gaze ray is tried " +
                 "instead: the planet under it is carried along the gaze at the distance it was " +
                 "hit at. Found (HeadPoseFusion) if unset, then the camera.")]
        [SerializeField] Transform m_Head;
        [Tooltip("Ray reach for gaze grabs, in meters.")]
        [SerializeField] float m_GazeMaxDistance = 12f;
        [Tooltip("Layers the gaze can hit for a grab.")]
        [SerializeField] LayerMask m_GazeMask = Physics.DefaultRaycastLayers;

        [Header("Zoom")]
        [Tooltip("Smallest scale factor of a gesture (pinch all the way in).")]
        [SerializeField, Range(0.05f, 1f)] float m_MinZoom = 0.25f;
        [Tooltip("Largest scale factor of a gesture (pinch all the way out).")]
        [SerializeField, Range(1f, 8f)] float m_MaxZoom = 4f;

        [Header("Throw")]
        [Tooltip("How much of the carry, in seconds, is averaged for the release velocity.")]
        [SerializeField] float m_ThrowWindow = 0.12f;
        [Tooltip("Throws are clamped to this speed, in meters per second.")]
        [SerializeField] float m_MaxThrowSpeed = 6f;
        [Tooltip("Throw damping, in seconds, for the glide: the glide loses about half its speed per two of these.")]
        [SerializeField] float m_GlideDampSeconds = 2.5f;
        [Tooltip("A thrown planet slower than this, in meters per second, stops drifting.")]
        [SerializeField] float m_GlideStopSpeed = 0.3f;

        [Header("Tap to select")]
        [Tooltip("A grab shorter than this, in seconds, with the object moving less than m_TapMaxMove, is a tap.")]
        [SerializeField] float m_TapMaxSeconds = 0.28f;
        [Tooltip("How far the object may move during a tap, in meters.")]
        [SerializeField] float m_TapMaxMove = 0.1f;

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
        // The carry path's samples, kept for the release velocity.
        readonly Vector3[] m_Positions = new Vector3[k_History];
        readonly float[] m_SampleTimes = new float[k_History];
        int m_SampleCursor;
        int m_SampleCount;
        // The grab in progress, if it came from the gaze instead of a hand on the planet.
        bool m_GazeGrab;
        float m_GrabDepth;
        // Throw bookkeeping: a carried planet released with speed glides along its release velocity.
        Transform m_GlideTransform;
        Vector3 m_Glide;
        // The tap-detection state, captured at the gesture's first frame.
        float m_GestureStartTime;
        Vector3 m_GestureStartPosition;

        GrabMode m_Mode = GrabMode.Idle;
        // The object under the current gesture (a grabbed planet), its transform on
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

        /// <summary>The selected planet two-hand gestures act on, or null while nothing is selected.</summary>
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
            if (m_Head == null)
            {
                var fusion = FindAnyObjectByType<HeadPoseFusion>();
                m_Head = fusion != null ? fusion.transform : Camera.main != null ? Camera.main.transform : null;
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
            GlideUpdate(dt);
            ResolveSubsystem();
            var left = ReadHand(Handedness.Left, m_LeftPinch);
            var right = ReadHand(Handedness.Right, m_RightPinch);
            UpdateTouches(left, right, dt);
            UpdateGestures(left, right, dt);
            if (m_Active != null)
                SampleCarryPath();
        }

        void OnDrawGizmosSelected()
        {
            if (!Application.isPlaying)
                return;
            DrawHandGizmo(Handedness.Left, m_LeftPinch);
            DrawHandGizmo(Handedness.Right, m_RightPinch);
        }

        // Selects a planet for two-hand gestures to act on (null clears the selection).
        public void SelectPlanet(Transform planet)
        {
            if (SelectedPlanet == planet)
                return;
            SelectedPlanet = planet;
            SelectionChanged?.Invoke(planet);
            Debug.Log(planet != null ? $"[SolarSystem] Selected {planet.name}" : "[SolarSystem] Selection cleared");
        }

        // Clears the selection.
        public void ClearSelection() => SelectPlanet(null);

        // Stops any throw in progress, e.g. when the planets are recalled; the body stays where it is.
        public void StopGlide()
        {
            m_Glide = Vector3.zero;
            m_GlideTransform = null;
        }

        // The object the gestures act on: the selected planet, or null with no selection — the
        // whole system isn't grabbable.
        Transform ActiveObject => SelectedPlanet;

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
            var active = PickGrabTarget(hand.PinchPoint);
            var gaze = false;
            var depth = 0f;
            Vector3 offset;
            if (active != null)
            {
                offset = active.position - hand.PinchPoint;
            }
            else if (TryGazeGrab(out var gazePlanet, out depth, out var hitPoint))
            {
                active = gazePlanet;
                offset = active.position - hitPoint;
                gaze = true;
            }
            // Empty space: no grab at all — the system stays where it is.
            else
            {
                return;
            }
            // Planets leave their orbits while carried; the orbit guides stay put.
            if (IsPlanet(active))
            {
                active.SetParent(m_Target, true);
                active.localRotation = Quaternion.identity;
            }
            m_Active = active;
            m_Mode = GrabMode.OneHand;
            m_OneHandIsLeft = isLeft;
            m_GrabOffset = offset;
            m_GazeGrab = gaze;
            m_GrabDepth = depth;
            m_GestureStartTime = Time.unscaledTime;
            m_GestureStartPosition = active.position;
            m_SampleCursor = 0;
            m_SampleCount = 0;
            m_Glide = Vector3.zero;
            m_GlideTransform = null;
            m_HasLastDesiredRotation = false;
            // Selection happens on a tap (see EndGesture), not on grab begin.
            Debug.Log(gaze
                ? $"[SolarSystem] Gaze grabbed {active.name} at {depth:F2} m along the gaze"
                : $"[SolarSystem] Grabbed {active.name}");
        }

        // Keep dragging with the grab offset captured at the pinch, eased toward the hand's point,
        // or — for a gaze grab — toward the gaze ray at the distance the object was hit at.
        void HandleOneHand(in HandState hand, float dt)
        {
            var t = Damp(m_PositionEase, dt);
            var desired = m_GazeGrab && m_Head != null
                ? m_Head.position + m_Head.forward * m_GrabDepth + m_GrabOffset
                : hand.PinchPoint + m_GrabOffset;
            m_Active.position = Vector3.Lerp(m_Active.position, desired, t);
        }

        void BeginTwoHand(in HandState left, in HandState right)
        {
            // Carrying something already (a one-hand grab)? The two-hand gesture keeps carrying the
            // same object. Otherwise it acts on the selected planet — or nothing, with no selection:
            // two hands don't move the whole system.
            m_Active = m_Mode == GrabMode.OneHand && m_Active != null ? m_Active : ActiveObject;
            if (m_Active == null)
                return;
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
            var released = m_Active;
            var tap = released != null
                && Time.unscaledTime - m_GestureStartTime < m_TapMaxSeconds
                && Vector3.Distance(released.position, m_GestureStartPosition) < m_TapMaxMove;
            m_Mode = GrabMode.Idle;
            m_Active = null;
            m_GazeGrab = false;
            m_HasLastDesiredRotation = false;
            // A planet flicked out of the hand drifts on with the release velocity; the system stays.
            if (released != null && IsPlanet(released))
            {
                var velocity = CarryVelocity();
                if (velocity.sqrMagnitude > 0.15f)
                {
                    m_GlideTransform = released;
                    m_Glide = velocity;
                    Debug.Log($"[SolarSystem] Threw {released.name} at {velocity.magnitude:F2} m/s");
                }
            }
            // A tap toggles that planet's selection; tapping empty space changes nothing (a tap on
            // a panel button must not clear it — the panel's close button does).
            if (tap && IsPlanet(released))
                SelectPlanet(SelectedPlanet == released ? null : released);
        }

        // A planet is anything in the collected set that isn't the system root (the sun carries no
        // collider, so it's never collected).
        bool IsPlanet(Transform t)
        {
            if (t == null || t == m_Target)
                return false;
            for (var i = 0; i < m_Visuals.Count; i++)
                if (m_Visuals[i].Root == t)
                    return true;
            return false;
        }

        // The planet (if any) under the head's gaze ray: the nearest hit whose ancestor chain reaches
        // a collected planet, so the moon resolves to itself, not to the Earth.
        bool TryGazeGrab(out Transform planet, out float depth, out Vector3 hitPoint)
        {
            planet = null;
            depth = 0f;
            hitPoint = default;
            if (m_Head == null)
                return false;
            if (!Physics.Raycast(new Ray(m_Head.position, m_Head.forward), out var hit, m_GazeMaxDistance, m_GazeMask,
                    QueryTriggerInteraction.Ignore))
                return false;
            for (var t = hit.collider.transform; t != null; t = t.parent)
                for (var i = 0; i < m_Visuals.Count; i++)
                    if (m_Visuals[i].Root == t)
                    {
                        planet = t;
                        depth = hit.distance;
                        hitPoint = hit.point;
                        return true;
                    }
            return false;
        }

        // Samples the carried object for the release velocity.
        void SampleCarryPath()
        {
            m_Positions[m_SampleCursor] = m_Active.position;
            m_SampleTimes[m_SampleCursor] = Time.unscaledTime;
            m_SampleCursor = (m_SampleCursor + 1) % k_History;
            m_SampleCount = Mathf.Min(m_SampleCount + 1, k_History);
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

        // The throw in progress: the body glides along its release velocity, slowing to a stop.
        void GlideUpdate(float dt)
        {
            if (m_GlideTransform == null)
                return;
            if (m_Glide.sqrMagnitude <= m_GlideStopSpeed * m_GlideStopSpeed)
            {
                m_Glide = Vector3.zero;
                m_GlideTransform = null;
                return;
            }
            m_GlideTransform.position += m_Glide * dt;
            m_Glide *= Mathf.Exp(-dt / m_GlideDampSeconds);
        }

        // The planet whose surface is nearest the pinch point, within m_GrabRadius — or null when
        // no planet is close enough to grab.
        Transform PickGrabTarget(Vector3 pinchPoint)
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
            return bestRoot;
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

        // The planets: an explicit list, else every child of the target with a collider. The sun
        // carries none (it isn't grabbable), so rings and the sun are skipped automatically.
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
