using UnityEngine;
using System.Collections.Generic;
using System.Reflection;

// V5: multi-storey chamber templates + controller-accurate validation + trap scan.
// Planning lives in DeadlineCoursePlanner; this component reads the player's real settings,
// builds the start room, asks the planner for validated rooms ahead of the player, and creates
// one GameObject per wall/platform, grouped as Room_### / Chamber_# / blocks.
[DefaultExecutionOrder(-200)]
public class DeadlinePatternCourse : MonoBehaviour
{
    public PlayerController player;
    public Sprite square;
    public Material material;
    public PhysicsMaterial2D physicsMaterial;
    public int environmentLayer = 8;
    public int seed = 12345;
    [Tooltip("Off = the same course every Play (use 'seed'). On = a new random course each Play.")]
    public bool newSeedEachRun = true;
    [Min(60)] public float generateAhead = 160;

    [Header("Pickups: speed boost and death-wall rewind (no teleport)")]
    public bool spawnPickups = true;
    [Min(1)] public int minimumRoomsBetweenPickups = 2;
    [Min(1)] public int maximumRoomsBetweenPickups = 3;

    [Header("Slowdown zones (red strips, never lethal)")]
    public bool spawnSlowdownZones = true;
    [Tooltip("Chance that a room gets any slowdown zones at all. The rest stay clean.")]
    [Range(0f, 1f)] public float slowdownRoomChance = 0.6f;
    [Tooltip("Chance that each designed candidate spot is used, in a room that has zones.")]
    [Range(0f, 1f)] public float slowdownCandidateChance = 0.35f;
    [Range(0, 4)] public int maxSlowdownZonesPerRoom = 2;

    [Header("Room shape and checks")]
    [Range(0f, 1f)] public float twoChamberRoomChance = 0.75f;
    [Tooltip("Proves every standable spot in a chamber can still reach its exit (no dead ends).")]
    public bool trapScan = true;
    public bool logRoomsToConsole = true;

    [Header("Fallback slide settings (the real values are read from the player at Play)")]
    public float slideHeightRatio = 0.6f;
    public float slideWidthRatio = 1.2f;
    public float slideDeceleration = 4f;

    [Header("Runtime diagnostics")]
    [SerializeField] private string generatorVersion = "5 - multi-storey chambers";
    [SerializeField] private int activeSeed;
    [SerializeField] private int validatedRooms, rejectedChambers, fallbackRooms;
    [SerializeField] private string lastRejection;
    [SerializeField] private string lastRoom;
    [SerializeField] private float currentElevation;
    public int ActiveSeed { get; private set; }

    // World-space copies used by the Autoplay test (DeadlineAutoplayBot).
    public readonly List<Vector2> AutoplayRoute = new List<Vector2>();
    public readonly List<Rect> AutoplaySolids = new List<Rect>();
    public DeadlineRouteValidator.MoveSpec Spec { get { return spec; } }

    // The PlayerController forces gravityScale = 3 in its Awake (which runs after this script).
    private const float PlayerGravityScale = 3f;

    private DeadlineCoursePlanner planner;
    private DeadlineRouteValidator.MoveSpec spec;
    private DeathWall wall;
    private float h, w;
    private Vector2 bodyOffset;
    private readonly List<List<Vector3>> mainRoutes = new List<List<Vector3>>();
    private readonly List<List<Vector3>> traces = new List<List<Vector3>>();
    private readonly List<Vector3> optionalSpots = new List<Vector3>();

