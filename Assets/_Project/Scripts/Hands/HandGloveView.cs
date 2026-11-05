using System;
using CardboardXR.Stereo;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

namespace CardboardXR.Hands
{
    // A glove for the tracked hands, posed from XR Hands itself: the subsystem's 26 joints per hand
    // (Wrist, Palm, 24 finger joints — Unity's XRHandJointID, which is what ARKit's native skeleton
    // maps onto), positions and rotations both. Two modes:
    //   Rig — assign m_LeftRig/m_RightRig: roots whose descendants are named like the joints
    //   ("Wrist", "Palm", "ThumbTip", …; an "XR_" prefix is also accepted). Every joint found gets
    //   the subsystem's world pose. Make the rig's hierarchy mirror the skeleton's (Wrist at the
    //   root, Palm and the five finger chains under it) and a skinned glove on it deforms correctly
    //   from the raw data; extra joints are ignored and missing ones are skipped.
    //   Primitives — build the glove from spheres (joints) and capsules (bones), colored by finger.
    // World poses come from the subsystem's tracking space via m_TrackingSpace (auto-found: the
    // XR Origin's camera floor offset if unset; on visionOS via PolySpatial the subsystem supplies
    // them the same way). Joints ease toward their poses and snap across tracking jumps; a hand
    // that stops being tracked hides its glove. Set the pinch threshold in the interaction scripts,
    // not here. Logs are not printed (a glove doesn't narrate). Put it on an empty in the scene.
    public class HandGloveView : MonoBehaviour
    {
        const int k_JointCount = 26;
        // Past this distance between frames a joint teleports; easing across it looks like a smear.
        const float k_SnapDistance = 0.25f;

        static readonly XRHandJointID[] k_Joints =
        {
            XRHandJointID.Wrist, XRHandJointID.Palm,
            XRHandJointID.ThumbMetacarpal, XRHandJointID.ThumbProximal, XRHandJointID.ThumbDistal, XRHandJointID.ThumbTip,
            XRHandJointID.IndexMetacarpal, XRHandJointID.IndexProximal, XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip,
            XRHandJointID.MiddleMetacarpal, XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip,
            XRHandJointID.RingMetacarpal, XRHandJointID.RingProximal, XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip,
            XRHandJointID.LittleMetacarpal, XRHandJointID.LittleProximal, XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal, XRHandJointID.LittleTip,
        };

        // Thumb red, index orange, middle yellow, ring green, little blue; wrist and palm white.
        static Color JointColor(XRHandJointID id) => id switch
        {
            XRHandJointID.Wrist or XRHandJointID.Palm => Color.white,
            XRHandJointID.ThumbMetacarpal or XRHandJointID.ThumbProximal or XRHandJointID.ThumbDistal or XRHandJointID.ThumbTip => new Color(1f, 0.35f, 0.3f),
            XRHandJointID.IndexMetacarpal or XRHandJointID.IndexProximal or XRHandJointID.IndexIntermediate or XRHandJointID.IndexDistal or XRHandJointID.IndexTip => new Color(1f, 0.6f, 0.2f),
            XRHandJointID.MiddleMetacarpal or XRHandJointID.MiddleProximal or XRHandJointID.MiddleIntermediate or XRHandJointID.MiddleDistal or XRHandJointID.MiddleTip => new Color(1f, 0.9f, 0.25f),
            XRHandJointID.RingMetacarpal or XRHandJointID.RingProximal or XRHandJointID.RingIntermediate or XRHandJointID.RingDistal or XRHandJointID.RingTip => new Color(0.3f, 0.95f, 0.45f),
            _ => new Color(0.4f, 0.6f, 1f),
        };

