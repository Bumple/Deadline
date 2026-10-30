using UnityEngine;
using System.Reflection;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

// Movement and danger sounds for the player. It only LISTENS to PlayerController (it never changes
// movement): jump, wall jump, landing, slide, game over, and a rumble that grows as the death wall
// gets close. Pickup / slowdown / rewind sounds are played by EffectProcessor.
//
// Every sound has a slot in the Inspector. Leave a slot empty to use a simple generated placeholder,
// or drag in your own AudioClip later. Add it with the menu: Deadline > Add Sound Effects To Player.
[DefaultExecutionOrder(100)] // after PlayerController.FixedUpdate, so we see the velocity it just set
[RequireComponent(typeof(Rigidbody2D))]
public class DeadlineSoundEffects : MonoBehaviour
{
    [Header("Your own clips (optional - empty slots use generated placeholders)")]
    public AudioClip jumpClip;
    public AudioClip wallJumpClip;
    public AudioClip landClip;
    public AudioClip slideClip;
    public AudioClip gameOverClip;
    public AudioClip wallRumbleLoop;

    [Header("Volume")]
    [Range(0f, 1f)] public float masterVolume = 0.6f;
    [Range(0f, 1f)] public float jumpVolume = 0.5f;
    [Range(0f, 1f)] public float landVolume = 0.6f;
    [Range(0f, 1f)] public float slideVolume = 0.5f;
    [Range(0f, 1f)] public float gameOverVolume = 0.8f;
    [Range(0f, 1f)] public float rumbleVolume = 0.5f;

    [Header("Death wall rumble")]
    [Tooltip("The rumble starts when the wall is this many units behind the player and is loudest at contact.")]
    public float rumbleStartDistance = 12f;

    private PlayerController player;
    private Rigidbody2D rb;
    private Collider2D col;
    private DeathWall wall;
    private AudioSource oneShots, rumble;
    private FieldInfo groundedField, slidingField;
    private bool wasGrounded = true, wasSliding, gameOverPlayed;
    private float previousVy, airMinVy, landCooldown;

    private void Start()
    {
        player = GetComponent<PlayerController>();
        rb = GetComponent<Rigidbody2D>();
        col = GetComponent<Collider2D>();
        wall = Object.FindAnyObjectByType<DeathWall>();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        groundedField = typeof(PlayerController).GetField("debugGrounded", flags);
        slidingField = typeof(PlayerController).GetField("debugSliding", flags);

        oneShots = gameObject.AddComponent<AudioSource>();
        oneShots.playOnAwake = false;
        oneShots.spatialBlend = 0f;

        if (jumpClip == null) jumpClip = Sweep("Jump", 0.12f, 330f, 700f, 0.5f, true);
        if (wallJumpClip == null) wallJumpClip = Sweep("WallJump", 0.13f, 500f, 950f, 0.5f, true);
        if (landClip == null) landClip = Thump("Land", 0.12f);
        if (slideClip == null) slideClip = Noise("Slide", 0.35f, 0.25f, true);
        if (gameOverClip == null) gameOverClip = Sweep("GameOver", 0.9f, 440f, 70f, 0.7f, true);
        if (wallRumbleLoop == null) wallRumbleLoop = Noise("WallRumble", 1.0f, 0.02f, false);

        rumble = gameObject.AddComponent<AudioSource>();
        rumble.clip = wallRumbleLoop;
        rumble.loop = true;
        rumble.playOnAwake = false;
        rumble.spatialBlend = 0f;
        rumble.volume = 0f;
        rumble.Play();
    }

    private bool Read(FieldInfo f) { return f != null && player != null && (bool)f.GetValue(player); }

    private void FixedUpdate()
    {
        if (player == null) return;
        bool grounded = Read(groundedField);
        bool sliding = Read(slidingField);
        float vy = rb.linearVelocity.y;
        landCooldown = Mathf.Max(0f, landCooldown - Time.fixedDeltaTime);

        // Jump: the controller just set a large upward velocity this step.
        if (vy > 5f && vy > previousVy + 5f)
            Play(wasGrounded ? jumpClip : wallJumpClip, jumpVolume, Random.Range(0.95f, 1.05f));

        if (!grounded) airMinVy = Mathf.Min(airMinVy, previousVy);
        // Landing: louder for bigger drops; tiny hops and step-offs stay quiet.
        if (grounded && !wasGrounded)
        {
            float impact = Mathf.InverseLerp(3f, 20f, -airMinVy);
            if (impact > 0f && landCooldown <= 0f)
            {
                Play(landClip, landVolume * Mathf.Lerp(0.35f, 1f, impact), Mathf.Lerp(1.15f, 0.8f, impact));
                landCooldown = 0.1f;
            }
            airMinVy = 0f;
        }
        if (grounded) airMinVy = 0f;

        if (sliding && !wasSliding) Play(slideClip, slideVolume, Random.Range(0.95f, 1.05f));

        wasGrounded = grounded;
        wasSliding = sliding;
        previousVy = vy;
    }

