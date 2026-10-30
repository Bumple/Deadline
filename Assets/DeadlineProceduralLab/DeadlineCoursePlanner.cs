using UnityEngine;
using System.Collections.Generic;

// V5 planning logic (no GameObjects). DeadlinePatternCourse turns these plans into blocks.
//
// A ROOM is 1-3 CHAMBERS joined by short connectors. Each chamber comes from a hand-designed
// STRUCTURE template (multi-storey switchback, drop shaft, gallery over a recovery floor, climb
// over a bridge then drop, stepped rhythm run). Inside every walkway a FILLER places a rhythm of
// obstacles that fits the walkway's headroom (hurdles, slide gates, hurdle + low divider pairs,
// red floor strips). All template numbers are in PLAYER HEIGHTS so they scale with the collider.
//
// Every chamber is accepted only if:
//  1. the main route passes the step-by-step movement check at normal speed AND while slowed,
//  2. a "trap scan" proves that EVERY standable spot in the chamber can still reach the exit
//     (so missed jumps, drops and optional detours always have a way back),
//  3. reward spots are reachable from the main route (and, by 2, can return to it).
// Rooms are also checked through the doorway that joins them to the previous room.
public sealed class DeadlineCoursePlanner
{
    // ---------------------------------------------------------------- template constants (player heights)
    private const float C = 2.4f;     // comfortable climb for required routes
    private const float T = 0.4f;     // shelf thickness
    private const float S = 0.8f;     // slide-gate clearance (slide body is 0.6)
    private const float Low = 1.45f;  // walk-under divider clearance (blocks a jump)
    public const float MainJumpScale = 0.94f;  // required jumps use at most ~88% of full jump height
    public const float ScanJumpScale = 0.97f;

    public static readonly string[] Structures =
        { "SwitchbackUp", "SwitchbackDown", "DropShaft", "Gallery", "ClimbDrop", "Rhythm" };

    public sealed class Settings
    {
        public bool spawnPickups = true, spawnHazards = true, trapScan = true;
        public int pickupMin = 2, pickupMax = 3;
        public float hazardRoomChance = 0.6f, hazardCandidateChance = 0.35f;
        public int maxHazardsPerRoom = 2;
        public float twoChamberChance = 0.75f;
        public float slowMultiplier = 0.5f;
        public int chamberAttempts = 8, roomAttempts = 6;
    }

    public sealed class Hazard
    {
        public string label;
        public Rect trigger, visual;
        public int kind; // 0 floor, 1 wall, 2 ceiling
    }

    public sealed class Chamber
    {
        public string name, kind;
        public int seed, bias;
        public float h;
        public float length, exitY, roof, floor; // player heights
        public readonly List<string> labels = new List<string>();
        public readonly List<Rect> blocks = new List<Rect>();      // world units, chamber-local
        public List<Vector2> route = new List<Vector2>();           // main route feet points
        public List<Vector2> rewards = new List<Vector2>();         // validated reward feet points
        public readonly List<Hazard> hazards = new List<Hazard>();  // candidates
        public readonly List<Vector2> noHazardX = new List<Vector2>();
        public readonly List<Vector4> trace = new List<Vector4>();  // simulated main-route body path

        public void Block(string label, float x0, float x1, float y0, float y1)
        {
            if (x1 <= x0 || y1 <= y0 + 1e-4f) return;
            labels.Add(label);
            blocks.Add(Rect.MinMaxRect(x0 * h, y0 * h, x1 * h, y1 * h));
        }
        public void Shelf(string label, float x0, float x1, float top) { Block(label, x0, x1, top - T, top); }
        public void Point(float x, float y) { route.Add(new Vector2(x * h, y * h)); }
        public void Reward(float x, float y) { rewards.Add(new Vector2(x * h, y * h)); }
        public void Forbid(float x0, float x1) { noHazardX.Add(new Vector2(x0 * h, x1 * h)); }

        public void HazardFloor(string label, float x0, float x1, float y)
        {
            hazards.Add(new Hazard
            {
                label = label, kind = 0,
                visual = Rect.MinMaxRect(x0 * h, y * h, x1 * h, y * h + 0.09f),
                trigger = Rect.MinMaxRect(x0 * h + 0.04f, y * h, x1 * h - 0.04f, y * h + 0.14f)
            });
        }
        /// <summary>Red strip on a vertical face at x. side -1: the face looks left; +1: looks right.</summary>
        public void HazardWall(string label, float x, float y0, float y1, float side)
        {
            float xw = x * h;
            hazards.Add(new Hazard
            {
                label = label, kind = 1,
                visual = side < 0 ? Rect.MinMaxRect(xw - 0.09f, y0 * h, xw, y1 * h) : Rect.MinMaxRect(xw, y0 * h, xw + 0.09f, y1 * h),
                trigger = side < 0 ? Rect.MinMaxRect(xw - 0.12f, y0 * h + 0.03f, xw, y1 * h - 0.03f)
                                   : Rect.MinMaxRect(xw, y0 * h + 0.03f, xw + 0.12f, y1 * h - 0.03f)
            });
        }
        public void HazardCeiling(string label, float x0, float x1, float y)
        {
            hazards.Add(new Hazard
            {
                label = label, kind = 2,
                visual = Rect.MinMaxRect(x0 * h, y * h - 0.09f, x1 * h, y * h),
                trigger = Rect.MinMaxRect(x0 * h + 0.04f, y * h - 0.12f, x1 * h - 0.04f, y * h)
            });
        }
        public void AddRoute(List<Vector2> unitPoints)
        {
            foreach (Vector2 p in unitPoints) route.Add(p * h);
        }
        public void DedupeRoute()
        {
            var clean = new List<Vector2>();
            foreach (Vector2 p in route)
                if (clean.Count == 0 || (clean[clean.Count - 1] - p).sqrMagnitude > 1e-8f) clean.Add(p);
            route = clean;
        }
    }

