using UnityEngine;
using TMPro;

public class HUDManager : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI speedText;
    [SerializeField] private TextMeshProUGUI teleportText;
    [SerializeField] private TextMeshProUGUI warningText;

    private EffectProcessor playerEffects;
    private float warningTimer;

    private void Start()
    {
        var player = GameObject.Find("player");
        if (player != null) playerEffects = player.GetComponent<EffectProcessor>();
        if (playerEffects == null) playerEffects = Object.FindAnyObjectByType<EffectProcessor>();
        if (speedText != null) speedText.richText = true;
    }

    [SerializeField] private TextMeshProUGUI gameOverText;

    private void Update()
    {
        if (warningTimer > 0)
        {
            warningTimer -= Time.deltaTime;
            if (warningTimer <= 0 && warningText != null) warningText.text = "";
        }
        if (playerEffects == null) return;

        float boost = playerEffects.GetBoostTime();
        float slow = playerEffects.GetSlowdownTime();

        // One line per active effect with a draining bar, then the combined speed.
        string status = "";
        if (boost > 0f)
            status += "<color=#FFD94A>SPEED BOOST x" + playerEffects.BoostMultiplier.ToString("0.#") + "  "
                   + Bar(boost, playerEffects.BoostDuration) + " " + boost.ToString("0.0") + "s</color>\n";
        if (slow > 0f)
            status += "<color=#FF5A5A>SLOWED x" + playerEffects.SlowdownMultiplier.ToString("0.##") + "  "
                   + Bar(slow, playerEffects.SlowdownDuration) + " " + slow.ToString("0.0") + "s</color>\n";
        if (boost > 0f || slow > 0f)
            status += "SPEED x" + playerEffects.GetTotalMultiplier().ToString("0.##");
        if (speedText != null) speedText.text = status;

        if (teleportText != null) teleportText.text = playerEffects.HasTeleportCharge() ? "E: TELEPORT READY" : "";
    }

    private static string Bar(float remaining, float full)
    {
        const int cells = 10;
        int filled = Mathf.Clamp(Mathf.CeilToInt(cells * remaining / Mathf.Max(0.01f, full)), 0, cells);
        return "[" + new string('|', filled) + new string('.', cells - filled) + "]";
    }

    public void ShowGameOver()
    {
        if (gameOverText != null)
        {
            gameOverText.text = "GAME OVER - Press R to Restart";
            gameOverText.gameObject.SetActive(true);
        }
    }

    public void ShowWarning(string msg)
    {
        if (warningText != null)
        {
            warningText.text = msg;
            warningTimer = 2f;
        }
    }
}
