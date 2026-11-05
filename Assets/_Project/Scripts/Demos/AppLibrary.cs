using System.Collections.Generic;
using CardboardXR.Hands;
using CardboardXR.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.Demos
{
    // The app library, like visionOS's or Quest's: a grid of app icons and names above the settings
    // panel, which its Apps button shows and hides. The lobby comes first, then each demo in the
    // catalog; pick one and the panel closes and that app opens, from wherever you are, so there's no
    // need to go back to the lobby between demos (DemoManager restores what one demo switched on
    // before applying the next's needs). The app running now is marked with a dot under its name.
    // Looking at an app shows its one-line tagline under the grid. Entering the lobby opens the
    // settings panel with the library showing (Open, from LobbyFlow), since the lobby is only a room
    // to start from. Part of the settings panel, so it follows the panel and
    // closes with it. Tiles are built at runtime from the catalog, so a new demo needs only a catalog
    // entry. Lives on an object that's always active (the Demos object), and shows and hides m_Root.
    // Logs [Demos].
    public class AppLibrary : MonoBehaviour
    {
        // In dp: each app's cell, its icon tile, and the gaps between cells and around the grid.
        public const float k_CellWidth = 128f;
        // Kept short, so it sits at eye level with the settings panel close below it.
        public const float k_CellHeight = 124f;
        const float k_IconSize = 72f;
        const float k_IconRadius = 18f;
        const float k_Gap = 8f;
        const float k_Padding = 16f;
        const float k_TaglineHeight = 28f;
        public const int k_Columns = 3;
        const string k_Prompt = "Look at an app to see what it is";
        // Picks this soon after the library appears are ignored: it opens where the user is looking,
        // and the pinch that opened it (or a flicker of it) landed on a tile 0.6 s later, reopening the
        // demo just left.
        const float k_PickGuardSeconds = 0.8f;

        [SerializeField] DemoManager m_Demos;
        [SerializeField] SettingsSummoner m_Settings;
        [Tooltip("The settings panel's Apps button, shown selected while the library shows.")]
        [SerializeField] M3Button m_AppsButton;
        [Tooltip("The library's surface, under the settings panel, shown and hidden.")]
        [SerializeField] RectTransform m_Root;
        [Tooltip("Where the tiles go.")]
        [SerializeField] RectTransform m_Grid;
        [SerializeField] TMP_Text m_Tagline;
        [Header("The lobby, which isn't in the catalog")]
        [SerializeField] string m_LobbyTitle = "Lobby";
        [SerializeField] string m_LobbyTagline = "A quiet room to start from";
        [SerializeField] string m_LobbyIcon = "home";
        [SerializeField] Color m_LobbyTint = new(0.29f, 0.36f, 0.54f);

        readonly List<(DemoCatalog.Entry entry, GameObject running)> m_Tiles = new();
        float m_ShownAt = float.NegativeInfinity;
        bool m_Built;

        public bool IsShown => m_Root != null && M3Fade.IsShown(m_Root.gameObject);

        // The library's size in dp for this many apps, and the y of the tagline's middle from the top.
        public static Vector2 Size(int apps)
        {
            var rows = Mathf.Max(1, (apps + k_Columns - 1) / k_Columns);
            return new Vector2(k_Columns * k_CellWidth + (k_Columns - 1) * k_Gap + 2f * k_Padding,
                rows * k_CellHeight + (rows - 1) * k_Gap + k_Gap + k_TaglineHeight + 2f * k_Padding);
        }

        // Where an app's cell goes in the grid, its middle from the library's middle, in dp.
        public static Vector2 CellPosition(int index, Vector2 size)
        {
            var column = index % k_Columns;
            var row = index / k_Columns;
            var x = -size.x / 2f + k_Padding + k_CellWidth / 2f + column * (k_CellWidth + k_Gap);
            var y = size.y / 2f - k_Padding - k_CellHeight / 2f - row * (k_CellHeight + k_Gap);
            return new Vector2(x, y);
        }

        void Awake()
        {
            if (m_Settings != null)
                m_Settings.OpenChanged += OnSettingsOpenChanged;
        }

        void OnDestroy()
        {
            if (m_Settings != null)
                m_Settings.OpenChanged -= OnSettingsOpenChanged;
        }

        // The Apps button.
        public void Toggle() => Show(!IsShown);

        public void Show(bool show)
        {
            if (show)
                Build();
            if (show && !IsShown)
                m_ShownAt = Time.unscaledTime;
            M3Fade.SetShown(m_Root.gameObject, show);
            if (m_AppsButton != null)
                m_AppsButton.Selected = show;
            if (show)
            {
                m_Tagline.text = k_Prompt;
                MarkRunning();
            }
        }

        // The settings panel, opened in front of the user, with the library showing: how the lobby
        // greets the user (LobbyFlow).
        public void Open()
        {
            if (m_Settings != null)
                m_Settings.Open();
            Show(true);
        }

        // Closed with the panel, so it opens next time as the panel alone.
        void OnSettingsOpenChanged(bool open)
        {
            if (!open && IsShown)
                Show(false);
        }

        void Pick(DemoCatalog.Entry entry)
        {
            var title = entry != null ? entry.Title : m_LobbyTitle;
            if (Time.unscaledTime - m_ShownAt < k_PickGuardSeconds)
            {
                Debug.Log($"[Demos] Ignored a pick of {title} {Time.unscaledTime - m_ShownAt:F2} s after the library appeared");
                return;
            }
            if (m_Settings != null)
                m_Settings.Close();
            if (entry == m_Demos.Current)
            {
                Debug.Log($"[Demos] Picked {title} in the app library: already running");
                return;
            }
            Debug.Log($"[Demos] Picked {title} in the app library");
            if (entry == null)
                m_Demos.ExitToLobby();
            else
                m_Demos.Enter(entry);
        }

        void MarkRunning()
        {
            foreach (var (entry, running) in m_Tiles)
                running.SetActive(entry == m_Demos.Current);
        }

        // The lobby's tile, then one per demo; once.
        public void Build()
        {
            if (m_Built || m_Demos == null || m_Demos.Catalog == null)
                return;
            m_Built = true;
            var demos = m_Demos.Catalog.Demos;
            var size = Size(demos.Count + 1);
            Tile(0, size, null, m_LobbyTitle, m_LobbyTagline, m_LobbyIcon, m_LobbyTint);
            for (var i = 0; i < demos.Count; i++)
                Tile(i + 1, size, demos[i], demos[i].Title, demos[i].Tagline, demos[i].Icon, demos[i].Tint);
            m_Tagline.text = k_Prompt;
        }

        // One app: an icon on a colored rounded square over its name, the whole cell the target, with
        // a state layer behind it for the gaze, and a dot under the name while it's running.
        void Tile(int index, Vector2 size, DemoCatalog.Entry entry, string title, string tagline, string icon, Color tint)
        {
            var cell = M3.Group($"App {title}", m_Grid, CellPosition(index, size), new Vector2(k_CellWidth, k_CellHeight));
            var state = M3.Surface("State Layer", cell, Vector2.zero, cell.sizeDelta, Color.clear, M3Theme.k_ShapeLarge,
                raycastTarget: true);
            var button = cell.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = state;
            button.onClick.AddListener(() => Pick(entry));
            cell.gameObject.AddComponent<M3StateLayer>().Initialize(state, button, M3Theme.OnSurface);

            var tile = M3.Surface("Icon Tile", cell, new Vector2(0f, 16f), new Vector2(k_IconSize, k_IconSize), tint, k_IconRadius);
            M3.Icon("Icon", tile.rectTransform, icon, 40f, Vector2.zero, Color.white);
            var name = M3.Text("Name", cell, title, M3Theme.Type.LabelLarge, new Vector2(0f, -36f), new Vector2(k_CellWidth - 8f, 20f));
            name.textWrappingMode = TextWrappingModes.NoWrap;
            var running = M3.Surface("Running", cell, new Vector2(0f, -53f), new Vector2(6f, 6f), M3Theme.Primary, M3Theme.k_ShapeFull)
                .gameObject;
            running.SetActive(false);
            cell.gameObject.AddComponent<AppTileGaze>().Initialize(m_Tagline, string.IsNullOrEmpty(tagline) ? title : tagline, k_Prompt);
            m_Tiles.Add((entry, running));
        }
    }
}
