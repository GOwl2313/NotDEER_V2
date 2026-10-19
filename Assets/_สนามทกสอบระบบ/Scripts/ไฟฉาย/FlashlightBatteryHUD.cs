using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The flashlight BATTERY BAR HUD (v1.1).
///
/// WHAT IT SHOWS
///   A green bar with a WHITE FRAME around it, pinned to the BOTTOM-RIGHT
///   corner of the player's screen. As flashlight power is used, the green
///   bar SHRINKS TOWARD THE RIGHT SIDE (the last sliver of green sits in the
///   corner — switch "Shrink Direction" to Left if you prefer the opposite).
///   When the battery is fully out the frame stays with a dark empty interior.
///
///   Optional warning colours: the bar turns YELLOW when low and RED +
///   flashing when critical (green -> yellow -> red). All adjustable below.
///
/// HOW TO USE — TWO WAYS
///
///   A. ZERO SETUP (unchanged): FlashlightController v2.1 creates this HUD
///      automatically when the game starts (its "Auto Create Battery HUD"
///      option, ON by default — and it never duplicates a HUD that already
///      exists in the scene). Just keep this file in the project next to
///      FlashlightController.cs.
///
///   B. EDITABLE IN THE EDITOR (new in v1.1 — move & scale the bar WITHOUT
///      touching code): right-click the FlashlightController component header
///      in the Inspector and choose
///      "Battery: Create Editable HUD In Scene (editor)".
///      A permanent "Flashlight Battery HUD" object appears in the scene.
///      In the Hierarchy select its "BatteryBar" child and then:
///        - MOVE it:    drag it with the Scene view Move / Rect tool, or edit
///                      the RectTransform Anchored Position in the Inspector;
///        - SCALE it:   the Scale tool (or the Rect tool's corner handles)
///                      scales the whole bar, frame included;
///        - RESIZE it:  the RectTransform Width / Height fields;
///        - anchors / pivot changes work too — the bar simply follows.
///      The layout is SAVED WITH THE SCENE and automatically reused at Play.
///
///      This component runs in EDIT MODE too ([ExecuteAlways]), so Inspector
///      changes (colours, frame thickness, thresholds, the Editor Preview
///      battery level) apply LIVE in the editor — no Play mode needed. The
///      "Bar Size" fields below are the INITIAL size used when the bar is
///      first created; afterwards the "BatteryBar" RectTransform is the
///      source of truth. Two context menus on this component re-apply the
///      initial values on demand ("Reset Size", "Re-Pin To Corner").
///
///      You can also add this component by hand to any active GameObject
///      (with or without a Canvas) — it finds the flashlight on its own.
///
/// The whole bar is built from code — no sprites, fonts or scene objects
/// needed. It is purely visual: it never blocks mouse clicks.
/// </summary>
[DisallowMultipleComponent]
[ExecuteAlways]   // v1.1: build, preview and keep the bar in sync in EDIT mode as well
public class FlashlightBatteryHUD : MonoBehaviour
{
    /// <summary>Which screen edge the remaining green collapses toward while draining.</summary>
    public enum ShrinkToward { Right, Left }

    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Flashlight")]
    [Tooltip("The flashlight whose battery this bar displays. Auto-found when left empty.")]
    [SerializeField] private FlashlightController flashlightController;

    [Header("Placement (initial — when the HUD is created)")]
    [Tooltip("INITIAL distance from the screen's bottom-right corner to the bar when it is created. " +
             "Afterwards just MOVE the 'BatteryBar' object — your layout is saved with the scene.")]
    [SerializeField] private float edgeMargin = 24f;

    [Tooltip("Sorting order of the HUD canvas (applied when this HUD creates its own canvas). " +
             "200 = far below the pause menu (32767), far above the world. A canvas saved with the scene is edited directly on its Canvas component instead.")]
    [SerializeField] private int canvasSortingOrder = 200;

