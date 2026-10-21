using System;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// WORLD WAYPOINT (v1.0) — a floating marker you can see from anywhere on the map.
///
/// WHAT IT DOES
///   Put this component on a CHILD GameObject of anything you want marked
///   (a Totem, a door, a spawner…) and it gets a screen marker that:
///     • always faces the player (it is drawn ON the screen — it can never
///       turn away),
///     • is never hidden by walls, props or fog (it draws OVER the 3D world,
///       so nothing can ever be "in front of" it),
///     • keeps EXACTLY the same size no matter how far away the player walks,
///     • shows YOUR icon image (any Sprite), scaled however you like,
///     • shows YOUR text label ABOVE the icon,
///     • shows the live distance in meters BELOW the icon ("23 m"),
///     • follows its parent object perfectly: parent moves → the marker moves.
///
/// LIFECYCLE — WORKS ALONG WITH THE PARENT (this is the part you asked for):
///   • Attach it as a CHILD of the totem. When the totem is PICKED UP
///     (ItemPickup DESTROYS the item), the waypoint dies in the same instant —
///     it can never float over an empty spot.
///   • Parent deactivated (SetActive(false))? The waypoint disappears with it,
///     and comes back the moment the parent is activated again. A waypoint
///     whose parent "has not appeared yet" is simply not there yet.
///   • No manager, no scene setup: the WaypointHUD canvas builds itself the
///     moment the first waypoint appears, and cleans itself up with the scene.
///
/// HOW IT IS DRAWN (why your three "always" rules are guaranteed):
///   The waypoint itself is only a world-space ANCHOR (this GameObject's
///   position). The visible marker is created by WaypointHUD on a screen-space
///   overlay canvas: every frame (after the camera has finished moving) the
///   anchor's position is projected onto the screen and the icon/text are
///   placed there. Because the marker lives on the UI layer:
///     • facing the player is inherent (screen UI always "faces" you),
///     • occlusion is impossible (overlay draws above all 3D geometry),
///     • size is constant (screen pixels don't shrink with distance).
///   No shaders, no ZTest tricks, nothing that breaks in builds.
///
/// FOR FUTURE SCRIPTS (plug-in friendly):
///   • SetLabel(text) / SetIcon(sprite) / SetIconScale(f) / SetIconTint(color)
///     / Show() / Hide() — change anything live, from any script.
///   • CurrentDistance / IsVisibleOnScreen — live state, readable any time.
///   • GetAllWaypoints() — snapshot of every active waypoint in the scene.
///   • Events (Inspector-wireable AND code-subscribable):
///       onBecameVisible / onBecameHidden            (per waypoint)
///       WorldWaypoint.AnyWaypointBecameVisible /
///       WorldWaypoint.AnyWaypointBecameHidden       (static, all of them)
///   • Nothing here references ItemPickup, the HUD scripts or the pause
///     system — waypoints are fully standalone and decoupled. (While the
///     game is paused the world stands still, so the markers stand still
///     with it — no special pause code needed, nothing can desync.)
///
/// SETUP
///   1. Create an empty child GameObject under your object (e.g. Totem_1 →
///      "Waypoint"), move it where the marker should float (usually above
///      the model).
///   2. Add this component. Assign an Icon Sprite, write a Label.
///   3. Press Play. That's everything — the HUD builds itself.
///
/// v1.0 — initial release.
/// </summary>
[DisallowMultipleComponent]
public class WorldWaypoint : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Icon")]

    [Tooltip("The icon image shown at the waypoint. Any Sprite works (import your image as a Sprite: Texture Type = Sprite (2D and UI)). Left empty: a plain white square (tinted by 'Icon Tint').")]
    [SerializeField] private Sprite iconSprite = null;

    [Tooltip("Icon size multiplier. 1 = the HUD's base icon size (64 px by default), 2 = twice as big, 0.5 = half. The size NEVER changes with distance — only this value changes it.")]
    [SerializeField, Min(0.05f)] private float iconScale = 1f;

    [Tooltip("Colour tint of the icon. White = image as-is. Works great to colour a white silhouette icon.")]
    [SerializeField] private Color iconTint = Color.white;

    [Header("Label (above the icon)")]

    [Tooltip("Show the label text above the icon.")]
    [SerializeField] private bool showLabel = true;

    [Tooltip("The label text (written ABOVE the icon). Any length — it never clips.")]
    [SerializeField, TextArea(2, 4)] private string labelText = "Waypoint";

    [Tooltip("Font size of the label.")]
    [SerializeField, Range(8, 128)] private int labelFontSize = 26;

    [Tooltip("Colour of the label text.")]
    [SerializeField] private Color labelColor = Color.white;

    [Header("Distance (below the icon)")]

    [Tooltip("Show the live distance below the icon (e.g. '23 m').")]
    [SerializeField] private bool showDistance = true;

    [Tooltip("Distance unit shown after the number. 'm' → '23 m'. Empty string → just the number.")]
    [SerializeField] private string distanceSuffix = "m";

    [Tooltip("Font size of the distance number.")]
    [SerializeField, Range(8, 128)] private int distanceFontSize = 22;

    [Tooltip("Colour of the distance number.")]
    [SerializeField] private Color distanceColor = Color.white;

    [Tooltip("Who the distance is measured TO. Player = the tagged 'Player' object (falls back to the camera if no player is found, with a one-time warning). MainCamera = the camera itself.")]
    [SerializeField] private DistanceReference distanceReference = DistanceReference.Player;

    [Header("Visibility")]

    [Tooltip("Keep the marker pinned to the screen edge when the waypoint is behind the player or out of view — it then acts like a compass direction instead of disappearing. OFF (default): the marker is simply only visible while the waypoint is on screen.")]
    [SerializeField] private bool clampToScreenEdge = false;

    [Tooltip("With 'Clamp To Screen Edge': how close to the screen border the marker stops (0.08 = 8% margin from each edge).")]
    [SerializeField, Range(0f, 0.45f)] private float screenEdgeMargin = 0.08f;

    [Tooltip("Hide the marker beyond this distance (meters, same reference as the distance number). 0 = no limit — visible from anywhere on the map.")]
    [SerializeField, Min(0f)] private float maxViewDistance = 0f;

    [Tooltip("Extra screen-space offset in pixels, applied to the whole marker (icon + texts). X = right, Y = up.")]
    [SerializeField] private Vector2 screenOffset = Vector2.zero;

    [Header("Events")]

    [Tooltip("Fired when this waypoint's marker appears on screen (in view, within range, not manually hidden).")]
    [SerializeField] private UnityEvent onBecameVisible = new UnityEvent();

    [Tooltip("Fired when this waypoint's marker leaves the screen (out of view, out of range, manually hidden, parent deactivated/destroyed…).")]
    [SerializeField] private UnityEvent onBecameHidden = new UnityEvent();

    [Header("Debug")]

    [Tooltip("Write waypoint visibility changes to the Console.")]
    [SerializeField] private bool debugLogs = false;

    // ------------------------------------------------------------------ //
    // Types
    // ------------------------------------------------------------------ //

    /// <summary>What the distance number is measured to.</summary>
    public enum DistanceReference
    {
        /// <summary>The GameObject tagged 'Player' (falls back to the camera when no player exists).</summary>
        Player = 0,

        /// <summary>The main camera's position.</summary>
        MainCamera = 1
    }

    // ------------------------------------------------------------------ //
    // Runtime state (written by WaypointHUD, readable by everyone)
    // ------------------------------------------------------------------ //

    /// <summary>Every currently-active waypoint in the scene (registration list). WaypointHUD iterates this; your scripts should use GetAllWaypoints().</summary>
    internal static readonly System.Collections.Generic.List<WorldWaypoint> AllActive =
        new System.Collections.Generic.List<WorldWaypoint>();

    /// <summary>
    /// Raised whenever ANY waypoint's marker appears on screen. Subscribe in
    /// OnEnable, unsubscribe in OnDisable — never leaks across scene reloads.
    /// </summary>
    public static event Action<WorldWaypoint> AnyWaypointBecameVisible;

    /// <summary>Raised whenever ANY waypoint's marker leaves the screen.</summary>
    public static event Action<WorldWaypoint> AnyWaypointBecameHidden;

    /// <summary>Live distance to the reference (Player or camera) in meters. -1 until the HUD has run its first frame.</summary>
    public float CurrentDistance { get; internal set; }

    /// <summary>True while the marker is actually shown on screen (in view, in range, not manually hidden, parent active).</summary>
    public bool IsVisibleOnScreen { get; internal set; }

    /// <summary>True while manually hidden via Hide() (independent of view/range — Show() brings it back).</summary>
    public bool IsManuallyHidden { get; private set; }

    // ------------------------------------------------------------------ //
    // Read-only Inspector access (for WaypointHUD and your scripts)
    // ------------------------------------------------------------------ //

    /// <summary>The icon sprite (null = plain tinted square).</summary>
    public Sprite IconSprite { get { return iconSprite; } }

    /// <summary>Icon size multiplier (1 = base size).</summary>
    public float IconScale { get { return iconScale; } }

    /// <summary>Icon colour tint.</summary>
    public Color IconTint { get { return iconTint; } }

    /// <summary>Whether the label above the icon is shown.</summary>
    public bool ShowLabel { get { return showLabel; } }

    /// <summary>The label text above the icon.</summary>
    public string LabelText { get { return labelText; } }

    /// <summary>Label font size.</summary>
    public int LabelFontSize { get { return labelFontSize; } }

    /// <summary>Label text colour.</summary>
    public Color LabelColor { get { return labelColor; } }

    /// <summary>Whether the distance below the icon is shown.</summary>
    public bool ShowDistance { get { return showDistance; } }

    /// <summary>The text appended after the distance number ('m' → '23 m').</summary>
    public string DistanceSuffix { get { return distanceSuffix; } }

    /// <summary>Distance number font size.</summary>
    public int DistanceFontSize { get { return distanceFontSize; } }

    /// <summary>Distance number colour.</summary>
    public Color DistanceColor { get { return distanceColor; } }

    /// <summary>What the distance is measured to.</summary>
    public DistanceReference Reference { get { return distanceReference; } }

    /// <summary>Pin the marker to the screen edge when out of view instead of hiding it.</summary>
    public bool ClampToScreenEdge { get { return clampToScreenEdge; } }

    /// <summary>Screen-edge clamp margin (0..0.45 of the screen).</summary>
    public float ScreenEdgeMargin { get { return screenEdgeMargin; } }

    /// <summary>Hide beyond this distance (0 = unlimited).</summary>
    public float MaxViewDistance { get { return maxViewDistance; } }

    /// <summary>Extra pixel offset applied to the whole marker.</summary>
    public Vector2 ScreenOffset { get { return screenOffset; } }

    /// <summary>Write waypoint visibility changes to the Console.</summary>
    public bool DebugLogs { get { return debugLogs; } }

    // ------------------------------------------------------------------ //
    // Public API — live control for your (and your friends') future scripts
    // ------------------------------------------------------------------ //

    /// <summary>How many waypoints are currently active in the scene.</summary>
    public static int TotalWaypoints { get { return AllActive.Count; } }

    /// <summary>
    /// A snapshot array of every active waypoint (safe to iterate and modify
    /// — it is a copy). Empty array when none exist.
    /// </summary>
    public static WorldWaypoint[] GetAllWaypoints()
    {
        WorldWaypoint[] copy = new WorldWaypoint[AllActive.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = AllActive[i];
        return copy;
    }

    /// <summary>Changes the label text above the icon, live.</summary>
    public void SetLabel(string text)
    {
        labelText = text ?? string.Empty;
    }

    /// <summary>Changes the icon image, live. Null = plain tinted square.</summary>
    public void SetIcon(Sprite sprite)
    {
        iconSprite = sprite;
    }

    /// <summary>Changes the icon size multiplier, live (1 = base size).</summary>
    public void SetIconScale(float scale)
    {
        iconScale = Mathf.Max(0.05f, scale);
    }

    /// <summary>Changes the icon tint colour, live.</summary>
    public void SetIconTint(Color tint)
    {
        iconTint = tint;
    }

    /// <summary>Manually hides this waypoint's marker (same as walking it out of view — fires On Became Hidden).</summary>
    public void Hide()
    {
        SetVisible(false);
    }

    /// <summary>Un-hides this waypoint's marker after a manual Hide().</summary>
    public void Show()
    {
        SetVisible(true);
    }

    /// <summary>Manual show/hide switch (does nothing when the state is already the same).</summary>
    public void SetVisible(bool visible)
    {
        if (IsManuallyHidden == !visible) return;   // already in that state
        IsManuallyHidden = !visible;
        // The visibility events are raised by WaypointHUD on the next frame,
        // when the marker actually disappears/appears — one source of truth.
    }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void OnEnable()
    {
        if (!AllActive.Contains(this)) AllActive.Add(this);
        WaypointHUD.RegisterWaypoint(this);
    }

    private void OnDisable()
    {
        AllActive.Remove(this);
        WaypointHUD.UnregisterWaypoint(this);
    }

    /// <summary>Clamps Inspector values so a waypoint can never receive nonsense input.</summary>
    private void OnValidate()
    {
        iconScale = Mathf.Max(0.05f, iconScale);
        labelFontSize = Mathf.Clamp(labelFontSize, 8, 128);
        distanceFontSize = Mathf.Clamp(distanceFontSize, 8, 128);
        maxViewDistance = Mathf.Max(0f, maxViewDistance);
        screenEdgeMargin = Mathf.Clamp(screenEdgeMargin, 0f, 0.45f);
        if (distanceSuffix == null) distanceSuffix = string.Empty;
    }

    /// <summary>Small yellow ring in the Scene view so you can see where the anchor sits while editing.</summary>
    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 0.92f, 0.23f, 1f);
        Gizmos.DrawWireSphere(transform.position, 0.25f);
    }

    // ------------------------------------------------------------------ //
    // Internal notifications (called by WaypointHUD — same assembly)
    // ------------------------------------------------------------------ //

    internal void RaiseBecameVisible()
    {
        if (debugLogs) Debug.Log("[WorldWaypoint] '" + name + "' became visible (" + Mathf.RoundToInt(Mathf.Max(0f, CurrentDistance)) + " m).", this);
        if (onBecameVisible != null) onBecameVisible.Invoke();
        if (AnyWaypointBecameVisible != null) AnyWaypointBecameVisible(this);
    }

    internal void RaiseBecameHidden()
    {
        if (debugLogs) Debug.Log("[WorldWaypoint] '" + name + "' became hidden.", this);
        if (onBecameHidden != null) onBecameHidden.Invoke();
        if (AnyWaypointBecameHidden != null) AnyWaypointBecameHidden(this);
    }
}