    private void Awake()
    {
        generatorVersion = "5 - multi-storey chambers";
        if (!player || !square) { StopGeneration("Assign player and square sprite."); return; }
        if (transform.position.sqrMagnitude > 0.0001f ||
            Quaternion.Angle(transform.rotation, Quaternion.identity) > 0.01f ||
            (transform.lossyScale - Vector3.one).sqrMagnitude > 0.0001f)
        { StopGeneration("ProceduralCourse needs position/rotation zero and scale one."); return; }

        spec = ReadMoveSpec(player, this);
        if (spec == null) { StopGeneration("Player needs a BoxCollider2D with positive size, speed and jump."); return; }
        h = spec.height; w = spec.width;
        var col = player.GetComponent<BoxCollider2D>();
        Vector3 scale = player.transform.lossyScale;
        bodyOffset = Vector2.Scale(col.offset, new Vector2(scale.x, scale.y));

        ActiveSeed = newSeedEachRun
            ? System.BitConverter.ToInt32(System.Guid.NewGuid().ToByteArray(), 0) & int.MaxValue : seed;
        activeSeed = ActiveSeed;
        planner = new DeadlineCoursePlanner(spec, MakeSettings(), ActiveSeed);
        Debug.Log("[Deadline v5] Course seed " + ActiveSeed + " (untick 'New Seed Each Run' and type this seed to replay it). " +
                  "Player " + w.ToString("0.##") + " x " + h.ToString("0.##") + ", speed " + spec.speed + ", jump " + spec.jump +
                  ", full jump height " + spec.Apex(1f).ToString("0.00") + " units.", this);

        // Hand-made start room: flat, 12 player-heights long, 3-height doorway on the right.
        List<Rect> startBlocks = StartBlocks(h);
        string[] startNames = { "Floor", "Ceiling", "BackWall", "ExitCap" };
        var startRoot = new GameObject("Room_000_Start").transform;
        startRoot.SetParent(transform, false);
        for (int i = 0; i < startBlocks.Count; i++) Box(startRoot, startNames[i], startBlocks[i], SolidColor);
        Marker(startRoot, "Exit", new Vector2(12f * h, 0f));
        planner.SetStart(startBlocks, new Vector2(10.5f * h, 0f), 12f * h, 0f);
        AutoplaySolids.AddRange(startBlocks);
        AutoplayRoute.Add(new Vector2(2f * h, 0f));
        AutoplayRoute.Add(new Vector2(10.5f * h, 0f));
        player.transform.position = new Vector3(2f * h - bodyOffset.x, h / 2f + 0.03f - bodyOffset.y, player.transform.position.z);
        Extend();
    }

    /// <summary>Start room blocks: floor, ceiling, back wall, cap above the exit doorway.</summary>
    public static List<Rect> StartBlocks(float h)
    {
        return new List<Rect>
        {
            Rect.MinMaxRect(0f, -0.5f * h, 12f * h, 0f),
            Rect.MinMaxRect(0f, 6f * h, 12f * h, 6.5f * h),
            Rect.MinMaxRect(-h, -0.5f * h, 0f, 6.5f * h),
            Rect.MinMaxRect(11.7f * h, 3f * h, 12f * h, 6.5f * h)
        };
    }

    public DeadlineCoursePlanner.Settings MakeSettings()
    {
        var effects = player.GetComponent<EffectProcessor>();
        return new DeadlineCoursePlanner.Settings
        {
            spawnPickups = spawnPickups,
            spawnHazards = spawnSlowdownZones,
            trapScan = trapScan,
            pickupMin = Mathf.Max(1, minimumRoomsBetweenPickups),
            pickupMax = Mathf.Max(Mathf.Max(1, minimumRoomsBetweenPickups), maximumRoomsBetweenPickups),
            hazardRoomChance = slowdownRoomChance,
            hazardCandidateChance = slowdownCandidateChance,
            maxHazardsPerRoom = maxSlowdownZonesPerRoom,
            twoChamberChance = twoChamberRoomChance,
            slowMultiplier = effects != null ? effects.SlowdownMultiplier : 0.5f
        };
    }

    /// <summary>Copy the movement values the validator needs from the actual player.</summary>
    public static DeadlineRouteValidator.MoveSpec ReadMoveSpec(PlayerController p, DeadlinePatternCourse fallback)
    {
        var col = p.GetComponent<BoxCollider2D>();
        if (!col) return null;
        Vector3 scale = p.transform.lossyScale;
        var m = new DeadlineRouteValidator.MoveSpec
        {
            width = col.size.x * Mathf.Abs(scale.x),
            height = col.size.y * Mathf.Abs(scale.y),
            speed = p.moveSpeed,
            jump = p.jumpForce,
            gravity = Mathf.Abs(Physics2D.gravity.y) * PlayerGravityScale,
            release = p.jumpReleaseMultiplier,
            slideHeight = ReadFloat(p, "slideHeightMultiplier", fallback ? fallback.slideHeightRatio : 0.6f),
            slideWidth = ReadFloat(p, "slideWidthMultiplier", fallback ? fallback.slideWidthRatio : 1.2f),
            deceleration = ReadFloat(p, "slideDeceleration", fallback ? fallback.slideDeceleration : 4f),
            crawl = ReadFloat(p, "forcedCrouchSpeedMultiplier", 0.4f),
            wallSlide = p.wallSlidingSpeed
        };
        if (m.width <= 0f || m.height <= 0f || m.speed <= 0f || m.jump <= 0f || m.gravity < 0.01f) return null;
        return m;
    }

    private static float ReadFloat(PlayerController p, string field, float fallback)
    {
        FieldInfo f = typeof(PlayerController).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (f != null && f.FieldType == typeof(float)) return (float)f.GetValue(p);
        return fallback;
    }

    private void Update()
    {
        if (planner == null) return;
        if (!wall) wall = Object.FindAnyObjectByType<DeathWall>();
        if (wall && wall.IsGameOver()) return;
        Extend();
    }