    [Header("Bar Size (initial — then edit the RectTransform)")]
    [Tooltip("INITIAL total width of the bar (including the white frame) when it is created. " +
             "Afterwards resize the 'BatteryBar' RectTransform in the editor — that is the source of truth.")]
    [SerializeField] private float barWidth = 220f;

    [Tooltip("INITIAL total height of the bar (including the white frame) when it is created. " +
             "Afterwards resize the 'BatteryBar' RectTransform in the editor.")]
    [SerializeField] private float barHeight = 16f;

    [Header("White Frame (applies live)")]
    [Tooltip("Thickness of the WHITE FRAME drawn around the green bar.")]
    [SerializeField] private float frameThickness = 3f;

    [Tooltip("Colour of the frame around the bar (white by default).")]
    [SerializeField] private Color frameColor = Color.white;

    [Header("Colours (apply live)")]
    [Tooltip("Colour of the bar while the battery is healthy (green).")]
    [SerializeField] private Color fillColorFull = new Color(0.19f, 0.85f, 0.37f, 1f);

    [Tooltip("Colour of the empty space behind the green fill (dark).")]
    [SerializeField] private Color emptyBackgroundColor = new Color(0f, 0f, 0f, 0.55f);

    [Tooltip("Warning colours as the battery runs out: green -> yellow -> red. OFF = stays green until empty.")]
    [SerializeField] private bool useLowBatteryColours = true;

    [Tooltip("Bar colour when the battery is LOW (yellow).")]
    [SerializeField] private Color fillColorLow = new Color(0.95f, 0.78f, 0.25f, 1f);

    [Tooltip("Bar colour when the battery is CRITICAL (red).")]
    [SerializeField] private Color fillColorCritical = new Color(0.92f, 0.25f, 0.21f, 1f);

    [Tooltip("Battery fraction below which the bar counts as LOW (yellow).")]
    [SerializeField, Range(0.05f, 0.95f)] private float lowThreshold = 0.3f;

    [Tooltip("Battery fraction below which the bar counts as CRITICAL (red + flashing).")]
    [SerializeField, Range(0.01f, 0.5f)] private float criticalThreshold = 0.15f;

    [Tooltip("Pulse the bar's brightness while the battery is critical, so the player knows to hurry.")]
    [SerializeField] private bool flashWhenCritical = true;

    [Tooltip("Flashes per second while critical.")]
    [SerializeField] private float flashSpeed = 2.5f;

    [Header("Behaviour")]
    [Tooltip("RIGHT (default) = the green bar shrinks TOWARD the right side: the empty gap grows on the left and the last sliver of green stays in the bottom-right corner. LEFT = the opposite.")]
    [SerializeField] private ShrinkToward shrinkDirection = ShrinkToward.Right;

    [Tooltip("Hide the bar when the flashlight has its battery system switched off (instead of showing a full bar).")]
    [SerializeField] private bool hideWhenBatteryDisabled = true;

    [Tooltip("Smooth the bar length instead of it jumping with each drain tick.")]
    [SerializeField] private bool smoothFill = true;

    [Tooltip("How quickly the shown value catches up with the real battery level (higher = snappier).")]
    [SerializeField] private float fillSmoothing = 10f;

    [Header("Editor Preview (edit mode only)")]
    [Tooltip("EDIT-MODE PREVIEW ONLY: battery level the bar shows in the editor while NOT playing — " +
             "set it low to preview the yellow / red flashing states while tuning colours. " +
             "Completely ignored at runtime.")]
    [SerializeField, Range(0f, 1f)] private float editorPreviewBatteryLevel = 1f;

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private Canvas canvas;
    private bool ownsCanvas;          // true when we created the canvas object ourselves (safe to destroy later)
    private RectTransform barRoot;    // the whole bar (frame + background + fill) — THE user-editable object
    private RectTransform fillRect;   // the green fill
    private Image frameImage;         // the white frame
    private Image backgroundImage;    // the dark empty background
    private Image fillImage;
    private float displayedFraction = 1f;
    private float innerWidth;         // usable width inside the frame
    private float innerHeight;        // usable height inside the frame
    private bool everBound;           // we had a controller at least once (destroy when it goes away)
    private float searchTimer;        // retry cadence while no flashlight is found yet
    private float unboundTime;        // how long we have been waiting for a flashlight
    private bool warnedMalformedBar;  // one-time warning if the BatteryBar child is not a UI object

