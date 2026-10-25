using UnityEngine;

/// <summary>
/// SHARED VR-AWARE PLAYER RESOLUTION (Unity 6 + XR Interaction Toolkit 3.x).
/// 
/// One small static helper every gameplay script can use to answer "where is
/// the player, actually?". It replaces the old pattern
/// 
///     GameObject.FindGameObjectWithTag("Player")
/// 
/// which, in VR scenes, resolves to the XR Origin's ROOT — the rig's FLOOR
/// point — even though room-scale walking can leave the real player (the
/// headset inside the rig) several meters away from that point.
/// </summary>
public static class PlayerHeadResolver
{
    // ------------------------------------------------------------------ //
    // Root resolution
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Resolves the player ROOT object:
    /// explicit reference → XR Origin (XR Rig) → 'fallbackTag' tag.
    /// Returns null only when nothing exists yet (callers re-resolve later).
    /// </summary>
    public static Transform ResolveRoot(Transform explicitRef, string fallbackTag = "Player")
    {
        if (explicitRef != null) return explicitRef;

        Unity.XR.CoreUtils.XROrigin xrOrigin = FindXROrigin();
        if (xrOrigin != null) return xrOrigin.transform;

        if (!string.IsNullOrEmpty(fallbackTag))
        {
            GameObject tagged = GameObject.FindGameObjectWithTag(fallbackTag);
            if (tagged != null) return tagged.transform;
        }

        return null;
    }

    /// <summary>
    /// Version-aware XR Origin lookup. Unity 2023+ (including Unity 6) uses
    /// the current FindAnyObjectByType; older Unity uses FindObjectOfType in
    /// a branch that never compiles there — no deprecation warning anywhere.
    /// </summary>
    public static Unity.XR.CoreUtils.XROrigin FindXROrigin()
    {
#if UNITY_2023_1_OR_NEWER
        return UnityEngine.Object.FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
#else
        return UnityEngine.Object.FindObjectOfType<Unity.XR.CoreUtils.XROrigin>();
#endif
    }

    // ------------------------------------------------------------------ //
    // Position resolution (head-aware)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// The world point gameplay should measure against:
    ///   • player is an XR Origin  → the headset's horizontal position at the
    ///     rig's floor height (room-scale correct);
    ///   • otherwise               → the transform's own position (unchanged
    ///     desktop behaviour).
    /// Y stays the rig's floor Y — horizontal distance math (dx/dz) is
    /// unaffected, and floor height is the natural reference for gameplay.
    /// </summary>
    public static Vector3 GetPosition(Transform player)
    {
        if (player == null) return Vector3.zero;

        Unity.XR.CoreUtils.XROrigin xrOrigin = player.GetComponent<Unity.XR.CoreUtils.XROrigin>();
        if (xrOrigin == null) return player.position;

        if (xrOrigin.Camera != null)
        {
            Vector3 head = xrOrigin.Camera.transform.position;
            return new Vector3(head.x, player.position.y, head.z);
        }

        return player.position;
    }
}