        // Bones for the primitives mode: each finger's chain from its metacarpal, across the
        // knuckles, and wrist to palm. Precomputed as indices into k_Joints.
        static readonly (XRHandJointID a, XRHandJointID b)[] k_BoneIds =
        {
            (XRHandJointID.Wrist, XRHandJointID.Palm),
            (XRHandJointID.Wrist, XRHandJointID.ThumbMetacarpal),
            (XRHandJointID.ThumbMetacarpal, XRHandJointID.ThumbProximal),
            (XRHandJointID.ThumbProximal, XRHandJointID.ThumbDistal),
            (XRHandJointID.ThumbDistal, XRHandJointID.ThumbTip),
            (XRHandJointID.Wrist, XRHandJointID.IndexMetacarpal),
            (XRHandJointID.IndexMetacarpal, XRHandJointID.IndexProximal),
            (XRHandJointID.IndexProximal, XRHandJointID.IndexIntermediate),
            (XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal),
            (XRHandJointID.IndexDistal, XRHandJointID.IndexTip),
            (XRHandJointID.Wrist, XRHandJointID.MiddleMetacarpal),
            (XRHandJointID.MiddleMetacarpal, XRHandJointID.MiddleProximal),
            (XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate),
            (XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal),
            (XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip),
            (XRHandJointID.Wrist, XRHandJointID.RingMetacarpal),
            (XRHandJointID.RingMetacarpal, XRHandJointID.RingProximal),
            (XRHandJointID.RingProximal, XRHandJointID.RingIntermediate),
            (XRHandJointID.RingIntermediate, XRHandJointID.RingDistal),
            (XRHandJointID.RingDistal, XRHandJointID.RingTip),
            (XRHandJointID.Wrist, XRHandJointID.LittleMetacarpal),
            (XRHandJointID.LittleMetacarpal, XRHandJointID.LittleProximal),
            (XRHandJointID.LittleProximal, XRHandJointID.LittleIntermediate),
            (XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal),
            (XRHandJointID.LittleDistal, XRHandJointID.LittleTip),
            (XRHandJointID.IndexMetacarpal, XRHandJointID.MiddleMetacarpal),
            (XRHandJointID.MiddleMetacarpal, XRHandJointID.RingMetacarpal),
            (XRHandJointID.RingMetacarpal, XRHandJointID.LittleMetacarpal),
        };

        static readonly int[] k_BoneFrom = Array.ConvertAll(k_BoneIds, b => Array.IndexOf(k_Joints, b.a));
        static readonly int[] k_BoneTo = Array.ConvertAll(k_BoneIds, b => Array.IndexOf(k_Joints, b.b));

        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int k_Color = Shader.PropertyToID("_Color");
        static readonly int k_EmissionColor = Shader.PropertyToID("_EmissionColor");
        static readonly int k_Smoothness = Shader.PropertyToID("_Smoothness");

        public enum VisualMode { Primitives, Rig }

        [Tooltip("Primitives build the glove from capsules and spheres; Rig poses your named hierarchy instead.")]
        [SerializeField] VisualMode m_Mode = VisualMode.Primitives;
        [Tooltip("The space XR Hands' poses are relative to: the XR Origin's camera floor offset. Auto-found if unset; the poses are then world poses.")]
        [SerializeField] Transform m_TrackingSpace;
        [Tooltip("Rig mode: the left hand's root; descendants named like the joints (optionally prefixed XR_) get posed.")]
        [SerializeField] Transform m_LeftRig;
        [Tooltip("Rig mode: the right hand's root; descendants named like the joints (optionally prefixed XR_) get posed.")]
        [SerializeField] Transform m_RightRig;
        [Tooltip("Hides the glove in passthrough, where the real hand is already in the picture; the flat passthrough can't fuse with a 3D glove.")]
        [SerializeField] PassthroughController m_Passthrough;
        [Tooltip("Primitives mode: the material for the capsules and spheres.")]
        [SerializeField] Material m_Material;
        [Tooltip("Primitives mode: joint sphere diameter.")]
        [SerializeField] float m_JointSize = 0.012f;
        [Tooltip("Primitives mode: bone capsule width.")]
        [SerializeField] float m_BoneWidth = 0.006f;
        [Tooltip("Primitives mode: the cybernetic look — dark parts with emissive fingertips that pulse gently cyan. Off uses the plain flat finger colors.")]
        [SerializeField] bool m_CyberneticGlow = true;
        [Tooltip("Time constant, in seconds, for the joints easing toward their poses.")]
        [SerializeField] float m_EaseSeconds = 0.06f;

