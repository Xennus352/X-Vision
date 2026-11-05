using System.Collections.Generic;
using CardboardXR.Demos;
using CardboardXR.Hands;
using CardboardXR.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.Demos.Portal
{
    // The planet detail card, Vision Pro-style: opens when a planet (or the Moon) is tapped in the
    // solar-system hologram. Dark frosted glass, a soft-shaded hero ball lit from the upper left,
    // micro-caps labels with wide tracking, an accent hairline in the body's color, an italic fact,
    // and a single filled "Recall planets" pill to put every grabbed body back on its orbit. Closed
    // by the close button, by tapping the same body again, or by tapping another body to switch.
    // Looked at like every Material panel: gaze to a button, pinch (or tap the screen) to press it.
    // Facts are rounded approximations, labelled ≈ where they're not exact. Logs [Portal].
    public class SolarSystemInfoPanel : MonoBehaviour
    {
        struct PlanetInfo
        {
            public string Kind;          // "Planet", "Star" or "Moon"
            public string Diameter;
            public string DistanceLabel; // "From the Sun", or "From Earth" for the Moon
            public string Distance;
            public string Day;
            public string Year;
            public string Moons;
            public string Fact;
        }

        static readonly PlanetInfo k_Mercury = new()
        {
            Kind = "Planet", Diameter = "≈4,879 km", DistanceLabel = "From the Sun",
            Distance = "57.9M km (0.39 AU)", Day = "≈58.6 Earth d", Year = "≈88 Earth d", Moons = "0",
            Fact = "One Mercury sunrise to the next is 176 Earth days — a Mercury day outlasts its year.",
        };
        static readonly PlanetInfo k_Venus = new()
        {
            Kind = "Planet", Diameter = "≈12,104 km", DistanceLabel = "From the Sun",
            Distance = "108.2M km (0.72 AU)", Day = "≈243 Earth d (backwards)", Year = "≈224.7 Earth d", Moons = "0",
            Fact = "Venus spins backwards, and its day is longer than its year.",
        };
        static readonly PlanetInfo k_Earth = new()
        {
            Kind = "Planet", Diameter = "≈12,742 km", DistanceLabel = "From the Sun",
            Distance = "149.6M km (1 AU)", Day = "≈23.9 h", Year = "≈365.25 d", Moons = "1",
            Fact = "The only world known to harbour life — and liquid oceans.",
        };
        static readonly PlanetInfo k_Mars = new()
        {
            Kind = "Planet", Diameter = "≈6,779 km", DistanceLabel = "From the Sun",
            Distance = "227.9M km (1.52 AU)", Day = "≈24.6 h", Year = "≈687 Earth d", Moons = "2",
            Fact = "Olympus Mons is its great volcano — three times the height of Everest.",
        };
        static readonly PlanetInfo k_Jupiter = new()
        {
            Kind = "Planet", Diameter = "≈139,820 km", DistanceLabel = "From the Sun",
            Distance = "778.6M km (5.2 AU)", Day = "≈9.9 h", Year = "≈11.9 Earth yr", Moons = "95",
            Fact = "The Great Red Spot is a storm wider than Earth, raging for centuries.",
        };
        static readonly PlanetInfo k_Saturn = new()
        {
            Kind = "Planet", Diameter = "≈116,460 km", DistanceLabel = "From the Sun",
            Distance = "1.43B km (9.6 AU)", Day = "≈10.7 h", Year = "≈29.4 Earth yr", Moons = "146",
            Fact = "Its rings span up to 282,000 km and are only ~10 metres thick.",
        };
        static readonly PlanetInfo k_Uranus = new()
        {
            Kind = "Planet", Diameter = "≈50,724 km", DistanceLabel = "From the Sun",
            Distance = "2.87B km (19.2 AU)", Day = "≈17.2 h", Year = "≈84 Earth yr", Moons = "28",
            Fact = "It rolls around the Sun tilted on its side, at 98 degrees.",
        };
        static readonly PlanetInfo k_Neptune = new()
        {
            Kind = "Planet", Diameter = "≈49,244 km", DistanceLabel = "From the Sun",
            Distance = "4.50B km (30.1 AU)", Day = "≈16.1 h", Year = "≈164.8 Earth yr", Moons = "16",
            Fact = "Winds here reach 2,100 km/h — the fastest in the solar system.",
        };
        static readonly PlanetInfo k_Moon = new()
        {
            Kind = "Moon", Diameter = "≈3,474 km", DistanceLabel = "From Earth",
            Distance = "≈384,400 km", Day = "≈27.3 Earth d", Year = "— (orbits Earth)", Moons = "0",
            Fact = "The same side of the Moon always faces Earth.",
        };

        const float k_Distance = 1.5f;

        Transform m_Head;
        SolarSystemTwoHandController m_Controller;
        SolarSystemHologram m_Hologram;
        RectTransform m_Panel;
        Image m_Hero;
        Image m_Accent;
        TextMeshProUGUI m_Title;
        TextMeshProUGUI m_Kind;
        TextMeshProUGUI[] m_Values;
        TextMeshProUGUI m_DistanceLabel;
        TextMeshProUGUI m_Fact;
        // One soft-shaded ball sprite per body, made in memory on first use and kept for the session.
        static readonly Dictionary<string, Sprite> s_PlanetSprites = new();

        // Builds the panel (hidden until a body is tapped) and hooks the tap selection feed.
        public void Initialize(Transform head)
        {
            m_Head = head;
            m_Controller = FindAnyObjectByType<SolarSystemTwoHandController>();
            m_Hologram = FindAnyObjectByType<SolarSystemHologram>();
            if (m_Controller != null)
                m_Controller.SelectionChanged += OnSelected;
            else
                Debug.LogWarning("[Portal] No solar-system gestures found; the panel won't open");
            BuildPanel();
            m_Panel.gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            if (m_Controller != null)
                m_Controller.SelectionChanged -= OnSelected;
        }

        void OnSelected(Transform planet)
        {
            if (planet == null)
            {
                m_Panel.gameObject.SetActive(false);
                return;
            }
            if (!TryGetInfo(planet.name, out var info))
            {
                Debug.LogWarning($"[Portal] No facts for {planet.name}; closing the panel");
                m_Panel.gameObject.SetActive(false);
                return;
            }
            Show(planet.name, in info);
        }

        // Finds the facts for a body; the object names are the planet names ("Earth", "Moon"…).
        static bool TryGetInfo(string name, out PlanetInfo info)
        {
            switch (name)
            {
                case "Mercury": info = k_Mercury; return true;
                case "Venus": info = k_Venus; return true;
                case "Earth": info = k_Earth; return true;
                case "Mars": info = k_Mars; return true;
                case "Jupiter": info = k_Jupiter; return true;
                case "Saturn": info = k_Saturn; return true;
                case "Uranus": info = k_Uranus; return true;
                case "Neptune": info = k_Neptune; return true;
                case "Moon": info = k_Moon; return true;
                default: info = default; return false;
            }
        }

        void Show(string bodyName, in PlanetInfo info)
        {
            SolarSystemHologram.TryGetBodyColor(bodyName, out var color);
            m_Hero.sprite = PlanetSprite(bodyName, color);
            m_Title.text = bodyName;
            m_Kind.text = info.Kind.ToUpperInvariant();
            m_Kind.color = Color.Lerp(color, Color.white, 0.45f);
            m_Accent.color = Color.Lerp(color, Color.white, 0.15f);
            var rows = new[]
            {
                ("Diameter", info.Diameter),
                (info.DistanceLabel, info.Distance),
                ("Day", info.Day),
                ("Year", info.Year),
                ("Moons", info.Moons),
            };
            m_DistanceLabel.text = info.DistanceLabel.ToUpperInvariant();
            for (var i = 0; i < m_Values.Length; i++)
                m_Values[i].text = rows[i].Item2;
            m_Fact.text = info.Fact;
            if (m_Head != null)
                WorldUi.PlaceInFront(m_Panel, m_Head, k_Distance, pitchDegrees: 4f, yawDegrees: 18f);
            m_Panel.gameObject.SetActive(true);
            Debug.Log($"[Portal] {bodyName} panel opened");
        }

        // The premium card: a dark frosted-glass panel, a soft-shaded hero ball lit from the upper
        // left, micro-caps labels with wide tracking, an accent hairline in the planet's color and a
        // single filled CTA. The gaze works it like every Material panel in the app.
        void BuildPanel()
        {
            m_Panel = WorldUi.Panel("Planet Panel", transform, new Vector2(480f, 640f), k_Distance,
                new Color(0.055f, 0.065f, 0.095f, 0.94f), 36f);

            // The hero ball, sitting above the name.
            var heroGo = new GameObject("Hero", typeof(RectTransform));
            heroGo.transform.SetParent(m_Panel, false);
            var heroRect = (RectTransform)heroGo.transform;
            heroRect.anchoredPosition = new Vector2(0f, 190f);
            heroRect.sizeDelta = new Vector2(152f, 152f);
            m_Hero = heroGo.AddComponent<Image>();
            m_Hero.raycastTarget = false;
            m_Hero.preserveAspect = true;

            m_Title = M3.Text("Title", m_Panel, "", M3Theme.Type.HeadlineSmall, new Vector2(0f, 90f), new Vector2(400f, 44f));
            m_Kind = M3.Text("Kind", m_Panel, "", M3Theme.Type.BodyMedium, new Vector2(0f, 62f), new Vector2(400f, 24f));
            m_Kind.characterSpacing = 22f;
            m_Accent = M3.Surface("Accent", m_Panel, new Vector2(0f, 38f), new Vector2(64f, 4f), Color.white, 2f);
            M3.IconButton("Close", m_Panel, "close", M3Button.Variant.Text, new Vector2(198f, 270f),
                () => m_Controller?.ClearSelection());

            string[] labels = { "DIAMETER", "FROM THE SUN", "DAY", "YEAR", "MOONS" };
            m_Values = new TextMeshProUGUI[labels.Length];
            for (var i = 0; i < labels.Length; i++)
            {
                var y = -4f - i * 42f;
                var label = M3.Text($"Label {i}", m_Panel, labels[i], M3Theme.Type.BodyMedium,
                    new Vector2(-124f, y), new Vector2(190f, 26f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Left);
                label.characterSpacing = 12f;
                if (i == 1)
                    m_DistanceLabel = label;
                m_Values[i] = M3.Text($"Value {i}", m_Panel, "", M3Theme.Type.BodyMedium,
                    new Vector2(126f, y), new Vector2(196f, 26f), M3Theme.OnSurface, TextAlignmentOptions.Right);
            }

            M3.Surface("Hairline", m_Panel, new Vector2(0f, -182f), new Vector2(392f, 1.5f), M3Theme.OutlineVariant, 0.75f);
            m_Fact = M3.Text("Fact", m_Panel, "", M3Theme.Type.BodyMedium, new Vector2(0f, -218f), new Vector2(410f, 56f),
                M3Theme.OnSurfaceVariant);
            m_Fact.fontStyle = FontStyles.Italic;
            M3.Button("Recall", m_Panel, "Recall planets", M3Button.Variant.Filled, new Vector2(0f, -274f),
                () => m_Hologram?.RecallPlanets(), "restart_alt", 280f);
        }

        // A soft-shaded planet ball, lit from the upper left: a radial falloff to a dark rim, with
        // an anti-aliased edge and a bright crown. Built in memory, cached for the session.
        static Sprite PlanetSprite(string name, Color baseColor)
        {
            if (s_PlanetSprites.TryGetValue(name, out var sprite))
                return sprite;
            const int size = 128;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            var half = size * 0.5f;
            var light = new Vector2(size * 0.36f, size * 0.64f);
            var invMax = 1f / (size * 0.72f);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    var edge = Mathf.Clamp01((half - Vector2.Distance(p, new Vector2(half, half)) - 1f) / 2f);
                    var lit = Mathf.Clamp01(1f - Vector2.Distance(p, light) * invMax);
                    var shade = Mathf.Pow(lit, 1.65f);
                    var c = Color.Lerp(baseColor * 0.12f, baseColor * 1.4f, shade);
                    pixels[y * size + x] = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), edge);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
            s_PlanetSprites.Add(name, sprite);
            return sprite;
        }
    }
}