    private void Extend()
    {
        // Older geometry is kept so a wall rewind never exposes empty space.
        int budget = 4;
        while (enabled && planner != null && planner.EndX < player.transform.position.x + generateAhead && budget-- > 0)
            BuildRoom();
    }

    private void BuildRoom()
    {
        DeadlineCoursePlanner.Room room = planner.PlanNextRoom();
        ReportRejections();
        if (room == null) { StopGeneration("No validated room could be planned (seed " + ActiveSeed + ")."); return; }
        BuildRoomObjects(room);
        validatedRooms++;
        rejectedChambers = planner.RejectedChambers;
        fallbackRooms = planner.FallbackRooms;
        currentElevation = planner.EndY;
    }

    private void ReportRejections()
    {
        // Each rejection names the chamber type and chamberSeed so it can be reproduced.
        var list = planner.rejections;
        for (int i = Mathf.Max(0, list.Count - 10); i < list.Count; i++)
        {
            if (logRoomsToConsole) Debug.Log("[Deadline v5] seed " + ActiveSeed + " | " + list[i], this);
            lastRejection = list[i];
        }
        list.Clear();
    }

    private static readonly Color SolidColor = new Color(0.08f, 0.18f, 0.36f);
    private static readonly Color DividerColor = new Color(0.13f, 0.26f, 0.5f);
    private static readonly Color RewardColor = new Color(0.36f, 0.3f, 0.12f);

    private void BuildRoomObjects(DeadlineCoursePlanner.Room room)
    {
        var names = new List<string>();
        foreach (var c in room.chambers) names.Add(c.name);
        Vector2 rootPos = room.connectorOrigins[0];
        var root = new GameObject("Room_" + room.index.ToString("000") + "_" + string.Join("_", names)).transform;
        root.SetParent(transform, false);
        root.localPosition = rootPos;

        for (int i = 0; i < room.chambers.Count; i++)
        {
            DeadlineCoursePlanner.Chamber c = room.chambers[i];
            Vector2 origin = room.origins[i];

            // Connector (short sealed corridor) leading into this chamber.
            var con = new GameObject("Connector_" + (i + 1)).transform;
            con.SetParent(root, false);
            con.localPosition = room.connectorOrigins[i] - rootPos;
            foreach (Rect r in planner.Connector(0f, 0f)) Box(con, r.yMin < 0f ? "ConnectorFloor" : "ConnectorRoof", r, SolidColor);
            AutoplaySolids.AddRange(planner.Connector(room.connectorOrigins[i].x, room.connectorOrigins[i].y));
            foreach (Rect r in c.blocks) AutoplaySolids.Add(new Rect(r.position + origin, r.size));
            foreach (Vector2 p in c.route) AutoplayRoute.Add(p + origin);

            var ch = new GameObject("Chamber_" + (i + 1) + "_" + c.name + "_seed" + c.seed).transform;
            ch.SetParent(root, false);
            ch.localPosition = origin - rootPos;
            for (int b = 0; b < c.blocks.Count; b++)
            {
                string label = c.labels[b];
                Color color = label.Contains("Gate") || label.Contains("Divider") ? DividerColor
                            : label.StartsWith("Reward") ? RewardColor : SolidColor;
                Box(ch, label, c.blocks[b], color);
            }
            Marker(ch, "ChamberEntrance", c.route[0]);
            Marker(ch, "ChamberExit", c.route[c.route.Count - 1]);
            if (i == 0) Marker(root, "Entrance", c.route[0] + origin - rootPos);
            if (i == room.chambers.Count - 1) Marker(root, "Exit", c.route[c.route.Count - 1] + origin - rootPos);

            // Slowdown zones chosen for this chamber.
            Transform zones = null;
            for (int k = 0; k < room.hazards.Count; k++)
            {
                if (room.hazardChamber[k] != i) continue;
                if (zones == null) { zones = new GameObject("SlowdownZones").transform; zones.SetParent(ch, false); }
                SpawnHazard(zones, room.hazards[k]);
            }
            // Pickup.
            if (room.pickupChamber == i && room.pickupKind >= 0)
            {
                var holder = new GameObject("Pickups").transform;
                holder.SetParent(ch, false);
                SpawnPickup(holder, room.pickupLocal, room.pickupKind);
            }
            // Gizmo data.
            var route = new List<Vector3>();
            foreach (Vector2 p in c.route) route.Add(p + origin + Vector2.up * (h / 2f));
            mainRoutes.Add(route);
            var tr = new List<Vector3>();
            // Trace entries are (x, feet y, width, height); draw the body centre.
            for (int t = 0; t < c.trace.Count; t += 2)
                tr.Add(new Vector3(c.trace[t].x + origin.x, c.trace[t].y + origin.y + c.trace[t].w * 0.5f, 0f));
            traces.Add(tr);
            foreach (Vector2 r in c.rewards) optionalSpots.Add(r + origin + Vector2.up * (h / 2f));
        }

        string pickup = room.pickupKind == 0 ? "speed boost" : room.pickupKind == 1 ? "death-wall rewind" : "none";
        var parts = new List<string>();
        foreach (var c in room.chambers) parts.Add(c.name + "(chamberSeed " + c.seed + ")");
        lastRoom = "Room " + room.index + ": " + string.Join(" + ", parts) + " | slowdown zones " + room.hazards.Count + " | pickup " + pickup
                 + (room.usedFallback ? " | FALLBACK" : "");
        if (logRoomsToConsole) Debug.Log("[Deadline v5] " + lastRoom, this);
        if (room.usedFallback) Debug.LogWarning("[Deadline v5] Used the plain fallback hall for room " + room.index + " (seed " + ActiveSeed + ").", this);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.green; DrawRoutes(mainRoutes);
        Gizmos.color = new Color(0.4f, 1f, 1f, 0.6f); DrawRoutes(traces);
        Gizmos.color = Color.yellow;
        foreach (Vector3 s in optionalSpots) Gizmos.DrawWireCube(s, Vector3.one * h * 0.6f);
    }

