using UnityEngine;

/// <summary>
/// CODE EXTENSION BASE CLASS for the InteractionPoint system.
///
/// InteractionPoint is a UNIVERSAL base: by itself it only shows the glowing
/// ring and manages its own lifecycle (single use / respawn / permanent).
/// The actual behaviour comes from ONE of three plug-in points — pick
/// whichever fits:
///
///   1. NO CODE  — use the UnityEvents on the InteractionPoint component
///                 (On Interact / On Depleted / On Respawned).
///
///   2. THIS CLASS — write a script that extends InteractionPointListener,
///                 override OnPointActivated, and attach it to the SAME
///                 GameObject as the InteractionPoint (children also work).
///                 The point finds it automatically — zero wiring.
///
///   3. ANY SCRIPT — call point.TryActivate(player) / Deplete() / Respawn()
///                 directly from your own code (public API).
///
/// HOW OnPointActivated CONSUMPTION WORKS:
///   • Return TRUE  when your behaviour "used" the interaction. The point
///     then follows its Usage Mode (SingleUse disappears, Respawnable starts
///     its timer, Permanent starts its repeat delay).
///   • Return FALSE to keep the point ready and untouched — e.g. a battery
///     recharge point whose battery is already full (see
///     BatteryRechargePoint.cs for a full example).
///   • With several listeners attached, ANY listener returning true is
///     enough to consume the interaction.
///
/// v1.0 — initial release (InteractionPoint system).
/// </summary>
public abstract class InteractionPointListener : MonoBehaviour
{
    /// <summary>
    /// Called when a player activates the point. Return true to consume the
    /// interaction (the point then applies its Usage Mode), return false to
    /// keep the point ready.
    /// </summary>
    /// <param name="point">The interaction point that was activated.</param>
    /// <param name="player">The player's root GameObject (the one tagged "Player").</param>
    public virtual bool OnPointActivated(InteractionPoint point, GameObject player)
    {
        return true;
    }

    /// <summary>Called when the point is used up (SingleUse) or starts a respawn wait (Respawnable).</summary>
    /// <param name="point">The interaction point that was depleted.</param>
    public virtual void OnPointDepleted(InteractionPoint point)
    {
    }

    /// <summary>Called when a Respawnable point comes back after its timer.</summary>
    /// <param name="point">The interaction point that respawned.</param>
    public virtual void OnPointRespawned(InteractionPoint point)
    {
    }
}