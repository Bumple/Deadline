#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Editor-only room authoring tool. No runtime controller changes.
public class DeadlineRoomBuilder : EditorWindow
{
    private float slideClearanceRatio = 0.75f;
    private float precisionClearanceRatio = 1.1f;
    private float jumpGapRatio = 0.75f;
    private bool keepOriginalRoomFirst = true;
    private string result = "";
    private Sprite square;
    private Material material;
    private PhysicsMaterial2D physicsMaterial;
    private Color solidColor = new Color(0.08f, 0.17f, 0.34f);
    private int wallLayer;
    private string destination;
    private float h, w;
    private GameObject player;

    [MenuItem("Deadline/Build Hall 1 and Room 2")]
    private static void ShowWindow()
    {
        GetWindow<DeadlineRoomBuilder>("Deadline Rooms");
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Build a separate playable layout preview", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Open your working deadline2 scene first. The tool saves a COPY, keeps your player and camera, and builds editable room prefabs. Your source scene and controller are not modified. Layouts approximate your drawings and require play-testing.", MessageType.Info);
        keepOriginalRoomFirst = EditorGUILayout.Toggle("Start with existing Room01", keepOriginalRoomFirst);
        slideClearanceRatio = EditorGUILayout.Slider("Slide opening / player height", slideClearanceRatio, 0.65f, 0.9f);
        precisionClearanceRatio = EditorGUILayout.Slider("Precision opening / height", precisionClearanceRatio, 1.05f, 1.4f);
        jumpGapRatio = EditorGUILayout.Slider("Long jump / calculated reach", jumpGapRatio, 0.5f, 0.85f);
        EditorGUILayout.HelpBox("Slide shape remains 60% height and 120% width. Openings have extra clearance. No hazards, pits, generation, or power-ups are added.", MessageType.None);
        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            if (GUILayout.Button("Build preview scene and prefabs", GUILayout.Height(36))) Build();
        if (!string.IsNullOrEmpty(result)) EditorGUILayout.HelpBox(result, MessageType.Info);
    }

    private void Build()
    {
        try
        {
            Scene source = SceneManager.GetActiveScene();
            player = FindPlayer(source);
            if (player == null) throw new Exception("No active object with a PlayerController was found. Open deadline2 first.");
            BoxCollider2D collider = player.GetComponent<BoxCollider2D>();
            if (collider == null) throw new Exception("The player needs a BoxCollider2D.");
            h = collider.size.y * Mathf.Abs(player.transform.lossyScale.y);
            w = collider.size.x * Mathf.Abs(player.transform.lossyScale.x);
            if (h <= 0f || w <= 0f) throw new Exception("Player dimensions must be positive.");
            wallLayer = LayerMask.NameToLayer("Wall");
            if (wallLayer < 0) throw new Exception("Create the Wall layer before using this tool.");
            // Save-copy includes current scene contents without overwriting the source.
            Directory.CreateDirectory("Assets/GeneratedDeadlineRooms");
            AssetDatabase.Refresh();
            destination = AssetDatabase.GenerateUniqueAssetPath("Assets/GeneratedDeadlineRooms/LayoutSet");
            Directory.CreateDirectory(destination);
            AssetDatabase.Refresh();
            string scenePath = destination + "/RoomsPreview.unity";
            if (!EditorSceneManager.SaveScene(source, scenePath, true))
                throw new Exception("Could not save the scene copy.");
            // Open only the new copy; original scene assets are unchanged.
            Scene preview = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            player = FindPlayer(preview);
            if (player == null) throw new Exception("Could not find the player in the copied scene.");
            collider = player.GetComponent<BoxCollider2D>();
            physicsMaterial = collider.sharedMaterial;
            if (physicsMaterial == null && player.GetComponent<Rigidbody2D>() != null)
                physicsMaterial = player.GetComponent<Rigidbody2D>().sharedMaterial;
            GameObject originalRoom = null;
            foreach (GameObject root in preview.GetRootGameObjects())
            {
                if (root.name == "Room01") originalRoom = root;
                bool keep = root == player || player.transform.IsChildOf(root.transform) ||
                    root.GetComponentInChildren<Camera>(true) != null ||
                    root.name == "Global Light 2D" || root.name == "EventSystem" ||
                    (keepOriginalRoomFirst && root.name == "Room01");
                if (!keep) root.SetActive(false);
            }
            if (!keepOriginalRoomFirst && originalRoom != null) originalRoom.SetActive(false);
            MakeArtAssets();
            GameObject hall = Hall();
            GameObject room = Room();
            PrefabUtility.SaveAsPrefabAssetAndConnect(hall, destination + "/Hall01.prefab", InteractionMode.AutomatedAction);
            PrefabUtility.SaveAsPrefabAssetAndConnect(room, destination + "/Room02.prefab", InteractionMode.AutomatedAction);
            if (keepOriginalRoomFirst && originalRoom != null)
            {
                originalRoom.SetActive(true);
                Transform exit = originalRoom.transform.Find("Exit");
                if (exit == null) throw new Exception("Room01 has no direct child named Exit. Uncheck Start with existing Room01 and rebuild.");
                Align(hall, exit.position);
            }
            else
            {
                Transform spawn = hall.transform.Find("SpawnPoint");
                // Account for collider offsets, keeping the actual feet above the floor.
                player.transform.position = spawn.position - player.transform.TransformVector((Vector3)collider.offset);
            }
            Align(room, hall.transform.Find("Exit").position);
            Camera camera = null;
            foreach (GameObject root in preview.GetRootGameObjects())
                if (root.activeInHierarchy && root.GetComponentInChildren<Camera>() != null)
                    camera = root.GetComponentInChildren<Camera>();
            if (camera != null)
                camera.transform.position = new Vector3(player.transform.position.x + 4f * h, player.transform.position.y + 2f * h, camera.transform.position.z);
            EditorSceneManager.MarkSceneDirty(preview);
            EditorSceneManager.SaveScene(preview);
            AssetDatabase.SaveAssets();
            Selection.activeGameObject = hall;
            result = "Created " + scenePath + ". Press Play to test. Your original scene remains in its original path. Every build creates a new folder.";
            Debug.Log(result);
        }
        catch (Exception ex)
        {
            result = ex.Message;
            Debug.LogError("Room builder: " + ex.Message);
        }
    }

    private static GameObject FindPlayer(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
            foreach (MonoBehaviour component in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (component != null && component.GetType().Name == "PlayerController" && component.gameObject.activeInHierarchy)
                    return component.gameObject;
        return null;
    }

    private void MakeArtAssets()
    {
        // Persistent one-unit sprite, independent of the player's artwork.
        Texture2D texture = new Texture2D(16, 16, TextureFormat.RGBA32, false);
        Color[] pixels = new Color[256];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = Color.white;
        texture.SetPixels(pixels);
        texture.Apply();
        string path = destination + "/RoomSquare.png";
        File.WriteAllBytes(path, texture.EncodeToPNG());
        DestroyImmediate(texture);
        AssetDatabase.ImportAsset(path);
        TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Sprite;
        importer.spritePixelsPerUnit = 16f;
        importer.filterMode = FilterMode.Point;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.SaveAndReimport();
        square = AssetDatabase.LoadAssetAtPath<Sprite>(path);
        Shader shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        if (shader == null) throw new Exception("Could not find a sprite shader.");
        material = new Material(shader);
        AssetDatabase.CreateAsset(material, destination + "/RoomMaterial.mat");
    }

    // All rectangle coordinates are edges, expressed in player-height units.
    private void Block(GameObject root, string name, float left, float bottom, float right, float top)
    {
        if (right <= left || top <= bottom) throw new Exception("Invalid rectangle: " + name);
        GameObject block = new GameObject(name);
        block.layer = wallLayer;
        block.transform.SetParent(root.transform, false);
        block.transform.localPosition = new Vector3((left + right) * h * 0.5f, (bottom + top) * h * 0.5f, 0f);
        block.transform.localScale = new Vector3((right - left) * h, (top - bottom) * h, 1f);
        SpriteRenderer renderer = block.AddComponent<SpriteRenderer>();
        renderer.sprite = square;
        renderer.sharedMaterial = material;
        renderer.color = solidColor;
        BoxCollider2D box = block.AddComponent<BoxCollider2D>();
        box.size = Vector2.one;
        box.sharedMaterial = physicsMaterial;
    }

    private void Marker(GameObject root, string name, float x, float y)
    {
        GameObject marker = new GameObject(name);
        marker.transform.SetParent(root.transform, false);
        marker.transform.localPosition = new Vector3(x * h, y * h, 0f);
        // Empty transform: no renderer and no collider.
    }

    private GameObject Hall()
    {
        GameObject root = new GameObject("Hall01");
        Block(root, "ContinuousRecoveryFloor", 0, -1, 40, 0);
        Block(root, "Ceiling", 0, 8, 40, 8.5f);
        Block(root, "FirstHurdle", 4, 0, 5.3f, 1.3f);
        Block(root, "SlideUnderHangingBlock", 9, slideClearanceRatio, 11, 8);
        Block(root, "PickupPlatform_EmptyForNow", 14, 2.1f, 17, 2.5f);
        Block(root, "StepToJump", 18, 0, 19.5f, 0.7f);
        Block(root, "LongJumpTakeoff", 20, 0, 23, 1.5f);
        // Approximate equal-height reach; leave a margin for the collider and timing.
        Component controller = player.GetComponent("PlayerController");
        SerializedObject settings = new SerializedObject(controller);
        float speed = settings.FindProperty("moveSpeed").floatValue;
        float jump = settings.FindProperty("jumpForce").floatValue;
        float gravityScale = player.GetComponent<Rigidbody2D>().gravityScale;
        // The supplied controller sets gravityScale to 3 in Awake.
        gravityScale = 3f;
        float gravity = Mathf.Abs(Physics2D.gravity.y) * gravityScale;
        float reach = gravity > 0.01f ? speed * 2f * jump / gravity : 7f * h;
        float gap = Mathf.Clamp((reach * jumpGapRatio - w) / h, 3f, 9f);
        float landing = 23f + gap;
        Block(root, "LongJumpLanding", landing, 0, 38, 1.5f);
        // Lower floor and the landing wall allow recovery after a missed jump.
        Marker(root, "Entrance", 0, 0.5f);
        Marker(root, "SpawnPoint", 2, 0.55f);
        Marker(root, "Exit", 40, 0.5f);
        Marker(root, "FutureRandomPowerup", 15.5f, 3.4f);
        return root;
    }

    private GameObject Room()
    {
        GameObject root = new GameObject("Room02");
        Block(root, "ContinuousRecoveryFloor", 0, -1, 34, 0);
        Block(root, "Ceiling", 0, 14, 34, 14.5f);
        Block(root, "HighEntrancePlatform", 0, 0, 3, 10);
        // The first hanging divider forces descent; its foot passage needs a slide.
        Block(root, "LeftHangingDivider", 5, slideClearanceRatio, 6.5f, 14);
        // Wall-jump up the middle divider and pass through the snug top opening.
        float top = 14f - precisionClearanceRatio;
        Block(root, "MiddleClimbWall", 10, 0, 11.5f, top);
        Block(root, "UpperLeftShelf", 11.5f, 9, 20, 9.6f);
        Block(root, "LowerLeftShelf", 11.5f, 4.7f, 20, 5.3f);
        Block(root, "UpperRightShelf", 23, 7, 29, 7.6f);
        Block(root, "LowerRightShelf", 23, 2.3f, 29, 2.9f);
        Block(root, "RightSupport", 28, 2.9f, 29, 7);
        // Snug gap between this hanging column and the upper right shelf.
        Block(root, "RightPrecisionBaffle", 26, 7.6f + precisionClearanceRatio, 27, 14);
        Block(root, "FinalSlideBaffle", 31, slideClearanceRatio, 32.5f, 14);
        Marker(root, "Entrance", 0, 10.5f);
        Marker(root, "SpawnPoint", 1.5f, 10.55f);
        Marker(root, "Exit", 34, 0.5f);
        return root;
    }

    private static void Align(GameObject root, Vector3 previousExit)
    {
        root.transform.position += previousExit - root.transform.Find("Entrance").position;
    }
}
#endif
