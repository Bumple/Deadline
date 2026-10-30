using UnityEngine;
using System.Collections.Generic;

// V5 route validator.
// Instead of drawing ideal arcs, this replays the player's movement one physics step at a time,
// using the same rules as PlayerController.FixedUpdate:
//   - horizontal velocity is set directly from input (instant air control),
//   - jump sets vertical velocity; releasing early multiplies it by jumpReleaseMultiplier,
//   - gravity is applied every 0.02 s step, then the full player box is moved and stopped by blocks,
//   - slides only start while grounded and moving, use the 60% x 120% box, and decelerate,
//   - a crouched player who cannot stand crawls at forcedCrouchSpeedMultiplier.
// A move between two standing waypoints is valid if any scripted input "program" (walk, jump with
// different take-off points / release times / mid-air pauses / reversals, or slide) arrives there.
// Wall jumps are deliberately NOT used, so required routes never depend on them.
// This is a numerical check of the controller rules, not a replay of Unity's physics engine.
public static class DeadlineRouteValidator
{
    public const float Dt = 0.02f;
    private const float Eps = 0.001f;

    /// <summary>Movement numbers copied from the player at runtime.</summary>
    public sealed class MoveSpec
    {
        public float width = 0.8f, height = 0.8f, speed = 10f, jump = 13f, gravity = 29.43f;
        public float release = 0.3f, slideHeight = 0.6f, slideWidth = 1.2f, deceleration = 4f;
        public float crawl = 0.4f, wallSlide = 2f;

        public float Apex(float jumpScale)
        {
            float v = jump * jumpScale - gravity * Dt * 0.5f;
            return v * v / (2f * gravity);
        }
    }

    public enum Kind { Walk, Jump, Slide }

    /// <summary>One scripted input sequence.</summary>
    public struct Program
    {
        public Kind kind;
        public int pre, release, delay, away, hold;
        public Program(Kind kind, int pre = 0, int release = -1, int delay = 0, int away = 0, int hold = 0)
        {
            this.kind = kind; this.pre = pre; this.release = release;
            this.delay = delay; this.away = away; this.hold = hold;
        }
        public override string ToString()
        {
            return kind + "(pre " + pre + ", release " + release + ", delay " + delay + ", away " + away + ", hold " + hold + ")";
        }
    }

    public static readonly Program[] MainPrograms = BuildMain();
    public static readonly Program[] ScanPrograms =
    {
        new Program(Kind.Walk), new Program(Kind.Jump), new Program(Kind.Jump, delay: 10),
        new Program(Kind.Jump, delay: 18), new Program(Kind.Jump, away: 8), new Program(Kind.Jump, release: 8),
        new Program(Kind.Jump, pre: 8), new Program(Kind.Jump, away: 14),
        new Program(Kind.Slide, pre: 3, hold: 120), new Program(Kind.Slide, pre: 3, hold: 25)
    };

    private static Program[] BuildMain()
    {
        var list = new List<Program> { new Program(Kind.Walk) };
        int[] pres = { 0, 4, 8, 12, 18, 26, 36 };
        int[] releases = { -1, 12, 8, 5 };
        foreach (int pre in pres)
            foreach (int rel in releases) list.Add(new Program(Kind.Jump, pre: pre, release: rel));
        foreach (int pre in new[] { 0, 8, 16 })
            foreach (int d in new[] { 5, 10, 16, 22 }) list.Add(new Program(Kind.Jump, pre: pre, delay: d));
        foreach (int pre in new[] { 0, 8 })
            foreach (int a in new[] { 5, 10, 15 }) list.Add(new Program(Kind.Jump, pre: pre, away: a));
        foreach (int pre in new[] { 0, 4, 10, 20, 32 })
            foreach (int hold in new[] { 12, 30, 120 }) list.Add(new Program(Kind.Slide, pre: pre, hold: hold));
        return list.ToArray();
    }

    // ------------------------------------------------------------------ geometry helpers
    public static bool Overlap(float x, float y, float bw, float bh, Rect r)
    {
        return x + bw / 2f > r.xMin + Eps && x - bw / 2f < r.xMax - Eps
            && y + bh > r.yMin + Eps && y < r.yMax - Eps;
    }

    public static bool Fits(List<Rect> solids, float x, float y, float bw, float bh)
    {
        for (int i = 0; i < solids.Count; i++)
            if (Overlap(x, y, bw, bh, solids[i])) return false;
        return true;
    }

    public static bool Supported(List<Rect> solids, Vector2 p, float width)
    {
        foreach (Rect r in solids)
            if (Mathf.Abs(p.y - r.yMax) < 0.003f && p.x + width * 0.35f > r.xMin && p.x - width * 0.35f < r.xMax)
                return true;
        return false;
    }

