using UnityEngine;

// Generated pickup (standalone file, so Unity can always resolve the component).
// kind 0 = speed boost (x1.5 for 4 s), kind 1 = death-wall rewind (3 s of its current travel).
// The v5 generator only spawns kinds 0 and 1. Kind 2 (teleport) is kept for old scenes only.
public class DeadlineLabPickup : MonoBehaviour
{
    public int kind;
    private bool collected;
    private SpriteRenderer sprite;
    private Color baseColor;
    private Vector3 baseScale;

    private void Awake()
    {
        sprite = GetComponent<SpriteRenderer>();
        if (sprite != null) baseColor = sprite.color;
        baseScale = transform.localScale;
    }

    private void Update()
    {
        // Gentle pulse and spin so pickups read as collectable, not as geometry.
        float pulse = 1f + 0.12f * Mathf.Sin(Time.time * 5f);
        transform.localScale = baseScale * pulse;
        transform.localRotation = Quaternion.Euler(0f, 0f, Time.time * 90f);
        if (sprite != null) sprite.color = Color.Lerp(baseColor, Color.white, 0.25f + 0.25f * Mathf.Sin(Time.time * 7f));
    }

    private void OnTriggerEnter2D(Collider2D other) { Collect(other); }
    private void OnTriggerStay2D(Collider2D other) { Collect(other); }

    private void Collect(Collider2D other)
    {
        if (collected || !other.attachedRigidbody) return;
        var effects = other.attachedRigidbody.GetComponent<EffectProcessor>();
        if (!effects) return;
        if (kind == 0) effects.ApplySpeedBoost();
        else if (kind == 1) { if (!effects.RewindBarrier()) return; }
        else { if (effects.HasTeleportCharge()) return; effects.AddTeleportCharge(); }
        collected = true;
        gameObject.SetActive(false);
        Destroy(gameObject);
    }
}
