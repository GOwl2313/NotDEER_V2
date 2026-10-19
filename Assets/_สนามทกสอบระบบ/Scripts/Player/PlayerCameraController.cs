using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// RE4-style over-the-shoulder third-person camera. Attach to your Main Camera.
///
///   - Stays behind the player's back and orbits with the mouse (cursor),
///     so the view direction follows the cursor like Resident Evil 4.
///   - Shoulder Offset shifts the camera to the RIGHT of the player so the
///     character sits off-center to the LEFT of the screen, leaving the view
///     ahead clear.
///   - Spherecast collision keeps the camera from sinking into walls or
///     ending up behind objects: it pulls in instantly when blocked and
///     recovers smoothly afterwards. A minimum distance guarantees the
///     player always stays on screen.
///
/// Independent from PlayerMovementController (they only share Camera.main),
/// and can be replaced by a Cinemachine setup at any time – see the README
/// for the step-by-step Cinemachine guide.
///
/// v1.1 – the Cursor Toggle Key Inspector field now genuinely works on the
/// new Input System as well (bridged via KeyCodeBridge), fixing the CS0414
/// "assigned but its value is never used" warning that appeared in
/// new-Input-System projects.
///
/// v1.2 – Pause-menu integration: while the game is paused (GamePauseManager)
/// mouse look, the cursor toggle and click-to-relock are suspended, and the
/// pause key (Esc by default) is left to the pause menu — the camera never
/// double-handles it. Set 'Cursor Toggle Key' to a different key if you want
/// a separate free-cursor toggle next to the pause menu.
///
/// v1.3 – CURSOR KEEP-BACK: Unity silently drops the cursor lock whenever the
/// window (or, in the editor, the Game view) does not have focus — the exact
/// reason the cursor could sit VISIBLE on screen at the start of the game
/// until the first click. The camera now re-applies the wanted lock state
/// every frame and when the application regains focus (OnApplicationFocus),
/// so the cursor re-engages on its own the moment focus arrives — no click
/// required, and alt-tab no longer brings the cursor back for good. Never
/// fights the pause menu (skipped while paused) or the jumpscare / ending
/// systems (they disable this component while they own the cursor).
///
/// v1.4 – START-OF-GAME CURSOR, final piece. Two changes finish the fix:
///   1. The lock is now ALSO requested in Awake() — the earliest possible
///      moment, before ANY other script's Start could ever show the cursor
///      (Start repeats the request; it is idempotent).
///   2. The real remaining cause lived OUTSIDE game code: in the Unity EDITOR
///      every Cursor.lockState request is silently DROPPED while the Game
///      view has no keyboard focus — and pressing Play does NOT focus the
///      Game view (focus stays in the Scene view / Hierarchy). That is why
///      even v1.3's every-frame keep-back could not hide the cursor until
///      the player's first click gave the Game view focus. The NEW
///      GameViewAutoFocus.cs (editor-only, zero setup, compiles away in
///      builds) focuses the Game view the instant Play Mode starts, so the
///      very first request — now from Awake — engages: cursor hidden from
///      frame zero, no click needed. Keep that file next to this one.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
public class PlayerCameraController : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Target")]

    [Tooltip("The player the camera follows. Auto-found via the 'Player' tag if empty.")]
    [SerializeField] private Transform target;

    [Tooltip("Height above the player's pivot the camera orbits around (eye height).")]
    [SerializeField] private float pivotHeight = 1.6f;

    [Header("Framing (RE4 over-the-shoulder)")]

    [Tooltip("Distance behind the player.")]
    [SerializeField] private float cameraDistance = 3f;

    [Tooltip("Positive shifts the camera to the RIGHT so the player appears off-center LEFT " +
             "on screen (Resident Evil 4 style). Negative mirrors to the other shoulder.")]
    [SerializeField] private float shoulderOffset = 0.6f;

    [Tooltip("Closest the camera may be pulled in when colliding – keeps the player on screen.")]
    [SerializeField] private float minCameraDistance = 0.8f;

    [Header("Look Input")]

    [Tooltip("Base look sensitivity. Raise/lower to taste.")]
    [SerializeField] private float lookSensitivity = 150f;

    [Tooltip("Invert vertical look.")]
    [SerializeField] private bool invertY = false;

    [Tooltip("Maximum upward look angle in degrees.")]
    [SerializeField] private float maxUpwardAngle = 50f;

    [Tooltip("Maximum downward look angle in degrees.")]
    [SerializeField] private float maxDownwardAngle = 60f;

    [Header("Smoothing")]

    [Tooltip("Time for the camera to catch up to the player's position. 0 = rigid follow " +
             "(recommended when the player's Rigidbody uses interpolation).")]
    [SerializeField] private float followSmoothTime = 0.05f;

    [Tooltip("Time for the camera to extend back out after a collision (pulling in is always instant).")]
    [SerializeField] private float distanceRecoverTime = 0.25f;

    [Header("Collision")]

    [Tooltip("Keep the camera out of walls and objects.")]
    [SerializeField] private bool avoidObstacles = true;

    [Tooltip("Radius of the camera collision probe (approximates camera size).")]
    [SerializeField] private float collisionRadius = 0.2f;

    [Tooltip("Layers that block the camera. Exclude the Player layer if needed.")]
    [SerializeField] private LayerMask collisionMask = ~0;

    [Tooltip("Gap kept between the camera probe and obstacles.")]
    [SerializeField] private float collisionPadding = 0.15f;

    [Header("Cursor")]

    [Tooltip("Lock and hide the cursor while playing (mouse-look style).")]
    [SerializeField] private bool lockCursor = true;

    [Tooltip("Key that toggles the cursor free/locked. Works on both the legacy " +
             "Input Manager and the new Input System (keep KeyCodeBridge.cs in the " +
             "project for the new Input System).")]
    [SerializeField] private KeyCode toggleCursorKey = KeyCode.Escape;

    [Tooltip("Re-lock the cursor when clicking in the Game view after toggling it free.")]
    [SerializeField] private bool relockCursorOnClick = true;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private float yaw;
    private float pitch;
    private float currentOffsetLength;
    private float lengthSmoothVelocity;
    private Vector3 smoothedPivot;
    private Vector3 pivotSmoothVelocity;
    private Rigidbody targetRigidbody;
    private readonly RaycastHit[] hitBuffer = new RaycastHit[16];
    private bool cursorLocked;

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        // v1.4: request the lock at the EARLIEST possible moment — Awake runs
        // before every script's Start, so no other script's initialization can
        // leave the cursor visible for even a single frame in a build. Start()
        // repeats the request (idempotent) and the keep-back keeps it engaged.
        // If this component starts DISABLED (someone else owns the cursor),
        // Unity calls OnDisable right after Awake and releases it again — the
        // ownership rules stay perfectly balanced.
        if (lockCursor) ApplyCursorLock(true);
    }

    private void Start()
    {
        if (target == null)
        {
            GameObject found = GameObject.FindGameObjectWithTag("Player");
            if (found != null) target = found.transform;
        }

        if (target == null)
        {
            Debug.LogError("[PlayerCameraController] No target assigned and no GameObject tagged " +
                           "'Player' found. Disabling camera controller.", this);
            enabled = false;
            return;
        }

        targetRigidbody = target.GetComponentInParent<Rigidbody>();

        // Start from the scene placement of the camera so the opening framing
        // can be art-directed in the editor.
        yaw = NormalizeAngle(transform.eulerAngles.y);
        pitch = Mathf.Clamp(NormalizeAngle(transform.eulerAngles.x), -maxUpwardAngle, maxDownwardAngle);

        smoothedPivot = GetPivot();
        currentOffsetLength = GetDesiredOffset().magnitude;

        transform.SetPositionAndRotation(
            smoothedPivot + GetOffsetDirection() * currentOffsetLength,
            ComputeRotation());

        ApplyCursorLock(lockCursor);
    }

    private void Update()
    {
        HandleCursorToggle();

        // v1.2: no mouse look while the pause menu is open.
        if (GamePauseManager.IsPaused) return;

        // v1.3: the pause menu owns the cursor while paused, so this runs only
        // during normal gameplay — see KeepCursorLocked().
        KeepCursorLocked();

        Vector2 delta = ReadLookDelta();
        yaw = NormalizeAngle(yaw + delta.x);

        // Positive Euler X pitches down, so mouse-up (positive delta.y) lowers pitch.
        pitch += invertY ? delta.y : -delta.y;
        pitch = Mathf.Clamp(pitch, -maxUpwardAngle, maxDownwardAngle);
    }

    private void LateUpdate()
    {
        if (target == null) return;

        // 1) Smoothed pivot point at eye height.
        Vector3 pivot = GetPivot();
        smoothedPivot = followSmoothTime <= 0f
            ? pivot
            : Vector3.SmoothDamp(smoothedPivot, pivot, ref pivotSmoothVelocity, followSmoothTime);

        // 2) Desired offset: behind the player plus the shoulder shift.
        Vector3 offset = GetDesiredOffset();
        Vector3 offsetDir = GetOffsetDirection();
        float desiredLength = offset.magnitude;

        // 3) Collision: clamp how far along the offset the camera may sit.
        float allowedLength = avoidObstacles
            ? CastForAllowedLength(smoothedPivot, offsetDir, desiredLength)
            : desiredLength;

        // Pull in instantly (never inside a wall, even for one frame),
        // extend back out smoothly.
        if (allowedLength < currentOffsetLength)
        {
            currentOffsetLength = allowedLength;
            lengthSmoothVelocity = 0f;
        }
        else
        {
            currentOffsetLength = Mathf.SmoothDamp(currentOffsetLength, allowedLength,
                ref lengthSmoothVelocity, Mathf.Max(distanceRecoverTime, 0.0001f));
        }

        // 4) Final placement.
        transform.SetPositionAndRotation(smoothedPivot + offsetDir * currentOffsetLength, ComputeRotation());
    }

    private void OnDisable()
    {
        // Never leave the cursor locked when the camera controller is turned off
        // (also how the jumpscare / ending systems take cursor ownership: they
        // disable this component first, then set the cursor themselves).
        ApplyCursorLock(false);
    }

    /// <summary>
    /// v1.3: focus regained (window clicked / alt-tabbed back). Unity drops the
    /// cursor lock when the app loses focus; re-apply our wanted state right
    /// away so gameplay continues with an invisible cursor.
    /// </summary>
    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus) return;
        if (!enabled) return;                        // someone else owns the cursor right now
        if (GamePauseManager.IsPaused) return;       // the pause menu owns the cursor while open
        if (!lockCursor || !cursorLocked) return;    // only when we still want the lock
        ApplyCursorLock(true);
    }

    /// <summary>Clamps Inspector values so the camera can never receive nonsense input.</summary>
    private void OnValidate()
    {
        cameraDistance = Mathf.Max(0.5f, cameraDistance);
        minCameraDistance = Mathf.Clamp(minCameraDistance, 0.2f, cameraDistance);
        lookSensitivity = Mathf.Max(0.01f, lookSensitivity);
        maxUpwardAngle = Mathf.Max(0f, maxUpwardAngle);
        maxDownwardAngle = Mathf.Max(0f, maxDownwardAngle);
        followSmoothTime = Mathf.Max(0f, followSmoothTime);
        distanceRecoverTime = Mathf.Max(0f, distanceRecoverTime);
        collisionRadius = Mathf.Max(0f, collisionRadius);
        collisionPadding = Mathf.Max(0f, collisionPadding);
        pivotHeight = Mathf.Max(0f, pivotHeight);
    }

    // ------------------------------------------------------------------ //
    // Public API
    // ------------------------------------------------------------------ //

    /// <summary>Current orbit yaw in degrees ([-180, 180] – read it to align gameplay with the view).</summary>
    public float Yaw { get { return yaw; } }

    /// <summary>Current orbit pitch in degrees (negative looks up, positive looks down).</summary>
    public float Pitch { get { return pitch; } }

    // ------------------------------------------------------------------ //
    // Framing math
    // ------------------------------------------------------------------ //

    private Quaternion ComputeRotation()
    {
        return Quaternion.Euler(pitch, yaw, 0f);
    }

    private Vector3 GetPivot()
    {
        return target.position + Vector3.up * pivotHeight;
    }

    private Vector3 GetDesiredOffset()
    {
        Quaternion rot = ComputeRotation();
        // Horizontal right so vertical aim does not skew the shoulder shift.
        Vector3 flatRight = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
        return rot * Vector3.back * cameraDistance + flatRight * shoulderOffset;
    }

    private Vector3 GetOffsetDirection()
    {
        Vector3 offset = GetDesiredOffset();
        return offset.magnitude > 0.0001f ? offset.normalized : Vector3.back;
    }

    // ------------------------------------------------------------------ //
    // Collision
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Spherecasts from the pivot toward the desired camera position and returns
    /// the allowed offset length. Hits on the followed player itself are ignored.
    /// </summary>
    private float CastForAllowedLength(Vector3 origin, Vector3 direction, float desiredLength)
    {
        int hitCount = Physics.SphereCastNonAlloc(origin, collisionRadius, direction, hitBuffer,
            desiredLength, collisionMask, QueryTriggerInteraction.Ignore);

        float allowed = desiredLength;

        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit hit = hitBuffer[i];

            // The followed player must never block its own camera.
            if (hit.collider.transform == target || hit.collider.transform.IsChildOf(target)) continue;
            if (targetRigidbody != null && hit.collider.attachedRigidbody == targetRigidbody) continue;

            float candidate = hit.distance - collisionPadding;
            if (candidate < allowed) allowed = Mathf.Max(candidate, minCameraDistance);
        }

        return Mathf.Clamp(allowed, minCameraDistance, desiredLength);
    }

    // ------------------------------------------------------------------ //
    // Cursor handling
    // ------------------------------------------------------------------ //

    private void HandleCursorToggle()
    {
        // v1.2: the pause menu owns the pause key — never double-handle it.
        KeyCode pauseKey = GamePauseManager.PauseKey;
        if (pauseKey != KeyCode.None && toggleCursorKey == pauseKey) return;

        // v1.2: the pause menu owns the cursor while the game is paused.
        if (GamePauseManager.IsPaused) return;

        if (ReadToggleKeyPressed())
        {
            ApplyCursorLock(!cursorLocked);
        }
        else if (!cursorLocked && lockCursor && relockCursorOnClick && ReadMouseClicked())
        {
            ApplyCursorLock(true);
        }
    }

    private void ApplyCursorLock(bool locked)
    {
        cursorLocked = locked;
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    /// <summary>
    /// v1.3 — the cursor keep-back. Setting Cursor.lockState once in Start() is
    /// NOT enough: when the Game view / window does not have focus (game just
    /// started, editor layout, alt-tab), Unity silently ignores or resets the
    /// lock — leaving a visible, free cursor on screen until the player clicks.
    /// While we want the cursor locked, every frame that Unity reports the lock
    /// slipped is corrected here, so the lock engages by itself the moment the
    /// window gains focus. Idempotent — nothing is written while the lock holds.
    /// </summary>
    private void KeepCursorLocked()
    {
        if (!cursorLocked || !lockCursor) return;
        if (Cursor.lockState == CursorLockMode.Locked && !Cursor.visible) return;
        ApplyCursorLock(true);
    }

    // ------------------------------------------------------------------ //
    // Input (supports both the legacy Input Manager and the new Input System)
    // ------------------------------------------------------------------ //

    private Vector2 ReadLookDelta()
    {
#if ENABLE_INPUT_SYSTEM
        Mouse mouse = Mouse.current;
        if (mouse == null) return Vector2.zero;

        // Mouse delta is in pixels; 0.01 rescales it to roughly match the
        // legacy axis magnitude so one Look Sensitivity value fits both backends.
        return mouse.delta.ReadValue() * (lookSensitivity * 0.01f);
#else
        return new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * lookSensitivity;
#endif
    }

    private bool ReadToggleKeyPressed()
    {
#if ENABLE_INPUT_SYSTEM
        // v1.1: the Cursor Toggle Key field drives the new Input System path
        // too, so changing the key in the Inspector works on BOTH backends.
        return KeyCodeBridge.WasKeyPressedThisFrame(Keyboard.current, toggleCursorKey);
#else
        return Input.GetKeyDown(toggleCursorKey);
#endif
    }

    private static bool ReadMouseClicked()
    {
#if ENABLE_INPUT_SYSTEM
        Mouse mouse = Mouse.current;
        return mouse != null && mouse.leftButton.wasPressedThisFrame;
#else
        return Input.GetMouseButtonDown(0);
#endif
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    private static float NormalizeAngle(float angle)
    {
        angle %= 360f;
        if (angle > 180f) angle -= 360f;
        if (angle < -180f) angle += 360f;
        return angle;
    }
}