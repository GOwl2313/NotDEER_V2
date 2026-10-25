using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The flashlight BATTERY PANEL for VR (v1.0) — a world-space Canvas "plate"
/// attached directly to the FLASHLIGHT (the screen-corner HUD is useless in VR,
/// where Screen Space Overlay canvases never render into the headset).
///
/// WHAT IT LOOKS LIKE
///   A small dark plate (about the size of a postage stamp, sized in METRES)
///   floating just behind the flashlight's light origin, carrying the same
///   bar as the screen HUD: a green bar with a WHITE FRAME that shrinks as
///   the battery drains (green -> yellow -> red, flashing when critical).
///   The plate moves with the flashlight because it is a CHILD of it, so it
///   follows the player's hand everywhere.
///
/// BILLBOARD
///   "Billboard" (ON by default) keeps the plate POSITIONED on the flashlight
///   but rotates it to face the headset's camera every frame, so the bar is
///   readable at any hand pose. Turn it OFF to lock the plate to the
///   flashlight's local "Local Euler" rotation instead (a fully diegetic
///   meter painted on the torch body).
///
/// HOW TO USE
///   A. ZERO SETUP (this scene): the component sits on the flashlight
///      GameObject in the "VR version" scene and binds itself — nothing
///      else to configure. FlashlightController's "Auto Create Battery HUD"
///      is switched OFF in that scene so the old screen-corner HUD is not
///      duplicated (it is invisible in VR anyway).
///   B. Any other scene: add this component to ANY GameObject — it
///      auto-finds the flashlight (same object first, then the scene).
///
/// TUNING
///   Layout is driven purely by the Inspector fields (position offset,
///   rotation, plate size, padding, colours, thresholds) and applies LIVE
///   during Play mode — tweak the numbers with the headset on and copy the
///   final values back into the component when happy.
///
/// TECH NOTES
///   - The plate is built entirely from code (no sprites, fonts, prefabs).
///   - World-space canvas: 1 unit = 1 metre, so sizes are real-world CM.
///   - Nothing is created in EDIT mode (the scene stays exactly as saved);
///     the plate exists only while playing.
///   - Every visual is raycastTarget = false: VR interaction rays pass
///     straight through, the plate can never "eat" a click or grab.
///   - Purely visual: independent from the enemy scripts, references only
///     FlashlightController (same decoupled design as the screen HUD).
/// </summary>
[DisallowMultipleComponent]
[ExecuteAlways]   // so OnValidate clamps keep Inspector edits sane in edit mode too
public class FlashlightBatteryWorldHUD : MonoBehaviour
{
    /// <summary>Which side the remaining green collapses toward while draining.</summary>
    public enum ShrinkToward { Right, Left }

    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Flashlight")]
    [Tooltip("The flashlight whose battery this plate displays. " +
             "Auto-found on this object first, then in the scene.")]
    [SerializeField] private FlashlightController flashlightController;

    [Header("Panel Layout (metres, flashlight LOCAL space)")]
    [Tooltip("Plate position in the flashlight's local space. " +
             "The flashlight's local +Z is the beam direction. Default centres the " +
             "plate ON TOP of the handle: the model's grip is about 19 cm long " +
             "(z −0.10…+0.09, top surface y ≈ +0.04), so y = 0.043 floats the plate " +
             "just above the grip and z = −0.22 puts it mid-grip relative to the " +
             "light origin (which sits 0.22 m into the model).")]
    [SerializeField] private Vector3 localOffset = new Vector3(0f, 0.043f, -0.22f);

    [Tooltip("Plate rotation (degrees) used when Billboard is OFF — the plate is " +
             "then locked to the flashlight body. Default (−90, 0, 90) lies the plate " +
             "flat facing UP with its LONG side running PARALLEL to the handle. " +
             "Ignored while Billboard is on.")]
    [SerializeField] private Vector3 localEuler = new Vector3(-90f, 0f, 90f);

    [Tooltip("Width of the battery bar itself, in metres (0.17 = 17 cm along the handle).")]
    [SerializeField] private float panelWidth = 0.17f;

    [Tooltip("Height of the battery bar itself, in metres (0.06 = 6 cm across the handle).")]
    [SerializeField] private float panelHeight = 0.06f;