        Material m_CreatedMaterial;
        readonly HandVisual[] m_Hands = { new HandVisual(), new HandVisual() };
        // Cybernetic glow: one block reused for every property-block write, plus the per-fingertip
        // state for the pulse.
        readonly MaterialPropertyBlock m_GlowBlock = new();
        readonly List<Renderer> m_TipRenderers = new();
        readonly List<Color> m_TipBase = new();
        readonly List<Color> m_TipEmission = new();
        readonly List<float> m_TipPhases = new();

        void Awake()
        {
            if (m_TrackingSpace == null)
            {
                var origin = FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
                m_TrackingSpace = origin != null && origin.CameraFloorOffsetObject != null
                    ? origin.CameraFloorOffsetObject.transform
                    : null;
            }
            if (m_Mode == VisualMode.Primitives && m_Material == null)
            {
                // The cybernetic look needs an emissive material; if the glow is off, plain Unlit is fine.
                var shaderName = m_CyberneticGlow ? "Universal Render Pipeline/Lit" : "Universal Render Pipeline/Unlit";
                var shader = Shader.Find(shaderName);
                if (shader != null)
                    m_Material = m_CreatedMaterial = new Material(shader);
                else
                    Debug.LogError("[Hands] No URP shader and no m_Material; the glove will render magenta");
            }
            foreach (var hand in m_Hands)
            {
                hand.JointShown = new Vector3[k_JointCount];
                hand.JointShownRotation = new Quaternion[k_JointCount];
            }
            if (m_Mode == VisualMode.Primitives)
            {
                if (m_CyberneticGlow && m_Material != null)
                    m_Material.EnableKeyword("_EMISSION");
                BuildPrimitives(0, m_LeftRig != null ? m_LeftRig.name : "Left Glove");
                BuildPrimitives(1, m_RightRig != null ? m_RightRig.name : "Right Glove");
            }
            else
            {
                MapRigJoints(0, m_LeftRig);
                MapRigJoints(1, m_RightRig);
            }
        }

        void OnDisable()
        {
            foreach (var hand in m_Hands)
                SetHandVisible(hand, false);
        }

        void OnDestroy()
        {
            if (m_CreatedMaterial != null)
                Destroy(m_CreatedMaterial);
        }

        void LateUpdate()
        {
            var dt = Mathf.Clamp(Time.deltaTime, 1e-5f, 0.1f);
            var subsystem = HandSubsystemLocator.Get();
            var shown = subsystem != null && (m_Passthrough == null || !m_Passthrough.IsOn);
            for (var i = 0; i < 2; i++)
            {
                var hand = subsystem == null ? default : i == 0 ? subsystem.leftHand : subsystem.rightHand;
                var visual = m_Hands[i];
                var tracked = shown && hand.isTracked;
                if (visual.Root != null && visual.Root.gameObject.activeSelf != tracked)
                    visual.Root.gameObject.SetActive(tracked);
                if (!tracked)
                    continue;
                poseHand(hand, visual, dt);
            }
            if (m_CyberneticGlow)
                PulseGlow();
        }

        void poseHand(XRHand hand, HandVisual visual, float dt)
        {
            var ease = 1f - Mathf.Exp(-dt / Mathf.Max(1e-4f, m_EaseSeconds));
            for (var i = 0; i < k_Joints.Length; i++)
            {
                if (!hand.GetJoint(k_Joints[i]).TryGetPose(out var pose))
                    continue;
                var world = m_TrackingSpace != null ? m_TrackingSpace.TransformPoint(pose.position) : pose.position;
                var rotation = m_TrackingSpace != null ? m_TrackingSpace.rotation * pose.rotation : pose.rotation;
                var snap = !visual.HasJoint[i] || (world - visual.JointShown[i]).sqrMagnitude > k_SnapDistance * k_SnapDistance;
                visual.JointShown[i] = snap ? world : Vector3.Lerp(visual.JointShown[i], world, ease);
                visual.JointShownRotation[i] = snap
                    ? rotation
                    : Quaternion.Slerp(visual.JointShownRotation[i], rotation, ease);
                visual.HasJoint[i] = true;
            }
            if (m_Mode == VisualMode.Rig)
                poseRig(visual);
            else
                posePrimitives(visual);
        }

