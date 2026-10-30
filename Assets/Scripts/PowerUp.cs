using UnityEngine;

public abstract class PowerUp : MonoBehaviour
{
    private bool collected = false;

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (collected) return;
        if (other.attachedRigidbody != null && other.attachedRigidbody.TryGetComponent<EffectProcessor>(out var ep))
        {
            if (OnCollect(ep))
            {
                collected = true;
                Destroy(gameObject);
            }
        }
    }

    protected abstract bool OnCollect(EffectProcessor ep);
}

public class SpeedBoostPickup : PowerUp
{
    protected override bool OnCollect(EffectProcessor ep)
    {
        ep.ApplySpeedBoost(4f);
        return true;
    }
}

public class TeleportPickup : PowerUp
{
    protected override bool OnCollect(EffectProcessor ep)
    {
        if (ep.HasTeleportCharge()) return false;
        ep.AddTeleportCharge();
        return true;
    }
}
