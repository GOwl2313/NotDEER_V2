using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// VR WORLD UI CANVAS (v1.0) — turns ANY canvas into a VR "world UI":
/// hovering in front of the player at arm's length, at least an arm's
/// length wide, and clickable with the VR UI pointers' white lines.
///
/// THE RULE IT IMPLEMENTS
///   "Every UI except the HUD and the jumpscare should hover in front of
///    the player, at least an arm's length wide."
///   Add this component to a canvas (the pause menu panels, popup dialogs,
///   the scene's "ui" object…) and it:
///     • switches the canvas to World Space,
///     • scales it so its on-screen size is a real-world panel size
///       (clamped between Min and Max World Width — a converted 1920 px
///       overlay becomes a 1 m panel, not a 1920 m one),
///     • positions it in front of the player's HEAD at arm's length and
///       keeps it facing them (Follow Head), OR just turns it to face the
///       head (Face Head), OR leaves the placement alone (None — used by
///       the VR pause menu, which spawns from the left hand instead),
///     • registers it so BOTH hands' VRUIPointer components can aim at it
///       with their white lines and click its buttons.
///
/// HUD (battery/totem counters) and the jumpscare screens are NOT given
/// this component — they stay exactly like the original game.
///
/// MOVEMENT USES UNSCALED TIME on purpose: panels keep following the head
/// while the game is paused (Time.timeScale = 0), which is exactly when
/// the pause menu needs to stay in front of your face.
///
/// v1.1 — the head is resolved through VRPlayerSetup.XRCamera (the XR
///        rig's camera) instead of a coin-flip Camera.main between two
///        MainCamera-tagged cameras.
/// v1.0 — initial release with the NotDEER VR layer.
/// </summary>
[RequireComponent(typeof(Canvas))]
[DisallowMultipleComponent]
public class VRWorldUICanvas : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    public enum FollowMode
    {
        None,        // stay wherever it was placed (the VR pause menu uses this — it spawns from the left hand)
        FaceHead,    // keep the position, always rotate to face the player's head
        FollowHead   // arm's length in front of the head, facing it (the default for popup UIs)
    }

    [Header("Placement")]

    [Tooltip("None = stay put (menu at the hand). FaceHead = rotate to face the head. FollowHead = arm's length in front of the head, always facing it.")]
    [SerializeField] private FollowMode followMode = FollowMode.FollowHead;

    [Tooltip("Distance in front of the head while following (meters). Arm's length ≈ 0.65 m.")]
    [SerializeField, Min(0.2f)] private float followDistance = 0.65f;

    [Tooltip("Panel center offset from the eye line (meters, negative = a touch below the eyes for comfortable reading).")]
    [SerializeField] private float heightOffset = -0.08f;

    [Tooltip("How quickly the panel catches up with head movement (higher = stiffer).")]
    [SerializeField, Range(0f, 60f)] private float followSmoothness = 14f;

    [Header("Size (the arm's-length rule)")]

    [Tooltip("Smallest allowed world-space width (meters). Converted panels never get thinner than an arm's length.")]
    [SerializeField, Min(0.1f)] private float minWorldWidth = 0.6f;

    [Tooltip("Largest allowed world-space width (meters). A converted 1920 px overlay becomes this wide, not 1920 meters.")]
    [SerializeField, Min(0.1f)] private float maxWorldWidth = 1.0f;

    [Header("Interaction")]

    [Tooltip("Let the VR UI pointers (white lines) aim and click at this canvas. Turn off for purely informational panels.")]
    [SerializeField] private bool interactive = true;

    [Header("Diagnostics")]

    [Tooltip("Log the conversion (render mode + applied scale) once in the Console.")]
    [SerializeField] private bool debugLogs = false;

    // ------------------------------------------------------------------ //
    // Static registry (what the VRUIPointer components raycast against)
    // ------------------------------------------------------------------ //

    private static readonly List<VRWorldUICanvas> s_Registered = new List<VRWorldUICanvas>();

    /// <summary>
    /// Play-mode entry reset — correct even with Domain Reload disabled.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        s_Registered.Clear();
    }

    /// <summary>
    /// All registered canvases (active and inactive — callers check
    /// IsActiveAndEnabled themselves so a disabled canvas can never match).
    /// </summary>
    public static IList<VRWorldUICanvas> RegisteredCanvases
    {
        get { return s_Registered; }
    }

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private Canvas canvas;
    private RectTransform rectTransform;
    private Transform headCamera;
    private Graphic[] graphicCache;
    private float graphicCacheTimer;

    /// <summary>The canvas this component drives (never null — RequireComponent).</summary>
    public Canvas Canvas
    {
        get { return canvas; }
    }

    /// <summary>True while this canvas should receive pointer rays (component enabled + canvas active + interactive).</summary>
    public bool IsInteractive
    {
        get { return interactive && isActiveAndEnabled && canvas != null && canvas.isActiveAndEnabled; }
    }

    /// <summary>The follow mode (settable from code — the pause menu flips itself to None after placing itself at the hand).</summary>
    public FollowMode Mode
    {
        get { return followMode; }
        set { followMode = value; }
    }

    /// <summary>
    /// Cached raycast targets on this canvas. Refreshed on enable and every
    /// second after that (buttons can appear at runtime — the VR pause menu
    /// builds itself from code).
    /// </summary>
    public Graphic[] CachedGraphics
    {
        get
        {
            if (graphicCache == null) RefreshGraphicCache();
            return graphicCache;
        }
    }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        canvas = GetComponent<Canvas>();
        rectTransform = canvas.GetComponent<RectTransform>();

        // The conversion: whatever the canvas was (overlay, camera space),
        // in VR it is a panel in the world.
        if (canvas.renderMode != RenderMode.WorldSpace)
        {
            canvas.renderMode = RenderMode.WorldSpace;
            if (debugLogs) Debug.Log("[VRWorldUICanvas] '" + name + "' converted to World Space.", this);
        }
    }

    private void OnEnable()
    {
        if (!s_Registered.Contains(this)) s_Registered.Add(this);
        RefreshGraphicCache();
        ApplyWorldSize();
    }

    private void OnDisable()
    {
        s_Registered.Remove(this);
    }

    private void OnDestroy()
    {
        s_Registered.Remove(this);
    }

    private void Update()
    {
        // Panels must keep tracking the head while the game is paused —
        // unscaled time on purpose (see class docs).
        float dt = Time.unscaledDeltaTime;
        if (dt <= 0f) dt = 0.0001f;

        RefreshGraphicCacheOverTime(dt);

        Transform head = ResolveHeadCamera();
        if (head == null) return;

        switch (followMode)
        {
            case FollowMode.FaceHead:
                FaceTowards(head);
                break;

            case FollowMode.FollowHead:
                FollowHead(head, dt);
                break;
        }

        ApplyWorldSize();
    }

    /// <summary>Clamps Inspector values so a panel can never receive nonsense input.</summary>
    private void OnValidate()
    {
        if (maxWorldWidth < minWorldWidth) maxWorldWidth = minWorldWidth;
        followDistance = Mathf.Max(0.2f, followDistance);
        followSmoothness = Mathf.Max(0f, followSmoothness);
        minWorldWidth = Mathf.Max(0.1f, minWorldWidth);
    }

    // ------------------------------------------------------------------ //
    // Placement
    // ------------------------------------------------------------------ //

    private void FollowHead(Transform head, float dt)
    {
        // Arm's length in front of the EYES, following only the head's yaw
        // so looking down doesn't shove the panel into the ground.
        Vector3 flatForward = head.forward;
        flatForward.y = 0f;
        if (flatForward.sqrMagnitude < 0.0001f) flatForward = Vector3.forward;
        flatForward.Normalize();

        Vector3 target = head.position + flatForward * followDistance;
        target.y += heightOffset;

        if (followSmoothness <= 0f || transform.position.sqrMagnitude == 0f)
        {
            transform.position = target;
        }
        else
        {
            // Frame-rate independent catch-up (never overshoots).
            float blend = 1f - Mathf.Exp(-followSmoothness * dt);
            transform.position = Vector3.Lerp(transform.position, target, blend);
        }

        FaceTowards(head);
    }

    private void FaceTowards(Transform head)
    {
        Vector3 toPanel = transform.position - head.position;
        toPanel.y = 0f;                     // upright panel — comfortable to read in VR

        if (toPanel.sqrMagnitude < 0.0001f)
        {
            transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
            return;
        }

        transform.rotation = Quaternion.LookRotation(toPanel.normalized, Vector3.up);
    }

    // ------------------------------------------------------------------ //
    // Sizing (min / max world width)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Scales the canvas so its world width lands between Min and Max World
    /// Width. Idempotent — once the size is in range this is a no-op (user
    /// tweaks to localScale are respected as long as they stay in range).
    /// </summary>
    private void ApplyWorldSize()
    {
        if (rectTransform == null) return;

        float worldWidth = rectTransform.rect.width * transform.localScale.x;
        if (worldWidth <= 0.0001f) return;

        if (worldWidth < minWorldWidth)
        {
            transform.localScale = transform.localScale * (minWorldWidth / worldWidth);
            if (debugLogs) Debug.Log("[VRWorldUICanvas] '" + name + "' scaled UP to " +
                                     minWorldWidth.ToString("F2") + " m world width.", this);
        }
        else if (worldWidth > maxWorldWidth)
        {
            transform.localScale = transform.localScale * (maxWorldWidth / worldWidth);
            if (debugLogs) Debug.Log("[VRWorldUICanvas] '" + name + "' scaled DOWN to " +
                                     maxWorldWidth.ToString("F2") + " m world width.", this);
        }
    }

    // ------------------------------------------------------------------ //
    // Graphic cache (what the pointer rays can hit)
    // ------------------------------------------------------------------ //

    private void RefreshGraphicCacheOverTime(float dt)
    {
        graphicCacheTimer += dt;
        if (graphicCacheTimer < 1f) return;

        graphicCacheTimer = 0f;
        RefreshGraphicCache();
    }

    /// <summary>Re-collects the raycast-target graphics (call after adding buttons from code).</summary>
    public void RefreshGraphicCache()
    {
        graphicCacheTimer = 0f;
        graphicCache = canvas != null ? canvas.GetComponentsInChildren<Graphic>(false) : new Graphic[0];
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    /// <summary>
    /// The head is the XR rig's camera — the setup's static reference first
    /// (always the VR camera), Camera.main only as a fallback for manually
    /// assembled scenes. With two MainCamera-tagged cameras in the scene
    /// Camera.main alone is a coin flip — panels ended up "following" the
    /// wrong camera entirely.
    /// </summary>
    private Transform ResolveHeadCamera()
    {
        if (headCamera != null) return headCamera;

        Transform xrHead = VRPlayerSetup.XRHead;
        if (xrHead != null)
        {
            headCamera = xrHead;
            return headCamera;
        }

        Camera main = Camera.main;
        if (main != null) headCamera = main.transform;
        return headCamera;
    }

    /// <summary>Force the panel in front of the head RIGHT NOW (no smoothing) — used right after spawning.</summary>
    public void SnapInFrontOfHead()
    {
        Transform head = ResolveHeadCamera();
        if (head == null) return;

        Vector3 flatForward = head.forward;
        flatForward.y = 0f;
        if (flatForward.sqrMagnitude < 0.0001f) flatForward = Vector3.forward;
        flatForward.Normalize();

        transform.position = head.position + flatForward * followDistance + Vector3.up * heightOffset;
        FaceTowards(head);
    }
}