        // Rig mode: pose the named descendants from the subsystem's world poses.
        void poseRig(HandVisual visual)
        {
            if (visual.RigRoot == null)
                return;
            if (visual.HasJoint[0])
                visual.RigRoot.SetPositionAndRotation(visual.JointShown[0], visual.JointShownRotation[0]);
            for (var i = 0; i < k_Joints.Length; i++)
            {
                var joint = visual.Joints[i];
                if (joint != null && visual.HasJoint[i])
                    joint.SetPositionAndRotation(visual.JointShown[i], visual.JointShownRotation[i]);
            }
        }

        // Primitives mode: bones stretch between joints, joints sit at their poses.
        void posePrimitives(HandVisual visual)
        {
            for (var i = 0; i < k_Joints.Length; i++)
            {
                var sphere = visual.Spheres[i];
                sphere.gameObject.SetActive(visual.HasJoint[i]);
                if (visual.HasJoint[i])
                    sphere.SetPositionAndRotation(visual.JointShown[i], visual.JointShownRotation[i]);
            }
            for (var i = 0; i < k_BoneIds.Length; i++)
            {
                var from = k_BoneFrom[i];
                var to = k_BoneTo[i];
                var bone = visual.Bones[i];
                var both = visual.HasJoint[from] && visual.HasJoint[to];
                bone.gameObject.SetActive(both);
                if (!both)
                    continue;
                var fromPosition = visual.JointShown[from];
                var toPosition = visual.JointShown[to];
                var delta = toPosition - fromPosition;
                bone.position = (fromPosition + toPosition) * 0.5f;
                if (delta.sqrMagnitude > 1e-10f)
                {
                    bone.rotation = Quaternion.FromToRotation(Vector3.up, delta.normalized);
                    bone.localScale = new Vector3(m_BoneWidth, delta.magnitude * 0.5f, m_BoneWidth);
                }
            }
        }

        // Builds the sphere/capsule parts and stashes the joints' identity under one root.
        void BuildPrimitives(int index, string rootName)
        {
            var visual = m_Hands[index];
            var root = new GameObject(rootName).transform;
            root.SetParent(transform, false);
            visual.Root = root;
            visual.RigRoot = root;
            visual.Spheres = new Transform[k_JointCount];
            visual.Bones = new Transform[k_BoneIds.Length];
            visual.Joints = new Transform[k_JointCount];
            var block = new MaterialPropertyBlock();
            for (var i = 0; i < k_JointCount; i++)
            {
                var part = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                part.name = k_Joints[i].ToString();
                Destroy(part.GetComponent<Collider>());
                part.transform.SetParent(root, false);
                part.transform.localScale = Vector3.one * m_JointSize;
                var renderer = part.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = m_Material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                block.SetColor(k_BaseColor, JointColor(k_Joints[i]));
                block.SetColor(k_Color, JointColor(k_Joints[i]));
                renderer.SetPropertyBlock(block);
                visual.Spheres[i] = part.transform;
                visual.Joints[i] = part.transform;
            }
            for (var i = 0; i < k_BoneIds.Length; i++)
            {
                var part = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                part.name = $"{k_BoneIds[i].a}-{k_BoneIds[i].b}";
                Destroy(part.GetComponent<Collider>());
                part.transform.SetParent(root, false);
                var renderer = part.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = m_Material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                block.SetColor(k_BaseColor, Color.white * 0.85f);
                block.SetColor(k_Color, Color.white * 0.85f);
                renderer.SetPropertyBlock(block);
                visual.Bones[i] = part.transform;
            }
            if (m_CyberneticGlow)
                ApplyCyberneticLook(visual.Spheres, visual.Bones);
            visual.Root.gameObject.SetActive(false);
        }