    [Tooltip("Dark margin added around the bar so the plate reads as a physical panel (metres).")]
    [SerializeField] private float panelPadding = 0.005f;

    [Tooltip("Colour of the plate behind the bar (dark translucent plastic).")]
    [SerializeField] private Color panelBackgroundColor = new Color(0.02f, 0.02f, 0.02f, 0.72f);

    [Header("Billboard (runtime)")]
    [Tooltip("ON = the plate stays attached to the flashlight but always ROTATES to face " +
             "the headset camera (readable at any hand pose). OFF = the plate is locked " +
             "to the flashlight's Local Euler rotation like a painted-on meter. " +
             "Default OFF: the plate is a physical panel stuck to the top of the handle.")]
    [SerializeField] private bool billboard = false;

    [Header("Edit-Mode Preview (no Play required)")]
    [Tooltip("ON = build and show the plate in the EDITOR (Scene view) without pressing Play. The preview " +
             "is transient: never saved into the scene (HideFlags.DontSave), removed when this is switched " +
             "off, the component is deleted, or the scene is closed.")]
    [SerializeField] private bool editModePreview = false;

    [Tooltip("Cosmetic battery level shown by the preview only (0 = empty, 1 = full). The real battery " +
             "only runs in Play mode.")]
    [SerializeField, Range(0f, 1f)] private float editModePreviewLevel = 1f;

    [Header("White Frame (applies live)")]
    [Tooltip("Thickness of the WHITE FRAME drawn around the green bar (metres).")]
    [SerializeField] private float frameThickness = 0.0035f;

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
    [Tooltip("RIGHT (default) = the green bar shrinks TOWARD the right edge: the empty gap " +
             "grows on the left and the last sliver of green stays at the right end. LEFT = the opposite.")]
    [SerializeField] private ShrinkToward shrinkDirection = ShrinkToward.Right;

    [Tooltip("Hide the whole plate when the flashlight has its battery system switched off " +
             "(instead of showing a full bar).")]
    [SerializeField] private bool hideWhenBatteryDisabled = true;

    [Tooltip("Smooth the bar length instead of it jumping with each drain tick.")]
    [SerializeField] private bool smoothFill = true;

    [Tooltip("How quickly the shown value catches up with the real battery level (higher = snappier).")]
    [SerializeField] private float fillSmoothing = 10f;

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private Canvas panelCanvas;
    private RectTransform panelRoot;   // the plate (canvas root) — child of the flashlight
    private RectTransform barRoot;     // the bar inside the plate
    private RectTransform fillRect;    // the green fill
    private Image plateImage;          // the dark translucent backing plate
    private Image frameImage;          // the white frame
    private Image backgroundImage;     // the dark empty background
    private Image fillImage;
    private float displayedFraction = 1f;
    private float innerWidth;          // usable width inside the frame
    private float innerHeight;         // usable height inside the frame
    private bool everBound;            // we had a controller at least once
    private bool previewOwned;         // WE created the current panel in EDIT mode (safe to delete)
    private float searchTimer;         // retry cadence while no flashlight is found yet
    private float unboundTime;         // how long we have been waiting for a flashlight
    private Transform billboardCamera; // the headset / game camera the plate faces
    private float cameraSearchTimer;

    /// <summary>Percentage of battery shown last frame (0..1) — handy for debugging.</summary>
    public float DisplayedFraction { get { return displayedFraction; } }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        // Play mode: make sure the plate exists (and if an edit-mode preview
        // panel survived into play, absorb it as a normal object). Edit-mode
        // OnValidate still clamps values; the preview is built by Update.
        if (!Application.isPlaying) return;

