using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// WAYPOINT HUD (v1.0) — the drawing engine behind WorldWaypoint.
///
/// WHAT IT DOES
///   Owns the overlay canvas that every WorldWaypoint marker is drawn on, and
///   once per frame (after the camera has fully moved — execution order 1000)
///   it projects each waypoint's anchor position onto the screen and places
///   the marker there: icon, label above, distance below.
///
/// ZERO SETUP
///   You normally never touch this script: the first WorldWaypoint that
///   enables itself builds the HUD automatically ("Waypoint HUD (auto)":
///   Screen Space Overlay canvas, sorting order 150, 1920x1080 reference
///   scaling — the same setup your other HUDs use). It dies with the scene
///   and rebuilds itself on the next load — nothing to wire, nothing to leak.
///
/// SORTING ORDER (where the markers draw)
///   150 by default = UNDER your battery HUD (200), totem counter (201) and
///   interaction prompts (250): world markers never cover your gameplay UI.
///   Change 'Canvas Sorting Order' if you ever want them above something.
///   (Only applies to the AUTO-built canvas — if you place this component on
///   your own canvas yourself, that canvas's settings are kept untouched.)
///
/// PERFORMANCE
///   • Zero allocations per frame in the steady state: every visual property
///     is written ONLY when it actually changed (distance number included —
///     it is rebuilt just when the whole-meter value changes).
///   • No raycast blockers: the canvas has no GraphicRaycaster and every
///     graphic has raycastTarget off — waypoints can never eat your clicks.
///   • Waypoints that are hidden (behind the player, out of range, parent
///     inactive) cost nothing but a position check.
///
/// FOR SCRIPTS
///   WaypointHUD.Instance — the live HUD (null before the first waypoint
///   exists). Waypoints register/unregister themselves via OnEnable/OnDisable
///   — parents being destroyed or deactivated is handled automatically and
///     the marker dies with them, exactly as WorldWaypoint documents.
///
/// v1.0 — initial release.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(1000)]   // after camera controllers: markers use the camera's FINAL position
public class WaypointHUD : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Auto-Built Canvas")]

    [Tooltip("Sorting order of the auto-created overlay canvas. 150 = under the battery HUD (200), totem counter (201) and prompts (250) — world markers never cover your gameplay UI. Only used when this canvas is built automatically.")]
    [SerializeField] private int canvasSortingOrder = 150;

    [Tooltip("Icon size in pixels at Icon Scale 1 (all waypoints scale relative to this).")]
    [SerializeField, Min(8f)] private float baseIconSize = 64f;

    [Tooltip("Gap between the icon and each text, in pixels.")]
    [SerializeField, Min(0f)] private float textSpacing = 6f;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    /// <summary>The live HUD, or null before the first waypoint exists.</summary>
    public static WaypointHUD Instance { get { return instance; } }

    private static WaypointHUD instance;

    private Canvas canvas;                       // reused when sitting on a user canvas
    private Canvas ownCanvas;                    // non-null only when WE created the canvas
    private readonly Dictionary<WorldWaypoint, MarkerUI> markers = new Dictionary<WorldWaypoint, MarkerUI>();

    private Camera mainCamera;
    private Transform playerTransform;
    private bool warnedNoPlayer;
    private bool warnedDuplicate;

    /// <summary>All visual elements belonging to one waypoint.</summary>
    private sealed class MarkerUI
    {
        public RectTransform Root;
        public Image Icon;
        public RectTransform IconRect;
        public GameObject LabelRoot;
        public Text Label;
        public GameObject DistanceRoot;
        public Text Distance;

        // cached values — every write below happens ONLY on a real change
        public bool IsShown;
        public Sprite LastSprite;
        public float LastIconSize = -1f;
        public Color LastIconTint;
        public string LastLabel;
        public int LastLabelSize;
        public Color LastLabelColor;
        public bool LastShowLabel;
        public string LastSuffix;
        public int LastDistanceValue = -1;
        public int LastDistanceSize;
        public Color LastDistanceColor;
        public bool LastShowDistance;
    }

    // ------------------------------------------------------------------ //
    // Registration (called by WorldWaypoint.OnEnable / OnDisable)
    // ------------------------------------------------------------------ //

    /// <summary>Registers a waypoint with the HUD (creating the HUD first if needed). Safe to call any number of times.</summary>
    public static void RegisterWaypoint(WorldWaypoint waypoint)
    {
        if (waypoint == null) return;
        WaypointHUD hud = EnsureInstance();
        if (hud != null) hud.InternalRegister(waypoint);
    }

    /// <summary>Removes a waypoint's marker. Safe to call any number of times, even when the HUD is already gone.</summary>
    public static void UnregisterWaypoint(WorldWaypoint waypoint)
    {
        if (waypoint == null) return;
        if (instance == null) return;   // HUD already destroyed (e.g. scene teardown)
        instance.InternalUnregister(waypoint);
    }

    private static WaypointHUD EnsureInstance()
    {
        if (instance != null) return instance;

#if UNITY_2023_1_OR_NEWER
        instance = Object.FindAnyObjectByType<WaypointHUD>();
#else
        instance = FindObjectOfType<WaypointHUD>();
#endif
        if (instance != null) return instance;

        // Build the standard HUD: canvas first (so WaypointHUD.Awake finds and
        // reuses it), then the component.
        GameObject go = new GameObject("Waypoint HUD (auto)");
        Canvas newCanvas = go.AddComponent<Canvas>();
        newCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        newCanvas.sortingOrder = 150;   // the documented default (auto path)

        CanvasScaler scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        instance = go.AddComponent<WaypointHUD>();
        return instance;
    }

    private void InternalRegister(WorldWaypoint waypoint)
    {
        if (waypoint == null) return;
        if (markers.ContainsKey(waypoint)) return;   // already registered

        MarkerUI ui = BuildMarker(waypoint);
        markers.Add(waypoint, ui);
        // Nothing else: the very next LateUpdate positions and shows it.
    }

    private void InternalUnregister(WorldWaypoint waypoint)
    {
        if (waypoint == null) return;
        MarkerUI ui;
        if (!markers.TryGetValue(waypoint, out ui)) return;

        markers.Remove(waypoint);
        bool wasShown = ui != null && ui.IsShown;
        if (ui != null && ui.Root != null)
        {
            Destroy(ui.Root.gameObject);
        }

        // A waypoint going away while its marker was on screen counts as
        // "became hidden" (parent deactivated or destroyed — the WorldWaypoint
        // docs promise exactly that).
        waypoint.IsVisibleOnScreen = false;
        waypoint.CurrentDistance = -1f;
        if (wasShown) waypoint.RaiseBecameHidden();
    }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            if (!warnedDuplicate)
            {
                warnedDuplicate = true;
                Debug.LogWarning("[WaypointHUD] A second WaypointHUD was found ('" + name + "') — only the first one ('" + instance.name + "') draws waypoints. Keep one per scene (the first one builds itself automatically).", this);
            }
            enabled = false;
            return;
        }

        instance = this;

        // Reuse the canvas this component sits on; build one only when missing.
        canvas = GetComponent<Canvas>();
        if (canvas == null)
        {
            GameObject canvasGO = new GameObject("Waypoint HUD Canvas (auto)");
            canvasGO.transform.SetParent(transform, false);
            canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = canvasSortingOrder;

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            ownCanvas = canvas;
        }
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;

        // a canvas WE created is removed with us; a reused user canvas stays
        if (ownCanvas != null)
        {
            Destroy(ownCanvas.gameObject);
            ownCanvas = null;
        }
    }

    // ------------------------------------------------------------------ //
    // The frame loop — projection + guarded visual sync
    // ------------------------------------------------------------------ //

    private void LateUpdate()
    {
        if (WorldWaypoint.AllActive.Count == 0) return;

        if (mainCamera == null) mainCamera = Camera.main;

        if (mainCamera == null)
        {
            // No camera in the scene: everything hides until one exists.
            for (int i = WorldWaypoint.AllActive.Count - 1; i >= 0; i--)
            {
                ApplyHidden(WorldWaypoint.AllActive[i]);
            }
            return;
        }

        float pixelWidth = mainCamera.pixelWidth;
        float pixelHeight = mainCamera.pixelHeight;
        Vector3 cameraPos = mainCamera.transform.position;
        Transform player = ResolvePlayerTransform();
        Vector3 playerPos = player != null ? player.position : cameraPos;

        // Backwards: a waypoint dying inside an event handler (a pickup
        // destroying a totem, for example) removes itself from the same list
        // mid-loop — backwards iteration plus idempotent syncing makes that
        // safe without copying (zero allocations).
        for (int i = WorldWaypoint.AllActive.Count - 1; i >= 0; i--)
        {
            WorldWaypoint waypoint = WorldWaypoint.AllActive[i];
            if (waypoint == null) continue;

            UpdateMarker(waypoint, pixelWidth, pixelHeight, cameraPos, playerPos);
        }
    }

    private void UpdateMarker(WorldWaypoint waypoint, float pixelWidth, float pixelHeight,
        Vector3 cameraPos, Vector3 playerPos)
    {
        MarkerUI ui;
        if (!markers.TryGetValue(waypoint, out ui))
        {
            ui = BuildMarker(waypoint);   // registered before the HUD existed
            markers.Add(waypoint, ui);
        }

        // ---- distance (always fresh, even while hidden) ----------------- //
        Vector3 referencePos = waypoint.Reference == WorldWaypoint.DistanceReference.MainCamera
            ? cameraPos
            : playerPos;
        float distance = Vector3.Distance(referencePos, waypoint.transform.position);
        waypoint.CurrentDistance = distance;

        if (waypoint.MaxViewDistance > 0f && distance > waypoint.MaxViewDistance)
        {
            ApplyHidden(waypoint);
            return;
        }

        // ---- project the anchor onto the screen ------------------------- //
        Vector3 viewport = mainCamera.WorldToViewportPoint(waypoint.transform.position);
        bool behind = viewport.z <= 0f;

        if (behind && !waypoint.ClampToScreenEdge)
        {
            ApplyHidden(waypoint);
            return;
        }

        float vx = viewport.x;
        float vy = viewport.y;
        if (behind)
        {
            // Behind the camera the projection comes out mirrored — flip it,
            // then the clamp below pins it to the correct edge.
            vx = -vx;
            vy = -vy;
        }

        if (waypoint.ClampToScreenEdge)
        {
            float margin = waypoint.ScreenEdgeMargin;
            vx = Mathf.Clamp(vx, margin, 1f - margin);
            vy = Mathf.Clamp(vy, margin, 1f - margin);
        }

        // A clamped marker counts as on screen (the clamp above has already
        // pulled it inside the margins, even when it started behind the
        // camera). An unclamped marker must be both in front and in bounds.
        bool inBounds = vx >= 0f && vx <= 1f && vy >= 0f && vy <= 1f;
        bool onScreen = inBounds && (!behind || waypoint.ClampToScreenEdge);
        if (!onScreen)
        {
            ApplyHidden(waypoint);
            return;
        }

        // ---- show / hide transitions (events fire ONLY on real changes) - //
        bool shown = !waypoint.IsManuallyHidden;
        if (shown != ui.IsShown)
        {
            ui.IsShown = shown;
            ui.Root.gameObject.SetActive(shown);
            waypoint.IsVisibleOnScreen = shown;
            if (shown) waypoint.RaiseBecameVisible();
            else waypoint.RaiseBecameHidden();
        }
        if (!shown)
        {
            waypoint.IsVisibleOnScreen = false;
            return;
        }

        // ---- position (icon + texts move as one unit) ------------------- //
        Vector2 offset = waypoint.ScreenOffset;
        ui.Root.position = new Vector3(vx * pixelWidth + offset.x, vy * pixelHeight + offset.y, 0f);

        // ---- guarded syncing: every write happens only on a real change - //
        SyncIcon(waypoint, ui);
        SyncLabel(waypoint, ui);
        SyncDistance(waypoint, ui, distance);
    }

    private void ApplyHidden(WorldWaypoint waypoint)
    {
        MarkerUI ui;
        if (!markers.TryGetValue(waypoint, out ui)) return;

        if (ui.IsShown)
        {
            ui.IsShown = false;
            ui.Root.gameObject.SetActive(false);
            waypoint.IsVisibleOnScreen = false;
            waypoint.RaiseBecameHidden();
        }
        else
        {
            waypoint.IsVisibleOnScreen = false;
        }
    }

    private void SyncIcon(WorldWaypoint waypoint, MarkerUI ui)
    {
        if (!ReferenceEquals(ui.LastSprite, waypoint.IconSprite))
        {
            ui.LastSprite = waypoint.IconSprite;
            ui.Icon.sprite = waypoint.IconSprite;
        }

        if (waypoint.IconTint != ui.LastIconTint)
        {
            ui.LastIconTint = waypoint.IconTint;
            ui.Icon.color = waypoint.IconTint;
        }

        float targetSize = baseIconSize * waypoint.IconScale;
        if (Mathf.Abs(targetSize - ui.LastIconSize) > 0.01f)
        {
            ui.LastIconSize = targetSize;
            ui.IconRect.sizeDelta = new Vector2(targetSize, targetSize);
        }
    }

    private void SyncLabel(WorldWaypoint waypoint, MarkerUI ui)
    {
        bool showLabel = waypoint.ShowLabel;
        if (showLabel != ui.LastShowLabel)
        {
            ui.LastShowLabel = showLabel;
            ui.LabelRoot.SetActive(showLabel);
        }

        string text = waypoint.LabelText ?? string.Empty;
        if (!string.Equals(ui.LastLabel, text))
        {
            ui.LastLabel = text;
            ui.Label.text = text;
        }

        if (waypoint.LabelFontSize != ui.LastLabelSize)
        {
            ui.LastLabelSize = waypoint.LabelFontSize;
            ui.Label.fontSize = waypoint.LabelFontSize;
        }

        if (waypoint.LabelColor != ui.LastLabelColor)
        {
            ui.LastLabelColor = waypoint.LabelColor;
            ui.Label.color = waypoint.LabelColor;
        }
    }

    private void SyncDistance(WorldWaypoint waypoint, MarkerUI ui, float distance)
    {
        bool showDistance = waypoint.ShowDistance;
        if (showDistance != ui.LastShowDistance)
        {
            ui.LastShowDistance = showDistance;
            ui.DistanceRoot.SetActive(showDistance);
        }

        string suffix = waypoint.DistanceSuffix ?? string.Empty;
        if (!string.Equals(ui.LastSuffix, suffix))
        {
            ui.LastSuffix = suffix;
            ui.LastDistanceValue = -1;   // force the number to rebuild with the new suffix
        }

        int wholeMeters = Mathf.RoundToInt(Mathf.Max(0f, distance));
        if (wholeMeters != ui.LastDistanceValue)
        {
            ui.LastDistanceValue = wholeMeters;
            ui.Distance.text = suffix.Length == 0
                ? wholeMeters.ToString()
                : wholeMeters.ToString() + " " + suffix;
        }

        if (waypoint.DistanceFontSize != ui.LastDistanceSize)
        {
            ui.LastDistanceSize = waypoint.DistanceFontSize;
            ui.Distance.fontSize = waypoint.DistanceFontSize;
        }

        if (waypoint.DistanceColor != ui.LastDistanceColor)
        {
            ui.LastDistanceColor = waypoint.DistanceColor;
            ui.Distance.color = waypoint.DistanceColor;
        }
    }

    private Transform ResolvePlayerTransform()
    {
        if (playerTransform != null) return playerTransform;

        GameObject player = GameObject.FindWithTag("Player");
        if (player != null)
        {
            playerTransform = player.transform;
        }
        else if (!warnedNoPlayer)
        {
            warnedNoPlayer = true;
            Debug.LogWarning("[WaypointHUD] No GameObject tagged 'Player' found — waypoint distances are measured to the camera instead (one-time notice).", this);
        }
        return playerTransform;
    }

    // ------------------------------------------------------------------ //
    // Marker construction (house style: built-in font, outline, no clipping)
    // ------------------------------------------------------------------ //

    private MarkerUI BuildMarker(WorldWaypoint waypoint)
    {
        MarkerUI ui = new MarkerUI();

        // Root — sits at the projected screen point; everything hangs off it.
        GameObject rootGO = new GameObject("Waypoint: " + waypoint.name);
        ui.Root = rootGO.AddComponent<RectTransform>();
        ui.Root.SetParent(canvas.transform, false);
        ui.Root.anchorMin = ui.Root.anchorMax = ui.Root.pivot = new Vector2(0.5f, 0.5f);
        ui.Root.sizeDelta = Vector2.zero;
        ui.Root.gameObject.SetActive(false);   // hidden until the first frame proves it is on screen

        // Icon — the anchor everything else measures against.
        GameObject iconGO = new GameObject("Icon");
        ui.IconRect = iconGO.AddComponent<RectTransform>();
        ui.IconRect.SetParent(ui.Root, false);
        ui.IconRect.anchorMin = ui.IconRect.anchorMax = ui.IconRect.pivot = new Vector2(0.5f, 0.5f);
        ui.IconRect.anchoredPosition = Vector2.zero;

        ui.Icon = iconGO.AddComponent<Image>();
        ui.Icon.sprite = waypoint.IconSprite;
        ui.Icon.color = waypoint.IconTint;
        ui.Icon.raycastTarget = false;
        ui.LastSprite = waypoint.IconSprite;
        ui.LastIconTint = waypoint.IconTint;

        float iconSize = baseIconSize * waypoint.IconScale;
        ui.IconRect.sizeDelta = new Vector2(iconSize, iconSize);
        ui.LastIconSize = iconSize;

        // Label — above the icon.
        GameObject labelGO = new GameObject("Label");
        RectTransform labelRect = labelGO.AddComponent<RectTransform>();
        labelRect.SetParent(ui.IconRect, false);
        labelRect.anchorMin = labelRect.anchorMax = new Vector2(0.5f, 1f);   // the icon's top edge
        labelRect.pivot = new Vector2(0.5f, 0f);                             // grows upwards
        labelRect.anchoredPosition = new Vector2(0f, textSpacing);
        labelRect.sizeDelta = Vector2.zero;

        ui.Label = labelGO.AddComponent<Text>();
        ui.Label.font = LoadBuiltinFont();
        ui.Label.fontSize = waypoint.LabelFontSize;
        ui.Label.fontStyle = FontStyle.Bold;
        ui.Label.color = waypoint.LabelColor;
        ui.Label.alignment = TextAnchor.LowerCenter;
        ui.Label.horizontalOverflow = HorizontalWrapMode.Overflow;   // any text length — never clips
        ui.Label.verticalOverflow = VerticalWrapMode.Overflow;
        ui.Label.raycastTarget = false;
        ui.Label.text = waypoint.LabelText ?? string.Empty;
        ui.LastLabel = ui.Label.text;
        ui.LastLabelSize = waypoint.LabelFontSize;
        ui.LastLabelColor = waypoint.LabelColor;

        Shadow labelShadow = labelGO.AddComponent<Shadow>();
        labelShadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
        labelShadow.effectDistance = new Vector2(2f, -2f);

        ui.LabelRoot = labelGO;
        ui.LabelRoot.SetActive(waypoint.ShowLabel);
        ui.LastShowLabel = waypoint.ShowLabel;

        // Distance — below the icon.
        GameObject distanceGO = new GameObject("Distance");
        RectTransform distanceRect = distanceGO.AddComponent<RectTransform>();
        distanceRect.SetParent(ui.IconRect, false);
        distanceRect.anchorMin = distanceRect.anchorMax = new Vector2(0.5f, 0f);   // the icon's bottom edge
        distanceRect.pivot = new Vector2(0.5f, 1f);                                // grows downwards
        distanceRect.anchoredPosition = new Vector2(0f, -textSpacing);
        distanceRect.sizeDelta = Vector2.zero;

        ui.Distance = distanceGO.AddComponent<Text>();
        ui.Distance.font = LoadBuiltinFont();
        ui.Distance.fontSize = waypoint.DistanceFontSize;
        ui.Distance.fontStyle = FontStyle.Bold;
        ui.Distance.color = waypoint.DistanceColor;
        ui.Distance.alignment = TextAnchor.UpperCenter;
        ui.Distance.horizontalOverflow = HorizontalWrapMode.Overflow;
        ui.Distance.verticalOverflow = VerticalWrapMode.Overflow;
        ui.Distance.raycastTarget = false;
        ui.Distance.text = string.Empty;
        ui.LastSuffix = waypoint.DistanceSuffix ?? string.Empty;
        ui.LastDistanceSize = waypoint.DistanceFontSize;
        ui.LastDistanceColor = waypoint.DistanceColor;

        Shadow distanceShadow = distanceGO.AddComponent<Shadow>();
        distanceShadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
        distanceShadow.effectDistance = new Vector2(2f, -2f);

        ui.DistanceRoot = distanceGO;
        ui.DistanceRoot.SetActive(waypoint.ShowDistance);
        ui.LastShowDistance = waypoint.ShowDistance;

        return ui;
    }

    /// <summary>Built-in font lookup that works across Unity versions (LegacyRuntime since 2022, Arial before).</summary>
    private static Font LoadBuiltinFont()
    {
        Font font = null;
        try { font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
        if (font == null)
        {
            try { font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
        }
        return font;
    }
}
