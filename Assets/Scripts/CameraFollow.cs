using UnityEngine;

public class CameraFollow : MonoBehaviour
{
    [SerializeField] private Transform target;
    [Tooltip("Fraction of the remaining distance covered per 60 fps frame (same feel as before, now frame-rate independent).")]
    [SerializeField] private float smoothSpeed = 0.125f;
    [SerializeField] private Vector3 offset = new Vector3(0, 0, -10);
    [Header("Vertical handling for drops and climbs")]
    [Tooltip("How far the camera looks below the player while falling fast.")]
    [SerializeField] private float fallLookAhead = 3f;
    [Tooltip("Vertical distance beyond which the camera catches up faster (big drops).")]
    [SerializeField] private float verticalCatchUpDistance = 4f;

    private Rigidbody2D targetBody;
    private float lookAhead;

    private void LateUpdate()
    {
        if (target == null) return;
        if (targetBody == null || targetBody.transform != target) targetBody = target.GetComponent<Rigidbody2D>();

        // Look further down while falling so landings below are visible before you reach them.
        float vy = targetBody != null ? targetBody.linearVelocity.y : 0f;
        float wanted = vy < -6f ? -fallLookAhead * Mathf.InverseLerp(6f, 18f, -vy) : 0f;
        lookAhead = Mathf.MoveTowards(lookAhead, wanted, 8f * Time.deltaTime);

        Vector3 desired = target.position + offset + Vector3.up * lookAhead;
        float frames = Time.deltaTime * 60f;
        float t = 1f - Mathf.Pow(1f - Mathf.Clamp01(smoothSpeed), frames);
        float dy = Mathf.Abs(desired.y - transform.position.y);
        float tY = dy > verticalCatchUpDistance ? 1f - Mathf.Pow(1f - Mathf.Clamp01(smoothSpeed * 2f), frames) : t;
        Vector3 p = transform.position;
        transform.position = new Vector3(Mathf.Lerp(p.x, desired.x, t), Mathf.Lerp(p.y, desired.y, tY), desired.z);
    }

    public void SetTarget(Transform newTarget)
    {
        target = newTarget;
    }
}
