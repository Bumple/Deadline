using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Text;

// Editor commands for the procedural test scene (ProceduralLab 2). RoomsPreview is never modified.
public static class DeadlinePatternLabSetup
{
    private static void CopySlideSettings(DeadlinePatternCourse course)
    {
        if (!course.player) return;
        var data = new SerializedObject(course.player);
        var height = data.FindProperty("slideHeightMultiplier");
        var width = data.FindProperty("slideWidthMultiplier");
        var deceleration = data.FindProperty("slideDeceleration");
        if (height != null) course.slideHeightRatio = height.floatValue;
        if (width != null) course.slideWidthRatio = width.floatValue;
        if (deceleration != null) course.slideDeceleration = deceleration.floatValue;
    }

    private static DeadlinePatternCourse FindCourse(bool requireLabScene)
    {
        var scene = SceneManager.GetActiveScene();
        if (requireLabScene && !scene.name.StartsWith("ProceduralLab"))
        {
            EditorUtility.DisplayDialog("Open ProceduralLab 2",
                "Open Assets/DeadlineProceduralLab/ProceduralLab 2 first (not RoomsPreview).", "OK");
            return null;
        }
        var course = Object.FindAnyObjectByType<DeadlinePatternCourse>();
        if (!course) Debug.LogError("No ProceduralCourse (DeadlinePatternCourse) found in this scene.");
        return course;
    }

    [MenuItem("Deadline/Upgrade Current Procedural Lab")]
    public static void Upgrade()
    {
        if (EditorApplication.isPlaying) { Debug.LogWarning("Stop Play Mode first."); return; }
        var course = FindCourse(true);
        if (!course) return;
        var scene = SceneManager.GetActiveScene();
        Undo.RecordObject(course, "Upgrade procedural lab to v5");
        course.newSeedEachRun = true;
        course.spawnPickups = true;
        course.minimumRoomsBetweenPickups = 2;
        course.maximumRoomsBetweenPickups = 3;
        course.spawnSlowdownZones = true;
        course.slowdownRoomChance = 0.6f;
        course.slowdownCandidateChance = 0.35f;
        course.maxSlowdownZonesPerRoom = 2;
        course.twoChamberRoomChance = 0.75f;
        course.trapScan = true;
        course.generateAhead = Mathf.Max(course.generateAhead, 160f);
        CopySlideSettings(course);
        EditorUtility.SetDirty(course);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Selection.activeGameObject = course.gameObject;
        Debug.Log("[Deadline v5] Lab ready: multi-storey chambers, slowdown zones on, one optional pickup every 2-3 rooms " +
                  "(speed boost / death-wall rewind). Press Play.");
    }

    [MenuItem("Deadline/Autoplay Test (while playing)")]
    public static void Autoplay()
    {
        if (!EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("Press Play first", "Open ProceduralLab 2, press Play, then choose Deadline > Autoplay Test.", "OK");
            return;
        }
        var player = Object.FindAnyObjectByType<PlayerController>();
        if (!player) { Debug.LogError("No PlayerController in the playing scene."); return; }
        if (!player.GetComponent<DeadlineAutoplayBot>()) player.gameObject.AddComponent<DeadlineAutoplayBot>();
        Selection.activeGameObject = player.gameObject;
    }

    [MenuItem("Deadline/Test Procedural Seeds (no Play Mode)")]
    public static void TestSeeds()
    {
        if (EditorApplication.isPlaying) { Debug.LogWarning("Stop Play Mode first."); return; }
        var course = FindCourse(false);
        if (!course || !course.player) { Debug.LogError("Assign the player on ProceduralCourse first."); return; }
        DeadlineRouteValidator.MoveSpec spec = DeadlinePatternCourse.ReadMoveSpec(course.player, course);
        if (spec == null) { Debug.LogError("Could not read the player's collider/movement settings."); return; }
        const int seeds = 20, rooms = 12;
        var report = new StringBuilder();
        var seedLines = new StringBuilder();
        int totalRooms = 0, fallback = 0, rejected = 0, speed = 0, rewind = 0, zones = 0, clean = 0, failedSeeds = 0;
        float minY = 0f, maxY = 0f;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            for (int s = 1; s <= seeds; s++)
            {
                EditorUtility.DisplayProgressBar("Deadline v5 seed test", "Seed " + s + " of " + seeds, (s - 1f) / seeds);
                var planner = new DeadlineCoursePlanner(spec, course.MakeSettings(), s);
                float h = spec.height;
                planner.SetStart(DeadlinePatternCourse.StartBlocks(h), new Vector2(10.5f * h, 0f), 12f * h, 0f);
                var line = new StringBuilder("seed " + s + ": ");
                bool failed = false;
                for (int r = 0; r < rooms; r++)
                {
                    DeadlineCoursePlanner.Room room = planner.PlanNextRoom();
                    if (room == null) { failed = true; line.Append(" FAILED at room " + (r + 1)); break; }
                    totalRooms++;
                    if (room.usedFallback) fallback++;
                    zones += room.hazards.Count;
                    if (room.hazards.Count == 0) clean++;
                    if (room.pickupKind == 0) speed++;
                    if (room.pickupKind == 1) rewind++;
                    var names = new List<string>();
                    foreach (var c in room.chambers) names.Add(c.name);
                    line.Append(string.Join("+", names)).Append(room.pickupKind == 0 ? "[S]" : room.pickupKind == 1 ? "[R]" : "")
                        .Append(room.hazards.Count > 0 ? "{" + room.hazards.Count + "}" : "").Append("  ");
                    minY = Mathf.Min(minY, planner.EndY);
                    maxY = Mathf.Max(maxY, planner.EndY);
                }
                rejected += planner.RejectedChambers;
                foreach (string why in planner.rejections) report.AppendLine("    " + why);
                if (failed) failedSeeds++;
                seedLines.AppendLine(line.ToString());
            }
        }
        finally { EditorUtility.ClearProgressBar(); }
        Debug.Log("[Deadline v5] Seed test: " + seeds + " seeds x " + rooms + " rooms in " + (watch.ElapsedMilliseconds / 1000f).ToString("0.0") + " s\n" +
                  "rooms " + totalRooms + ", failed seeds " + failedSeeds + ", fallback rooms " + fallback + ", rejected chambers (retried) " + rejected + "\n" +
                  "pickups: speed " + speed + ", rewind " + rewind + " | slowdown zones " + zones + ", rooms with none " + clean + "\n" +
                  "elevation range " + minY.ToString("0.0") + " to " + maxY.ToString("0.0") + " units\n" +
                  "[S]=speed pickup [R]=rewind pickup {n}=slowdown zones\n" + seedLines + "Rejections (each was retried):\n" + report);
    }
}
