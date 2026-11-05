using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace CardboardXR.Stereo
{
    // Stops active surface scanning once the room's initial surfaces are in. ARCore's plane finding
    // (and a mesh subsystem, where there is one) keeps refining surfaces for as long as it's on, and
    // that costs CPU the frame rate and the heat budget need; RoomBoundary's own floor scan already
    // stops itself a few seconds after the floor is found, and this covers the rest: waits until a
    // handful of real surfaces — at least m_MinSurfaces with area >= m_MinPlaneArea, and one of them
    // an upward horizontal plane, if m_RequireFloor — have held for m_SettleSeconds, then ends any
    // running scan, sets the plane manager's requested detection mode to None and disables it (the
    // room's existing planes stay, they just stop being refined), and stops the mesh manager, whose
    // OnDisable stops its subsystem.
    //   A room scan a demo asked for with ScanRoom is the exception: while one is running
    //   (RoomBoundary.IsRoomScan), plane detection keeps going so it doesn't starve — mesh scanning
    //   is still stopped, and anything else flips back once the room scan ends (ScanRoom stops
    //   tracking soon after nothing new turns up, and the planes manager is disabled then the usual
    //   way). Call Resume() to turn detection back on, e.g. from the settings panel. Logs [Scan].
    public class SurfaceScanCleanup : MonoBehaviour
    {
        const float k_PollSeconds = 0.5f;

        [Tooltip("The plane manager to stop; on this object, a parent or a child if unset.")]
        [SerializeField] ARPlaneManager m_Planes;
        [Tooltip("Optional: the mesh manager to stop; on this object, a parent or a child if unset.")]
        [SerializeField] ARMeshManager m_Mesh;
        [Tooltip("Optional: used to end any scan cleanly (and to respect an explicit room scan); on this object, a parent or a child if unset.")]
        [SerializeField] RoomBoundary m_Boundary;
        [Tooltip("The smallest plane area, in square meters, that counts as a surface.")]
        [SerializeField] float m_MinPlaneArea = 0.25f;
        [Tooltip("How many qualifying surfaces count as the room's initial set.")]
        [SerializeField, Range(1, 16)] int m_MinSurfaces = 3;
        [Tooltip("Require at least one upward horizontal plane (a floor) among the initial surfaces.")]
        [SerializeField] bool m_RequireFloor = true;
        [Tooltip("How long the surfaces must hold before scanning stops, in seconds.")]
        [SerializeField] float m_SettleSeconds = 1.5f;

        float m_NextPoll;
        float m_SatisfiedSince = -1f;
        bool m_Done;

        void Start()
        {
            if (m_Planes == null)
                m_Planes = GetComponent<ARPlaneManager>() ?? GetComponentInParent<ARPlaneManager>() ?? GetComponentInChildren<ARPlaneManager>();
            if (m_Mesh == null)
                m_Mesh = GetComponent<ARMeshManager>() ?? GetComponentInParent<ARMeshManager>() ?? GetComponentInChildren<ARMeshManager>();
            if (m_Boundary == null)
                m_Boundary = GetComponent<RoomBoundary>() ?? GetComponentInParent<RoomBoundary>() ?? GetComponentInChildren<RoomBoundary>();
            if (m_Planes == null && m_Mesh == null)
            {
                Debug.LogWarning("[Scan] No ARPlaneManager or ARMeshManager found; nothing to stop");
                enabled = false;
            }
        }

        void Update()
        {
            if (m_Done || Time.unscaledTime < m_NextPoll)
                return;
            m_NextPoll = Time.unscaledTime + k_PollSeconds;
            var surfaces = 0;
            var area = 0f;
            var hasFloor = false;
            if (m_Planes != null)
            {
                foreach (var plane in m_Planes.trackables)
                {
                    if (plane.trackingState != TrackingState.Tracking || plane.subsumedBy != null)
                        continue;
                    var planeArea = plane.size.x * plane.size.y;
                    if (planeArea < m_MinPlaneArea)
                        continue;
                    surfaces++;
                    area += planeArea;
                    if (plane.alignment == PlaneAlignment.HorizontalUp)
                        hasFloor = true;
                }
            }
            var satisfied = surfaces >= m_MinSurfaces && (!m_RequireFloor || hasFloor);
            if (!satisfied)
            {
                m_SatisfiedSince = -1f;
                return;
            }
            if (m_SatisfiedSince < 0f)
                m_SatisfiedSince = Time.unscaledTime;
            else if (Time.unscaledTime - m_SatisfiedSince >= m_SettleSeconds)
                Cleanup(surfaces, area, hasFloor);
        }

        void Cleanup(int surfaces, float area, bool hasFloor)
        {
            var roomScan = m_Boundary != null && m_Boundary.IsRoomScan;
            if (m_Boundary != null && m_Boundary.IsScanning && !roomScan)
                m_Boundary.FinishScan();
            if (m_Planes != null && !roomScan)
            {
                m_Planes.requestedDetectionMode = PlaneDetectionMode.None;
                m_Planes.enabled = false;
            }
            if (m_Mesh != null)
                m_Mesh.enabled = false;
            m_Done = true;
            Debug.Log($"[Scan] Initial room surfaces found: {surfaces} surfaces, {area:F1} m2{(hasFloor ? ", floor" : "")}; " +
                      (roomScan ? "mesh scanning stopped; plane detection left on for the room scan in progress"
                                : "plane and mesh scanning stopped"));
        }

        // Turns detection back on, e.g. when a demo or the settings panel asks for a scan again.
        public void Resume()
        {
            m_Done = false;
            m_SatisfiedSince = -1f;
            if (m_Planes != null)
            {
                m_Planes.requestedDetectionMode = PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical;
                m_Planes.enabled = true;
            }
            if (m_Mesh != null)
                m_Mesh.enabled = true;
            Debug.Log("[Scan] Surface scanning resumed");
        }
    }
}
