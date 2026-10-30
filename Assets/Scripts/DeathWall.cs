using UnityEngine;
using UnityEngine.Events;

public class DeathWall : MonoBehaviour
{
    [Header("Settings")]
    public float startDelay = 3f;
    public float travelSpeed = 2f;
    public float acceleration = 0.1f; // Units per second per second
    public float maxSpeed = 10f;
    public Transform player;

    [Header("Visuals")]
    [SerializeField] private SpriteRenderer leadingEdge;
    [SerializeField] private SpriteRenderer trailArea;

    private float currentX;
    private bool isMoving = false;
    private bool isGameOver = false;
    private float startTime;

    public UnityEvent OnPlayerCaught;

    private void Start()
    {
        if (player == null)
        {
            GameObject p = GameObject.Find("player");
            if (p != null) player = p.transform;
        }

        // Start 10 units behind player
        currentX = (player != null) ? player.position.x - 10f : transform.position.x;
        transform.position = new Vector3(currentX, transform.position.y, transform.position.z);
        
        startTime = Time.time;
        UpdateVisuals();
    }

    private void Update()
    {
        if (isGameOver) return;

        if (!isMoving && Time.time - startTime >= startDelay)
        {
            isMoving = true;
        }

        if (isMoving)
        {
            // Apply acceleration
            travelSpeed = Mathf.MoveTowards(travelSpeed, maxSpeed, acceleration * Time.deltaTime);

            currentX += travelSpeed * Time.deltaTime;
            transform.position = new Vector3(currentX, transform.position.y, transform.position.z);
        }

        UpdateVisuals();
        CheckCollision();
    }

    private void UpdateVisuals()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        // Cover the camera's height
        float camHeight = cam.orthographicSize * 2f;
        float camWidth = camHeight * cam.aspect;
        
        if (leadingEdge != null)
        {
            leadingEdge.size = new Vector2(0.2f, camHeight * 2f); // Large enough to cover vertical range
            leadingEdge.transform.position = new Vector3(currentX, cam.transform.position.y, 0);
        }

        if (trailArea != null)
        {
            // Cover area to the left of the leading edge
            trailArea.size = new Vector2(camWidth, camHeight * 2f);
            trailArea.transform.position = new Vector3(currentX - camWidth / 2f, cam.transform.position.y, 0);
        }
    }

    private void CheckCollision()
    {
        if (player == null) return;

        // Use player collider bounds to find left edge
        Collider2D col = player.GetComponent<Collider2D>();
        float playerLeft = (col != null) ? col.bounds.min.x : player.position.x;

        if (playerLeft <= currentX)
        {
            TriggerGameOver();
        }
    }

    private void TriggerGameOver()
    {
        isGameOver = true;
        isMoving = false;
        OnPlayerCaught?.Invoke();
    }

    public void Rewind(float distance)
    {
        currentX -= distance;
        transform.position = new Vector3(currentX, transform.position.y, transform.position.z);
        rewindFlash = 0.6f;
    }

    /// <summary>Move the wall back by `seconds` of travel at its CURRENT speed. The speed itself is
    /// unchanged, so this rewinds position only. Returns the distance moved.</summary>
    public float RewindSeconds(float seconds)
    {
        float distance = Mathf.Max(0f, travelSpeed) * seconds;
        Rewind(distance);
        return distance;
    }

    private float rewindFlash;
    private Color edgeColor, trailColor;
    private bool colorsCached;

    private void LateUpdate()
    {
        // Brief cyan flash after a rewind so the push-back is easy to see.
        if (!colorsCached)
        {
            if (leadingEdge != null) edgeColor = leadingEdge.color;
            if (trailArea != null) trailColor = trailArea.color;
            colorsCached = true;
        }
        if (rewindFlash <= 0f) return;
        rewindFlash = Mathf.Max(0f, rewindFlash - Time.deltaTime);
        float k = rewindFlash / 0.6f;
        Color flash = new Color(0.3f, 0.95f, 1f, 1f);
        if (leadingEdge != null) leadingEdge.color = Color.Lerp(edgeColor, flash, k);
        if (trailArea != null) trailArea.color = Color.Lerp(trailColor, new Color(flash.r, flash.g, flash.b, trailColor.a), k * 0.6f);
    }

    public float GetCurrentX() => currentX;
    public bool IsGameOver() => isGameOver;
}
