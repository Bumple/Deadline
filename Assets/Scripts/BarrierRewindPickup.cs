using UnityEngine;

public class BarrierRewindPickup : PowerUp
{
    protected override bool OnCollect(EffectProcessor ep)
    {
        return ep.RewindBarrier();
    }
}
