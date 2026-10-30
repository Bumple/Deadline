using UnityEngine;
using System.Collections.Generic;
using System.Reflection;

// PLAY-MODE TEST using the REAL PlayerController and Rigidbody2D.
// Added by the menu "Deadline/Autoplay Test (while playing)". For each pair of route waypoints it asks
// DeadlineRouteValidator for an input program from the player's ACTUAL position, then feeds those inputs
// into PlayerController (the same fields its Input System callbacks set). It reports any waypoint the
// real controller cannot reach. The death wall is paused while the bot drives, so failures are about
// geometry/movement, not about the bot's pace. Remove the component (or restart) to play normally.
[DefaultExecutionOrder(-150)] // runs before PlayerController.FixedUpdate each physics step
public class DeadlineAutoplayBot : MonoBehaviour
{
    public bool pauseDeathWall = true;
    [Min(1)] public int roomsToTest = 10;
    public float segmentTimeout = 6f;
    [SerializeField] private int waypoint = 1, reached, replans, failures;
    [SerializeField] private string status = "starting";

    private PlayerController pc;
    private Rigidbody2D rb;
    private BoxCollider2D col;
    private EffectProcessor effects;
    private DeadlinePatternCourse course;
    private DeathWall wall;
    private FieldInfo moveInputField, jumpRequestedField, jumpReleasedField, crouchField, groundedField, crouchedField;

    private bool running, hasProgram;
    private DeadlineRouteValidator.Program prog;
    private Vector2 target;
    private int tick, jumpedAt, attempts;
    private float dirB, segmentTime, speedMult;

    private void Start()
    {
        pc = GetComponent<PlayerController>();
        rb = GetComponent<Rigidbody2D>();
        col = GetComponent<BoxCollider2D>();
        effects = GetComponent<EffectProcessor>();
        course = Object.FindAnyObjectByType<DeadlinePatternCourse>();
        wall = Object.FindAnyObjectByType<DeathWall>();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var t = typeof(PlayerController);
        moveInputField = t.GetField("moveInput", flags);
        jumpRequestedField = t.GetField("jumpRequested", flags);
        jumpReleasedField = t.GetField("jumpReleased", flags);
        crouchField = t.GetField("crouchButtonHeld", flags);
        groundedField = t.GetField("debugGrounded", flags);
        crouchedField = t.GetField("debugCrouched", flags);
        if (!pc || !rb || !col || !course || course.Spec == null || moveInputField == null || jumpRequestedField == null
            || jumpReleasedField == null || crouchField == null || groundedField == null)
        {
            Debug.LogError("[Autoplay] Needs the player, a running ProceduralCourse (v5) and the original PlayerController fields.", this);
            enabled = false;
            return;
        }
        if (pauseDeathWall && wall) wall.enabled = false;
        // Start from the first waypoint ahead of the player.
        Vector2 feet = Feet();
        waypoint = 1;
        while (waypoint < course.AutoplayRoute.Count - 1 && course.AutoplayRoute[waypoint].x < feet.x - 0.5f) waypoint++;
        running = true;
        Debug.Log("[Autoplay] Driving the real PlayerController along the validated route (course seed " + course.ActiveSeed + ").", this);
    }

    private void OnDisable()
    {
        if (pc != null && moveInputField != null)
        {
            moveInputField.SetValue(pc, Vector2.zero);
            crouchField.SetValue(pc, false);
        }
        if (pauseDeathWall && wall) wall.enabled = true;
    }

    private Vector2 Feet()
    {
        // Physics leaves a tiny contact gap; snap to the surface the player is standing on.
        Bounds b = col.bounds;
        var feet = new Vector2(b.center.x, b.min.y);
        foreach (Rect r in course.AutoplaySolids)
            if (Mathf.Abs(r.yMax - feet.y) < 0.03f && feet.x + b.extents.x > r.xMin && feet.x - b.extents.x < r.xMax)
            { feet.y = r.yMax; break; }
        return feet;
    }

    private bool Grounded() { return (bool)groundedField.GetValue(pc); }
    private bool Crouched() { return crouchedField != null && (bool)crouchedField.GetValue(pc); }

