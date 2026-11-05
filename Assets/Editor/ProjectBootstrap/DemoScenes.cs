using CardboardXR.Demos;
using CardboardXR.Demos.Paint;
using CardboardXR.Demos.Portal;
using CardboardXR.Demos.Rush;
using CardboardXR.Demos.Video;
using CardboardXR.Interaction;
using CardboardXR.Stereo;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectBootstrap
{
    // Creates the demo catalog, the lobby and the starter demo scenes, but only those that don't
    // exist yet. Unlike Core, these are made once and then edited by hand in the Editor, so a build
    // never overwrites them; delete one to have it made again. Their materials go in their own
    // folders for the same reason, since Core's generated ones are replaced on every build.
    public static class DemoScenes
    {
        const string DemosFolder = "Assets/_Project/Demos";
        internal const string CatalogPath = DemosFolder + "/DemoCatalog.asset";
        const string LobbyFolder = DemosFolder + "/Lobby";
        const string LobbyScene = LobbyFolder + "/Lobby.unity";
        const string SandboxFolder = DemosFolder + "/Sandbox";
        const string SandboxScene = SandboxFolder + "/Sandbox.unity";
        const string VideoFolder = DemosFolder + "/Video";
        const string VideoScene = VideoFolder + "/Video.unity";
        const string PaintFolder = DemosFolder + "/Paint";
        const string PaintScene = PaintFolder + "/Paint.unity";
        const string RushFolder = DemosFolder + "/Rush";
        const string RushScene = RushFolder + "/Rush.unity";
        const string PortalFolder = DemosFolder + "/Portal";
        const string PortalScene = PortalFolder + "/Portal.unity";
        const string DepthMaskShaderPath = "Assets/_Project/Shaders/DepthMask.shader";
        const string UnlitShaderName = "Universal Render Pipeline/Unlit";

        const float k_RoomSize = 10f;
        // The head sits at the origin, standing eye height above the floor.
        const float k_EyeHeight = 1.6f;

        // Makes whatever is missing and returns the catalog. Call it before opening another scene,
        // since it opens and saves its own.
        public static DemoCatalog EnsureAll()
        {
            StereoBuilder.EnsureFolder(DemosFolder);
            var catalog = EnsureCatalog();
            EnsureLobby();
            EnsureSandbox();
            EnsureVideo();
            EnsurePaint();
            EnsureRush();
            EnsurePortal();
            AssetDatabase.SaveAssets();
            return catalog;
        }

        static DemoCatalog EnsureCatalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<DemoCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<DemoCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            // Entries are added once each; after that the catalog is edited by hand, so a changed
            // title or order stays.
            EnsureEntry(catalog, "sandbox", "Cube Sandbox",
                "Your own room, through the camera. Look at a table or the floor and tap or pinch to put a cube there, then pick cubes up with your hand and move them.",
                SandboxScene, new DemoRequirements { SixDof = Need.On, Passthrough = Need.On, Hands = Need.On, ScanSurfaces = true });
            EnsureEntry(catalog, "video", "360 Video",
                "Watch 360 and VR180 videos, 2D or 3D, all around you. Tap the video for the controls. Plays videos copied to the phone or linked from Google Drive.",
                VideoScene, new DemoRequirements
                {
                    SixDof = Need.Off, Passthrough = Need.Off, Hands = Need.Off,
                });
            EnsureEntry(catalog, "paint", "Air Paint",
                "Paint in the air around you with your hand, like Tilt Brush. Pinch to paint; look at the tool panel and pinch to change color, brush and size.",
                PaintScene, new DemoRequirements
                {
                    SixDof = Need.On, Passthrough = Need.On, Hands = Need.On,
                });
            EnsureEntry(catalog, "rush", "Orb Rush",
                "Orbs fly toward you: hit them with your hand as they reach the rings. One hand, a minute a round, getting faster.",
                RushScene, new DemoRequirements
                {
                    SixDof = Need.On, Passthrough = Need.Off, Hands = Need.On,
                });
            EnsureEntry(catalog, "portal", "Portal",
                "Open a doorway on your floor and walk through it into another world, then look back at your room through the door.",
                PortalScene, new DemoRequirements
                {
                    SixDof = Need.On, Passthrough = Need.On, Hands = Need.On,
                });
            return catalog;
        }

        static void EnsureEntry(DemoCatalog catalog, string id, string title, string description, string scenePath,
            DemoRequirements requirements)
        {
            if (catalog.IndexOf(id) >= 0)
                return;
            var serialized = new SerializedObject(catalog);
            var demos = serialized.FindProperty("m_Demos");
            demos.arraySize++;
            var entry = demos.GetArrayElementAtIndex(demos.arraySize - 1);
            entry.FindPropertyRelative("Id").stringValue = id;
            entry.FindPropertyRelative("Title").stringValue = title;
            entry.FindPropertyRelative("Description").stringValue = description;
            entry.FindPropertyRelative("ScenePath").stringValue = scenePath;
            var needs = entry.FindPropertyRelative("Requirements");
            needs.FindPropertyRelative("SixDof").enumValueIndex = (int)requirements.SixDof;
            needs.FindPropertyRelative("Passthrough").enumValueIndex = (int)requirements.Passthrough;
            needs.FindPropertyRelative("Hands").enumValueIndex = (int)requirements.Hands;
            needs.FindPropertyRelative("ScanSurfaces").boolValue = requirements.ScanSurfaces;
            needs.FindPropertyRelative("KeepPlacedObjects").boolValue = requirements.KeepPlacedObjects;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log($"[DemoScenes] Added {id} to the catalog");
        }

        // The checkerboard room with its colored markers, which passthrough hides, and the demo cards.
        // Straight checker edges should look straight through the viewer.
        static void EnsureLobby()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(LobbyScene) != null)
                return;
            StereoBuilder.EnsureFolder(LobbyFolder);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var unlit = Shader.Find(UnlitShaderName);

            var environment = new GameObject("Environment");
            environment.AddComponent<VirtualEnvironment>();
            // Two checks per texture repeat, so one check is one meter. The tinted floor and ceiling
            // make a sideways or upside-down view obvious.
            var checker = CreateCheckerTexture(LobbyFolder);
            var tiling = k_RoomSize / 2f;
            BuildRoom(environment.transform,
                StereoBuilder.CreateMaterial("Checker Walls", unlit, Color.white, checker, tiling, LobbyFolder),
                StereoBuilder.CreateMaterial("Checker Floor", unlit, new Color(0.55f, 0.75f, 1f), checker, tiling, LobbyFolder),
                StereoBuilder.CreateMaterial("Checker Ceiling", unlit, new Color(1f, 0.8f, 0.55f), checker, tiling, LobbyFolder));
            BuildMarkers(environment.transform, unlit);

            // Not part of the environment: the hint stays in passthrough.
            new GameObject("Lobby Hint").AddComponent<LobbyHint>();

            EditorSceneManager.SaveScene(scene, LobbyScene);
            Debug.Log($"[DemoScenes] Made {LobbyScene}");
        }

        // Cubes on real surfaces, in passthrough: Core's default placement, with a checker floor in
        // case passthrough is turned off.
        static void EnsureSandbox()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(SandboxScene) != null)
                return;
            StereoBuilder.EnsureFolder(SandboxFolder);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var unlit = Shader.Find(UnlitShaderName);

            new GameObject("Sandbox Demo").AddComponent<Demo>();
            var environment = new GameObject("Environment");
            environment.AddComponent<VirtualEnvironment>();
            var checker = CreateCheckerTexture(SandboxFolder);
            var floor = StereoBuilder.CreatePrimitive(PrimitiveType.Quad, "Floor", environment.transform,
                StereoBuilder.CreateMaterial("Sandbox Floor", unlit, new Color(0.55f, 0.75f, 1f), checker, k_RoomSize / 2f, SandboxFolder),
                keepCollider: true);
            floor.transform.SetLocalPositionAndRotation(new Vector3(0f, -k_EyeHeight, 0f), Quaternion.Euler(90f, 0f, 0f));
            floor.transform.localScale = new Vector3(k_RoomSize, k_RoomSize, 1f);

            EditorSceneManager.SaveScene(scene, SandboxScene);
            Debug.Log($"[DemoScenes] Made {SandboxScene}");
        }

        // The video player: a sphere to draw on, the player and its panel (built at runtime).
        static void EnsureVideo()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(VideoScene) != null)
                return;
            StereoBuilder.EnsureFolder(VideoFolder);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            // Double-sided in case the sphere is ever seen from outside, and white so the video's own
            // colors show.
            var material = StereoBuilder.CreateMaterial("Video Sphere", Shader.Find(UnlitShaderName), Color.white, null, 1f, VideoFolder);

            var sphere = new GameObject("Video Sphere").AddComponent<VideoSphere>();
            var serializedSphere = new SerializedObject(sphere);
            serializedSphere.FindProperty("m_Material").objectReferenceValue = material;
            serializedSphere.ApplyModifiedPropertiesWithoutUndo();

            var demo = new GameObject("Video Demo").AddComponent<VideoDemo>();
            var serializedDemo = new SerializedObject(demo);
            serializedDemo.FindProperty("m_Sphere").objectReferenceValue = sphere;
            serializedDemo.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, VideoScene);
            Debug.Log($"[DemoScenes] Made {VideoScene}");
        }

        // Air Paint: the stroke materials, a dark floor grid for VR, and the demo.
        static void EnsurePaint()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(PaintScene) != null)
                return;
            StereoBuilder.EnsureFolder(PaintFolder);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var unlit = Shader.Find(UnlitShaderName);

            // White and double-sided: each stroke is colored with a property block, and a ribbon is
            // seen from both sides.
            var solid = StereoBuilder.CreateMaterial("Stroke", unlit, Color.white, null, 1f, PaintFolder);
            var glow = StereoBuilder.CreateMaterial("Stroke Glow", unlit, Color.white, null, 1f, PaintFolder);
            MakeAdditive(glow);

            var demo = new GameObject("Paint Demo").AddComponent<PaintDemo>();
            var serializedDemo = new SerializedObject(demo);
            serializedDemo.FindProperty("m_SolidMaterial").objectReferenceValue = solid;
            serializedDemo.FindProperty("m_GlowMaterial").objectReferenceValue = glow;
            serializedDemo.ApplyModifiedPropertiesWithoutUndo();

            // A dark floor with a faint grid every half meter, so there's a sense of space and scale
            // in VR. Passthrough hides it.
            var environment = new GameObject("Environment");
            environment.AddComponent<VirtualEnvironment>();
            var grid = StereoBuilder.SaveAsset(CreateGridTexture(), $"{PaintFolder}/Grid.asset");
            const float floorSize = 30f;
            var floor = StereoBuilder.CreatePrimitive(PrimitiveType.Quad, "Floor", environment.transform,
                StereoBuilder.CreateMaterial("Paint Floor", unlit, Color.white, grid, floorSize / 0.5f, PaintFolder));
            floor.transform.SetLocalPositionAndRotation(new Vector3(0f, -k_EyeHeight, 0f), Quaternion.Euler(90f, 0f, 0f));
            floor.transform.localScale = new Vector3(floorSize, floorSize, 1f);

            EditorSceneManager.SaveScene(scene, PaintScene);
            Debug.Log($"[DemoScenes] Made {PaintScene}");
        }

        // Orb Rush: the orbs' core and glowing halo, the track's rails, a dark floor grid, and the demo.
        static void EnsureRush()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(RushScene) != null)
                return;
            StereoBuilder.EnsureFolder(RushFolder);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var unlit = Shader.Find(UnlitShaderName);

            // White: each orb is colored with a property block.
            var orb = StereoBuilder.CreateMaterial("Orb", unlit, Color.white, null, 1f, RushFolder);
            var halo = StereoBuilder.CreateMaterial("Orb Halo", unlit, Color.white, null, 1f, RushFolder);
            MakeAdditive(halo);
            var rail = StereoBuilder.CreateMaterial("Rail", unlit, new Color(0.25f, 0.4f, 0.7f), null, 1f, RushFolder);

            var demo = new GameObject("Rush Demo").AddComponent<RushDemo>();
            var serializedDemo = new SerializedObject(demo);
            serializedDemo.FindProperty("m_OrbMaterial").objectReferenceValue = orb;
            serializedDemo.FindProperty("m_HaloMaterial").objectReferenceValue = halo;
            serializedDemo.FindProperty("m_RailMaterial").objectReferenceValue = rail;
            serializedDemo.ApplyModifiedPropertiesWithoutUndo();

            var environment = new GameObject("Environment");
            environment.AddComponent<VirtualEnvironment>();
            var grid = StereoBuilder.SaveAsset(CreateGridTexture(), $"{RushFolder}/Grid.asset");
            const float floorSize = 40f;
            var floor = StereoBuilder.CreatePrimitive(PrimitiveType.Quad, "Floor", environment.transform,
                StereoBuilder.CreateMaterial("Rush Floor", unlit, Color.white, grid, floorSize / 0.5f, RushFolder));
            floor.transform.SetLocalPositionAndRotation(new Vector3(0f, -k_EyeHeight, 0f), Quaternion.Euler(90f, 0f, 0f));
            floor.transform.localScale = new Vector3(floorSize, floorSize, 1f);

            EditorSceneManager.SaveScene(scene, RushScene);
            Debug.Log($"[DemoScenes] Made {RushScene}");
        }

        // Portal: the portal prefab (built out in code when placed) with its materials, and the demo.
        static void EnsurePortal()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(PortalScene) != null)
                return;
            StereoBuilder.EnsureFolder(PortalFolder);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var unlit = Shader.Find(UnlitShaderName);

            var sky = StereoBuilder.CreateMaterial("Night Sky", unlit, Color.white,
                StereoBuilder.SaveAsset(CreateSkyTexture(), $"{PortalFolder}/Night Sky.asset"), 1f, PortalFolder);
            var ground = StereoBuilder.CreateMaterial("Portal Ground", unlit, new Color(0.8f, 0.7f, 1f),
                StereoBuilder.SaveAsset(CreateGridTexture(), $"{PortalFolder}/Grid.asset"), 46f / 1f, PortalFolder);
            var glow = StereoBuilder.CreateMaterial("Portal Glow", unlit, Color.white, null, 1f, PortalFolder);
            MakeAdditive(glow);
            var mask = StereoBuilder.SaveAsset(new Material(AssetDatabase.LoadAssetAtPath<Shader>(DepthMaskShaderPath)) { name = "Depth Mask" },
                $"{PortalFolder}/Depth Mask.mat");

            var portal = new GameObject("Portal").AddComponent<PortalWorld>();
            var serializedPortal = new SerializedObject(portal);
            serializedPortal.FindProperty("m_Sky").objectReferenceValue = sky;
            serializedPortal.FindProperty("m_Ground").objectReferenceValue = ground;
            serializedPortal.FindProperty("m_Glow").objectReferenceValue = glow;
            serializedPortal.FindProperty("m_Mask").objectReferenceValue = mask;
            serializedPortal.ApplyModifiedPropertiesWithoutUndo();
            var prefab = PrefabUtility.SaveAsPrefabAsset(portal.gameObject, $"{PortalFolder}/Portal.prefab");
            Object.DestroyImmediate(portal.gameObject);

            var demo = new GameObject("Portal Demo").AddComponent<PortalDemo>();
            var serializedDemo = new SerializedObject(demo);
            serializedDemo.FindProperty("m_PortalPrefab").objectReferenceValue = prefab;
            serializedDemo.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, PortalScene);
            Debug.Log($"[DemoScenes] Made {PortalScene}");
        }

        // A night sky: deep blue overhead, a violet glow at the horizon, and stars, denser higher up.
        // Across is longitude (the dome maps half a turn to one repeat), up is height above the horizon.
        static Texture2D CreateSkyTexture()
        {
            const int width = 1024;
            const int height = 256;
            var zenith = new Color(0.01f, 0.02f, 0.07f);
            var horizon = new Color(0.16f, 0.08f, 0.3f);
            var pixels = new Color[width * height];
            for (var y = 0; y < height; y++)
            {
                var color = Color.Lerp(horizon, zenith, Mathf.Pow(y / (height - 1f), 0.6f));
                for (var x = 0; x < width; x++)
                    pixels[y * width + x] = color;
            }
            var random = new System.Random(5);
            for (var i = 0; i < 900; i++)
            {
                var y = (int)(Mathf.Sqrt((float)random.NextDouble()) * (height - 1));
                var x = random.Next(width);
                var brightness = Mathf.Lerp(0.4f, 1f, (float)random.NextDouble());
                pixels[y * width + x] = new Color(brightness, brightness, Mathf.Min(1f, brightness * 1.1f));
            }
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true)
            {
                name = "Night Sky",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
            };
            texture.SetPixels(pixels);
            texture.Apply(true);
            return texture;
        }

        // Transparent and additive, set up as URP's material inspector would. Saved as an asset, so
        // the build keeps the shader variant it needs.
        static void MakeAdditive(Material material)
        {
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
            EditorUtility.SetDirty(material);
        }

        static Texture2D CreateGridTexture()
        {
            const int size = 128;
            var background = new Color32(14, 16, 24, 255);
            var line = new Color32(55, 75, 110, 255);
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                    pixels[y * size + x] = x < 2 || y < 2 ? line : background;
            }
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                name = "Grid",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
            };
            texture.SetPixels32(pixels);
            texture.Apply(true);
            return texture;
        }

        static void BuildRoom(Transform parent, Material walls, Material floor, Material ceiling)
        {
            var room = new GameObject("Checker Room");
            room.transform.SetParent(parent, false);
            var half = k_RoomSize / 2f;
            var middle = half - k_EyeHeight;
            (string name, Vector3 position, Vector3 rotation, Material material)[] surfaces =
            {
                ("Front", new Vector3(0f, middle, half), Vector3.zero, walls),
                ("Back", new Vector3(0f, middle, -half), new Vector3(0f, 180f, 0f), walls),
                ("Right", new Vector3(half, middle, 0f), new Vector3(0f, 90f, 0f), walls),
                ("Left", new Vector3(-half, middle, 0f), new Vector3(0f, -90f, 0f), walls),
                ("Floor", new Vector3(0f, -k_EyeHeight, 0f), new Vector3(90f, 0f, 0f), floor),
                ("Ceiling", new Vector3(0f, k_RoomSize - k_EyeHeight, 0f), new Vector3(-90f, 0f, 0f), ceiling),
            };

            foreach (var (name, position, rotation, material) in surfaces)
            {
                // Colliders stop the gaze, so the reticle sits on the walls.
                var surface = StereoBuilder.CreatePrimitive(PrimitiveType.Quad, name, room.transform, material, keepCollider: true);
                surface.transform.SetLocalPositionAndRotation(position, Quaternion.Euler(rotation));
                surface.transform.localScale = new Vector3(k_RoomSize, k_RoomSize, 1f);
            }
        }

        // Colored cubes in known directions. The white cube sits above the red one and green is on
        // the right, so a flipped or mirrored image is obvious. The orange cube is close, for depth.
        // Each lightens under the gaze.
        static void BuildMarkers(Transform parent, Shader unlit)
        {
            var markers = new GameObject("Markers");
            markers.transform.SetParent(parent, false);
            (string name, Color color, Vector3 position, float size)[] cubes =
            {
                ("Ahead", Color.red, new Vector3(0f, 0f, 3f), 0.4f),
                ("Above Ahead", Color.white, new Vector3(0f, 0.8f, 3f), 0.2f),
                ("Right", Color.green, new Vector3(3f, 0f, 0f), 0.4f),
                ("Left", Color.blue, new Vector3(-3f, 0f, 0f), 0.4f),
                ("Behind", Color.magenta, new Vector3(0f, 0f, -3f), 0.4f),
                ("Near", new Color(1f, 0.5f, 0f), new Vector3(0.25f, -0.1f, 0.7f), 0.08f),
            };

            foreach (var (name, color, position, size) in cubes)
            {
                var material = StereoBuilder.CreateMaterial(name, unlit, color, null, 1f, LobbyFolder);
                var cube = StereoBuilder.CreatePrimitive(PrimitiveType.Cube, name, markers.transform, material, keepCollider: true);
                cube.transform.localPosition = position;
                cube.transform.localScale = Vector3.one * size;
                cube.AddComponent<GazeHighlight>();
            }
        }

        static Texture2D CreateCheckerTexture(string folder)
        {
            const int size = 256;
            var light = new Color32(215, 215, 215, 255);
            var dark = new Color32(60, 60, 75, 255);
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                    pixels[y * size + x] = (x < size / 2) == (y < size / 2) ? light : dark;
            }

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                name = "Checker",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
            };
            texture.SetPixels32(pixels);
            texture.Apply(true);
            return StereoBuilder.SaveAsset(texture, $"{folder}/Checker.asset");
        }
    }
}