    /// <summary>Percentage of battery shown last frame (0..1) — handy for debugging.</summary>
    public float DisplayedFraction { get { return displayedFraction; } }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        BuildCanvas();

        // Find-or-create the bar. A bar SAVED WITH THE SCENE (created with the
        // editor action, or left from a previous edit-mode session) is reused
        // EXACTLY as the user positioned/scaled it — never re-created, never
        // reset. Only a missing bar is built with the initial values.
        EnsureBar();

        // The runtime auto-created instance stays hidden until a flashlight is
        // bound moments later; a scene-saved bar keeps whatever active state
        // the user left it in (edit mode = visible and editable).
        if (Application.isPlaying)
            HideBar();
    }

    private void Start()
    {
        TryResolveController();
    }

    private void Update()
    {
        // ---- Self-heal: if a child was deleted in the editor, rebuild it
        //      (the user's BatteryBar RectTransform edits are preserved). ----
        if (barRoot == null || frameImage == null || backgroundImage == null ||
            fillImage == null || fillRect == null)
        {
            EnsureBar();
        }

        if (barRoot == null || frameImage == null || backgroundImage == null ||
            fillImage == null || fillRect == null)
        {
            return;   // still malformed (see the warning in EnsureBar) — retry next tick
        }

        // ---- Layout sync: the BatteryBar RectTransform (position / size /
        //      scale / anchors) is the single source of truth; the frame,
        //      background and fill follow it. All writes are equality-guarded
        //      so the editor is not dirtied when nothing changed. ----
        SyncBarLayout();

#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            // ---- EDIT MODE: live preview. The bar never drains here — it
            //      shows the Editor Preview battery level so colours, sizes
            //      and the layout can be tuned without entering Play. ----
            float preview = Mathf.Clamp01(editorPreviewBatteryLevel);
            displayedFraction = preview;

            Vector2 previewSize = new Vector2(innerWidth * preview, innerHeight);
            if (fillRect.sizeDelta != previewSize) fillRect.sizeDelta = previewSize;

            Color previewColour = ComputeBarColour(preview);
            if (fillImage.color != previewColour) fillImage.color = previewColour;
            return;
        }