    /// <summary>Blocks near a move, so each simulation only tests relevant geometry.</summary>
    public static List<Rect> Local(List<Rect> solids, Vector2 a, Vector2 b, float pad = 6f)
    {
        float x0 = Mathf.Min(a.x, b.x) - pad, x1 = Mathf.Max(a.x, b.x) + pad;
        float y0 = Mathf.Min(a.y, b.y) - pad, y1 = Mathf.Max(a.y, b.y) + pad;
        var result = new List<Rect>();
        foreach (Rect r in solids)
            if (r.xMax > x0 && r.xMin < x1 && r.yMax > y0 && r.yMin < y1) result.Add(r);
        return result;
    }

    // ------------------------------------------------------------------ the step simulation
    /// <summary>Run one program from standing feet position a to standing feet position b.
    /// Feet = (centre x, bottom y). Avoid = trigger boxes the body must never touch.
    /// Trace (optional) receives (x, y, width, height) for every step.</summary>
    public static bool Simulate(List<Rect> solids, Vector2 a, Vector2 b, Program prog, MoveSpec m,
        float speedMult, float jumpScale, List<Rect> avoid, List<Vector4> trace, int maxTicks = 260)
    {
        float x = a.x, y = a.y, tx = b.x, ty = b.y;
        float vx = 0f, vy = 0f;
        bool crouched = false, sliding = false, prevDown = false;
        float dirB = tx >= x ? 1f : -1f;
        float speed = m.speed * speedMult;
        float w = m.width, h = m.height;
        int jumpedAt = -1, blockedTicks = 0;
        for (int tick = 0; tick < maxTicks; tick++)
        {
            float bw = crouched ? w * m.slideWidth : w;
            float bh = crouched ? h * m.slideHeight : h;
            // Grounded probe: the controller uses a box 0.08 narrower than the body, only when not rising.
            float pw = Mathf.Max(0.02f, bw - 0.08f);
            bool grounded = false;
            if (vy <= 0.1f)
            {
                foreach (Rect r in solids)
                    if (Mathf.Abs(y - r.yMax) < 0.01f && x + pw / 2f > r.xMin && x - pw / 2f < r.xMax)
                    { grounded = true; break; }
            }
            // Input for this step.
            bool atTarget = Mathf.Abs(tx - x) < 1e-4f;
            float toward = atTarget ? 0f : (tx > x ? 1f : -1f);
            float input = toward;
            bool down = false, jumpPress = false, releaseNow = false;
            if (prog.kind == Kind.Jump)
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
            else if (prog.kind == Kind.Slide)
            {
                down = tick >= prog.pre && tick < prog.pre + prog.hold;
            }
            bool downPressed = down && !prevDown;
            prevDown = down;
            // Crouch / slide rules in the same order as PlayerController.
            if (downPressed && grounded && !crouched && Mathf.Abs(vx) > 0.1f)
            {
                if (Fits(solids, x, y + 0.03f, w * m.slideWidth - 0.01f, h * m.slideHeight - 0.035f))
                { crouched = true; sliding = true; }
            }
            if (sliding && (!down || !grounded)) sliding = false;
            if (crouched && (!down || !grounded) && Fits(solids, x, y + 0.03f, w - 0.01f, h - 0.035f))
            { sliding = false; crouched = false; }
            bw = crouched ? w * m.slideWidth : w;
            bh = crouched ? h * m.slideHeight : h;
            if (sliding) vx = Mathf.MoveTowards(vx, 0f, m.deceleration * Dt);
            else if (crouched && grounded) vx = down ? 0f : input * speed * m.crawl;
            else vx = input * speed;
            // A player can let go of the stick exactly at the target column.
            if (!sliding && vx != 0f && Mathf.Abs(tx - x) < Mathf.Abs(vx) * Dt && (tx - x) * vx > 0f)
                vx = (tx - x) / Dt;
            // Wall slide while pressing into a wall in the air.
            if (!grounded && input != 0f && vy < -m.wallSlide)
            {
                float probeX = x + input * (bw / 2f + 0.015f);
                foreach (Rect r in solids)
                    if (probeX + 0.035f > r.xMin && probeX - 0.035f < r.xMax && y + bh * 0.8f > r.yMin && y + bh * 0.2f < r.yMax)
                    { vy = -m.wallSlide; break; }
            }
            if (jumpPress && jumpedAt < 0)
            {
                bool canStand = !crouched || Fits(solids, x, y + 0.03f, w - 0.01f, h - 0.035f);
                if (canStand && grounded)
                {
                    crouched = false; sliding = false;
                    bw = w; bh = h;
                    vy = m.jump * jumpScale;
                    jumpedAt = tick;
                }
                else if (tick > prog.pre + 3) return false; // could not take off
            }
            if (releaseNow && vy > 0f) vy *= m.release;

            // Physics step: gravity, then horizontal and vertical box contacts.
            vy -= m.gravity * Dt;
            float nx = x + vx * Dt;
            bool hitX = false;
            foreach (Rect r in solids)
            {
                if (y + bh > r.yMin + Eps && y < r.yMax - Eps)
                {
                    if (vx > 0f && x + bw / 2f <= r.xMin + Eps && nx + bw / 2f > r.xMin) { nx = r.xMin - bw / 2f; hitX = true; }
                    else if (vx < 0f && x - bw / 2f >= r.xMax - Eps && nx - bw / 2f < r.xMax) { nx = r.xMax + bw / 2f; hitX = true; }
                }
            }
            if (hitX) vx = 0f;
            x = nx;
            float ny = y + vy * Dt;
            bool landed = false;
            foreach (Rect r in solids)
            {
                if (x + bw / 2f > r.xMin + Eps && x - bw / 2f < r.xMax - Eps)
                {
                    if (vy < 0f && y >= r.yMax - Eps && ny < r.yMax) { ny = r.yMax; landed = true; }
                    else if (vy > 0f && y + bh <= r.yMin + Eps && ny + bh > r.yMin) { ny = r.yMin - bh; vy = 0f; }
                }
            }
            if (landed) vy = 0f;
            y = ny;
            if (trace != null) trace.Add(new Vector4(x, y, bw, bh));
            if (avoid != null)
                foreach (Rect z in avoid)
                    if (Overlap(x, y, bw, bh, z)) return false;

            // Termination.
            if (landed || vy == 0f)
            {
                bool onGround = false;
                foreach (Rect r in solids)
                    if (Mathf.Abs(y - r.yMax) < 0.005f && x + bw / 2f > r.xMin && x - bw / 2f < r.xMax)
                    { onGround = true; break; }
                bool committed = prog.kind != Kind.Jump || jumpedAt >= 0;
                if (onGround)
                {
                    if (Mathf.Abs(x - tx) < 0.01f && Mathf.Abs(y - ty) < 0.01f && !crouched) return true;
                    if (committed && y < ty - 0.01f) return false; // landed below the target surface
                    if (hitX && !sliding)
                    {
                        blockedTicks++;
                        if (blockedTicks > 4 && committed) return false;
                    }
                    else blockedTicks = 0;
                }
            }
            if (y < Mathf.Min(a.y, ty) - 3f * h) return false;
        }
        return false;
    }

