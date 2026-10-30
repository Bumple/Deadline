using UnityEngine;

// Owns every speed effect on the player.
//
//   Final movement speed = base speed x boost multiplier x slowdown multiplier
//
// Speed boost: x1.5 for 4 s.  Slowdown: x0.5 for 2.5 s.  Both at once: x0.75.
// Each effect has its own timer, so one expiring never overwrites the other; the multiplier is
// recomputed from the timers every physics step (PlayerController.GetSpeedMultiplier).
// Refresh policy: collecting a boost again, or touching a red zone again, restarts THAT effect's
// timer to its full duration. Effects never stack with themselves (never x0.25, never x2.25)
// and never last longer than one full duration after the last hit.
public class EffectProcessor : MonoBehaviour
{
    [Header("Speed boost")]
    [SerializeField] private float boostMultiplier = 1.5f;
    [SerializeField] private float boostDuration = 4f;
    [Header("Slowdown")]
    [SerializeField] private float slowdownMultiplier = 0.5f;
    [SerializeField] private float slowdownDuration = 2.5f;
    [Header("Feedback")]
    [SerializeField] private Color boostTint = new Color(1f, 0.85f, 0.25f);
    [SerializeField] private Color slowTint = new Color(1f, 0.3f, 0.3f);
    [SerializeField] private bool playSounds = true;
    [Range(0f, 1f)] [SerializeField] private float volume = 0.35f;

    private float speedBoostTimer;
    private float slowdownTimer;
    private bool hasTeleportCharge;

    private PlayerController player;
    private HUDManager hud;
    private DeathWall deathWall;
    private SpriteRenderer visual;
    private Color visualBaseColor = Color.white;
    private bool tinted;
    private AudioSource audioSource;
    private AudioClip boostClip, slowClip, rewindClip;

    public float BoostMultiplier => boostMultiplier;
    public float SlowdownMultiplier => slowdownMultiplier;
    public float BoostDuration => boostDuration;
    public float SlowdownDuration => slowdownDuration;

    private void Awake()
    {
        player = GetComponent<PlayerController>();
    }

