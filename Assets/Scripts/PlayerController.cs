using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody2D), typeof(BoxCollider2D))]
public class PlayerController : MonoBehaviour
{
    [Header("Movement Settings")]
    public float moveSpeed = 7f;
    public float jumpForce = 12f;
    [Header("Jump Height")]
    [Range(0f, 1f)] public float jumpReleaseMultiplier = 0.5f;
    [Header("Detection")]
    public LayerMask groundLayer;
    public LayerMask wallLayer;
    public Transform groundCheck;
    public Transform wallCheck;
    public float groundCheckRadius = 0.12f;
    public float wallCheckRadius = 0.15f;
    [Header("Wall Settings")]
    public float wallSlidingSpeed = 2f;
    public Vector2 wallJumpForce = new Vector2(5f, 12f);
    public float wallJumpDuration = 0.4f;
    public float wallJumpSteering = 35f;
    [Header("Slide Settings")]
    [SerializeField] private float slideHeightMultiplier = 0.6f;
    [SerializeField] private float slideWidthMultiplier = 1.2f;
    [SerializeField] private float slideDeceleration = 4f;
    [SerializeField] private float slideAnimDuration = 0.1f;
    [SerializeField] private float forcedCrouchSpeedMultiplier = 0.4f;
    [SerializeField] private LayerMask environmentLayer;

    [Header("Runtime Status - observe during Play")]
    [SerializeField] private bool debugGrounded;
    [SerializeField] private bool debugDownHeld;
    [SerializeField] private bool debugSliding;
    [SerializeField] private bool debugCrouched;
    [SerializeField] private Vector2 debugColliderSize;
    [SerializeField] private string debugSlideStatus;

    private Rigidbody2D rb;
    private BoxCollider2D col;
    private Transform visual;
    private SpriteRenderer visualRenderer;
    private Vector2 moveInput;
    private bool jumpRequested, jumpReleased, downWasHeld, crouchButtonHeld;
    private bool crouched, sliding;
    private float wallJumpTimer;
    private Vector2 standingSize, standingOffset;
    private Vector3 standingVisualScale, standingVisualPosition;
    private float visualBottom, visualBlend;
    private float animationStart, animationTarget, animationTime;
    private readonly List<Collider2D> overlaps = new List<Collider2D>();
    private const float Skin = 0.005f;

