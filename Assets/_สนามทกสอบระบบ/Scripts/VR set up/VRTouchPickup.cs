using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// VR TOUCH PICKUP (v1.0) — pick items up by TOUCHING them with either hand.
///
/// Attach this component to BOTH hand anchors (VRPlayerSetup does that for
/// you). It gives the hand a small invisible touch sphere and, whenever that
/// sphere overlaps an item's collider, picks the item up through the REAL
/// ItemPickup path:
///
///   • the item's own 'On Picked Up' UnityEvent fires,
///   • the static AnyItemPickedUp event fires (the Totem HUD counts it),
///   • the item GameObject is properly destroyed — no double pickups, no
///     leftover colliders, everything exactly like the E-key pickup.
///
/// That is the whole difference from the flat game: instead of standing in
/// the pickup radius and pressing E, in VR you simply reach out and touch
/// the totem with either hand.
///
/// HOW IT PICKS UP (ItemPickup v1.2)
///   VRTouchPickup calls item.PickUpFromTouch() — a tiny public method added
///   to ItemPickup v1.2 that runs the exact same Pickup() the E key uses,
///   guarded by the same flags (already-picked-up, pause, one-per-frame), so
///   two hands touching the same totem on the same frame still pick it up
///   exactly once, and nothing can be picked up while the game is paused or
///   during the jumpscare.
///
/// COLLIDER NOTES
///   • the touch sphere is a TRIGGER with a kinematic Rigidbody — it never
///     physically pushes anything, never blocks the flashlight (trigger
///     colliders are ignored by the detector's line-of-sight rays), and
///     never trips InteractionPoints (those listen for the "Player"-tagged
///     root body entering them — the hands are untagged children);
///   • colliders without any ItemPickup in their parent chain are cached as
///     "not an item" after the first check, so holding the hand in the
///     terrain/props costs nothing.
///
/// v1.0 — initial release with the NotDEER VR layer.
/// </summary>
[DisallowMultipleComponent]
public class VRTouchPickup : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Touch Sphere (auto-created on this hand)")]

    [Tooltip("Radius of the invisible touch sphere at the hand's center (meters).")]
    [SerializeField, Range(0.02f, 0.3f)] private float touchRadius = 0.09f;

    [Tooltip("Create the trigger sphere + kinematic Rigidbody automatically when the hand has none. Turn off only if you added your own collider.")]
    [SerializeField] private bool autoCreateTouchCollider = true;

    [Header("Behaviour")]

    [Tooltip("No pickups while GamePauseManager's menu is open.")]
    [SerializeField] private bool respectPause = true;

    [Tooltip("No pickups while the jumpscare is playing.")]
    [SerializeField] private bool respectJumpscare = true;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private static readonly Dictionary<Collider, ItemPickup> s_ItemLookupCache = new Dictionary<Collider, ItemPickup>();

    /// <summary>Play-mode entry reset — correct even with Domain Reload disabled.</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        s_ItemLookupCache.Clear();
    }

    /// <summary>Which item this hand last picked up (diagnostics/testing).</summary>
    public ItemPickup LastPickedUp { get; private set; }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        if (!autoCreateTouchCollider) return;

        // A kinematic Rigidbody makes Unity fire trigger messages between
        // the moving hand and static item colliders.
        if (GetComponent<Rigidbody>() == null)
        {
            Rigidbody body = gameObject.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
        }

        if (GetComponent<SphereCollider>() == null)
        {
            SphereCollider touch = gameObject.AddComponent<SphereCollider>();
            touch.isTrigger = true;
            touch.radius = touchRadius;
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        TryPickUp(other);
    }

    // Stay covers the edge case of the hand already resting INSIDE the item's
    // collider when the item spawns/enables right on top of it.
    private void OnTriggerStay(Collider other)
    {
        TryPickUp(other);
    }

    /// <summary>Clamps Inspector values so a touch can never receive nonsense input.</summary>
    private void OnValidate()
    {
        touchRadius = Mathf.Max(0.02f, touchRadius);
    }

    // ------------------------------------------------------------------ //
    // Pickup
    // ------------------------------------------------------------------ //

    private void TryPickUp(Collider other)
    {
        if (respectPause && GamePauseManager.IsPaused) return;
        if (respectJumpscare && JumpscareGameOverManager.IsJumpscareActive) return;
        if (other == null) return;

        ItemPickup item = LookupItem(other);
        if (item == null || item.WasPickedUp) return;

        // The one true pickup path — same guards, same events, same destroy
        // as the E key (ItemPickup v1.2). False = someone got there first
        // this frame (the other hand, or a keyboard E press).
        if (item.PickUpFromTouch())
        {
            LastPickedUp = item;
        }
    }

    /// <summary>
    /// Collider → ItemPickup with a negative cache: colliders that proved
    /// not to belong to any item are remembered, so resting a hand in the
    /// terrain never re-searches the hierarchy every physics frame.
    /// </summary>
    private static ItemPickup LookupItem(Collider other)
    {
        if (s_ItemLookupCache.TryGetValue(other, out ItemPickup cached))
        {
            // Destroyed items turn into Unity fake-nulls — treat them as gone.
            return cached != null ? cached : null;
        }

        ItemPickup item = other.GetComponentInParent<ItemPickup>();
        s_ItemLookupCache[other] = item;   // null result cached too
        return item;
    }
}
