using UnityEngine;

// Standalone pickup so Unity can resolve this component from its own .cs asset.
[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider2D))]
public sealed class DeadlineSpeedPickup : MonoBehaviour
{
    [Min(0.01f)] public float duration = 4f;
    private bool collected;

    private void Reset()
    {
        GetComponent<BoxCollider2D>().isTrigger = true;
    }

    private void Awake()
    {
        GetComponent<BoxCollider2D>().isTrigger = true;
    }

    private void OnTriggerEnter2D(Collider2D other) { TryCollect(other); }
    // Also works when the player is already overlapping as the pickup is enabled.
    private void OnTriggerStay2D(Collider2D other) { TryCollect(other); }

    private void TryCollect(Collider2D other)
    {
        if (collected) return;
        EffectProcessor effects = other.attachedRigidbody != null
            ? other.attachedRigidbody.GetComponent<EffectProcessor>() : null;
        if (effects == null) effects = other.GetComponentInParent<EffectProcessor>();
        if (effects == null) return;
        collected = true;
        effects.ApplySpeedBoost(duration);
        Debug.Log("[SpeedBoost] Collected: +50% movement speed for " + duration + " seconds.", effects);
        // Disable immediately so multiple overlapping colliders cannot collect twice.
        gameObject.SetActive(false);
        Destroy(gameObject);
    }
}