    public sealed class Room
    {
        public int index;
        public readonly List<Chamber> chambers = new List<Chamber>();
        public readonly List<Vector2> origins = new List<Vector2>();
        public readonly List<Vector2> connectorOrigins = new List<Vector2>(); // floor point where each connector starts
        public readonly List<Hazard> hazards = new List<Hazard>();
        public readonly List<int> hazardChamber = new List<int>();
        public int pickupKind = -1; // 0 speed boost, 1 death-wall rewind
        public int pickupChamber = -1;
        public Vector2 pickupLocal;      // pickup centre, chamber-local
        public bool usedFallback;
    }

    // ---------------------------------------------------------------- state
    private readonly DeadlineRouteValidator.MoveSpec spec;
    private readonly Settings settings;
    private readonly System.Random rng;
    private readonly float h;
    private readonly List<string> bag = new List<string>();
    private readonly List<int> kindBag = new List<int>();
    private string lastKind;
    private float elevation;
    private int index, nextPickupRoom;
    private List<Rect> prevSolids = new List<Rect>();
    private Vector2 prevExitPoint;
    public float EndX { get; private set; }
    public float EndY { get; private set; }
    public readonly List<string> rejections = new List<string>();
    private void Reject(string message)
    {
        rejections.Add(message);
        if (rejections.Count > 300) rejections.RemoveAt(0);
    }
    public int RejectedChambers { get; private set; }
    public int FallbackRooms { get; private set; }

    public DeadlineCoursePlanner(DeadlineRouteValidator.MoveSpec spec, Settings settings, int seed)
    {
        this.spec = spec; this.settings = settings;
        h = spec.height;
        rng = new System.Random(seed);
        nextPickupRoom = RandomInt(settings.pickupMin, settings.pickupMax);
    }

    /// <summary>Tell the planner where the hand-made start room ends.</summary>
    public void SetStart(List<Rect> startSolids, Vector2 startExitPoint, float endX, float endY)
    {
        prevSolids = new List<Rect>(startSolids);
        prevExitPoint = startExitPoint;
        EndX = endX; EndY = endY;
    }

    private float Rr(float a, float b) { return a + (b - a) * (float)rng.NextDouble(); }
    private int RandomInt(int lowInclusive, int highInclusive)
    {
        int lo = Mathf.Max(1, lowInclusive), hi = Mathf.Max(lo, highInclusive);
        return rng.Next(lo, hi + 1);
    }
    private static float Rr(System.Random r, float a, float b) { return a + (b - a) * (float)r.NextDouble(); }

