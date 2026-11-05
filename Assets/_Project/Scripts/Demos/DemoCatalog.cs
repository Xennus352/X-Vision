using System;
using System.Collections.Generic;
using UnityEngine;

namespace CardboardXR.Demos
{
    // The demos the app offers, in the order the app library shows them (after the lobby). The "demo" launch option picks
    // one by its index here. The build includes each entry's scene after Core and the lobby.
    [CreateAssetMenu(menuName = "CardboardXR/Demo Catalog")]
    public class DemoCatalog : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            [Tooltip("Short and stable, for logs.")]
            public string Id;
            public string Title;
            [TextArea] public string Description;
            [Tooltip("One line, shown in the app library while the app is looked at.")]
            public string Tagline;
            [Tooltip("The app library icon: a Material icon name (see M3Icons).")]
            public string Icon = "apps";
            [Tooltip("The app library icon's tile color.")]
            public Color Tint = new(0.29f, 0.36f, 0.54f);
            [Tooltip("The scene's path, such as Assets/_Project/Demos/Video/Video.unity.")]
            public string ScenePath;
            public DemoRequirements Requirements;
        }

        [Tooltip("The lobby scene's path.")]
        [SerializeField] string m_LobbyScenePath = "Assets/_Project/Demos/Lobby/Lobby.unity";
        [Tooltip("What the lobby switches on when the app starts: 6DoF and passthrough, so the room can be scanned " +
                 "there (LobbyFlow) and demos start with tracking running and surfaces found.")]
        [SerializeField] DemoRequirements m_LobbyRequirements = new() { SixDof = Need.On, Passthrough = Need.On, Hands = Need.On };
        [SerializeField] List<Entry> m_Demos = new();

        public string LobbyScenePath => m_LobbyScenePath;
        public DemoRequirements LobbyRequirements => m_LobbyRequirements;
        public IReadOnlyList<Entry> Demos => m_Demos;

        public bool TryGet(int index, out Entry entry)
        {
            entry = index >= 0 && index < m_Demos.Count ? m_Demos[index] : null;
            return entry != null;
        }

        public int IndexOf(string id) => m_Demos.FindIndex(entry => entry.Id == id);
    }
}