    private void DrawRoutes(List<List<Vector3>> routes)
    {
        foreach (var route in routes)
            for (int i = 1; i < route.Count; i++) Gizmos.DrawLine(route[i - 1], route[i]);
    }

    private void StopGeneration(string reason) { Debug.LogError("[Deadline v5] Procedural lab stopped: " + reason, this); enabled = false; }

    private void Marker(Transform parent, string label, Vector2 feet)
    {
        var t = new GameObject(label).transform;
        t.SetParent(parent, false);
        // Markers sit where the player's transform would be when standing on that spot.
        t.localPosition = new Vector3(feet.x - bodyOffset.x, feet.y + h / 2f + 0.02f - bodyOffset.y, 0f);
    }

    private GameObject Box(Transform parent, string label, Rect r, Color color)
    {
        var go = new GameObject(label);
        go.layer = environmentLayer;
        go.transform.SetParent(parent, false);
        go.transform.localScale = new Vector3(r.width / square.bounds.size.x, r.height / square.bounds.size.y, 1f);
        Vector3 pivotOffset = Vector3.Scale(square.bounds.center, go.transform.localScale);
        go.transform.localPosition = new Vector3(r.center.x, r.center.y, 0f) - pivotOffset;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = square; sr.sharedMaterial = material; sr.color = color;
        var col = go.AddComponent<BoxCollider2D>();
        col.size = square.bounds.size; col.offset = square.bounds.center;
        col.sharedMaterial = physicsMaterial;
        return go;
    }

    private void SpawnHazard(Transform parent, DeadlineCoursePlanner.Hazard hz)
    {
        // The red strip is drawn on the surface; the trigger sticks out slightly so touching the surface counts.
        // It is a trigger on Ignore Raycast, so it never blocks the player or changes clearance.
        GameObject go = Box(parent, "Slowdown_" + hz.label, hz.visual, new Color(1f, 0.12f, 0.12f));
        go.layer = 2;
        go.GetComponent<SpriteRenderer>().sortingOrder = 5;
        var col = go.GetComponent<BoxCollider2D>();
        col.isTrigger = true;
        Vector3 s = go.transform.localScale;
        col.size = new Vector2(hz.trigger.width / s.x, hz.trigger.height / s.y);
        Vector2 delta = hz.trigger.center - hz.visual.center;
        col.offset = square.bounds.center + new Vector3(delta.x / s.x, delta.y / s.y, 0f);
        go.AddComponent<SlowdownHazard>();
    }

    private void SpawnPickup(Transform parent, Vector2 centre, int kind)
    {
        float size = Mathf.Min(w, h) * 0.6f;
        var r = new Rect(centre.x - size / 2f, centre.y - size / 2f, size, size);
        GameObject go = Box(parent, kind == 0 ? "Pickup_SpeedBoost" : "Pickup_DeathWallRewind", r,
            kind == 0 ? new Color(1f, 0.85f, 0.1f) : new Color(0.2f, 0.9f, 1f));
        go.layer = 2;
        go.GetComponent<BoxCollider2D>().isTrigger = true;
        go.GetComponent<SpriteRenderer>().sortingOrder = 10;
        go.AddComponent<DeadlineLabPickup>().kind = kind;
    }
}