    private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        col = GetComponent<BoxCollider2D>();
        rb.freezeRotation = true;
        // Match the previous controller's effective gravity.
        rb.gravityScale = 3f;
        standingSize = col.size;
        standingOffset = col.offset;
        SetupVisual();
    }

    private void SetupVisual()
    {
        visual = transform.Find("Visual");
        if (visual == null)
        {
            visual = new GameObject("Visual").transform;
            visual.SetParent(transform, false);
        }
        visualRenderer = visual.GetComponent<SpriteRenderer>();
        SpriteRenderer rootRenderer = GetComponent<SpriteRenderer>();
        if (visualRenderer == null || visualRenderer.sprite == null)
        {
            if (rootRenderer != null && rootRenderer.sprite != null)
            {
                if (visualRenderer == null)
                    visualRenderer = visual.gameObject.AddComponent<SpriteRenderer>();
                visualRenderer.sprite = rootRenderer.sprite;
                visualRenderer.color = rootRenderer.color;
                visualRenderer.sharedMaterials = rootRenderer.sharedMaterials;
                visualRenderer.sortingLayerID = rootRenderer.sortingLayerID;
                visualRenderer.sortingOrder = rootRenderer.sortingOrder;
                visualRenderer.flipX = rootRenderer.flipX;
                visualRenderer.flipY = rootRenderer.flipY;
                visualRenderer.drawMode = rootRenderer.drawMode;
                visualRenderer.size = rootRenderer.size;
            }
        }
        // A standing sprite on the root can hide the animated child.
        if (rootRenderer != null && visualRenderer != null && visualRenderer.sprite != null)
        {
            visualRenderer.enabled = true;
            rootRenderer.enabled = false;
        }
        standingVisualScale = visual.localScale;
        standingVisualPosition = visual.localPosition;
        if (visualRenderer != null && visualRenderer.sprite != null)
        {
            Bounds bounds = visualRenderer.localBounds;
            visualBottom = visualRenderer.flipY ? -bounds.max.y : bounds.min.y;
        }
    }

    private void LateUpdate()
    {
        if (visual == null) return;
        animationTime += Time.deltaTime;
        float t = slideAnimDuration <= 0f ? 1f : Mathf.Clamp01(animationTime / slideAnimDuration);
        visualBlend = Mathf.Lerp(animationStart, animationTarget, Mathf.SmoothStep(0f, 1f, t));
        float width = Mathf.Lerp(1f, slideWidthMultiplier, visualBlend);
        float height = Mathf.Lerp(1f, slideHeightMultiplier, visualBlend);
        Vector3 scale = standingVisualScale;
        scale.x *= width;
        scale.y *= height;
        visual.localScale = scale;
        // Retain the bottom of the actual sprite, including noncentral pivots.
        Vector3 localDelta = new Vector3(0f, visualBottom * (standingVisualScale.y - scale.y), 0f);
        visual.localPosition = standingVisualPosition + visual.localRotation * localDelta;
    }

    private ContactFilter2D Filter(int mask)
    {
        ContactFilter2D filter = new ContactFilter2D();
        filter.SetLayerMask(mask & ~(1 << gameObject.layer));
        filter.useTriggers = false;
        return filter;
    }

    private bool IsOwn(Collider2D other)
    {
        return other == col || other.attachedRigidbody == rb || other.transform.IsChildOf(transform);
    }

    private bool Probe(Vector2 center, Vector2 size, int mask)
    {
        overlaps.Clear();
        Physics2D.OverlapBox(center, size, 0f, Filter(mask), overlaps);
        foreach (Collider2D other in overlaps)
        {
            if (!IsOwn(other)) return true;
        }
        return false;
    }

    private bool ShapeFits(Vector2 size, Vector2 offset)
{
    Vector3 scale = transform.lossyScale;

    Vector2 worldSize = new Vector2(
        size.x * Mathf.Abs(scale.x),
        size.y * Mathf.Abs(scale.y)
    );

    // Use the physics position, avoiding an interpolated visual position.
    Quaternion rotation = Quaternion.Euler(0f, 0f, rb.rotation);

    Vector2 scaledOffset = new Vector2(
        offset.x * scale.x,
        offset.y * scale.y
    );

    Vector2 center = rb.position +
        (Vector2)(rotation * (Vector3)scaledOffset);

    float bottom = center.y - worldSize.y * 0.5f;

    // Check slightly above the feet so floor contact is allowed.
    const float floorTolerance = 0.03f;
    const float edgeTolerance = 0.005f;

    center.y += floorTolerance * 0.5f;
    worldSize.y = Mathf.Max(
        0.01f,
        worldSize.y - floorTolerance - edgeTolerance
    );
    worldSize.x = Mathf.Max(
        0.01f,
        worldSize.x - edgeTolerance * 2f
    );

    int mask = environmentLayer.value |
               groundLayer.value |
               wallLayer.value;

    overlaps.Clear();

    Physics2D.OverlapBox(
        center,
        worldSize,
        rb.rotation,
        Filter(mask),
        overlaps
    );

    foreach (Collider2D other in overlaps)
    {
        if (IsOwn(other))
            continue;

        // Supporting floors must not prevent changing posture.
        if (other.bounds.max.y <= bottom + floorTolerance)
            continue;

        return false;
    }

    return true;
}

    private void SetCrouch(bool value)
    {
        if (crouched == value) return;
        crouched = value;
        col.size = value ? new Vector2(standingSize.x * slideWidthMultiplier,
                                      standingSize.y * slideHeightMultiplier) : standingSize;
        col.offset = standingOffset + Vector2.down * ((standingSize.y - col.size.y) * 0.5f);
        animationStart = visualBlend;
        animationTarget = value ? 1f : 0f;
        animationTime = 0f;
    }

    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        Vector2 velocity = rb.linearVelocity;
        bool down = moveInput.y < -0.5f || crouchButtonHeld;
        bool downPressed = down && !downWasHeld;
        downWasHeld = down;

        float currentSpeedMultiplier = GetSpeedMultiplier();

        // Probe just outside the collider instead of relying on contact normals.
        // Shrink the ground probe horizontally so a side wall is not a floor.
        Bounds bounds = col.bounds;
        int groundMask = groundLayer.value | wallLayer.value;
        bool grounded = velocity.y <= 0.1f && Probe(
            new Vector2(bounds.center.x, bounds.min.y - 0.015f),
            new Vector2(Mathf.Max(0.02f, bounds.size.x - 0.08f), 0.07f),
            groundMask);
        Vector2 wallProbeSize = new Vector2(0.07f, Mathf.Max(0.02f, bounds.size.y * 0.6f));
        bool leftWall = Probe(new Vector2(bounds.min.x - 0.015f, bounds.center.y), wallProbeSize, wallLayer.value);
        bool rightWall = Probe(new Vector2(bounds.max.x + 0.015f, bounds.center.y), wallProbeSize, wallLayer.value);
        int wallSide = rightWall && (!leftWall || moveInput.x >= 0f) ? 1 : leftWall ? -1 : 0;

        debugGrounded = grounded;
        debugDownHeld = down;
        if (downPressed)
        {
            debugSlideStatus = !grounded ? "Not grounded" : crouched ? "Already crouched" :
                Mathf.Abs(velocity.x) <= 0.1f ? "Run before pressing down" : "Checking clearance";
        }
        wallJumpTimer = grounded ? 0f : Mathf.Max(0f, wallJumpTimer - dt);

        if (downPressed && grounded && !crouched && Mathf.Abs(velocity.x) > 0.1f)
        {
            Vector2 size = new Vector2(standingSize.x * slideWidthMultiplier, standingSize.y * slideHeightMultiplier);
            Vector2 offset = standingOffset + Vector2.down * ((standingSize.y - size.y) * 0.5f);
            if (ShapeFits(size, offset))
            {
                SetCrouch(true);
                sliding = true;
                debugSlideStatus = "Slide started";
            }
            else debugSlideStatus = "Wider slide shape blocked by environment";
        }
        if (sliding && (!down || !grounded)) sliding = false;
        if (crouched && (!down || !grounded) && ShapeFits(standingSize, standingOffset))
        {
            sliding = false;
            SetCrouch(false);
        }

        if (sliding)
            velocity.x = Mathf.MoveTowards(velocity.x, 0f, slideDeceleration * dt);
        else if (crouched && grounded)
            velocity.x = down ? 0f : moveInput.x * moveSpeed * currentSpeedMultiplier * forcedCrouchSpeedMultiplier;
        else if (wallJumpTimer > 0f)
            velocity.x = Mathf.MoveTowards(velocity.x, moveInput.x * moveSpeed * currentSpeedMultiplier, wallJumpSteering * dt);
        else
            velocity.x = moveInput.x * moveSpeed * currentSpeedMultiplier;

        if (!grounded && wallSide != 0 && moveInput.x * wallSide > 0f && wallJumpTimer <= 0f)
            velocity.y = Mathf.Max(velocity.y, -wallSlidingSpeed);

        if (jumpRequested)
        {
            bool canStand = !crouched || ShapeFits(standingSize, standingOffset);
            if (canStand && (grounded || (wallSide != 0 && wallJumpTimer <= 0f)))
            {
                sliding = false;
                SetCrouch(false);
                if (grounded) velocity.y = jumpForce;
                else
                {
                    velocity.x = -wallSide * wallJumpForce.x;
                    velocity.y = wallJumpForce.y;
                    wallJumpTimer = wallJumpDuration;
                }
            }
        }
        if (jumpReleased && velocity.y > 0f) velocity.y *= jumpReleaseMultiplier;
        jumpRequested = false;
        jumpReleased = false;
        rb.linearVelocity = velocity;
        UpdateMarkers();
        debugSliding = sliding;
        debugCrouched = crouched;
        debugColliderSize = col.size;
    }

    private float lastSpeedMultiplier = 1f;
    private float GetSpeedMultiplier()
    {
        float mult = 1f;
        if (TryGetComponent<EffectProcessor>(out var ep))
        {
            mult = ep.GetTotalMultiplier();
        }

        if (sliding && mult != lastSpeedMultiplier)
        {
            float ratio = mult / lastSpeedMultiplier;
            Vector2 v = rb.linearVelocity;
            v.x *= ratio;
            rb.linearVelocity = v;
        }
        lastSpeedMultiplier = mult;
        return mult;
    }

    public void TeleportTo(Vector3 targetPosition)
    {
        rb.position = targetPosition;
        rb.linearVelocity = Vector2.zero;
        sliding = false;
        SetCrouch(false);
        wallJumpTimer = 0f;
        jumpRequested = false;
        jumpReleased = false;
    }

    public void OnInteract(InputValue value)
    {
        if (value.isPressed)
        {
            if (TryGetComponent<EffectProcessor>(out var ep))
            {
                ep.TryUseTeleport();
            }
        }
    }

    private void UpdateMarkers()
    {
        // Optional scene markers; collider-edge probes drive detection.
        if (groundCheck != null)
            groundCheck.localPosition = new Vector3(col.offset.x, col.offset.y - col.size.y * 0.5f, 0f);
        if (wallCheck != null)
        {
            float side = Mathf.Abs(moveInput.x) > 0.01f ? Mathf.Sign(moveInput.x) : Mathf.Sign(wallCheck.localPosition.x);
            wallCheck.localPosition = new Vector3(col.offset.x + side * col.size.x * 0.5f, col.offset.y, 0f);
        }
    }

    public void OnCrouch(InputValue value) { crouchButtonHeld = value.isPressed; }

    private bool isGameOver = false;

    public void OnGameOver()
    {
        isGameOver = true;
        rb.linearVelocity = Vector2.zero;
        rb.simulated = false; 
    }

    public void OnRestart(InputValue value)
    {
        if (value.isPressed)
        {
            UnityEngine.SceneManagement.SceneManager.LoadScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
        }
    }

    public void OnMove(InputValue value) 
    { 
        if (isGameOver) return;
        moveInput = value.Get<Vector2>(); 
    }

    public void OnJump(InputValue value)
    {
        if (isGameOver) return;
        if (value.isPressed) jumpRequested = true;
        else jumpReleased = true;
    }
}