    // ---------------------------------------------------------------- room planning
    public Room PlanNextRoom()
    {
        index++;
        int count = rng.NextDouble() < settings.twoChamberChance ? 2 : (rng.NextDouble() < 0.5 ? 1 : 3);
        for (int attempt = 0; attempt < settings.roomAttempts; attempt++)
        {
            var chambers = new List<Chamber>();
            for (int i = 0; i < count; i++)
            {
                Chamber c = PlanChamber();
                if (c == null) break;
                chambers.Add(c);
            }
            if (chambers.Count < count) { Reject("Room " + index + ": chamber planning failed"); continue; }
            Room room = Place(chambers);
            if (room != null) return Finish(room);
        }
        // Fallback: a plain validated hall, so generation never stops.
        FallbackRooms++;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Chamber hall = MakeFallbackHall();
            string why;
            if (!ValidateChamber(hall, out why)) { Reject("Fallback rejected: " + why); continue; }
            Room room = Place(new List<Chamber> { hall });
            if (room != null) { room.usedFallback = true; return Finish(room); }
        }
        return null;
    }

    /// <summary>Position chambers from the previous exit and check every doorway. Null if a doorway fails.</summary>
    private Room Place(List<Chamber> chambers)
    {
        var room = new Room { index = index };
        List<Rect> previous = prevSolids;
        Vector2 previousPoint = prevExitPoint;
        float ex = EndX, ey = EndY;
        float[] speeds = { 1f, settings.slowMultiplier };
        foreach (Chamber c in chambers)
        {
            var origin = new Vector2(ex + 2f * h, ey);
            List<Rect> solids = Translate(c.blocks, origin);
            var check = new List<Rect>(previous);
            check.AddRange(Connector(ex, ey));
            check.AddRange(solids);
            var hop = new List<Vector2> { previousPoint, c.route[0] + origin };
            string why;
            if (!DeadlineRouteValidator.ValidateRoute(check, hop, spec, speeds, MainJumpScale, null, out why))
            {
                Reject("Room " + index + " doorway into " + c.name + " (seed " + c.seed + "): " + why);
                return null;
            }
            room.connectorOrigins.Add(new Vector2(ex, ey));
            room.chambers.Add(c);
            room.origins.Add(origin);
            previous = solids;
            previousPoint = c.route[c.route.Count - 1] + origin;
            ex = origin.x + c.length * h;
            ey = origin.y + c.exitY * h;
        }
        prevSolids = previous;
        prevExitPoint = previousPoint;
        EndX = ex; EndY = ey;
        foreach (Chamber c in chambers) elevation += c.exitY * h;
        return room;
    }

    private Room Finish(Room room)
    {
        // Slowdown zones: some rooms stay completely clean.
        if (settings.spawnHazards && rng.NextDouble() < settings.hazardRoomChance)
        {
            int budget = settings.maxHazardsPerRoom;
            for (int i = 0; i < room.chambers.Count && budget > 0; i++)
            {
                List<Hazard> chosen = ChooseHazards(room.chambers[i], Mathf.Min(budget, 2));
                budget -= chosen.Count;
                foreach (Hazard hz in chosen) { room.hazards.Add(hz); room.hazardChamber.Add(i); }
            }
        }
        // Pickups: occasional, on a validated optional spot, never on a chosen slowdown zone.
        if (settings.spawnPickups && index >= nextPickupRoom)
        {
            var options = new List<Vector2Int>();
            for (int i = 0; i < room.chambers.Count; i++)
                for (int r = 0; r < room.chambers[i].rewards.Count; r++)
                {
                    Vector2 spot = room.chambers[i].rewards[r];
                    bool onHazard = false;
                    for (int k = 0; k < room.hazards.Count; k++)
                        if (room.hazardChamber[k] == i && DeadlineRouteValidator.Overlap(spot.x, spot.y, spec.width, spec.height, room.hazards[k].trigger))
                            onHazard = true;
                    if (!onHazard) options.Add(new Vector2Int(i, r));
                }
            if (options.Count > 0)
            {
                Vector2Int pick = options[rng.Next(options.Count)];
                if (kindBag.Count == 0)
                {
                    // Shuffled pair: both pickup types appear within every two pickups.
                    if (rng.NextDouble() < 0.5) { kindBag.Add(0); kindBag.Add(1); } else { kindBag.Add(1); kindBag.Add(0); }
                }
                room.pickupKind = kindBag[kindBag.Count - 1];
                kindBag.RemoveAt(kindBag.Count - 1);
                room.pickupChamber = pick.x;
                room.pickupLocal = room.chambers[pick.x].rewards[pick.y] + new Vector2(0f, 0.55f * h);
                nextPickupRoom = index + RandomInt(settings.pickupMin, settings.pickupMax);
            }
        }
        return room;
    }

    private string Draw()
    {
        for (int i = 0; i < 24; i++)
        {
            if (bag.Count == 0)
            {
                bag.AddRange(Structures);
                for (int k = bag.Count - 1; k > 0; k--) { int j = rng.Next(k + 1); string t = bag[k]; bag[k] = bag[j]; bag[j] = t; }
            }
            string kind = bag[bag.Count - 1];
            bag.RemoveAt(bag.Count - 1);
            if (kind == lastKind) continue;
            // Keep the course from drifting endlessly up or down.
            if (elevation > 8f * h && kind == "SwitchbackUp") continue;
            if (elevation < -8f * h && (kind == "DropShaft" || kind == "SwitchbackDown" || kind == "ClimbDrop")) continue;
            lastKind = kind;
            return kind;
        }
        lastKind = "Rhythm";
        return "Rhythm";
    }

    private Chamber PlanChamber()
    {
        for (int attempt = 0; attempt < settings.chamberAttempts; attempt++)
        {
            string kind = Draw();
            int chamberSeed = rng.Next();
            int bias = elevation < -4f * h ? 1 : (elevation > 4f * h ? -1 : 0);
            Chamber c = MakeChamber(kind, chamberSeed, bias);
            string why;
            if (ValidateChamber(c, out why)) return c;
            RejectedChambers++;
            Reject("Rejected " + kind + " chamberSeed=" + chamberSeed + " bias=" + bias + ": " + why);
        }
        return null;
    }

    private static List<Rect> Translate(List<Rect> rects, Vector2 offset)
    {
        var list = new List<Rect>(rects.Count);
        foreach (Rect r in rects) list.Add(new Rect(r.position + offset, r.size));
        return list;
    }

    public List<Rect> Connector(float x, float y)
    {
        return new List<Rect>
        {
            Rect.MinMaxRect(x, y - 0.5f * h, x + 2f * h, y),
            Rect.MinMaxRect(x, y + 3f * h, x + 2f * h, y + 3.5f * h)
        };
    }

    // ---------------------------------------------------------------- validation
    public bool ValidateChamber(Chamber p, out string reason)
    {
        float[] speeds = { 1f, settings.slowMultiplier };
        p.trace.Clear();
        if (!DeadlineRouteValidator.ValidateRoute(p.blocks, p.route, spec, speeds, MainJumpScale, null, out reason, p.trace))
        { reason = "main route: " + reason; return false; }
        List<Vector2> canExit;
        if (settings.trapScan)
        {
            int trapped;
            Vector2 example;
            canExit = TrapScan(p, out trapped, out example);
            if (trapped > 0) { reason = trapped + " trapped spot(s), e.g. " + example; return false; }
        }
        else canExit = null;
        var good = new List<Vector2>();
        foreach (Vector2 r in p.rewards)
        {
            if (canExit != null)
            {
                bool inSet = false;
                foreach (Vector2 c in canExit)
                    if (Mathf.Abs(c.x - r.x) < 0.4f && Mathf.Abs(c.y - r.y) < 0.01f) { inSet = true; break; }
                if (!inSet) continue;
            }
            if (!DeadlineRouteValidator.Fits(p.blocks, r.x, r.y + 0.002f, spec.width, spec.height)) continue;
            var near = new List<Vector2>(p.route);
            near.Sort((a, b) => (Mathf.Abs(a.x - r.x) + Mathf.Abs(a.y - r.y)).CompareTo(Mathf.Abs(b.x - r.x) + Mathf.Abs(b.y - r.y)));
            for (int i = 0; i < Mathf.Min(6, near.Count); i++)
            {
                DeadlineRouteValidator.Program used;
                if (DeadlineRouteValidator.Move(DeadlineRouteValidator.Local(p.blocks, near[i], r), near[i], r, spec, 1f,
                    ScanJumpScale, null, DeadlineRouteValidator.MainPrograms, out used))
                { good.Add(r); break; }
            }
        }
        p.rewards = good;
        reason = "";
        return true;
    }

    /// <summary>Standable sample points on every top surface inside the chamber.</summary>
    private List<Vector2> Spots(Chamber p)
    {
        var raw = new List<Vector2>();
        float w = spec.width, roof = p.roof * h, L = p.length * h;
        for (int i = 0; i < p.blocks.Count; i++)
        {
            string label = p.labels[i];
            Rect b = p.blocks[i];
            if (label == "Ceiling" || label == "EntryUpperCap" || label == "ExitUpperCap" || b.yMax >= roof - 0.5f * h) continue;
            float a = Mathf.Max(b.xMin, 0.3f * h) + 0.4f * w, c = Mathf.Min(b.xMax, L - 0.3f * h) - 0.4f * w;
            if (c < a) continue;
            int n = Mathf.Max(1, (int)((c - a) / (1.5f * h)) + 1);
            for (int k = 0; k <= n; k++)
            {
                float x = a + (c - a) * k / Mathf.Max(1, n);
                if (DeadlineRouteValidator.Fits(p.blocks, x, b.yMax + 0.002f, w, spec.height - 0.004f))
                    raw.Add(new Vector2(x, b.yMax));
            }
        }
        var result = new List<Vector2>();
        foreach (Vector2 s in raw)
        {
            bool duplicate = false;
            foreach (Vector2 t in result)
                if (Mathf.Abs(s.x - t.x) <= 0.3f * h && Mathf.Abs(s.y - t.y) <= 0.01f) { duplicate = true; break; }
            if (!duplicate) result.Add(s);
        }
        return result;
    }

    /// <summary>Backward fix-point: every standable spot (and every reward spot) must reach the exit.
    /// Returns the spots that can reach the exit.</summary>
    private List<Vector2> TrapScan(Chamber p, out int trappedCount, out Vector2 example)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        List<Vector2> pts = Spots(p);
        pts.AddRange(p.rewards);
        Vector2 exit = p.route[p.route.Count - 1];
        var can = new List<Vector2> { exit };
        var pending = new List<Vector2>();
        foreach (Vector2 s in pts) if ((s - exit).sqrMagnitude > 1e-8f) pending.Add(s);
        float apex = spec.Apex(ScanJumpScale);
        var tried = new HashSet<long>();
        var ids = new Dictionary<Vector2, int>();
        int nextId = 0;
        System.Func<Vector2, int> id = v => { int k; if (!ids.TryGetValue(v, out k)) { k = nextId++; ids[v] = k; } return k; };
        bool changed = true;
        while (changed && pending.Count > 0)
        {
            changed = false;
            var still = new List<Vector2>();
            foreach (Vector2 s in pending)
            {
                bool ok = false;
                var candidates = new List<Vector2>(can);
                Vector2 start = s;
                candidates.Sort((a, b) => Cost(start, a).CompareTo(Cost(start, b)));
                int tests = 0;
                foreach (Vector2 t in candidates)
                {
                    if (t.y - s.y > apex - 0.05f || Mathf.Abs(t.x - s.x) > 12f * h) continue;
                    long key = (long)id(s) * 1000003L + id(t);
                    if (!tried.Add(key)) continue;
                    tests++;
                    DeadlineRouteValidator.Program used;
                    if (DeadlineRouteValidator.Move(DeadlineRouteValidator.Local(p.blocks, s, t), s, t, spec, 1f, ScanJumpScale,
                        null, DeadlineRouteValidator.ScanPrograms, out used)) { ok = true; break; }
                    if (tests >= 8) break;
                }
                if (ok) { can.Add(s); changed = true; }
                else still.Add(s);
            }
            pending = still;
            if (watch.ElapsedMilliseconds > 1500) break; // safety: treat a slow scan as a failure
        }
        trappedCount = pending.Count;
        example = pending.Count > 0 ? pending[0] : Vector2.zero;
        return can;
    }

    private static float Cost(Vector2 s, Vector2 t)
    {
        return Mathf.Abs(t.x - s.x) + 2f * Mathf.Max(0f, t.y - s.y) + 0.3f * Mathf.Max(0f, s.y - t.y);
    }

    /// <summary>Add slowdown zones one at a time; each must keep the main route valid at normal and slowed
    /// speed while the body never touches any chosen zone.</summary>
    private List<Hazard> ChooseHazards(Chamber p, int maxCount)
    {
        var candidates = new List<Hazard>(p.hazards);
        for (int k = candidates.Count - 1; k > 0; k--) { int j = rng.Next(k + 1); Hazard t = candidates[k]; candidates[k] = candidates[j]; candidates[j] = t; }
        var chosen = new List<Hazard>();
        var avoid = new List<Rect>();
        float[] speeds = { 1f, settings.slowMultiplier };
        float entryX = p.route[0].x, exitX = p.route[p.route.Count - 1].x;
        foreach (Hazard hz in candidates)
        {
            if (chosen.Count >= maxCount) break;
            if (rng.NextDouble() > settings.hazardCandidateChance) continue;
            Rect t = hz.trigger;
            float cx = t.center.x;
            // Never right at a doorway.
            if (Mathf.Abs(cx - entryX) < 4f * h || Mathf.Abs(cx - exitX) < 4f * h) continue;
            // Floor strips stay away from slide openings and blind drop landings.
            bool forbidden = false;
            if (hz.kind == 0)
                foreach (Vector2 z in p.noHazardX) if (t.xMax > z.x && t.xMin < z.y) forbidden = true;
            if (forbidden) continue;
            bool onReward = false;
            foreach (Vector2 r in p.rewards) if (DeadlineRouteValidator.Overlap(r.x, r.y, spec.width, spec.height, t)) onReward = true;
            if (onReward) continue;
            avoid.Add(t);
            string why;
            if (DeadlineRouteValidator.ValidateRoute(p.blocks, p.route, spec, speeds, MainJumpScale, avoid, out why)) chosen.Add(hz);
            else avoid.RemoveAt(avoid.Count - 1);
        }
        return chosen;
    }

    // ---------------------------------------------------------------- chamber construction
    public Chamber MakeChamber(string kind, int chamberSeed, int bias)
    {
        var r = new System.Random(chamberSeed);
        var p = new Chamber { kind = kind, name = kind, seed = chamberSeed, bias = bias, h = h };
        switch (kind)
        {
            case "SwitchbackUp": SwitchbackUp(p, r); break;
            case "SwitchbackDown": SwitchbackUp(p, r); p = Reverse(p); break;
            case "DropShaft": DropShaft(p, r); break;
            case "Gallery": Gallery(p, r); break;
            case "ClimbDrop": ClimbDrop(p, r); break;
            default: Rhythm(p, r); break;
        }
        Shell(p);
        return p;
    }

    private Chamber MakeFallbackHall()
    {
        var p = new Chamber { kind = "FallbackHall", name = "FallbackHall", seed = 0, h = h };
        p.Point(1.5f, 0f); p.Point(18.5f, 0f);
        p.length = 20f; p.exitY = 0f; p.roof = 5f; p.floor = 0f;
        Shell(p);
        return p;
    }

    /// <summary>Place obstacles along a walkway [x0,x1] at height y whose ceiling underside is at ceil.
    /// Returns waypoints in traversal order (player heights), excluding the walkway's endpoints.</summary>
    private static List<Vector2> Fill(Chamber p, System.Random r, float x0, float x1, float y, float ceil, int direction, bool allowStrip = true)
    {
        var pts = new List<Vector2>();
        float headroom = ceil - y, length = x1 - x0;
        System.Func<float, float> X = u => direction > 0 ? x0 + u : x1 - u;
        float u0 = 0.5f;
        string last = null;
        var options = new List<string>();
        while (true)
        {
            options.Clear();
            if (headroom >= 2.7f) { options.Add("hurdle"); options.Add("hurdle"); }
            if (headroom >= 1.5f) { options.Add("slide"); options.Add("slide"); }
            if (headroom >= 4.2f) options.Add("hurdleLow");
            if (allowStrip) options.Add("strip");
            if (last != null && options.Contains(last) && options.Count > 1)
            {
                options.RemoveAll(o => o == last);
                if (options.Count == 0) options.Add(last);
            }
            if (options.Count == 0) break;
            string kind = options[r.Next(options.Count)];
            float hh = 0f, wd, need, gap = 0f, dw = 0f;
            if (kind == "hurdle") { hh = Rr(r, 0.9f, Mathf.Min(2f, headroom - 1.7f)); wd = Rr(r, 0.9f, 1.4f); need = 1.7f + wd + 1.8f; }
            else if (kind == "slide") { float[] widths = { 1.2f, 1.6f, 2.4f, 3.2f }; wd = widths[r.Next(widths.Length)]; need = 2.3f + wd + 1.6f; }
            else if (kind == "hurdleLow") { hh = Rr(r, 0.9f, 1.3f); wd = 1f; gap = Rr(r, 2.8f, 3.4f); dw = Rr(r, 1f, 1.6f); need = 1.7f + wd + gap + dw + 1.8f; }
            else { wd = Rr(r, 1.3f, 1.9f); need = 1.6f + wd + 1.6f; }
            if (u0 + need > length - 0.4f) break;
            if (kind == "hurdle")
            {
                float n0 = Mathf.Min(X(u0 + 1.7f), X(u0 + 1.7f + wd)), n1 = Mathf.Max(X(u0 + 1.7f), X(u0 + 1.7f + wd));
                p.Block("Hurdle", n0, n1, y, y + hh);
                pts.Add(new Vector2(X(u0), y));
                pts.Add(new Vector2(X(u0 + 1.7f + wd + 1.8f), y));
                p.HazardWall("HurdleFace", direction > 0 ? n0 : n1, y + hh * 0.45f, y + hh, -direction);
                p.HazardFloor("HurdleTop", n0, n1, y + hh);
            }
            else if (kind == "slide")
            {
                float n0 = Mathf.Min(X(u0 + 2.3f), X(u0 + 2.3f + wd)), n1 = Mathf.Max(X(u0 + 2.3f), X(u0 + 2.3f + wd));
                p.Block("SlideGate", n0, n1, y + S, ceil);
                pts.Add(new Vector2(X(u0), y));
                pts.Add(new Vector2(X(u0 + 2.3f + wd + 1.6f), y));
                p.Forbid(n0 - 1.5f, n1 + 1.5f);
                p.HazardWall("GateFace", direction > 0 ? n0 : n1, y + S, Mathf.Min(ceil, y + S + 1.6f), -direction);
            }
            else if (kind == "hurdleLow")
            {
                float h0 = Mathf.Min(X(u0 + 1.7f), X(u0 + 1.7f + wd)), h1 = Mathf.Max(X(u0 + 1.7f), X(u0 + 1.7f + wd));
                p.Block("Hurdle", h0, h1, y, y + hh);
                float d0 = Mathf.Min(X(u0 + 1.7f + wd + gap), X(u0 + 1.7f + wd + gap + dw));
                float d1 = Mathf.Max(X(u0 + 1.7f + wd + gap), X(u0 + 1.7f + wd + gap + dw));
                p.Block("LowDivider", d0, d1, y + Low, ceil);
                pts.Add(new Vector2(X(u0), y));
                pts.Add(new Vector2(X(u0 + 1.7f + wd + gap * 0.5f), y));
                pts.Add(new Vector2(X(u0 + need - 0.1f), y));
                p.HazardCeiling("DividerUnderside", d0, d1, y + Low);
            }
            else
            {
                float n0 = Mathf.Min(X(u0 + 1.6f), X(u0 + 1.6f + wd)), n1 = Mathf.Max(X(u0 + 1.6f), X(u0 + 1.6f + wd));
                p.HazardFloor("FloorStrip", n0, n1, y);
                pts.Add(new Vector2(X(u0), y));
                pts.Add(new Vector2(X(u0 + need), y));
            }
            last = kind;
            u0 += need + Rr(r, 0.1f, 0.8f);
        }
        return pts;
    }

    // Three storeys: right along the ground under storey B, up and LEFT along B (with a gap jump
    // under storey C), then up and right along C to the exit. Reward shelf above C.
    private static void SwitchbackUp(Chamber p, System.Random r)
    {
        float W = Rr(r, 27f, 31f);
        bool step = r.NextDouble() < 0.5;
        float bgap = Rr(r, 1.8f, 2.6f), b2r = W - 8.5f;
        p.name = "SwitchbackUp";
        p.Shelf("StoreyB_Left", 0.3f, 10f, C);
        p.Shelf("StoreyB_Right", 10f + bgap, b2r, C);
        p.Shelf("StoreyC", 7f, W, 2f * C);
        float exitY = 2f * C;
        if (step) { p.Block("ExitStep", W - 4f, W, 2f * C, 3f * C); exitY = 3f * C; }
        float roof = 2f * C + 6.5f;
        p.Point(1.5f, 0f);
        p.AddRoute(Fill(p, r, 2f, b2r - 1f, 0f, C - T, +1, false));
        p.Point(b2r + 1.8f, 0f);
        p.Point(b2r - 1.5f, C);
        p.AddRoute(Fill(p, r, 10f + bgap + 1f, b2r - 1.5f, C, 2f * C - T, -1));
        p.Point(10f + bgap + 0.9f, C);
        p.Point(8.4f, C);
        p.HazardFloor("ShortLanding", 9f, 10f, C);
        p.Point(4.6f, C);
        p.Point(8.8f, 2f * C);
        float rewX = W - 9f;
        p.AddRoute(Fill(p, r, 9.4f, rewX - 1.2f, 2f * C, roof, +1));
        p.Shelf("RewardShelf", rewX, rewX + 2.6f, 2f * C + 2.2f);
        p.Reward(rewX + 1.3f, 2f * C + 2.2f);
        if (step) p.Point(W - 6.2f, 2f * C);
        p.Point(W - 1.5f, exitY);
        p.length = W; p.exitY = exitY; p.roof = roof; p.floor = 0f;
    }

    // Sharp drop into a deep shaft. A catch ledge (reward) can be reached by steering during the fall;
    // a long jump onto the slab is a faster high route; the lower corridor is the main route.
    private static void DropShaft(Chamber p, System.Random r)
    {
        float W = Rr(r, 29f, 35f), dp = Rr(r, 7f, 10f), fl = -dp;
        p.name = "DropShaft";
        p.Block("EntryLedge", 0f, 6f, fl, 0f);
        p.Shelf("CatchLedge", 10.5f, 12.6f, fl + dp * 0.5f);
        p.Reward(11.5f, fl + dp * 0.5f);
        float slabL = 14.5f, slabR = W - 8f;
        p.Shelf("FastSlab", slabL, slabR, fl + 3.4f);
        p.Block("ExitStep", W - 6f, W, fl, fl + C);
        p.Point(1.5f, 0f);
        p.Point(4.6f, 0f);
        p.Point(7.6f, fl);
        p.Forbid(6.5f, 9.5f);
        p.AddRoute(Fill(p, r, 8.4f, slabL - 0.2f, fl, 0f, +1, false));
        p.AddRoute(Fill(p, r, slabL, slabR - 0.3f, fl, fl + 3f, +1));
        p.Point(W - 7.4f, fl);
        p.Point(W - 1.5f, fl + C);
        Fill(p, r, slabL + 2.2f, slabR - 0.5f, fl + 3.4f, 4.5f, +1, false);   // the fast route has its own obstacles
        p.HazardFloor("SlabStrip", slabL + 0.6f, slabL + 1.8f, fl + 3.4f);
        p.length = W; p.exitY = fl + C; p.roof = 4.5f; p.floor = fl;
    }

    // Upper gallery of platforms with jumps (main route) over a recovery floor with its own obstacles
    // and stairs back up. Reward is tucked under the entry ledge on the recovery floor.
    private static void Gallery(Chamber p, System.Random r)
    {
        float L = Rr(r, 4.4f, 4.8f);
        bool step = r.NextDouble() < (p.bias > 0 ? 0.85 : 0.4);
        p.name = "GalleryOverRecovery";
        float targetW = Rr(r, 33f, 39f);
        p.Block("EntryMass", 0f, 5f, -L, 0f);
        float x = 5f;
        var plats = new List<Vector2>();
        while (true)
        {
            float g = Rr(r, 2.4f, 3.4f), pw = Rr(r, 4.6f, 6.6f);
            if (x + g + pw > targetW - 5f - 2.6f) break;
            plats.Add(new Vector2(x + g, x + g + pw));
            x = x + g + pw;
        }
        float W = x + Rr(r, 2.4f, 3.2f) + 5f;
        float roof = 6.5f;
        p.Block("ExitMass", W - 5f, W, -L, 0f);
        float exitY = 0f;
        if (step) { p.Block("ExitStep", W - 2.6f, W, 0f, C); exitY = C; }
        p.Block("RecoveryStep", W - 8f, W - 5f, -L, -L * 0.5f);
        p.Point(1.5f, 0f);
        p.Point(4f, 0f);
        int gateI = plats.Count > 0 ? r.Next(plats.Count) : -1;
        int n = Mathf.Max(1, plats.Count);
        int hurdleI = (gateI + 1 + r.Next(Mathf.Max(1, plats.Count - 1))) % n;
        for (int i = 0; i < plats.Count; i++)
        {
            float a = plats[i].x, b = plats[i].y;
            p.Shelf("Platform" + (i + 1), a, b, 0f);
            p.Point(a + 1f, 0f);
            p.HazardFloor("ShortLanding", a, a + 0.55f, 0f);
            if (i == gateI && b - a >= 5.4f)
            {
                float gx = a + 2.6f;
                p.Block("SlideGate", gx, gx + 1.2f, S, roof);
                p.Forbid(gx - 1.5f, gx + 2.7f);
                p.HazardWall("GateFace", gx, S, S + 1.6f, -1f);
            }
            else if (i == hurdleI && b - a >= 5f)
            {
                float hx = a + 2.5f, hh = Rr(r, 1f, 1.6f);
                p.Block("Hurdle", hx, hx + 0.9f, 0f, hh);
                p.HazardWall("HurdleFace", hx, hh * 0.45f, hh, -1f);
            }
            p.Point(b - 0.9f, 0f);
        }
        p.Point(W - 4f, 0f);
        p.Point(W - 1.5f, exitY);
        Fill(p, r, 6.8f, W - 8.6f, -L, -T, +1);   // recovery floor (never the main route)
        p.Reward(5.7f, -L);
        p.DedupeRoute();
        p.length = W; p.exitY = exitY; p.roof = roof; p.floor = -L;
    }

    // Hop up two pillars onto a bridge, slide under its divider, then a sharp drop into a lower hall.
    // A long jump off the bridge lands on a mezzanine (fast route + reward); missing it drops you
    // into the lower hall, which is the main route.
    private static void ClimbDrop(Chamber p, System.Random r)
    {
        float W = Rr(r, 37f, 43f), dp = Rr(r, 3.4f, 6f);
        float p1 = Rr(r, 1.8f, 2.3f);
        float p2 = p1 + Rr(r, 1f, 1.6f);
        float m = p2 + Rr(r, 0.8f, 1.3f);
        float roof = m + 5f;
        p.name = "ClimbOverDrop";
        p.Block("Ground", 0f, 21f, -dp, 0f);
        p.Block("Pillar1", 5f, 6.5f, 0f, p1);
        p.Block("Pillar2", 9.5f, 11f, 0f, p2);
        if (p2 - C > 0.05f) p.Block("PitStep", 11f, 13.5f, 0f, p2 - C);
        p.Block("Bridge", 13.5f, 21f, 0f, m);
        float gx = Rr(r, 16f, 17.2f);
        p.Block("BridgeGate", gx, gx + Rr(r, 1.2f, 2f), m + S, roof);
        p.Forbid(gx - 1.5f, gx + 3.5f);
        p.Point(1.5f, 0f);
        p.Point(3.4f, 0f);
        p.Point(5.75f, p1);
        p.Point(10.25f, p2);
        p.HazardWall("Pillar2Face", 9.5f, p2 - 1f, p2, -1f);
        p.Point(14.4f, m);
        p.Point(gx - 2.3f, m);
        p.Point(20.2f, m);
        p.Point(22.6f, -dp);
        p.Forbid(21f, 24.5f);
        // Lower hall: a mezzanine overhead is only reachable by a long jump off the bridge (fast route,
        // reward on it); the main route runs underneath with its own obstacles.
        float mzL = 21f + Rr(r, 4.6f, 5.6f), mzR = W - 5.5f, mzTop = -dp + 3.2f;
        p.Shelf("Mezzanine", mzL, mzR, mzTop);
        p.Reward(mzL + 1.6f, mzTop);
        p.AddRoute(Fill(p, r, 23.4f, mzL - 0.3f, -dp, roof, +1));
        p.AddRoute(Fill(p, r, mzL, mzR - 0.2f, -dp, mzTop - T, +1));
        p.Point(W - 1.5f, -dp);
        p.DedupeRoute();
        p.length = W; p.exitY = -dp; p.roof = roof; p.floor = -dp;
    }

    // Compact technical run: three floor levels joined by climbs or drops, each filled with obstacles.
    private static void Rhythm(Chamber p, System.Random r)
    {
        p.name = "RhythmRun";
        float W = Rr(r, 30f, 38f);
        float[] firstCuts = { 10f, 12f, 14f };
        float c0 = firstCuts[r.Next(firstCuts.Length)];
        var cuts = new List<float> { c0, Mathf.Min(c0 + Rr(r, 7f, 11f), W - 8f) };
        var levels = new List<float> { 0f };
        for (int i = 0; i < cuts.Count; i++)
        {
            float[] opts = p.bias > 0 ? new[] { C, C, 1.4f, 1.4f, -C }
                         : p.bias < 0 ? new[] { 1.4f, -C, -3.6f, -4.8f, -C }
                         : new[] { C, 1.4f, -C, -3.6f, -4.8f };
            levels.Add(levels[levels.Count - 1] + opts[r.Next(opts.Length)]);
        }
        float lo = Mathf.Min(0f, Min(levels)) - 0.5f;
        float roof = Max(levels) + 5.2f;
        var xs = new List<float> { 0f };
        xs.AddRange(cuts);
        xs.Add(W);
        p.Point(1.5f, 0f);
        for (int i = 0; i < levels.Count; i++)
        {
            float a = xs[i], b = xs[i + 1], y = levels[i];
            p.Block("Level" + i, a, b, lo, y);
            if (i > 0)
            {
                float prev = levels[i - 1];
                if (y > prev) { p.Point(a - 1f, prev); p.Point(a + 1f, y); }
                else { p.Point(a - 0.8f, prev); p.Point(a + 1.6f, y); p.Forbid(a, a + 3f); }
            }
            float start = a + (i > 0 ? 2.6f : 2.2f);
            p.AddRoute(Fill(p, r, start, b - 1.4f, y, roof, +1));
        }
        p.Point(W - 1.5f, levels[levels.Count - 1]);
        p.DedupeRoute();
        // Reward ledge above a stretch of floor with no obstacle nearby.
        for (int attempt = 0; attempt < 12; attempt++)
        {
            int seg = r.Next(levels.Count);
            if (xs[seg + 1] - xs[seg] <= 6.2f) continue;
            float rx = Rr(r, xs[seg] + 3f, xs[seg + 1] - 3f);
            bool busy = false;
            for (int i = 0; i < p.blocks.Count; i++)
            {
                if (p.labels[i].StartsWith("Level")) continue;
                float b0 = p.blocks[i].xMin / p.h, b1 = p.blocks[i].xMax / p.h;
                if (b1 > rx - 2.8f && b0 < rx + 2.8f) { busy = true; break; }
            }
            if (busy) continue;
            p.Shelf("RewardLedge", rx - 1.2f, rx + 1.2f, levels[seg] + C + 0.6f);
            p.Reward(rx, levels[seg] + C + 0.6f);
            break;
        }
        p.length = W; p.exitY = levels[levels.Count - 1]; p.roof = roof; p.floor = lo;
    }

    private static float Min(List<float> v) { float m = v[0]; foreach (float f in v) m = Mathf.Min(m, f); return m; }
    private static float Max(List<float> v) { float m = v[0]; foreach (float f in v) m = Mathf.Max(m, f); return m; }

    /// <summary>Mirror horizontally and reverse the route: an ascending chamber becomes descending,
    /// and each storey keeps its travel direction (so switchbacks stay switchbacks).</summary>
    private static Chamber Reverse(Chamber p)
    {
        float L = p.length * p.h, ey = p.exitY * p.h;
        var q = new Chamber { kind = p.kind, name = p.name.Replace("Up", "Down"), seed = p.seed, bias = p.bias, h = p.h };
        for (int i = 0; i < p.blocks.Count; i++)
        {
            Rect b = p.blocks[i];
            q.labels.Add(p.labels[i]);
            q.blocks.Add(Rect.MinMaxRect(L - b.xMax, b.yMin - ey, L - b.xMin, b.yMax - ey));
        }
        for (int i = p.route.Count - 1; i >= 0; i--) q.route.Add(new Vector2(L - p.route[i].x, p.route[i].y - ey));
        foreach (Vector2 v in p.rewards) q.rewards.Add(new Vector2(L - v.x, v.y - ey));
        foreach (Hazard hz in p.hazards)
        {
            q.hazards.Add(new Hazard
            {
                label = hz.label, kind = hz.kind,
                trigger = Rect.MinMaxRect(L - hz.trigger.xMax, hz.trigger.yMin - ey, L - hz.trigger.xMin, hz.trigger.yMax - ey),
                visual = Rect.MinMaxRect(L - hz.visual.xMax, hz.visual.yMin - ey, L - hz.visual.xMin, hz.visual.yMax - ey)
            });
        }
        foreach (Vector2 z in p.noHazardX) q.noHazardX.Add(new Vector2(L - z.y, L - z.x));
        q.length = p.length; q.exitY = -p.exitY; q.roof = p.roof - p.exitY; q.floor = p.floor - p.exitY;
        return q;
    }

    /// <summary>Seal the chamber: bottom floor, ceiling, and caps around 3-height doorways.</summary>
    private static void Shell(Chamber p)
    {
        float L = p.length, ey = p.exitY, roof = p.roof, bottom = p.floor;
        p.Block("BottomFloor", 0f, L, bottom - 0.5f, bottom);
        p.Block("Ceiling", 0f, L, roof, roof + 0.5f);
        p.Block("EntryLowerCap", 0f, 0.3f, bottom, 0f);
        p.Block("EntryUpperCap", 0f, 0.3f, 3f, roof + 0.5f);
        p.Block("ExitLowerCap", L - 0.3f, L, bottom, ey);
        p.Block("ExitUpperCap", L - 0.3f, L, ey + 3f, roof + 0.5f);
    }
}
