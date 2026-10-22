using System;
using UnityEngine;
using UnityEngine.Events;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// ITEM PICKUP (v1.1) — the E-key pickup component for world items.
///
/// WHAT IT DOES
///   The player walks within the PICKUP RADIUS of this object and presses the
///   interaction key (E by default — the SAME universal interaction key the
///   InteractionPoints use). The item is then picked up:
///     • the local 'On Picked Up' UnityEvent fires first (sounds, effects…),
///     • the static ItemPickup.AnyItemPickedUp event fires next — the Totem
///       Collection HUD (and anything else you build later) listens to that;
///       no script ever references another script directly,
///     • the GameObject is DESTROYED properly — the item can never be picked
///       up again (no infinite pickups, no leftover colliders or meshes).
///
/// UNIVERSAL E KEY — one press, one action, THE ITEM WINS (v1.1):
///   • One key press picks ONE item: the frame a pickup consumes the press,
///     every other item in range ignores that same press.
///   • When the player is inside this item's pickup radius AND stands in a
///     'Press Key' InteractionPoint at the same time, the ITEM gets the press
///     — you walked right up to the item, so that is what E should do. The
///     point simply waits for the next press (it skips this one via
///     KeyCodeBridge's key claim; InteractionPoint v3.2 or later checks it).
///   • This component runs EARLY in the frame ([DefaultExecutionOrder(-50)])
///     so this arbitration is reliable no matter in which order the
///     components were added.
///   • v1.1 removed the old 'Yield To Interaction Points' option — it let a
///     Permanent point (e.g. a recharging battery point) sitting under an
///     item eat every E press forever, which made pickups impossible.
///
/// NO INVENTORY, NO COUPLING:
///   ItemPickup knows nothing about any HUD — it only announces pickups.
///   A game without any UI listening still works perfectly. Since v1.1 it
///   also no longer references InteractionPoint at all — it is fully
///   standalone (KeyCodeBridge.cs v2.0+ must stay in the project).
///
/// SETUP
///   1. Put this component on the item's ROOT GameObject (e.g. a Totem).
///   2. Tag your player root 'Player' (or drag it into 'Player Transform').
///   3. Optional: add the separate ProximityText component and assign your
///      FloatText object for the "Press [E] to pick up" floating text.
///
/// Works with BOTH input backends (legacy Input Manager and the new Input
/// System — keep KeyCodeBridge.cs v2.0+ in the project for the latter) and
/// freezes while GamePauseManager's pause menu is open.
///
/// v1.0 — initial release.
/// v1.1 — the ITEM now wins the E press (was: yielded to Press-Key points,
///        which could starve pickups forever). Distance is measured
///        horizontally (XZ) so the radius matches the Scene-view gizmo disc
///        and works for items on pedestals/above the player. Pickups are
///        logged to the Console (visible feedback), out-of-range presses
///        only with Debug Logs on. Radius gizmo added.
/// v1.2 — VR touch pickup (NotDEER VR): new public PickUpFromTouch() lets
///        the VR hands (VRTouchPickup.cs) pick items up by TOUCHING them.
///        It runs the exact same Pickup() path the E key uses — same
///        events, same destroy, same one-pickup-per-frame guard — so a
///        keyboard E press and a hand touch can never double-pick. The
///        E-key behaviour itself is unchanged.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-50)]
public class ItemPickup : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Item")]

    [Tooltip("Which counter this item belongs to. Only a HUD tracking the SAME Id counts this pickup (TotemCollectionHUD tracks 'Totem' by default). Use other Ids for future item types.")]
    [SerializeField] private string itemId = "Totem";

    [Tooltip("How close the player has to be before E can pick this item up (meters, measured HORIZONTALLY from the player's tagged object to this object — height differences do not count).")]
    [SerializeField, Min(0.1f)] private float pickupRadius = 2.5f;

    [Header("Interaction Key (universal)")]

    [Tooltip("Key that picks this item up. Keep it the SAME as the InteractionPoints' key so E stays the game's universal interaction key. Works on BOTH input backends — keep KeyCodeBridge.cs in the project when using the new Input System.")]
    [SerializeField] private KeyCode interactKey = KeyCode.E;

    [Header("Player")]

    [Tooltip("The player the pickup radius is measured to. Auto-found by tag when left empty.")]
    [SerializeField] private Transform playerTransform;

    [Tooltip("Tag used to find the player automatically. Tag the player's ROOT 'Player' (Tags & Layers).")]
    [SerializeField] private string playerTag = "Player";

    [Header("Behaviour")]

    [Tooltip("Freeze pickups while GamePauseManager's pause menu is open.")]
    [SerializeField] private bool respectPause = true;

    [Header("Events")]

    [Tooltip("Fired the moment this item is picked up, right before it is destroyed. Wire sounds, particle effects, stats…")]
    [SerializeField] private UnityEvent onPickedUp = new UnityEvent();

    [Tooltip("Write extra diagnostics to the Console (e.g. an E press while out of range, with the distance).")]
    [SerializeField] private bool debugLogs = false;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private bool pickedUp;
    private float searchTimer;
    private bool warnedNoPlayer;

    /// <summary>
    /// Raised for EVERY item picked up anywhere in the scene (any ItemId).
    /// UI and progression scripts subscribe to this — the pickup system
    /// itself never references them. Unsubscribe in OnDisable/OnDestroy.
    /// </summary>
    public static event Action<ItemPickup> AnyItemPickedUp;

    /// <summary>
    /// The frame in which an item last consumed the key press. One press
    /// of the universal key can only ever pick ONE item.
    /// </summary>
    private static int lastPickupFrame = -1;

    // ------------------------------------------------------------------ //
    // Public API
    // ------------------------------------------------------------------ //

    /// <summary>True once this item has been picked up (it is being destroyed).</summary>
    public bool WasPickedUp
    {
        get { return pickedUp; }
    }

    /// <summary>True while the player is within the pickup radius (updated every frame).</summary>
    public bool IsPlayerInRange { get; private set; }

    /// <summary>The item's Id ('Totem' by default).</summary>
    public string ItemId
    {
        get { return itemId; }
    }

    /// <summary>The pickup radius in meters — change it live from code or the Inspector.</summary>
    public float PickupRadius
    {
        get { return pickupRadius; }
        set { pickupRadius = Mathf.Max(0.1f, value); }
    }

    /// <summary>The player this item measures the radius to (null while not found yet).</summary>
    public Transform PlayerTransform
    {
        get { return playerTransform; }
    }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Start()
    {
        TryFindPlayer();   // immediate first search, then every 0.5 s in Update
    }

    private void Update()
    {
        if (pickedUp) return;
        if (respectPause && GamePauseManager.IsPaused) return;

        // read the key ONCE — even out of range, so diagnostics can explain
        // why a press did nothing (Debug Logs only, never spam by default)
        bool keyPressed = ReadInteractKeyPressed();

        if (!ResolvePlayer())
        {
            IsPlayerInRange = false;
            return;
        }

        // HORIZONTAL distance (XZ): height differences never block a pickup
        // and the radius matches the Scene-view gizmo disc exactly
        Vector3 playerPos = playerTransform.position;
        Vector3 itemPos = transform.position;
        float dx = playerPos.x - itemPos.x;
        float dz = playerPos.z - itemPos.z;
        float distance = Mathf.Sqrt(dx * dx + dz * dz);

        IsPlayerInRange = distance <= pickupRadius;
        if (!IsPlayerInRange)
        {
            if (debugLogs && keyPressed)
                Debug.Log("[ItemPickup] '" + name + "': " + interactKey + " pressed but the player is " +
                          distance.ToString("F1") + "m away (pickup radius " + pickupRadius.ToString("F1") + "m).", this);
            return;
        }

        if (!keyPressed) return;

        // one press, one item — another item already took this frame's press
        if (Time.frameCount == lastPickupFrame) return;

        Pickup();
    }

    /// <summary>Clamps Inspector values so the pickup can never receive nonsense input.</summary>
    private void OnValidate()
    {
        pickupRadius = Mathf.Max(0.1f, pickupRadius);
        if (string.IsNullOrEmpty(itemId)) itemId = "Totem";
        if (string.IsNullOrEmpty(playerTag)) playerTag = "Player";
    }

    /// <summary>Scene-view gizmo: the pickup radius as a yellow disc on the ground plane through the item.</summary>
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.9f, 0.2f, 0.9f);
        DrawWireDisc(transform.position, Mathf.Max(0.1f, pickupRadius), 40);
    }

    private static void DrawWireDisc(Vector3 center, float radius, int segments)
    {
        Vector3 prev = center + new Vector3(radius, 0f, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float angle = (i / (float)segments) * Mathf.PI * 2f;
            Vector3 next = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
    }

    /// <summary>Play-mode entry reset — correct even with Domain Reload disabled.</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        lastPickupFrame = -1;
    }

    // ------------------------------------------------------------------ //
    // Pickup
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Picks this item up by TOUCH (v1.2 — called by VRTouchPickup when a VR
    /// hand's touch sphere overlaps the item's collider). Runs the exact
    /// same Pickup() path the E key uses; returns false — without side
    /// effects — when this item was already picked up, the game is paused
    /// (respectPause), or another pickup already claimed this frame (the
    /// other hand, or a keyboard E press). One touch = one item, ever.
    /// </summary>
    public bool PickUpFromTouch()
    {
        if (pickedUp) return false;
        if (respectPause && GamePauseManager.IsPaused) return false;
        if (Time.frameCount == lastPickupFrame) return false;

        Pickup();
        return true;
    }

    private void Pickup()
    {
        pickedUp = true;
        lastPickupFrame = Time.frameCount;

        // claim the universal key for this frame BEFORE anything else runs:
        // the InteractionPoints (and future E-key systems) will skip this
        // press — one press, one action, and the item that was walked up to
        // is the one that wins it
        KeyCodeBridge.ConsumeKey(interactKey);

        // 1) local hooks
        if (onPickedUp != null) onPickedUp.Invoke();

        // 2) global announcement (the HUD and future systems listen here)
        Debug.Log("[ItemPickup] '" + name + "' (" + ItemId + ") picked up by '" +
                  (playerTransform != null ? playerTransform.name : "player") + "'.", this);

        if (AnyItemPickedUp != null) AnyItemPickedUp(this);

        // 3) gone for good — the item can never be picked up again
        Destroy(gameObject);
    }

    // ------------------------------------------------------------------ //
    // Player finding (retry quietly until the player exists)
    // ------------------------------------------------------------------ //

    private bool ResolvePlayer()
    {
        if (playerTransform != null) return true;   // also survives destroy: Unity fake-null

        searchTimer += Time.unscaledDeltaTime;
        if (searchTimer < 0.5f) return false;       // retry every 0.5 seconds only
        searchTimer = 0f;

        TryFindPlayer();
        return playerTransform != null;
    }

    private void TryFindPlayer()
    {
        if (playerTransform != null) return;

        GameObject player = FindPlayerByTag();
        if (player != null)
        {
            playerTransform = player.transform;
            return;
        }

        if (!warnedNoPlayer)
        {
            warnedNoPlayer = true;
            Debug.LogWarning("[ItemPickup] '" + name + "': no GameObject tagged '" + playerTag +
                             "' found yet. Tag the player's ROOT '" + playerTag +
                             "' (or drag it into 'Player Transform'). Retrying every 0.5 s.", this);
        }
    }

    /// <summary>Tag lookup that survives a tag that does not exist in the project yet.</summary>
    private GameObject FindPlayerByTag()
    {
        if (string.IsNullOrEmpty(playerTag)) return null;

        try
        {
            return GameObject.FindGameObjectWithTag(playerTag);
        }
        catch
        {
            return null;   // the tag is not defined — the warning above already said it
        }
    }

    // ------------------------------------------------------------------ //
    // Input (supports both the legacy Input Manager and the new Input System)
    // ------------------------------------------------------------------ //

    private bool ReadInteractKeyPressed()
    {
#if ENABLE_INPUT_SYSTEM
        // KeyCodeBridge maps the Inspector KeyCode to the new Input System key,
        // so the key field genuinely works on both input backends.
        return KeyCodeBridge.WasKeyPressedThisFrame(Keyboard.current, interactKey);
#else
        return Input.GetKeyDown(interactKey);
#endif
    }
}