    /// <summary>Try programs in order; returns true and the first program that arrives.</summary>
    public static bool Move(List<Rect> solids, Vector2 a, Vector2 b, MoveSpec m, float speedMult, float jumpScale,
        List<Rect> avoid, Program[] programs, out Program used, List<Vector4> trace = null)
    {
        foreach (Program p in programs)
        {
            if (trace != null) trace.Clear();
            if (Simulate(solids, a, b, p, m, speedMult, jumpScale, avoid, trace)) { used = p; return true; }
        }
        used = default(Program);
        return false;
    }

    /// <summary>Every consecutive pair of waypoints must be connected at every listed speed.
    /// Speed 0.5 = while slowed, so a slowdown hit never makes the required route impossible.</summary>
    public static bool ValidateRoute(List<Rect> solids, List<Vector2> route, MoveSpec m, float[] speeds,
        float jumpScale, List<Rect> avoid, out string reason, List<Vector4> traceOut = null)
    {
        reason = "";
        if (route.Count < 2) { reason = "Missing entrance/exit route."; return false; }
        for (int i = 0; i < route.Count; i++)
        {
            Vector2 p = route[i];
            if (!Fits(solids, p.x, p.y + 0.002f, m.width, m.height - 0.004f) || !Supported(solids, p, m.width))
            { reason = "Blocked or unsupported waypoint " + i + " at " + p; return false; }
        }
        var trace = traceOut != null ? new List<Vector4>() : null;
        for (int i = 1; i < route.Count; i++)
        {
            Vector2 a = route[i - 1], b = route[i];
            List<Rect> local = Local(solids, a, b);
            for (int s = 0; s < speeds.Length; s++)
            {
                Program used;
                bool record = traceOut != null && s == 0;
                if (!Move(local, a, b, m, speeds[s], jumpScale, avoid, MainPrograms, out used, record ? trace : null))
                {
                    reason = "No body-sized movement from waypoint " + (i - 1) + " " + a + " to " + i + " " + b
                        + " at speed x" + speeds[s];
                    return false;
                }
                if (record) traceOut.AddRange(trace);
            }
        }
        return true;
    }
}
