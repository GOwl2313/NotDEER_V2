
using System;
using UnityEngine;
using UnityEngine.Events;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[DisallowMultipleComponent]
[DefaultExecutionOrder(-50)]
public class ItemPickup : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Item")]

    [Tooltip("Item ID tracked by the HUD.")]
    [SerializeField] private string itemId = "Totem";

    [Tooltip("Horizontal pickup radius in meters.")]
    [SerializeField, Min(0.1f)] private float pickupRadius = 2.5f;

    [Header("Interaction Key")]

    [Tooltip("Universal interaction key.")]
    [SerializeField] private KeyCode interactKey = KeyCode.E;

    [Header("Player")]

    [Tooltip("Player root. Auto-found when empty: prefers the XR Origin (XR Rig) in VR scenes, then the tag below.")]
    [SerializeField] private Transform playerTransform;

    [Tooltip("Tag used to find the player (fallback when no XR Origin exists).")]
    [SerializeField] private string playerTag = "Player";

    [Header("Pickup Animation")]

    [Tooltip("Controller on the player that plays the temporary pickup animation.")]
    [SerializeField] private PickupAnimationController pickupAnimationController;

    [Tooltip("Delay before the item is collected, in seconds. Set to 0 to collect immediately.")]
    [SerializeField, Min(0f)] private float pickupAnimationDelay = 0.5f;

    [Header("Behaviour")]

    [SerializeField] private bool respectPause = true;

    [Header("Events")]

    [Tooltip("Fires when the item is collected.")]
    [SerializeField] private UnityEvent onPickedUp = new UnityEvent();

    [SerializeField] private bool debugLogs = false;

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private bool pickedUp;
    private bool pickupPending;
    private float searchTimer;
    private bool warnedNoPlayer;
    private float pickupTimer;

    public static event Action<ItemPickup> AnyItemPickedUp;

    private static int lastPickupFrame = -1;

    public bool WasPickedUp => pickedUp;
    public bool IsPlayerInRange { get; private set; }
    public string ItemId => itemId;
    public float PickupRadius
    {
        get => pickupRadius;
        set => pickupRadius = Mathf.Max(0.1f, value);
    }
    public Transform PlayerTransform => playerTransform;

    // ------------------------------------------------------------------ //
    // Unity
    // ------------------------------------------------------------------ //

    private void Start()
    {
        TryFindPlayer();
        TryFindPickupAnimationController();
    }

    private void Update()
    {
        if (pickedUp) return;

        if (respectPause && GamePauseManager.IsPaused)
            return;

        // Wait for the pickup animation before collecting the item.
        if (pickupPending)
        {
            pickupTimer += Time.deltaTime;

            if (pickupTimer >= pickupAnimationDelay)
            {
                CompletePickup();
            }

            return;
        }

        bool keyPressed = ReadInteractKeyPressed();

        if (!ResolvePlayer())
        {
            IsPlayerInRange = false;
            return;
        }

        // v1.1 VR: measure to the HEADSET when the player is an XR Origin, so
        // room-scale walking counts even when the rig's floor point lags behind.
        Vector3 playerPos = PlayerHeadResolver.GetPosition(playerTransform);
        Vector3 itemPos = transform.position;

        float dx = playerPos.x - itemPos.x;
        float dz = playerPos.z - itemPos.z;
        float distance = Mathf.Sqrt(dx * dx + dz * dz);

        IsPlayerInRange = distance <= pickupRadius;

        if (!IsPlayerInRange)
        {
            if (debugLogs && keyPressed)
            {
                Debug.Log(
                    "[ItemPickup] Player is " +
                    distance.ToString("F1") +
                    "m away. Pickup radius: " +
                    pickupRadius.ToString("F1") + "m.",
                    this
                );
            }

            return;
        }

        if (!keyPressed) return;

        // One E press can collect only one item.
        if (Time.frameCount == lastPickupFrame) return;

        BeginPickup();
    }

    private void OnValidate()
    {
        pickupRadius = Mathf.Max(0.1f, pickupRadius);
        pickupAnimationDelay = Mathf.Max(0f, pickupAnimationDelay);

        if (string.IsNullOrEmpty(itemId))
            itemId = "Totem";

        if (string.IsNullOrEmpty(playerTag))
            playerTag = "Player";
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.9f, 0.2f, 0.9f);

        DrawWireDisc(
            transform.position,
            Mathf.Max(0.1f, pickupRadius),
            40
        );
    }

    private static void DrawWireDisc(
        Vector3 center,
        float radius,
        int segments)
    {
        Vector3 prev = center + new Vector3(radius, 0f, 0f);

        for (int i = 1; i <= segments; i++)
        {
            float angle = i / (float)segments * Mathf.PI * 2f;

            Vector3 next = center + new Vector3(
                Mathf.Cos(angle) * radius,
                0f,
                Mathf.Sin(angle) * radius
            );

            Gizmos.DrawLine(prev, next);
            prev = next;
        }
    }

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        lastPickupFrame = -1;
    }

    // ------------------------------------------------------------------ //
    // Pickup animation and collection
    // ------------------------------------------------------------------ //

    private void BeginPickup()
    {
        // Claim E immediately so other interactions cannot use this press.
        lastPickupFrame = Time.frameCount;
        KeyCodeBridge.ConsumeKey(interactKey);

        // Prevent this item from being picked up again.
        pickupPending = true;
        pickupTimer = 0f;

        if (pickupAnimationController == null)
            TryFindPickupAnimationController();

        // Play the temporary animation.
        if (pickupAnimationController != null)
        {
            pickupAnimationController.PlayPickupAnimation();
        }
        else if (debugLogs)
        {
            Debug.LogWarning(
                "[ItemPickup] PickupAnimationController not assigned.",
                this
            );
        }

        // Zero delay preserves immediate collection.
        if (pickupAnimationDelay <= 0f)
        {
            CompletePickup();
        }
    }

    private void CompletePickup()
    {
        if (pickedUp) return;

        pickedUp = true;
        pickupPending = false;

        // 1. Local events.
        onPickedUp?.Invoke();

        // 2. Global announcement for HUD and other listeners.
        Debug.Log(
            "[ItemPickup] '" + name +
            "' (" + ItemId +
            ") picked up by '" +
            (playerTransform != null
                ? playerTransform.name
                : "player") + "'.",
            this
        );

        AnyItemPickedUp?.Invoke(this);

        // 3. Destroy the collected item.
        Destroy(gameObject);
    }

    private void TryFindPickupAnimationController()
    {
        if (pickupAnimationController != null) return;

        if (playerTransform != null)
        {
            pickupAnimationController =
                playerTransform.GetComponentInChildren<
                    PickupAnimationController>(true);
        }
    }

    // ------------------------------------------------------------------ //
    // Player finding
    // ------------------------------------------------------------------ //

    private bool ResolvePlayer()
    {
        if (playerTransform != null) return true;

        searchTimer += Time.unscaledDeltaTime;

        if (searchTimer < 0.5f) return false;

        searchTimer = 0f;
        TryFindPlayer();

        return playerTransform != null;
    }

    private void TryFindPlayer()
    {
        if (playerTransform != null) return;

        // v1.1 VR: XR Origin (XR Rig) first, then the configured player tag.
        // The ROOT is resolved on purpose: the pickup animation controller is
        // searched in its children, which the headset camera is not part of.
        Transform resolved = PlayerHeadResolver.ResolveRoot(null, playerTag);
        if (resolved != null)
        {
            playerTransform = resolved;
            TryFindPickupAnimationController();
            return;
        }

        if (!warnedNoPlayer)
        {
            warnedNoPlayer = true;

            Debug.LogWarning(
                "[ItemPickup] No XR Origin or GameObject tagged '" +
                playerTag +
                "' found. Assign Player Transform or tag the player root.",
                this
            );
        }
    }

    // ------------------------------------------------------------------ //
    // Input
    // ------------------------------------------------------------------ //

    private bool ReadInteractKeyPressed()
    {
#if ENABLE_INPUT_SYSTEM
        // VR: the controller Trigger counts as the same press (VRInputBridge).
        return VRInputBridge.InteractPressedThisFrame ||
               KeyCodeBridge.WasKeyPressedThisFrame(
                   Keyboard.current,
                   interactKey
               );
#else
        return Input.GetKeyDown(interactKey);
#endif
    }
}