#endif

        FlashlightController controller = flashlightController;

        if (controller == null)
        {
            // Our flashlight was destroyed mid-game (scene teardown) — remove the HUD.
            if (everBound)
            {
                CleanUp();
                return;
            }

            // No flashlight yet: keep looking quietly for a while, then give up.
            unboundTime += Time.unscaledDeltaTime;
            searchTimer += Time.unscaledDeltaTime;
            if (searchTimer >= 0.5f)
            {
                searchTimer = 0f;
                TryResolveController();

                if (flashlightController == null && unboundTime >= 30f)
                {
                    Debug.LogWarning("[FlashlightBatteryHUD] No FlashlightController found in this scene after 30s — " +
                                     "removing the battery HUD. (It is created automatically by FlashlightController; " +
                                     "only place this component manually on scenes that really have a flashlight.)", this);
                    CleanUp();
                }
            }
            return;
        }

        // Battery system switched off on the flashlight?
        bool show = controller.UsesBattery || !hideWhenBatteryDisabled;
        if (barRoot.gameObject.activeSelf != show)
            barRoot.gameObject.SetActive(show);
        if (!show)
            return;

        // ---- Bar length ----
        float target = controller.BatteryLevel01;

        if (smoothFill)
        {
            float blend = 1f - Mathf.Exp(-fillSmoothing * Time.deltaTime);
            displayedFraction = Mathf.Lerp(displayedFraction, target, blend);
            if (Mathf.Abs(displayedFraction - target) < 0.001f)
                displayedFraction = target;   // snap when close enough (no endless creep)
        }
        else
        {
            displayedFraction = target;
        }

        Vector2 fillSize = new Vector2(innerWidth * Mathf.Clamp01(displayedFraction), innerHeight);
        if (fillRect.sizeDelta != fillSize) fillRect.sizeDelta = fillSize;

        // ---- Bar colour ----
        Color barColour = ComputeBarColour(target);
        if (fillImage.color != barColour) fillImage.color = barColour;
    }

    /// <summary>Keeps Inspector values sane. Colour / frame / threshold edits apply live (edit mode included).</summary>
    private void OnValidate()
    {
        barWidth = Mathf.Max(40f, barWidth);
        barHeight = Mathf.Max(6f, barHeight);
        frameThickness = Mathf.Clamp(frameThickness, 1f, Mathf.Min(barWidth, barHeight) * 0.25f);
        edgeMargin = Mathf.Max(0f, edgeMargin);
        lowThreshold = Mathf.Clamp(lowThreshold, 0.05f, 0.95f);
        if (criticalThreshold >= lowThreshold)
            criticalThreshold = Mathf.Max(0.01f, lowThreshold * 0.5f);
        fillSmoothing = Mathf.Max(0f, fillSmoothing);
        flashSpeed = Mathf.Max(0.5f, flashSpeed);
        editorPreviewBatteryLevel = Mathf.Clamp01(editorPreviewBatteryLevel);

        // live-apply the sorting order to a canvas we own (a saved/user canvas
        // is edited directly on its Canvas component instead)
        if (ownsCanvas && canvas != null && canvas.sortingOrder != canvasSortingOrder)
            canvas.sortingOrder = canvasSortingOrder;
    }

    // ------------------------------------------------------------------ //
    // Public API
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Binds this HUD to a flashlight. Called automatically by FlashlightController
    /// when it creates the HUD; safe to call from your own code too.
    /// </summary>
    public void Bind(FlashlightController controller)
    {
        if (controller == null) return;

        flashlightController = controller;
        everBound = true;
        displayedFraction = controller.BatteryLevel01;
        ShowBar();
    }

    // ------------------------------------------------------------------ //
    // Binding
    // ------------------------------------------------------------------ //

    private void TryResolveController()
    {
        if (flashlightController != null)
        {
            Bind(flashlightController);
            return;
        }

#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
        flashlightController = FindAnyObjectByType<FlashlightController>();
#else
        flashlightController = FindObjectOfType<FlashlightController>();
#endif
        if (flashlightController != null)
            Bind(flashlightController);
    }

    private void CleanUp()
    {
        if (ownsCanvas)
        {
            Destroy(gameObject);   // our own canvas object — remove it entirely
        }
        else
        {
            if (barRoot != null) Destroy(barRoot.gameObject);
            enabled = false;       // parked on someone else's object — remove only our bar and go inert
        }
    }

    // ------------------------------------------------------------------ //
    // UI construction (code-only, no assets)
    // ------------------------------------------------------------------ //

    private void BuildCanvas()
    {
        canvas = GetComponent<Canvas>();
        ownsCanvas = canvas == null;

        if (ownsCanvas)
        {
            canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = canvasSortingOrder;

            CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
        }
        // If this component was placed on an object that already has a Canvas
        // (or the canvas was saved with the scene), we reuse it untouched — no
        // sorting order stomping, no extra scaler.
    }

    /// <summary>
    /// Finds the "BatteryBar" child (with its Frame / EmptyBackground / Fill)
    /// or creates it with the INITIAL size/position values. A saved bar is
    /// reused exactly as the user left it — this never resets user edits, and
    /// never duplicates the bar on scene reloads.
    /// </summary>
    private bool EnsureBar()
    {
        Transform barChild = transform.Find("BatteryBar");

        if (barChild == null)
        {
            // ---- first creation: build the bar with the initial values ----
            GameObject barGO = new GameObject("BatteryBar", typeof(RectTransform));
            barGO.transform.SetParent(transform, false);
            barRoot = (RectTransform)barGO.transform;

            barRoot.anchorMin = new Vector2(1f, 0f);                    // bottom-right corner of the canvas
            barRoot.anchorMax = new Vector2(1f, 0f);
            barRoot.pivot = new Vector2(1f, 0f);                        // our own bottom-right is the reference point
            barRoot.anchoredPosition = new Vector2(-edgeMargin, edgeMargin);
            barRoot.sizeDelta = new Vector2(barWidth, barHeight);
        }
        else
        {
            barRoot = barChild as RectTransform;
            if (barRoot == null)
            {
                if (!warnedMalformedBar)
                {
                    warnedMalformedBar = true;
                    Debug.LogWarning("[FlashlightBatteryHUD] The 'BatteryBar' child of '" + name + "' is not a UI " +
                                     "RectTransform. Remove it (or fix it) and the HUD will rebuild a fresh bar on " +
                                     "its next update.", this);
                }
                return false;
            }
            // saved/user-edited instance: keep its RectTransform EXACTLY as it
            // was left (position, size, scale, anchors) — it is the source of truth.
        }

        frameImage = FindOrCreateImage("Frame", barRoot);
        backgroundImage = FindOrCreateImage("EmptyBackground", barRoot);
        fillImage = FindOrCreateImage("Fill", barRoot);
        fillRect = fillImage.rectTransform;

        // initial look (existing saved children keep their colours — the
        // per-update SyncBarLayout refreshes them anyway)
        frameImage.color = frameColor;
        backgroundImage.color = emptyBackgroundColor;
        fillImage.color = fillColorFull;

        innerWidth = Mathf.Max(1f, barWidth - frameThickness * 2f);
        innerHeight = Mathf.Max(1f, barHeight - frameThickness * 2f);

        return true;
    }

    /// <summary>Reuse a saved child Image when it exists; create it only when missing (self-heal).</summary>
    private static Image FindOrCreateImage(string childName, RectTransform parent)
    {
        Transform existing = parent.Find(childName);
        if (existing != null)
        {
            Image image = existing.GetComponent<Image>();
            if (image != null)
                return image;   // saved child — reuse exactly as saved
        }

        GameObject go = new GameObject(childName, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        Image created = go.AddComponent<Image>();
        created.raycastTarget = false;   // pure display — never blocks clicks
        return created;
    }

    /// <summary>
    /// Re-lays-out the frame / background / fill from the CURRENT BatteryBar
    /// RectTransform. Runs every update (edit mode included): whatever the
    /// user does to the bar — move, scale, resize, re-anchor — the visuals
    /// follow instantly. All writes are equality-guarded, so untouched
    /// values never dirty the scene.
    /// </summary>
    private void SyncBarLayout()
    {
        if (barRoot == null || fillRect == null) return;

        Rect barRect = barRoot.rect;
        innerWidth = Mathf.Max(1f, barRect.width - frameThickness * 2f);
        innerHeight = Mathf.Max(1f, barRect.height - frameThickness * 2f);

        // ---- WHITE FRAME: stretched across the whole bar ----
        ApplyStretch(frameImage.rectTransform, 0f);
        if (frameImage.color != frameColor) frameImage.color = frameColor;

        // ---- DARK EMPTY BACKGROUND: inset by the frame thickness ----
        ApplyStretch(backgroundImage.rectTransform, frameThickness);
        if (backgroundImage.color != emptyBackgroundColor) backgroundImage.color = emptyBackgroundColor;

        // ---- GREEN FILL: pinned to one side so the green collapses toward
        //      the corner as power is used. ShrinkToward.Right: anchor + pivot
        //      on the RIGHT edge, the fill extends leftward — the empty gap
        //      grows on the left and the last sliver of green stays in the
        //      bottom-right corner. Left mode mirrors it. ----
        float anchorX = (shrinkDirection == ShrinkToward.Right) ? 1f : 0f;
        Vector2 fillAnchor = new Vector2(anchorX, 0.5f);
        if (fillRect.anchorMin != fillAnchor) fillRect.anchorMin = fillAnchor;
        if (fillRect.anchorMax != fillAnchor) fillRect.anchorMax = fillAnchor;
        if (fillRect.pivot != fillAnchor) fillRect.pivot = fillAnchor;

        Vector2 fillPos = new Vector2(anchorX == 1f ? -frameThickness : frameThickness, 0f);
        if (fillRect.anchoredPosition != fillPos) fillRect.anchoredPosition = fillPos;
    }

    /// <summary>Stretches a RectTransform across its parent, optionally inset (no write when unchanged).</summary>
    private static void ApplyStretch(RectTransform rt, float inset)
    {
        if (rt.anchorMin != Vector2.zero) rt.anchorMin = Vector2.zero;
        if (rt.anchorMax != Vector2.one) rt.anchorMax = Vector2.one;

        Vector2 offsetMin = new Vector2(inset, inset);
        Vector2 offsetMax = new Vector2(-inset, -inset);
        if (rt.offsetMin != offsetMin) rt.offsetMin = offsetMin;
        if (rt.offsetMax != offsetMax) rt.offsetMax = offsetMax;
    }

    private void ShowBar()
    {
        if (barRoot != null) barRoot.gameObject.SetActive(true);
    }

    private void HideBar()
    {
        if (barRoot != null) barRoot.gameObject.SetActive(false);
    }

    // ------------------------------------------------------------------ //
    // Colour logic
    // ------------------------------------------------------------------ //

    private Color ComputeBarColour(float fraction)
    {
        Color colour = fillColorFull;

        if (useLowBatteryColours)
        {
            if (fraction <= criticalThreshold)
            {
                colour = fillColorCritical;
            }
            else if (fraction <= lowThreshold)
            {
                // smooth yellow -> red between the two thresholds
                float t = Mathf.Clamp01((lowThreshold - fraction) / Mathf.Max(0.0001f, lowThreshold - criticalThreshold));
                colour = Color.Lerp(fillColorLow, fillColorCritical, t);
            }
        }

        // critical: pulse the brightness so the player knows to hurry
        // (skipped at exactly 0 — an empty bar is invisible anyway)
        if (flashWhenCritical && fraction > 0f && fraction <= criticalThreshold)
        {
            float pulse = 0.45f + 0.55f * (0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * flashSpeed * (2f * Mathf.PI)));
            colour *= pulse;
        }

        return colour;
    }

    // ------------------------------------------------------------------ //
    // Context menu helpers (right-click this component header)
    // ------------------------------------------------------------------ //

    [ContextMenu("Bar: Reset Size To Inspector Values")]
    private void ResetBarSize()
    {
        if (barRoot == null) EnsureBar();
        if (barRoot == null) return;

        barRoot.sizeDelta = new Vector2(barWidth, barHeight);
    }

    [ContextMenu("Bar: Re-Pin To Bottom-Right Corner")]
    private void RePinBarToCorner()
    {
        if (barRoot == null) return;

        barRoot.anchorMin = new Vector2(1f, 0f);
        barRoot.anchorMax = new Vector2(1f, 0f);
        barRoot.pivot = new Vector2(1f, 0f);
        barRoot.anchoredPosition = new Vector2(-edgeMargin, edgeMargin);
    }
}