        EnsurePanel();
        if (panelRoot != null) ClearPreviewHideFlags(panelRoot);
    }

    private void Start()
    {
        if (!Application.isPlaying) return;

        TryResolveController();
    }

    private void Update()
    {
        if (!Application.isPlaying)
        {
            TickEditModePreview();
            return;
        }

        // ---- Self-heal: if the plate or a child was destroyed at runtime,
        //      rebuild it from the Inspector values. ----
        if (panelRoot == null || barRoot == null || plateImage == null || frameImage == null ||
            backgroundImage == null || fillImage == null || fillRect == null)
        {
            EnsurePanel();
        }

        if (panelRoot == null || barRoot == null || plateImage == null || frameImage == null ||
            backgroundImage == null || fillImage == null || fillRect == null)
        {
            return;   // still malformed — retry next tick
        }

        FlashlightController controller = flashlightController;

        if (controller == null)
        {
            // Our flashlight was destroyed mid-game (scene teardown) — remove the plate.
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
                    Debug.LogWarning("[FlashlightBatteryWorldHUD] No FlashlightController found after 30s — " +
                                     "removing the battery panel. (Place this component on a scene that really " +
                                     "has a flashlight.)", this);
                    CleanUp();
                }
            }
            return;
        }

        // ---- Plate sticks to the flashlight: position + scale follow the
        //      Inspector values (the plate is a CHILD, so hand movement is
        //      inherited automatically — we only enforce the local layout). ----
        if (panelRoot.localPosition != localOffset)
            panelRoot.localPosition = localOffset;
        if (panelRoot.localScale != Vector3.one)
            panelRoot.localScale = Vector3.one;

        if (!billboard && panelRoot.localRotation != Quaternion.Euler(localEuler))
            panelRoot.localRotation = Quaternion.Euler(localEuler);

        // ---- Visual sync from the Inspector values ----
        SyncPanelLayout();

        // Battery system switched off on the flashlight?
        bool show = controller.UsesBattery || !hideWhenBatteryDisabled;
        if (panelRoot.gameObject.activeSelf != show)
            panelRoot.gameObject.SetActive(show);
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

    private void LateUpdate()
    {
        // Billboard: keep facing the headset camera. Runs in LateUpdate so the
        // freshest camera pose of the frame is used (XR moves the centre eye late).
        if (!Application.isPlaying || !billboard || panelRoot == null) return;

        if (billboardCamera == null)
        {
            cameraSearchTimer += Time.unscaledDeltaTime;
            if (cameraSearchTimer >= 0.5f)
            {
                cameraSearchTimer = 0f;
                FindBillboardCamera();
            }
        }

        if (billboardCamera != null)
        {
            // Parallel to the headset's view plane — always perfectly readable.
            if (panelRoot.rotation != billboardCamera.rotation)
                panelRoot.rotation = billboardCamera.rotation;
        }
        else if (panelRoot.localRotation != Quaternion.Euler(localEuler))
        {
            // No camera yet (or none at all): fall back to the authored rotation.
            panelRoot.localRotation = Quaternion.Euler(localEuler);
        }
    }

    private void OnDestroy()
    {
        // Edit mode only: a plate WE built as a preview is removed with us.
        // (Saved/user-made plates are never touched — previewOwned is false.)
        if (!Application.isPlaying && previewOwned && panelRoot != null)
        {
            DestroyImmediate(panelRoot.gameObject);
            previewOwned = false;
        }
    }

    /// <summary>Keeps Inspector values sane. Colours / frame / thresholds apply live.</summary>
    private void OnValidate()
    {
        panelWidth = Mathf.Max(0.02f, panelWidth);
        panelHeight = Mathf.Max(0.008f, panelHeight);
        panelPadding = Mathf.Max(0f, panelPadding);
        frameThickness = Mathf.Clamp(frameThickness, 0.0005f, Mathf.Min(panelWidth, panelHeight) * 0.35f);
        lowThreshold = Mathf.Clamp(lowThreshold, 0.05f, 0.95f);
        if (criticalThreshold >= lowThreshold)
            criticalThreshold = Mathf.Max(0.01f, lowThreshold * 0.5f);
        fillSmoothing = Mathf.Max(0f, fillSmoothing);
        flashSpeed = Mathf.Max(0.5f, flashSpeed);
    }

    // ------------------------------------------------------------------ //
    // Public API
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Binds this panel to a flashlight. Called automatically on Start; safe
    /// to call from your own code too (e.g. a hand-spawned flashlight).
    /// </summary>
    public void Bind(FlashlightController controller)
    {
        if (controller == null) return;

        flashlightController = controller;
        everBound = true;
        displayedFraction = controller.BatteryLevel01;

        if (panelRoot != null) panelRoot.gameObject.SetActive(true);

        Debug.Log("[FlashlightBatteryWorldHUD] Battery panel attached to '" + controller.name + "' " +
                  "(world-space canvas, billboard " + (billboard ? "ON" : "OFF") +
                  ", plate " + (panelWidth + panelPadding * 2f).ToString("0.000") + " x " +
                  (panelHeight + panelPadding * 2f).ToString("0.000") + " m).", this);
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

        // Same object first (the usual setup — this component sits on the
        // flashlight GameObject), then anywhere in the scene.
        flashlightController = GetComponent<FlashlightController>();

#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
        if (flashlightController == null)
            flashlightController = FindAnyObjectByType<FlashlightController>();
#else
        if (flashlightController == null)
            flashlightController = FindObjectOfType<FlashlightController>();
#endif
        if (flashlightController != null)
            Bind(flashlightController);
    }

    /// <summary>Camera the billboard faces: Main Camera (the XR centre eye in XRI rigs) first, then any active camera.</summary>
    private void FindBillboardCamera()
    {
        Camera main = Camera.main;
        if (main != null)
        {
            billboardCamera = main.transform;
            return;
        }

#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
        Camera any = FindAnyObjectByType<Camera>();
#else
        Camera any = FindObjectOfType<Camera>();
#endif
        billboardCamera = any != null ? any.transform : null;
    }

    private void CleanUp()
    {
        if (panelRoot != null) Destroy(panelRoot.gameObject);
        enabled = false;   // go inert — the flashlight itself is never touched
    }

    // ------------------------------------------------------------------ //
    // UI construction (code-only, no assets)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Finds the "BatteryPanel" child (with its BatteryBar / Frame /
    /// EmptyBackground / Fill) or creates it. An existing plate is reused
    /// exactly as it is; only a missing plate is built.
    /// </summary>
    private void EnsurePanel()
    {
        Transform existing = transform.Find("BatteryPanel");

        if (existing != null)
        {
            panelRoot = existing as RectTransform;
            if (panelRoot == null)
            {
                // A non-UI object squatting on our name — remove it and rebuild.
                Debug.LogWarning("[FlashlightBatteryWorldHUD] The 'BatteryPanel' child of '" + name +
                                 "' is not a RectTransform — rebuilding the plate.", this);
                Destroy(existing.gameObject);
            }
            else
            {
                panelCanvas = panelRoot.GetComponent<Canvas>();
            }
        }

        if (panelRoot == null)
        {
            GameObject panelGO = new GameObject("BatteryPanel", typeof(RectTransform), typeof(Canvas));
            panelGO.layer = gameObject.layer;
            panelGO.transform.SetParent(transform, false);
            panelRoot = (RectTransform)panelGO.transform;

            panelCanvas = panelGO.GetComponent<Canvas>();
            panelCanvas.renderMode = RenderMode.WorldSpace;

            // NOTE: deliberately NO CanvasScaler (world space needs none) and
            // NO GraphicRaycaster (pure display — VR rays pass through).

            if (!Application.isPlaying) previewOwned = true;   // edit-mode preview: ours to clean up
        }

        // ---- PLATE (dark translucent backing) ----
        plateImage = FindOrCreateImage("PanelBackground", panelRoot);
        plateImage.color = panelBackgroundColor;

        // ---- BAR (same anatomy as the screen HUD) ----
        Transform barChild = panelRoot.Find("BatteryBar");
        if (barChild != null)
        {
            barRoot = barChild as RectTransform;
            if (barRoot == null)
            {
                Debug.LogWarning("[FlashlightBatteryWorldHUD] The 'BatteryBar' child is not a RectTransform — rebuilding it.", this);
                Destroy(barChild.gameObject);
            }
        }
        else
        {
            barRoot = null;
        }

        if (barRoot == null)
        {
            GameObject barGO = new GameObject("BatteryBar", typeof(RectTransform));
            barGO.transform.SetParent(panelRoot, false);
            barRoot = (RectTransform)barGO.transform;
        }

        frameImage = FindOrCreateImage("Frame", barRoot);
        backgroundImage = FindOrCreateImage("EmptyBackground", barRoot);
        fillImage = FindOrCreateImage("Fill", barRoot);
        fillRect = fillImage.rectTransform;

        frameImage.color = frameColor;
        backgroundImage.color = emptyBackgroundColor;
        fillImage.color = fillColorFull;

        SyncPanelLayout();
    }

    /// <summary>Reuse an existing child Image when it exists; create it only when missing (self-heal).</summary>
    private static Image FindOrCreateImage(string childName, RectTransform parent)
    {
        Transform existing = parent.Find(childName);

        if (existing != null)
        {
            Image image = existing.GetComponent<Image>();
            if (image == null)
            {
                image = existing.gameObject.AddComponent<Image>();
                image.raycastTarget = false;
            }
            return image;   // existing child — reuse exactly as it is
        }

        GameObject go = new GameObject(childName, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        Image created = go.AddComponent<Image>();
        created.raycastTarget = false;   // pure display — never blocks VR interaction
        return created;
    }

    /// <summary>
    /// Re-lays-out the plate / bar / frame / background / fill from the
    /// CURRENT Inspector values. Runs every update: whatever the fields say,
    /// the visuals follow instantly. All writes are equality-guarded so
    /// unchanged values never dirty anything.
    /// </summary>
    private void SyncPanelLayout()
    {
        if (panelRoot == null || barRoot == null || fillRect == null) return;

        // ---- PLATE: sized from bar + padding on both sides ----
        Vector2 plateSize = new Vector2(panelWidth + panelPadding * 2f, panelHeight + panelPadding * 2f);
        if (panelRoot.sizeDelta != plateSize) panelRoot.sizeDelta = plateSize;

        // ---- BAR: centred inside the plate ----
        Vector2 barSize = new Vector2(panelWidth, panelHeight);
        if (barRoot.sizeDelta != barSize) barRoot.sizeDelta = barSize;
        if (barRoot.anchorMin != new Vector2(0.5f, 0.5f)) barRoot.anchorMin = new Vector2(0.5f, 0.5f);
        if (barRoot.anchorMax != new Vector2(0.5f, 0.5f)) barRoot.anchorMax = new Vector2(0.5f, 0.5f);
        if (barRoot.pivot != new Vector2(0.5f, 0.5f)) barRoot.pivot = new Vector2(0.5f, 0.5f);
        if (barRoot.anchoredPosition != Vector2.zero) barRoot.anchoredPosition = Vector2.zero;

        innerWidth = Mathf.Max(0.001f, panelWidth - frameThickness * 2f);
        innerHeight = Mathf.Max(0.001f, panelHeight - frameThickness * 2f);

        // ---- DARK PLATE: stretched across the whole canvas root ----
        Stretch(plateImage.rectTransform, 0f);
        if (plateImage.color != panelBackgroundColor) plateImage.color = panelBackgroundColor;

        // ---- WHITE FRAME: stretched across the whole bar ----
        Stretch(frameImage.rectTransform, 0f);
        if (frameImage.color != frameColor) frameImage.color = frameColor;

        // ---- DARK EMPTY BACKGROUND: inset by the frame thickness ----
        Stretch(backgroundImage.rectTransform, frameThickness);
        if (backgroundImage.color != emptyBackgroundColor) backgroundImage.color = emptyBackgroundColor;

        // ---- GREEN FILL: pinned to one side so the green collapses toward
        //      that edge as power is used. ----
        float anchorX = (shrinkDirection == ShrinkToward.Right) ? 1f : 0f;
        Vector2 fillAnchor = new Vector2(anchorX, 0.5f);
        if (fillRect.anchorMin != fillAnchor) fillRect.anchorMin = fillAnchor;
        if (fillRect.anchorMax != fillAnchor) fillRect.anchorMax = fillAnchor;
        if (fillRect.pivot != fillAnchor) fillRect.pivot = fillAnchor;

        Vector2 fillPos = new Vector2(anchorX == 1f ? -frameThickness : frameThickness, 0f);
        if (fillRect.anchoredPosition != fillPos) fillRect.anchoredPosition = fillPos;

        if (fillImage.raycastTarget) fillImage.raycastTarget = false;
        if (frameImage.raycastTarget) frameImage.raycastTarget = false;
        if (backgroundImage.raycastTarget) backgroundImage.raycastTarget = false;
        if (plateImage.raycastTarget) plateImage.raycastTarget = false;
    }

    /// <summary>Stretches a RectTransform across its parent, optionally inset (no write when unchanged).</summary>
    private static void Stretch(RectTransform rt, float inset)
    {
        if (rt.anchorMin != Vector2.zero) rt.anchorMin = Vector2.zero;
        if (rt.anchorMax != Vector2.one) rt.anchorMax = Vector2.one;

        Vector2 offsetMin = new Vector2(inset, inset);
        Vector2 offsetMax = new Vector2(-inset, -inset);
        if (rt.offsetMin != offsetMin) rt.offsetMin = offsetMin;
        if (rt.offsetMax != offsetMax) rt.offsetMax = offsetMax;
    }

    // ------------------------------------------------------------------ //
    // Colour logic (identical rules to the screen HUD)
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
    // Edit-mode preview — the plate in the Scene view without pressing Play.
    // Never saved into the scene (HideFlags.DontSave). See the Inspector
    // "Edit-Mode Preview" toggle.
    // ------------------------------------------------------------------ //

    private void TickEditModePreview()
    {
        if (Application.isPlaying) return;

        if (editModePreview)
        {
            bool malformed = panelRoot == null || barRoot == null || plateImage == null || frameImage == null ||
                             backgroundImage == null || fillImage == null || fillRect == null;
            if (malformed) EnsurePanel();
            if (panelRoot == null) return;

            ApplyPreviewHideFlags(panelRoot);

            // lock the authored pose (billboard stays OFF in the editor)
            if (panelRoot.localPosition != localOffset) panelRoot.localPosition = localOffset;
            if (panelRoot.localScale != Vector3.one) panelRoot.localScale = Vector3.one;
            if (panelRoot.localRotation != Quaternion.Euler(localEuler))
                panelRoot.localRotation = Quaternion.Euler(localEuler);

            SyncPanelLayout();

            // cosmetic battery level for the preview (no battery runs in edit mode)
            float shown = Mathf.Clamp01(editModePreviewLevel);
            displayedFraction = shown;
            Vector2 fillSize = new Vector2(innerWidth * shown, innerHeight);
            if (fillRect.sizeDelta != fillSize) fillRect.sizeDelta = fillSize;
            Color barColour = ComputeBarColour(shown);
            if (fillImage.color != barColour) fillImage.color = barColour;

            if (!panelRoot.gameObject.activeSelf) panelRoot.gameObject.SetActive(true);
        }
        else if (previewOwned && panelRoot != null)
        {
            DestroyPreview();
        }
    }

    private void DestroyPreview()
    {
        if (panelRoot != null) DestroyImmediate(panelRoot.gameObject);
        // null every cached reference so the next tick rebuilds cleanly
        panelRoot = null;
        panelCanvas = null;
        barRoot = null;
        fillRect = null;
        plateImage = null;
        frameImage = null;
        backgroundImage = null;
        fillImage = null;
        previewOwned = false;
    }

    private static void ApplyPreviewHideFlags(Transform root)
    {
        if (root == null) return;
        root.hideFlags = HideFlags.DontSave;
        for (int i = 0; i < root.childCount; i++)
            ApplyPreviewHideFlags(root.GetChild(i));
    }

    private static void ClearPreviewHideFlags(Transform root)
    {
        if (root == null) return;
        root.hideFlags = HideFlags.None;
        for (int i = 0; i < root.childCount; i++)
            ClearPreviewHideFlags(root.GetChild(i));
    }

    // ------------------------------------------------------------------ //
    // Context menu helpers (right-click this component header)
    // ------------------------------------------------------------------ //

    [ContextMenu("Panel: Reset Layout To Defaults")]
    private void ResetLayoutToDefaults()
    {
        localOffset = new Vector3(0f, 0.043f, -0.22f);
        localEuler = new Vector3(-90f, 0f, 90f);
        panelWidth = 0.17f;
        panelHeight = 0.06f;
        panelPadding = 0.005f;
        billboard = false;
    }
}
