#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class RepairDeadlineSpeedPickup
{
    [MenuItem("Deadline/Repair Speed Boost Pickups")]
    private static void Repair()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorUtility.DisplayDialog("Stop Play mode", "Run this command outside Play mode.", "OK");
            return;
        }
        Scene scene = SceneManager.GetActiveScene();
        int count = 0;
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Repair speed pickups");
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                GameObject obj = child.gameObject;
                string normalized = obj.name.Replace("_", "").Replace(" ", "").ToLowerInvariant();
                bool isPickup = normalized.Contains("speedboost") && obj.GetComponent<Collider2D>() != null;
                MonoBehaviour[] components = obj.GetComponents<MonoBehaviour>();
                foreach (MonoBehaviour component in components)
                    if (component != null && component.GetType().Name == "SpeedBoostPickup") isPickup = true;
                if (!isPickup) continue;
                Undo.RegisterCompleteObjectUndo(obj, "Repair speed pickup");
                foreach (MonoBehaviour component in components)
                {
                    if (component != null && component.GetType().Name == "SpeedBoostPickup")
                        Undo.DestroyObjectImmediate(component);
                }
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(obj);
                DeadlineSpeedPickup pickup = obj.GetComponent<DeadlineSpeedPickup>();
                if (pickup == null) pickup = Undo.AddComponent<DeadlineSpeedPickup>(obj);
                Undo.RecordObject(pickup, "Set boost duration");
                pickup.duration = 4f;
                Collider2D collider = obj.GetComponent<Collider2D>();
                Undo.RecordObject(collider, "Set pickup trigger");
                collider.enabled = true;
                collider.isTrigger = true;
                EditorUtility.SetDirty(pickup);
                PrefabUtility.RecordPrefabInstancePropertyModifications(pickup);
                PrefabUtility.RecordPrefabInstancePropertyModifications(collider);
                count++;
            }
        }
        Undo.CollapseUndoOperations(group);
        if (count > 0) EditorSceneManager.MarkSceneDirty(scene);
        EditorUtility.DisplayDialog("Speed pickup repair", "Repaired " + count + " speed pickup(s). Save this scene, then Play and touch the yellow pickup. Teleport, slowdown, and player movement code were not changed.", "OK");
    }
}
#endif
