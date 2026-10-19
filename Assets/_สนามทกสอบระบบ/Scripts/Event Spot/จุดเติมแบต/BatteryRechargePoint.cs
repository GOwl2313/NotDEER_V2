using UnityEngine;

/// <summary>
/// READY-MADE BEHAVIOUR for the InteractionPoint system — the battery
/// recharging point this system originally grew out of.
///
/// Attach it to the SAME GameObject as an InteractionPoint (children work
/// too — the point finds listeners automatically). The glowing ring does the
/// visual work; this listener does the recharging when the player activates
/// the point:
///
///   • SingleUse    + this listener = a one-shot battery pickup
///                   (the ring disappears after picking it up).
///   • Respawnable  + this listener = a battery station that comes back
///                   after the point's "Respawn Time" seconds.
///   • Permanent    + this listener = an infinite recharging pad
///                   (repeat delay prevents spamming).
///
/// HOW IT FINDS THE FLASHLIGHT: the InteractionPoint passes the player's
/// root GameObject; the flashlight (FlashlightController, v2.0+) is searched
/// in the player's hierarchy — it is usually attached to the player's light
/// or camera child, not the root.
///
/// CONSUMPTION RULES (it never wastes the point):
///   • no flashlight found            → interaction not consumed, point stays;
///   • flashlight has no battery use  → interaction not consumed, point stays;
///   • battery already full AND "Keep When Battery Full" is on
///                                    → interaction not consumed, point stays;
///   • otherwise the battery is recharged (sound optional) and the point
///     follows its Usage Mode.
///
/// Requires FlashlightController.cs (v2.0 or newer) in the project.
///
/// v1.0 — initial release (InteractionPoint system).
/// </summary>
[DisallowMultipleComponent]
public class BatteryRechargePoint : InteractionPointListener
{
    public enum ChargeMode
    {
        [InspectorName("Add Charge (fraction of full battery)")] AddCharge,
        [InspectorName("Full Charge (battery to 100%)")] FullCharge
    }

    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Recharge")]

    [Tooltip("AddCharge adds 'Charge Amount' to the battery. FullCharge fills it completely.")]
    [SerializeField] private ChargeMode chargeMode = ChargeMode.AddCharge;

    [Tooltip("How much battery one use gives, as a fraction of the full battery (0.5 = half a battery). Only used by 'Add Charge'.")]
    [SerializeField, Range(0.05f, 1f)] private float chargeAmount = 0.5f;

    [Tooltip("If the battery is already full, the point is NOT used up — the player can save it for later.")]
    [SerializeField] private bool keepWhenBatteryFull = true;

    [Header("Feedback")]

    [Tooltip("Optional sound played when the battery is recharged (one-shot, plays in 3D at the point's position — no AudioSource setup needed).")]
    [SerializeField] private AudioClip rechargeSound;

    [Tooltip("Volume of the recharge sound (0-1).")]
    [SerializeField, Range(0f, 1f)] private float soundVolume = 0.8f;

    [Header("Flashlight Lookup")]

    [Tooltip("Search the whole player hierarchy for the FlashlightController (the flashlight is usually on the player's body or camera child). OFF = only the player's root object.")]
    [SerializeField] private bool searchWholeHierarchy = true;

    [Tooltip("Write debug messages to the Console.")]
    [SerializeField] private bool debugLogs = false;

    // warn-once guards so a missing flashlight cannot spam the Console
    private bool warnedNoFlashlight;
    private bool warnedNoBattery;

    // ------------------------------------------------------------------ //
    // InteractionPointListener
    // ------------------------------------------------------------------ //

    public override bool OnPointActivated(InteractionPoint point, GameObject player)
    {
        FlashlightController flashlight = FindFlashlight(player);

        if (flashlight == null)
        {
            if (!warnedNoFlashlight)
            {
                warnedNoFlashlight = true;
                Debug.LogWarning("[BatteryRechargePoint] No FlashlightController found on '" +
                                 (player != null ? player.name : "<null>") + "'. The point is not used up.", this);
            }
            return false;   // do not consume — nothing to recharge
        }

        if (!flashlight.UsesBattery)
        {
            if (!warnedNoBattery)
            {
                warnedNoBattery = true;
                Debug.LogWarning("[BatteryRechargePoint] The flashlight on '" + player.name +
                                 "' does not use the battery system (Use Battery is off). The point is not used up.", this);
            }
            return false;
        }

        if (keepWhenBatteryFull && flashlight.BatteryLevel01 >= 0.999f)
        {
            if (debugLogs) Debug.Log("[BatteryRechargePoint] Battery already full — the point stays for later.", this);
            return false;   // do not waste the pickup
        }

        if (chargeMode == ChargeMode.FullCharge)
        {
            flashlight.RechargeBatteryFull();
        }
        else
        {
            flashlight.RechargeBattery(chargeAmount);
        }

        if (rechargeSound != null)
        {
            AudioSource.PlayClipAtPoint(rechargeSound, point.transform.position, soundVolume);
        }

        if (debugLogs)
        {
            Debug.Log("[BatteryRechargePoint] Player '" + player.name + "' recharged the flashlight to " +
                      (flashlight.BatteryLevel01 * 100f).ToString("F0") + "% (mode: " + chargeMode + ").", this);
        }

        return true;   // consumed — the point follows its Usage Mode now
    }

    // ------------------------------------------------------------------ //
    // Internals
    // ------------------------------------------------------------------ //

    private FlashlightController FindFlashlight(GameObject player)
    {
        if (player == null) return null;

        if (searchWholeHierarchy) return player.GetComponentInChildren<FlashlightController>();
        return player.GetComponent<FlashlightController>();
    }
}