        // The cybernetic look: dark parts, each finger's own hue glowing — fingertips bright cyan-ish,
        // the other joints dim, bones a faint teal — over the shared material. Fingertips get added to
        // the pulse list; everything else is static.
        void ApplyCyberneticLook(Transform[] spheres, Transform[] bones)
        {
            for (var i = 0; i < spheres.Length; i++)
            {
                var renderer = spheres[i].GetComponent<MeshRenderer>();
                var id = k_Joints[i];
                var baseColor = JointColor(id);
                var tip = id.ToString().EndsWith("Tip", StringComparison.Ordinal);
                var dim = baseColor * 0.22f;
                var emission = baseColor * (tip ? 2.1f : 0.4f);
                m_GlowBlock.SetColor(k_BaseColor, dim);
                m_GlowBlock.SetColor(k_Color, dim);
                m_GlowBlock.SetColor(k_EmissionColor, emission);
                m_GlowBlock.SetFloat(k_Smoothness, 0.15f);
                renderer.SetPropertyBlock(m_GlowBlock);
                if (tip)
                {
                    m_TipRenderers.Add(renderer);
                    m_TipBase.Add(dim);
                    m_TipEmission.Add(emission);
                    m_TipPhases.Add(i * 1.7f);
                }
            }
            foreach (var bone in bones)
            {
                var renderer = bone.GetComponent<MeshRenderer>();
                m_GlowBlock.SetColor(k_BaseColor, new Color(0.12f, 0.14f, 0.16f));
                m_GlowBlock.SetColor(k_Color, new Color(0.12f, 0.14f, 0.16f));
                m_GlowBlock.SetColor(k_EmissionColor, new Color(0.08f, 0.25f, 0.32f));
                m_GlowBlock.SetFloat(k_Smoothness, 0.1f);
                renderer.SetPropertyBlock(m_GlowBlock);
            }
        }

        // The fingertips breathe: a slow sine on each tip's emission, phase-offset per finger.
        void PulseGlow()
        {
            if (m_TipRenderers.Count == 0)
                return;
            var time = Time.unscaledTime * 2.1f;
            for (var i = 0; i < m_TipRenderers.Count; i++)
            {
                var pulse = 0.7f + 0.3f * Mathf.Sin(time + m_TipPhases[i]);
                m_GlowBlock.SetColor(k_BaseColor, m_TipBase[i]);
                m_GlowBlock.SetColor(k_Color, m_TipBase[i]);
                m_GlowBlock.SetColor(k_EmissionColor, m_TipEmission[i] * pulse);
                m_GlowBlock.SetFloat(k_Smoothness, 0.15f);
                m_TipRenderers[i].SetPropertyBlock(m_GlowBlock);
            }
        }

        // Finds each joint's transform by name under the rig's root; names match the enum, with an
        // optional XR_ prefix.
        void MapRigJoints(int index, Transform rig)
        {
            var visual = m_Hands[index];
            if (rig == null)
                return;
            visual.RigRoot = rig;
            visual.Root = rig;
            visual.Joints = new Transform[k_JointCount];
            var byName = new System.Collections.Generic.Dictionary<string, Transform>();
            foreach (var child in rig.GetComponentsInChildren<Transform>(true))
            {
                var name = child.name;
                if (name.StartsWith("XR_"))
                    name = name.Substring(3);
                byName[name] = child;
            }
            var found = 0;
            for (var i = 0; i < k_Joints.Length; i++)
            {
                if (byName.TryGetValue(k_Joints[i].ToString(), out var joint))
                {
                    visual.Joints[i] = joint;
                    found++;
                }
            }
            Debug.Log($"[Hands] Glove rig '{rig.name}': {found} of {k_JointCount} joints mapped");
        }

        void SetHandVisible(HandVisual visual, bool visible)
        {
            if (visual.Root != null)
                visual.Root.gameObject.SetActive(visible);
        }

        sealed class HandVisual
        {
            public Transform Root;
            public Transform RigRoot;
            public Transform[] Spheres;
            public Transform[] Bones;
            public Transform[] Joints;
            public Vector3[] JointShown;
            public Quaternion[] JointShownRotation;
            public readonly bool[] HasJoint = new bool[k_JointCount];
        }
    }
}