    private void Update()
    {
        if (wall == null) wall = Object.FindAnyObjectByType<DeathWall>();
        if (wall == null || rumble == null) return;
        if (wall.IsGameOver())
        {
            rumble.volume = 0f;
            if (!gameOverPlayed) { Play(gameOverClip, gameOverVolume, 1f); gameOverPlayed = true; }
            return;
        }
        // Rumble grows as the wall closes in; muted while the wall is paused (e.g. Autoplay Test).
        float left = col != null ? col.bounds.min.x : transform.position.x;
        float gap = left - wall.GetCurrentX();
        float close = wall.enabled ? 1f - Mathf.Clamp01(gap / Mathf.Max(0.1f, rumbleStartDistance)) : 0f;
        rumble.volume = Mathf.MoveTowards(rumble.volume, masterVolume * rumbleVolume * close * close, Time.deltaTime * 2f);
        rumble.pitch = Mathf.Lerp(0.7f, 1.3f, close);
    }

    private void Play(AudioClip clip, float volume, float pitch)
    {
        if (clip == null || oneShots == null) return;
        oneShots.pitch = pitch;
        oneShots.PlayOneShot(clip, masterVolume * volume);
    }

    // ------------------------------------------------------------ generated placeholders
    private const int Rate = 44100;

    private static AudioClip Make(string name, float[] data)
    {
        AudioClip clip = AudioClip.Create(name, data.Length, 1, Rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    /// <summary>Pitch sweep; square=true gives a retro "blip".</summary>
    private static AudioClip Sweep(string name, float seconds, float fromHz, float toHz, float gain, bool square)
    {
        int n = Mathf.CeilToInt(seconds * Rate);
        var data = new float[n];
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / n;
            phase += 2.0 * System.Math.PI * Mathf.Lerp(fromHz, toHz, t) / Rate;
            float s = (float)System.Math.Sin(phase);
            if (square) s = Mathf.Sign(s) * 0.6f + s * 0.4f;
            data[i] = s * Mathf.Min(1f, t * 40f) * (1f - t) * gain;
        }
        return Make(name, data);
    }

    /// <summary>Short low thump for landings.</summary>
    private static AudioClip Thump(string name, float seconds)
    {
        int n = Mathf.CeilToInt(seconds * Rate);
        var data = new float[n];
        var rnd = new System.Random(7);
        double phase = 0;
        float smooth = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / n;
            phase += 2.0 * System.Math.PI * Mathf.Lerp(140f, 50f, t) / Rate;
            smooth = Mathf.Lerp(smooth, (float)(rnd.NextDouble() * 2.0 - 1.0), 0.15f);
            float env = (1f - t) * (1f - t);
            data[i] = ((float)System.Math.Sin(phase) * 0.8f + smooth * 0.5f) * env;
        }
        return Make(name, data);
    }

    /// <summary>Filtered noise. smoothing small = deep rumble, larger = airy swish.
    /// fade=false makes a seamless loop.</summary>
    private static AudioClip Noise(string name, float seconds, float smoothing, bool fade)
    {
        int n = Mathf.CeilToInt(seconds * Rate);
        var data = new float[n];
        var rnd = new System.Random(11);
        float smooth = 0f, peak = 0.0001f;
        for (int i = 0; i < n; i++)
        {
            smooth = Mathf.Lerp(smooth, (float)(rnd.NextDouble() * 2.0 - 1.0), smoothing);
            data[i] = smooth;
            peak = Mathf.Max(peak, Mathf.Abs(smooth));
        }
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / n;
            float env = fade ? Mathf.Min(1f, t * 20f) * (1f - t) : 1f;
            data[i] = data[i] / peak * 0.8f * env;
        }
        if (!fade)
        {
            // Cross-fade the ends so the loop has no click.
            int x = Mathf.Min(n / 4, 2000);
            for (int i = 0; i < x; i++)
            {
                float k = (float)i / x;
                data[i] = Mathf.Lerp(data[n - x + i], data[i], k);
            }
        }
        return Make(name, data);
    }

#if UNITY_EDITOR
    [MenuItem("Deadline/Add Sound Effects To Player")]
    private static void AddToPlayer()
    {
        if (EditorApplication.isPlaying) { Debug.LogWarning("Stop Play Mode first, so the change is saved."); return; }
        var p = Object.FindAnyObjectByType<PlayerController>();
        if (p == null) { Debug.LogError("No PlayerController in the open scene."); return; }
        if (p.GetComponent<DeadlineSoundEffects>() != null)
        {
            Debug.Log("This scene's player already has DeadlineSoundEffects.", p);
            Selection.activeGameObject = p.gameObject;
            return;
        }
        Undo.AddComponent<DeadlineSoundEffects>(p.gameObject);
        EditorSceneManager.MarkSceneDirty(p.gameObject.scene);
        Selection.activeGameObject = p.gameObject;
        Debug.Log("Added DeadlineSoundEffects to the player. Save the scene (Ctrl/Cmd+S). " +
                  "Drag your own clips into its slots any time; empty slots use placeholders.", p);
    }
#endif
}