    private void Start()
    {
        hud = Object.FindAnyObjectByType<HUDManager>();
        deathWall = Object.FindAnyObjectByType<DeathWall>();
        // PlayerController moves the sprite onto a child called "Visual" in its Awake.
        Transform v = transform.Find("Visual");
        visual = v != null ? v.GetComponent<SpriteRenderer>() : null;
        if (visual == null || !visual.enabled) visual = GetComponent<SpriteRenderer>();
        if (visual != null) visualBaseColor = visual.color;
        if (playSounds)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.spatialBlend = 0f;
            boostClip = Tone("BoostChirp", 0.22f, 520f, 1040f, 0.9f);
            slowClip = Tone("SlowBuzz", 0.28f, 180f, 110f, 0.6f);
            rewindClip = Tone("RewindSweep", 0.45f, 900f, 220f, 0.8f);
        }
    }

    private void Update()
    {
        if (speedBoostTimer > 0f) speedBoostTimer = Mathf.Max(0f, speedBoostTimer - Time.deltaTime);
        if (slowdownTimer > 0f) slowdownTimer = Mathf.Max(0f, slowdownTimer - Time.deltaTime);
    }

    private void LateUpdate()
    {
        // Tint the player while an effect is active, and restore the original colour afterwards.
        if (visual == null) return;
        bool boost = speedBoostTimer > 0f, slow = slowdownTimer > 0f;
        if (!boost && !slow)
        {
            if (tinted) { visual.color = visualBaseColor; tinted = false; }
            return;
        }
        if (!tinted) visualBaseColor = visual.color;
        Color target = boost && slow ? Color.Lerp(boostTint, slowTint, 0.5f) : boost ? boostTint : slowTint;
        // Flash faster during the last second so the player sees it is about to end.
        float remaining = Mathf.Max(boost ? speedBoostTimer : 0f, slow ? slowdownTimer : 0f);
        float rate = remaining < 1f ? 14f : 5f;
        float pulse = 0.55f + 0.45f * Mathf.Abs(Mathf.Sin(Time.time * rate));
        visual.color = Color.Lerp(visualBaseColor, target, pulse);
        tinted = true;
    }

    /// <summary>Start or refresh the speed boost (x1.5) for its full duration.</summary>
    public void ApplySpeedBoost() { ApplySpeedBoost(boostDuration); }

    public void ApplySpeedBoost(float duration)
    {
        speedBoostTimer = Mathf.Max(speedBoostTimer, duration);
        if (hud != null) hud.ShowWarning("SPEED BOOST  x" + boostMultiplier.ToString("0.#") + " for " + duration.ToString("0.#") + "s");
        Play(boostClip);
    }

    /// <summary>Start or refresh the slowdown. Returns true if the player was not already slowed.</summary>
    public bool ApplySlowdown() { return ApplySlowdown(slowdownDuration); }

    public bool ApplySlowdown(float duration)
    {
        bool fresh = slowdownTimer <= 0f;
        slowdownTimer = Mathf.Max(slowdownTimer, duration);
        if (fresh)
        {
            if (hud != null) hud.ShowWarning("SLOWED  x" + slowdownMultiplier.ToString("0.##") + " for " + duration.ToString("0.#") + "s");
            Play(slowClip);
        }
        return fresh;
    }

    /// <summary>Keeps an active slowdown topped up while the player is still touching a red zone (no new feedback).</summary>
    public void RefreshSlowdown(float duration)
    {
        if (slowdownTimer > 0f) slowdownTimer = Mathf.Max(slowdownTimer, duration);
        else ApplySlowdown(duration);
    }

    public void AddTeleportCharge()
    {
        hasTeleportCharge = true;
    }

    public bool HasTeleportCharge() => hasTeleportCharge;

    public void TryUseTeleport()
    {
        Debug.Log($"[EffectProcessor] TryUseTeleport. Has charge: {hasTeleportCharge}");
        if (!hasTeleportCharge || (deathWall != null && deathWall.IsGameOver())) return;

        // Find next room entrance
        Transform nextEntrance = FindNextRoomEntrance();
        if (nextEntrance != null)
        {
            Debug.Log($"[EffectProcessor] Found next entrance: {nextEntrance.name} at {nextEntrance.position}");
            if (IsDestinationSafe(nextEntrance.position))
            {
                player.TeleportTo(nextEntrance.position);
                hasTeleportCharge = false;
                Debug.Log("[EffectProcessor] Teleport successful.");
            }
            else
            {
                if (hud != null) hud.ShowWarning("TELEPORT DESTINATION UNSAFE");
                Debug.Log("[EffectProcessor] Teleport destination unsafe.");
            }
        }
        else
        {
            if (hud != null) hud.ShowWarning("NO NEXT ROOM FOUND");
            Debug.Log("[EffectProcessor] No next room found.");
        }
    }

    private bool IsDestinationSafe(Vector3 pos)
    {
        // Check if behind death wall
        if (deathWall != null && pos.x <= deathWall.GetCurrentX()) return false;

        // Check for ground in a wider range
        int envMask = player.groundLayer | player.wallLayer;
        RaycastHit2D hit = Physics2D.Raycast(pos + Vector3.up * 0.5f, Vector2.down, 2.0f, envMask);
        if (hit.collider == null)
        {
            Debug.Log("[EffectProcessor] Safety Check: No ground detected below entrance.");
            return false;
        }

        // Check for enough vertical space
        Collider2D overlap = Physics2D.OverlapBox(pos + Vector3.up * 0.4f, new Vector2(0.6f, 0.7f), 0f, envMask);
        if (overlap != null)
        {
            Debug.Log($"[EffectProcessor] Safety Check: Overlapping solid object: {overlap.name}");
            return false;
        }

        return true;
    }

    /// <summary>base x boost x slowdown. PlayerController multiplies its moveSpeed by this.</summary>
    public float GetTotalMultiplier()
    {
        float mult = 1f;
        if (speedBoostTimer > 0f) mult *= boostMultiplier;
        if (slowdownTimer > 0f) mult *= slowdownMultiplier;
        return mult;
    }

    public float GetBoostTime() => speedBoostTimer;
    public float GetSlowdownTime() => slowdownTimer;

    private Transform FindNextRoomEntrance()
    {
        GameObject[] objects = GameObject.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude);
        Transform bestEntrance = null;
        float minX = float.MaxValue;
        float playerX = transform.position.x;

        foreach (var obj in objects)
        {
            if (obj.name.StartsWith("Room") && !obj.name.Contains("Hall"))
            {
                Transform ent = obj.transform.Find("Entrance");
                if (ent != null)
                {
                    float entX = ent.position.x;
                    if (entX > playerX + 2f && entX < minX)
                    {
                        minX = entX;
                        bestEntrance = ent;
                    }
                }
            }
        }
        return bestEntrance;
    }

    /// <summary>Push the death wall back by about three seconds of its CURRENT travel
    /// (current speed x 3). Its speed is not changed.</summary>
    public bool RewindBarrier()
    {
        if (deathWall == null) deathWall = Object.FindAnyObjectByType<DeathWall>();
        if (deathWall != null)
        {
            float distance = deathWall.RewindSeconds(3f);
            if (hud != null) hud.ShowWarning("DEATH WALL PUSHED BACK  -" + distance.ToString("0.0") + "m");
            Play(rewindClip);
            return true;
        }

        if (hud != null) hud.ShowWarning("NO BARRIER TO REWIND");
        Debug.Log("Barrier Rewind: No barrier system detected to rewind.");
        return false;
    }

    private void Play(AudioClip clip)
    {
        if (playSounds && audioSource != null && clip != null) audioSource.PlayOneShot(clip, volume);
    }

    /// <summary>Small generated sweep tone, so feedback works before real sound effects are added.</summary>
    private static AudioClip Tone(string name, float seconds, float startHz, float endHz, float gain)
    {
        const int rate = 44100;
        int samples = Mathf.CeilToInt(seconds * rate);
        var data = new float[samples];
        double phase = 0;
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / samples;
            float hz = Mathf.Lerp(startHz, endHz, t);
            phase += 2.0 * Mathf.PI * hz / rate;
            float envelope = Mathf.Min(1f, t * 30f) * (1f - t);
            data[i] = (float)System.Math.Sin(phase) * envelope * gain;
        }
        AudioClip clip = AudioClip.Create(name, samples, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
