using System.Collections.Generic;
using CardboardXR.Hands;
using CardboardXR.Stereo;
using CardboardXR.UI;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace CardboardXR.Demos.Portal
{
    // A room-scale holographic solar system, floating in the player's room: the sun at the center
    // and the eight planets (plus the Moon beside the Earth) on a horizontal orbit plane, revolving
    // slowly. Built for the Portal demo; the sky dome keeps its stars, this floats in front of you.
    //   - Orbits are faint additive rings, which double as guides: the selected planet's ring burns
    //     bright while the others dim (FocusRing).
    //   - Every body has a name label (Roboto), billboarded to the head and outlined so it reads
    //     against the room.
    //   - Grab, carry and flick any planet (or the Moon): while carried it leaves its orbit and,
    //     released with a flick, glides with the release velocity, losing speed slowly, drifting
    //     without gravity like a hologram. Two-hand pinches scale and rotate it; the whole system
    //     drags when the empty space is grabbed instead.
    //   - RecallPlanets puts every body back on its orbit and restores its size.
    // Bodies are primitives with in-memory materials (Lit planets, HDR-bright unlit sun that blooms);
    // meshes and materials are cleaned up on scene unload. Logs [Portal].
    public class SolarSystemHologram : MonoBehaviour
    {
        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly Color k_RingNormal = new(0.30f, 0.42f, 0.60f);
        static readonly Color k_RingDim = new(0.10f, 0.15f, 0.24f);
        static readonly Color k_RingFocus = new(0.55f, 0.85f, 1.30f);

        // Orbit radius and display diameter, in meters, and the orbit speed in degrees per second.
        // Display sizes are compressed, not to scale — the planets need to be grabbable.
        static readonly (string Name, float Radius, float Diameter, Color Color, float Speed)[] k_Planets =
        {
            ("Mercury", 0.42f, 0.078f, new Color(0.60f, 0.55f, 0.48f), 16f),
            ("Venus", 0.53f, 0.094f, new Color(0.88f, 0.76f, 0.52f), 12.5f),
            ("Earth", 0.64f, 0.10f, new Color(0.35f, 0.58f, 0.95f), 10f),
            ("Mars", 0.75f, 0.086f, new Color(0.85f, 0.42f, 0.26f), 8f),
            ("Jupiter", 0.97f, 0.17f, new Color(0.82f, 0.68f, 0.48f), 5f),
            ("Saturn", 1.17f, 0.155f, new Color(0.89f, 0.81f, 0.60f), 4f),
            ("Uranus", 1.30f, 0.115f, new Color(0.62f, 0.87f, 0.87f), 2.8f),
            ("Neptune", 1.44f, 0.11f, new Color(0.35f, 0.42f, 0.90f), 2.2f),
        };

        const float k_SunDiameter = 0.44f;
        const float k_MoonDiameter = 0.031f;
        const float k_RingWidth = 0.008f;
        const float k_RingInner = 0.085f;
        const float k_RingOuter = 0.147f;

        // One body: what the label follows, and where Recall puts it back.
        sealed class Body
        {
            public string Name;
            public Transform Root;
            public Transform Pivot;    // its orbit pivot; null for the sun
            public Transform Home;     // where Recall sends it (the pivot, or the Earth for the Moon)
            public Vector3 HomePosition;
            public Vector3 HomeScale;
            public float Rise;         // how far above the body the label floats
            public TextMeshPro Label;
        }

        readonly List<Body> m_AllBodies = new();
        readonly List<Body> m_Planets = new();
        readonly List<(Renderer Renderer, Transform Owner)> m_Rings = new();
        readonly List<Material> m_OwnedMaterials = new();
        readonly List<Mesh> m_OwnedMeshes = new();
        readonly List<Transform> m_Orbits = new();
        readonly Dictionary<Transform, float> m_OrbitSpeeds = new();
        Transform m_Head;
        Material m_RingMaterial;
        MaterialPropertyBlock m_Block;
        SolarSystemTwoHandController m_Gestures;

        // Hooks the gesture feed: selecting a body burns its ring, clearing dims nothing.
        public void BindGestures(SolarSystemTwoHandController gestures)
        {
            m_Gestures = gestures;
            if (gestures != null)
                gestures.SelectionChanged += OnSelectionChanged;
        }

        void OnSelectionChanged(Transform planet) => FocusRing(planet);

        // Builds the whole system around `center`, on a horizontal plane. Call once, right after
        // the component is added; everything else (orbits, labels) works from here.
        public void Build(Vector3 center)
        {
            transform.SetPositionAndRotation(center, Quaternion.identity);
            m_Block = new MaterialPropertyBlock();
            m_Head = FindHead();
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            if (lit == null || unlit == null)
            {
                Debug.LogError("[Portal] URP shaders missing; the solar system won't render");
                return;
            }

            // The sun: opaque, HDR-bright, so the URP bloom turns it into a glare, not a circle.
            // No collider: the sun isn't grabbable, tappable or throwable.
            var sunMaterial = Own(MakeUnlit(unlit, new Color(2.2f, 1.5f, 0.65f)));
            var sun = Sphere("Sun", transform, Vector3.zero, k_SunDiameter, sunMaterial, addCollider: false);
            m_AllBodies.Add(new Body { Name = "Sun", Root = sun, Rise = k_SunDiameter / 2f + 0.09f, Home = transform,
                                        HomePosition = Vector3.zero, HomeScale = Vector3.one * k_SunDiameter });

            // The shared, faint additive material for the orbit rings and Saturn's ring.
            m_RingMaterial = Own(MakeAdditive(unlit));

            for (var i = 0; i < k_Planets.Length; i++)
            {
                var def = k_Planets[i];
                var orbit = new GameObject($"Orbit {def.Name}").transform;
                orbit.SetParent(transform, false);
                orbit.localRotation = Quaternion.Euler(0f, i * 137.5f, 0f);
                m_Orbits.Add(orbit);
                m_OrbitSpeeds[orbit] = def.Speed;

                var material = Own(MakeLit(lit, def.Color));
                var planet = Sphere(def.Name, orbit, new Vector3(def.Radius, 0f, 0f), def.Diameter, material);

                var ring = Ring($"{def.Name} Orbit", transform, Vector3.zero,
                    def.Radius - k_RingWidth, def.Radius + k_RingWidth, Quaternion.identity, k_RingNormal);
                m_Rings.Add((ring.GetComponent<Renderer>(), planet));

                var body = new Body
                {
                    Name = def.Name,
                    Root = planet,
                    Pivot = orbit,
                    Home = orbit,
                    HomePosition = new Vector3(def.Radius, 0f, 0f),
                    HomeScale = Vector3.one * def.Diameter,
                    Rise = def.Diameter / 2f + 0.055f,
                };
                m_AllBodies.Add(body);
                m_Planets.Add(body);

                if (def.Name == "Saturn")
                {
                    // Saturn's ring: child of the planet, so it follows every grab and flick.
                    Ring("Saturn Ring", planet, Vector3.zero, k_RingInner / def.Diameter, k_RingOuter / def.Diameter,
                        Quaternion.Euler(16f, 0f, 0f), new Color(0.75f, 0.70f, 0.50f));
                }
                else if (def.Name == "Earth")
                {
                    // The Moon rides with the Earth: a child, so thrown planets take it along. The
                    // earth is scaled, so the moon's local scale is the size ratio.
                    var moonScale = k_MoonDiameter / def.Diameter;
                    var moonMaterial = Own(MakeLit(lit, new Color(0.72f, 0.72f, 0.68f)));
                    var moon = Sphere("Moon", planet, new Vector3(0.55f, 0.32f, 0f), moonScale, moonMaterial);
                    var moonBody = new Body
                    {
                        Name = "Moon",
                        Root = moon,
                        Pivot = null,
                        Home = planet,
                        HomePosition = new Vector3(0.55f, 0.32f, 0f),
                        HomeScale = Vector3.one * moonScale,
                        Rise = (k_MoonDiameter / 2f) + 0.045f,
                    };
                    m_AllBodies.Add(moonBody);
                    m_Planets.Add(moonBody);
                }
            }

            foreach (var body in m_AllBodies)
                body.Label = Label(body);
            Debug.Log($"[Portal] Solar system built: {m_Planets.Count} bodies on orbit");
        }

        // Burns the selected body's orbit ring bright and dims the rest; passing null restores all.
        public void FocusRing(Transform selected)
        {
            foreach (var (renderer, owner) in m_Rings)
            {
                var color = selected == null ? k_RingNormal
                    : owner == selected ? k_RingFocus : k_RingDim;
                m_Block.SetColor(k_BaseColor, color);
                renderer.SetPropertyBlock(m_Block);
            }
        }

        // Puts every body (including the Moon) back on its orbit, size and spread as built.
        public void RecallPlanets()
        {
            var gesture = GetComponent<SolarSystemTwoHandController>();
            if (gesture != null)
                gesture.StopGlide();
            foreach (var body in m_Planets)
            {
                body.Root.SetParent(body.Home, false);
                body.Root.localPosition = body.HomePosition;
                body.Root.localRotation = Quaternion.identity;
                body.Root.localScale = body.HomeScale;
            }
            Debug.Log("[Portal] Planets recalled");
        }

        void Update()
        {
            var dt = Mathf.Min(Time.deltaTime, 0.1f);
            foreach (var orbit in m_Orbits)
                orbit.Rotate(0f, m_OrbitSpeeds[orbit] * dt, 0f, Space.Self);
        }

        void LateUpdate()
        {
            foreach (var body in m_AllBodies)
            {
                if (body.Label == null)
                    continue;
                var position = body.Root.position + Vector3.up * body.Rise;
                body.Label.transform.position = position;
                if (m_Head != null)
                    body.Label.transform.rotation = Quaternion.LookRotation(m_Head.position - position, Vector3.up);
            }
        }

        void OnDestroy()
        {
            if (m_Gestures != null)
                m_Gestures.SelectionChanged -= OnSelectionChanged;
            // Everything built in code goes with the scene; don't leak GPU objects.
            foreach (var mesh in m_OwnedMeshes)
                if (mesh != null)
                    Destroy(mesh);
            foreach (var material in m_OwnedMaterials)
                if (material != null)
                    Destroy(material);
        }

        // The head the labels face and the gaze grabs ray from; found, then the camera.
        static Transform FindHead()
        {
            var fusion = FindAnyObjectByType<HeadPoseFusion>();
            if (fusion != null)
                return fusion.transform;
            return Camera.main != null ? Camera.main.transform : null;
        }

        // A palette name for the panel, straight from the visual table.
        public static bool TryGetBodyColor(string bodyName, out Color color)
        {
            if (bodyName == "Sun")
            {
                color = new Color(1f, 0.86f, 0.5f);
                return true;
            }
            if (bodyName == "Moon")
            {
                color = new Color(0.72f, 0.72f, 0.68f);
                return true;
            }
            foreach (var def in k_Planets)
                if (def.Name == bodyName)
                {
                    color = def.Color;
                    return true;
                }
            color = Color.white;
            return false;
        }

        Transform Sphere(string name, Transform parent, Vector3 localPosition, float scale, Material material, bool addCollider = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.SetLocalPositionAndRotation(localPosition, Quaternion.identity);
            go.transform.localScale = Vector3.one * scale;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            if (!addCollider)
                Destroy(go.GetComponent<Collider>());
            return go.transform;
        }

        // A flat annulus in the XZ plane, for the orbit guides and Saturn's ring.
        Transform Ring(string name, Transform parent, Vector3 localPosition, float inner, float outer, Quaternion rotation, Color color)
        {
            var ring = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            ring.transform.SetParent(parent, false);
            ring.transform.SetLocalPositionAndRotation(localPosition, rotation);
            var mesh = RingMesh(inner, outer, 56);
            m_OwnedMeshes.Add(mesh);
            ring.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = ring.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = m_RingMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            m_Block.SetColor(k_BaseColor, color);
            renderer.SetPropertyBlock(m_Block);
            return ring.transform;
        }

        static Mesh RingMesh(float inner, float outer, int segments)
        {
            var vertices = new List<Vector3>((segments + 1) * 2);
            var triangles = new List<int>(segments * 6);
            for (var i = 0; i <= segments; i++)
            {
                var angle = Mathf.PI * 2f * i / segments;
                var sin = Mathf.Sin(angle);
                var cos = Mathf.Cos(angle);
                vertices.Add(new Vector3(cos * inner, 0f, sin * inner));
                vertices.Add(new Vector3(cos * outer, 0f, sin * outer));
            }
            for (var i = 0; i < segments; i++)
            {
                var b = i * 2;
                triangles.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
            }
            var mesh = new Mesh { name = "Ring" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // A floating name tag: Roboto Medium, billboarded to the head, outlined to stay readable
        // against the room, set to a fixed world width regardless of the glyphs it holds.
        TextMeshPro Label(Body body)
        {
            const float width = 0.17f;
            var go = new GameObject(body.Name + " Label");
            go.transform.SetParent(transform, false);
            go.transform.position = body.Root.position + Vector3.up * body.Rise;
            var tmp = go.AddComponent<TextMeshPro>();
            var font = M3Assets.Instance != null ? M3Assets.Instance.Medium : null;
            if (font != null)
                tmp.font = font;
            tmp.text = body.Name;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.fontSize = 4f;
            tmp.fontStyle = FontStyles.Normal;
            // Face lit in the body's own color, lifted toward white; a near-black outline keeps it
            // readable over a bright room.
            TryGetBodyColor(body.Name, out var color);
            tmp.color = Color.Lerp(color, Color.white, 0.55f);
            tmp.outlineWidth = 0.18f;
            tmp.outlineColor = new Color(0.02f, 0.03f, 0.06f, 1f);
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            var widthUnits = tmp.preferredWidth;
            if (widthUnits > 0.001f)
                go.transform.localScale = Vector3.one * (width / widthUnits);
            return tmp;
        }

        // URP Lit, flat and matte; the room's key light does the shading.
        Material MakeLit(Shader lit, Color color)
        {
            var material = new Material(lit);
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", 0.18f);
            material.SetFloat("_Metallic", 0f);
            return material;
        }

        // URP Unlit, flat color; HDR colors bloom.
        Material MakeUnlit(Shader unlit, Color color)
        {
            var material = new Material(unlit);
            material.SetColor("_BaseColor", color);
            return material;
        }

        // URP Unlit, transparent additive, writes no depth: the orbit glows lie on the room.
        Material MakeAdditive(Shader unlit)
        {
            var material = new Material(unlit);
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 2f);
            material.SetFloat("_SrcBlend", (float)BlendMode.One);
            material.SetFloat("_DstBlend", (float)BlendMode.One);
            material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            material.SetFloat("_DstBlendAlpha", (float)BlendMode.One);
            material.SetFloat("_ZWrite", 0f);
            material.SetOverrideTag("RenderType", "Transparent");
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)RenderQueue.Transparent;
            return material;
        }

        Material Own(Material material)
        {
            m_OwnedMaterials.Add(material);
            return material;
        }
    }
}
