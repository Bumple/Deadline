using UnityEngine;
using System.Collections.Generic;

// Red slowdown zone. Never lethal; it never blocks movement (its collider is a trigger).
// Policy (predictable, never stacking):
//   - First touch: slowdown x0.5 starts for its full duration (2.5 s) with HUD/sound feedback.
//   - Still touching, or touching again while slowed: the timer is topped back up to the full duration,
//     so the slowdown always ends 2.5 s after the LAST contact. The multiplier is always x0.5, never lower.
public class SlowdownHazard : MonoBehaviour
{
    [SerializeField] private float duration = 2.5f;
    private readonly HashSet<EffectProcessor> playersInside = new HashSet<EffectProcessor>();
    private SpriteRenderer sprite;
    private Color baseColor;
    private float flash;

    private void Awake()
    {
        sprite = GetComponent<SpriteRenderer>();
        if (sprite != null) baseColor = sprite.color;
    }

    private static EffectProcessor Find(Collider2D other)
    {
        if (other.attachedRigidbody != null && other.attachedRigidbody.TryGetComponent<EffectProcessor>(out var ep)) return ep;
        return null;
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        EffectProcessor ep = Find(other);
        if (ep == null) return;
        if (playersInside.Add(ep)) ep.ApplySlowdown(duration);
        flash = 0.4f;
    }

    private void OnTriggerStay2D(Collider2D other)
    {
        EffectProcessor ep = Find(other);
        if (ep != null) ep.RefreshSlowdown(duration);
    }

    private void OnTriggerExit2D(Collider2D other)
    {
        EffectProcessor ep = Find(other);
        if (ep != null) playersInside.Remove(ep);
    }

    private void Update()
    {
        // Flash white briefly when touched, so the player can see what slowed them.
        if (sprite == null || flash <= 0f) return;
        flash = Mathf.Max(0f, flash - Time.deltaTime);
        sprite.color = Color.Lerp(baseColor, Color.white, flash / 0.4f);
    }
}
