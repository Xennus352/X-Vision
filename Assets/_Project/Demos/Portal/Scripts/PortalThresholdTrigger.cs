using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace CardboardXR.Demos.Portal
{
    // Turns the portal's world from "only through the door" into "everywhere" once the main AR
    // camera has passed through the portal's threshold plane, and back again on the way out.
    //   The threshold is a mathematical plane on the portal's local XY plane (normal = portal's
    //   forward), not a collider: comparing the camera's signed distance is deterministic and works
    //   in AR, where physics and the head don't reliably mix. Set m_FlipDirection when the portal
    //   faces the other way.
    //   Crossing swaps every registered interior renderer between its stencil-tested and its
    //   immersive material and toggles the stencil mask's renderer, so the interior fills the view
    //   like any scene ("100% VR"). Wire m_ImmersiveChanged to anything AR-specific, e.g. hiding the
    //   passthrough/AR-background layer; passthrough itself is deliberately left alone by this
    //   component. Logs [Portal].
    public class PortalThresholdTrigger : MonoBehaviour
    {
        // +z is the way the portal faces: negative signed distance is through.
        [Tooltip("The portal quad whose plane is the threshold; this object if unset.")]
        [SerializeField] Transform m_Portal;
        [Tooltip("The main AR camera; Camera.main if unset.")]
        [SerializeField] Camera m_Camera;
        [Tooltip("Interior renderers (planets, space dome) to swap between the two material variants.")]
        [SerializeField] List<Renderer> m_InteriorRenderers = new();
        [Tooltip("The interior material used in front of the portal: stencil-tested (Ref 1, Equal).")]
        [SerializeField] Material m_StencilMaterial;
        [Tooltip("The interior material used once through: stencil test disabled (Comp Always).")]
        [SerializeField] Material m_ImmersiveMaterial;
        [Tooltip("The stencil mask quad; hidden while inside so the world shows everywhere.")]
        [SerializeField] Renderer m_StencilMask;
        [Tooltip("Treat the positive side of the plane as 'inside' (portal facing the far world).")]
        [SerializeField] bool m_FlipDirection;
        [Tooltip("Called with true on entering and false on leaving, e.g. to toggle the AR background.")]
        [SerializeField] UnityEvent<bool> m_ImmersiveChanged;

        Plane m_Plane;

        /// <summary>True while the camera is on the far side of the portal's plane.</summary>
        public bool IsInside { get; private set; }

        /// <summary>Raised on every crossing: true entering, false leaving.</summary>
        public event Action<bool> ImmersiveChanged;

        void Awake()
        {
            if (m_Portal == null)
                m_Portal = transform;
            if (m_Camera == null)
                m_Camera = Camera.main;
        }

        void OnEnable()
        {
            // With no camera yet there's no threshold to test: start from the safe, masked side.
            IsInside = m_Camera != null && ((SignedDistance() <= 0f) ^ m_FlipDirection);
            Apply(IsInside, raiseEvents: false);
        }

        void LateUpdate()
        {
            if (m_Camera == null || m_Portal == null)
                return;
            var inside = (SignedDistance() <= 0f) ^ m_FlipDirection;
            if (inside == IsInside)
                return;
            IsInside = inside;
            Apply(inside, raiseEvents: true);
        }

        // Distance from the camera to the portal's plane; negative once through.
        float SignedDistance()
        {
            var normal = m_Portal.forward;
            if (normal.sqrMagnitude < 1e-12f)
                normal = Vector3.forward;
            m_Plane.SetNormalAndPosition(normal, m_Portal.position);
            return m_Camera != null ? m_Plane.GetDistanceToPoint(m_Camera.transform.position) : 0f;
        }

        // Swaps every matching material slot between the two variants, toggles the mask, and reports
        // through the events. raiseEvents is false on enable, when nothing should be announced.
        void Apply(bool immersive, bool raiseEvents)
        {
            var from = immersive ? m_StencilMaterial : m_ImmersiveMaterial;
            var to = immersive ? m_ImmersiveMaterial : m_StencilMaterial;
            if (from != null && to != null)
            {
                foreach (var renderer in m_InteriorRenderers)
                {
                    if (renderer == null)
                        continue;
                    var shared = renderer.sharedMaterials;
                    var swapped = false;
                    for (var i = 0; i < shared.Length; i++)
                    {
                        // The swap only touches slots currently holding the other variant.
                        if (shared[i] != from)
                            continue;
                        shared[i] = to;
                        swapped = true;
                    }
                    if (swapped)
                        renderer.sharedMaterials = shared;
                }
            }
            if (m_StencilMask != null)
                m_StencilMask.enabled = !immersive;
            if (!raiseEvents)
                return;
            Debug.Log(immersive
                ? "[Portal] Through the threshold: interior unmasked (immersive)"
                : "[Portal] Back across the threshold: interior masked again");
            ImmersiveChanged?.Invoke(immersive);
            m_ImmersiveChanged?.Invoke(immersive);
        }
    }
}