    private void FixedUpdate()
    {
        if (!running) return;
        var route = course.AutoplayRoute;
        if (waypoint >= route.Count) { status = "waiting for more rooms"; SetInput(0f, false); return; }
        Vector2 feet = Feet();

        if (!hasProgram)
        {
            if (!Grounded()) { SetInput(0f, false); return; } // settle first
            target = route[waypoint];
            speedMult = effects != null ? effects.GetTotalMultiplier() : 1f;
            DeadlineRouteValidator.Program found;
            List<Rect> local = DeadlineRouteValidator.Local(course.AutoplaySolids, feet, target, 8f);
            // Plan from the real position with the real (full) jump.
            if (!DeadlineRouteValidator.Move(local, feet, target, course.Spec, speedMult, 1f, null,
                    DeadlineRouteValidator.MainPrograms, out found))
            {
                Fail("no input program reaches waypoint " + waypoint + " " + target + " from " + feet);
                return;
            }
            prog = found;
            hasProgram = true;
            tick = 0; jumpedAt = -1; segmentTime = 0f;
            dirB = target.x >= feet.x ? 1f : -1f;
            status = "-> waypoint " + waypoint + " using " + prog;
        }

        // Arrived?
        if (Mathf.Abs(feet.x - target.x) < 0.06f && Mathf.Abs(feet.y - target.y) < 0.06f && Grounded() && !Crouched()
            && Mathf.Abs(rb.linearVelocity.y) < 0.5f)
        {
            reached++; attempts = 0; hasProgram = false; waypoint++;
            SetInput(0f, false);
            if (reached % 25 == 0) Debug.Log("[Autoplay] " + reached + " waypoints reached, " + replans + " re-plans so far.", this);
            CheckDone();
            return;
        }

        segmentTime += Time.fixedDeltaTime;
        if (segmentTime > segmentTimeout || tick > 300)
        {
            attempts++; replans++;
            hasProgram = false;
            SetInput(0f, false);
            if (attempts > 3) Fail("could not reach waypoint " + waypoint + " " + target + "; stuck at " + feet);
            return;
        }

        // Same input logic as DeadlineRouteValidator.Simulate.
        float speed = course.Spec.speed * speedMult;
        bool atTarget = Mathf.Abs(target.x - feet.x) < 0.01f;
        float input = atTarget ? 0f : (target.x > feet.x ? 1f : -1f);
        bool down = false, jumpPress = false, releaseNow = false;
        if (prog.kind == DeadlineRouteValidator.Kind.Jump)
        {
            if (jumpedAt < 0)
            {
                if (tick >= prog.pre || atTarget) jumpPress = true;
            }
            else
            {
                int t = tick - jumpedAt;
                if (t <= prog.away) input = -dirB;
                else if (t <= prog.away + prog.delay) input = 0f;
                if (prog.release >= 0 && t == prog.release) releaseNow = true;
            }
        }
        else if (prog.kind == DeadlineRouteValidator.Kind.Slide)
        {
            down = tick >= prog.pre && tick < prog.pre + prog.hold;
        }
        // Ease off exactly at the target column, like a player releasing the stick.
        if (input != 0f && !down && Mathf.Abs(target.x - feet.x) < speed * Time.fixedDeltaTime)
            input = Mathf.Clamp((target.x - feet.x) / (speed * Time.fixedDeltaTime), -1f, 1f);

        if (jumpPress && jumpedAt < 0)
        {
            if (Grounded()) { jumpRequestedField.SetValue(pc, true); jumpedAt = tick; }
        }
        if (releaseNow) jumpReleasedField.SetValue(pc, true);
        SetInput(input, down);
        tick++;
    }

    private void SetInput(float x, bool down)
    {
        moveInputField.SetValue(pc, new Vector2(x, 0f));
        crouchField.SetValue(pc, down);
    }

    private void CheckDone()
    {
        // Count the room the player is in by the nearest "Room_###" whose entrance is behind the player.
        int roomIndex = 0;
        foreach (Transform child in course.transform)
        {
            Transform entrance = child.Find("Entrance");
            if (entrance != null && entrance.position.x < transform.position.x) roomIndex++;
        }
        if (roomIndex >= roomsToTest)
        {
            running = false;
            SetInput(0f, false);
            status = "done";
            Debug.Log("[Autoplay] PASSED: real controller reached room " + roomIndex + " (" + reached + " waypoints, " + replans +
                      " re-plans, " + failures + " failures). Course seed " + course.ActiveSeed + ".", this);
            enabled = false;
        }
    }

    private void Fail(string message)
    {
        failures++;
        running = false;
        SetInput(0f, false);
        status = "FAILED: " + message;
        Debug.LogError("[Autoplay] FAILED (course seed " + course.ActiveSeed + "): " + message +
                       ". Select ProceduralCourse to see the route gizmos, and send me this line.", this);
    }
}
