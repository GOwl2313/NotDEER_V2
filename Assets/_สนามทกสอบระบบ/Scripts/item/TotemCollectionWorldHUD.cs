using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// TOTEM COLLECTION PLATE (v1.0) — the "Totems collected" counter as a
/// world-space Canvas plate stuck to the FLASHLIGHT, next to the battery
/// plate (same diegetic style as FlashlightBatteryWorldHUD).
///
/// WHAT IT LOOKS LIKE
///   A small dark plate lying on the top of the flashlight body, just in
///   front of the battery bar (over the collar ring). It carries a row of
///   SEGMENT SLOTS — one per totem (4 by default). Each slot lights up gold
///   the moment its totem is collected, the newest one pulses briefly, and
///   a tiny "Totems 0/4" text sits underneath.
///
/// HOW IT COUNTS (fully decoupled — same architecture as TotemCollectionHUD)
///   It only LISTENS to the static ItemPickup.AnyItemPickedUp announcement
///   and counts pickups whose Item Id matches ('Totem' by default). Both
///   pickup styles end up there: ItemPickup (grab) and TotemTouchPickup
///   (touch-to-collect in VR, which triggers the same pipeline).
///
/// WHERE TO PUT IT
///   On the flashlight GameObject (the same "Spot Light" object that carries
///   FlashlightController + FlashlightBatteryWorldHUD in the "VR version"
///   scene). It builds its plate entirely from code — no prefabs, no fonts,
///   no sprites, nothing to configure in the Inspector.
///
/// EDIT-MODE PREVIEW
///   Tick "Edit Mode Preview" (on by default in the VR scene) and the plate
///   is built and visible in the Scene view WITHOUT pressing Play. The
///   preview is transient: it is NEVER saved into the scene
///   (HideFlags.DontSave) and disappears when the toggle goes off, the
///   component is removed, or the scene is closed. "Edit Mode Preview Level"
///   is purely cosmetic (0 = 0/4, 0.5 = 2/4, 1 = 4/4).
///
/// RESETTING
///   • Player death → scene reload rebuilds everything fresh (0/4).
///   • Respawn without a reload → call ResetProgress() (public).
/// </summary>
[ExecuteAlways, DisallowMultipleComponent]
public class TotemCollectionWorldHUD : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Tracking (same rules as the screen HUD)")]
    [Tooltip("Only ItemPickup pickups with this Item Id are counted (others are ignored).")]
    [SerializeField] private string trackedItemId = "Totem";

    [Tooltip("How many totems the player has to collect (4 in this game). The plate shows one slot per totem.")]
    [SerializeField, Min(1)] private int requiredTotems = 4;

    [Tooltip("Text shown under the slots: {0} = collected count, {1} = required count.")]
    [SerializeField] private string labelFormat = "Totems {0}/{1}";

    [Tooltip("Show the small count text under the slots. OFF = slots only.")]
    [SerializeField] private bool showCountText = true;

    [Header("Panel Layout (metres, flashlight LOCAL space)")]
    [Tooltip("Plate position in the flashlight's local space. Default sits the plate ON TOP of the body " +
             "just in front of the battery plate: over the collar ring (model z +0.09…+0.16, top y ≈ +0.046), " +
             "so the two plates read as one dashboard. The light origin sits 0.22 m into the model.")]
    [SerializeField] private Vector3 localOffset = new Vector3(0f, 0.049f, -0.095f);

    [Tooltip("Plate rotation (degrees) used when Billboard is OFF — the plate is then locked to the " +
             "flashlight body. Default (-90, 0, 90) lies the plate flat facing UP with its long side " +
             "running parallel to the handle (same as the battery plate). Ignored while Billboard is on.")]
    [SerializeField] private Vector3 localEuler = new Vector3(-90f, 0f, 90f);

    [Tooltip("Width of the slots row itself, in metres (0.055 = 5.5 cm along the handle).")]
    [SerializeField] private float panelWidth = 0.055f;

    [Tooltip("Height of the plate content, in metres (0.03 = 3 cm across the handle: slots + text strip).")]
    [SerializeField] private float panelHeight = 0.03f;

    [Tooltip("Dark margin added around the content so the plate reads as a physical panel (metres).")]
    [SerializeField] private float panelPadding = 0.005f;

    [Tooltip("Colour of the plate behind the slots (dark translucent plastic).")]
    [SerializeField] private Color panelBackgroundColor = new Color(0.02f, 0.02f, 0.02f, 0.72f);

    [Tooltip("ON = the plate stays attached to the flashlight but always ROTATES to face the headset " +
             "camera. OFF (default) = the plate is locked to the flashlight's Local Euler rotation, like " +
             "a painted-on meter.")]
    [SerializeField] private bool billboard = false;

    [Header("Slots")]
    [Tooltip("Colour of an uncollected slot (dark, still visible against the plate).")]
    [SerializeField] private Color segmentEmptyColor = new Color(0.35f, 0.35f, 0.35f, 0.45f);

    [Tooltip("Colour of a collected slot (ritual gold).")]
    [SerializeField] private Color segmentFilledColor = new Color(0.95f, 0.72f, 0.20f, 1f);

    [Tooltip("Gap between slots (metres).")]
    [SerializeField] private float segmentGap = 0.004f;

    [Tooltip("Inset between a slot's border and its fill (metres).")]
    [SerializeField] private float segmentInset = 0.0015f;

    [Tooltip("Flash the newest slot white for a moment when its totem is collected.")]
    [SerializeField] private bool pulseOnCollect = true;

    [Tooltip("How long the collect pulse lasts (seconds).")]
    [SerializeField] private float pulseDuration = 0.6f;

    [Tooltip("The colour the newest slot flashes when collected.")]
    [SerializeField] private Color pulseColor = Color.white;

    [Header("Count Text")]
    [Tooltip("Colour of the count text while collecting.")]
    [SerializeField] private Color textColor = Color.white;

    [Tooltip("Colour of the count text once every totem is collected.")]
    [SerializeField] private Color textCompleteColor = new Color(0.95f, 0.85f, 0.35f, 1f);

    [Header("Edit-Mode Preview (no Play required)")]
    [Tooltip("ON = build and show the plate in the EDITOR (Scene view) without pressing Play. The preview " +
             "is transient: never saved into the scene (HideFlags.DontSave), removed when this is switched " +
             "off, the component is deleted, or the scene is closed.")]
    [SerializeField] private bool editModePreview = false;

    [Tooltip("Cosmetic fill level of the preview only (0 = 0/4 empty, 0.5 = half the slots, 1 = all lit). " +
             "The real count only runs in Play mode.")]
    [SerializeField, Range(0f, 1f)] private float editModePreviewLevel = 0.5f;

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private Canvas panelCanvas;
    private RectTransform panelRoot;      // the plate (canvas root) — child of the flashlight
    private RectTransform segmentsRow;    // the row that holds the slots
    private Image plateImage;             // the dark translucent backing plate
    private Image[] segmentEmpties;       // slot backgrounds
    private Image[] segmentFills;         // slot fills
    private RectTransform[] segmentFillRects;
    private Text countText;               // small "Totems 0/4" strip
    private RectTransform countTextRect;
    private int builtSegmentCount = -1;   // how many slots exist right now

    private int collected;
    private float lastCollectTime = -999f;
    private bool previewOwned;            // WE created the current panel in EDIT mode (safe to delete)
    private Transform billboardCamera;
    private float cameraSearchTimer;

    /// <summary>How many totems this plate has counted so far.</summary>
    public int CollectedTotems { get { return collected; } }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        // Play mode: make sure the plate exists (and if an edit-mode preview
        // panel survived into play, absorb it as a normal object).
        if (!Application.isPlaying) return;

        EnsurePanel();
        if (panelRoot != null) ClearPreviewHideFlags(panelRoot);
    }

    private void OnEnable()
    {
        // The ONLY link to the pickup system: the static announcement.
        ItemPickup.AnyItemPickedUp += HandleItemPickedUp;
    }

    private void OnDisable()
    {
        ItemPickup.AnyItemPickedUp -= HandleItemPickedUp;
    }

    private void OnDestroy()
    {
        // belt-and-braces: never leave a dead plate subscribed
        ItemPickup.AnyItemPickedUp -= HandleItemPickedUp;

        // Edit mode only: a plate WE built as a preview is removed with us.
        // (Saved/user-made plates are never touched — previewOwned is false.)
        if (!Application.isPlaying && previewOwned && panelRoot != null)
        {
            DestroyImmediate(panelRoot.gameObject);
            previewOwned = false;
        }
    }

    private void Update()
    {
        if (!Application.isPlaying)
        {
            TickEditModePreview();
            return;
        }

        // ---- Self-heal: rebuild anything destroyed mid-game ----
        if (panelRoot == null || panelCanvas == null || segmentsRow == null ||
            panelRoot.gameObject == null)
        {
            EnsurePanel();
        }
        if (segmentEmpties == null || segmentFills == null || segmentFills.Length != Mathf.Max(1, requiredTotems))
        {
            RebuildSegments();
        }
        if (panelRoot == null || segmentsRow == null || segmentFills == null) return;

        // ---- Plate sticks to the flashlight ----
        if (panelRoot.localPosition != localOffset)
            panelRoot.localPosition = localOffset;
        if (panelRoot.localScale != Vector3.one)
            panelRoot.localScale = Vector3.one;
        if (!billboard && panelRoot.localRotation != Quaternion.Euler(localEuler))
            panelRoot.localRotation = Quaternion.Euler(localEuler);

        SyncPanelLayout();

        RefreshVisuals(Mathf.Clamp(collected, 0, Mathf.Max(1, requiredTotems)), lastCollectTime);
    }

    private void LateUpdate()
    {
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
            if (panelRoot.rotation != billboardCamera.rotation)
                panelRoot.rotation = billboardCamera.rotation;
        }
        else if (panelRoot.localRotation != Quaternion.Euler(localEuler))
        {
            panelRoot.localRotation = Quaternion.Euler(localEuler);
        }
    }

    /// <summary>Clamps Inspector values so the plate can never receive nonsense input.</summary>
    private void OnValidate()
    {
        panelWidth = Mathf.Max(0.02f, panelWidth);
        panelHeight = Mathf.Max(0.01f, panelHeight);
        panelPadding = Mathf.Max(0f, panelPadding);
        segmentGap = Mathf.Max(0f, segmentGap);
        segmentInset = Mathf.Max(0f, segmentInset);
        requiredTotems = Mathf.Max(1, requiredTotems);
        pulseDuration = Mathf.Max(0.1f, pulseDuration);
        if (string.IsNullOrEmpty(trackedItemId)) trackedItemId = "Totem";
        if (string.IsNullOrEmpty(labelFormat)) labelFormat = "Totems {0}/{1}";
    }

    // ------------------------------------------------------------------ //
    // Public API
    // ------------------------------------------------------------------ //

    /// <summary>Resets the count to 0/4. The normal scene-reload restart does this automatically —
    /// call this only for respawns that do not reload the scene.</summary>
    public void ResetProgress()
    {
        collected = 0;
        lastCollectTime = -999f;
    }

    // ------------------------------------------------------------------ //
    // The listener (the whole link to the pickup system)
    // ------------------------------------------------------------------ //

    private void HandleItemPickedUp(ItemPickup item)
    {
        if (item == null) return;
        if (item.ItemId != trackedItemId) return;              // other items are none of our business
        if (collected >= requiredTotems) return;               // clamped — never shows 5/4

        collected++;
        lastCollectTime = Time.unscaledTime;
    }

    // ------------------------------------------------------------------ //
    // Camera (billboard)
    // ------------------------------------------------------------------ //

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

    // ------------------------------------------------------------------ //
    // UI construction (code-only, no assets)
    // ------------------------------------------------------------------ //

    /// <summary>Finds the "TotemPanel" child or creates it. Existing plates are reused as-is.</summary>
    private void EnsurePanel()
    {
        Transform existing = transform.Find("TotemPanel");

        if (existing != null)
        {
            panelRoot = existing as RectTransform;
            if (panelRoot == null)
            {
                Debug.LogWarning("[TotemCollectionWorldHUD] The 'TotemPanel' child of '" + name +
                                 "' is not a RectTransform — rebuilding the plate.", this);
                SafeDestroy(existing.gameObject);
            }
            else
            {
                panelCanvas = panelRoot.GetComponent<Canvas>();
            }
        }

        if (panelRoot == null)
        {
            GameObject panelGO = new GameObject("TotemPanel", typeof(RectTransform), typeof(Canvas));
            panelGO.layer = gameObject.layer;
            panelGO.transform.SetParent(transform, false);
            panelRoot = (RectTransform)panelGO.transform;
            panelCanvas = panelGO.GetComponent<Canvas>();
            panelCanvas.renderMode = RenderMode.WorldSpace;
            // Deliberately NO CanvasScaler (world space needs none) and NO
            // GraphicRaycaster (pure display — VR rays pass through).

            if (!Application.isPlaying) previewOwned = true;   // edit-mode preview: ours to clean up
        }

        // ---- DARK PLATE backing ----
        plateImage = FindOrCreateImage("PanelBackground", panelRoot);
        plateImage.color = panelBackgroundColor;

        // ---- SLOTS ROW ----
        Transform row = panelRoot.Find("Segments");
        if (row == null)
        {
            GameObject rowGO = new GameObject("Segments", typeof(RectTransform));
            rowGO.transform.SetParent(panelRoot, false);
            row = rowGO.transform;
        }
        segmentsRow = row as RectTransform;

        // ---- COUNT TEXT ----
        Transform textChild = panelRoot.Find("CountText");
        if (textChild != null)
        {
            countText = textChild.GetComponent<Text>();
            if (countText == null)
            {
                Debug.LogWarning("[TotemCollectionWorldHUD] The 'CountText' child has no UI Text — rebuilding it.", this);
                SafeDestroy(textChild.gameObject);
                countText = null;
            }
        }
        if (countText == null) BuildCountText();
        countTextRect = countText.rectTransform;

        RebuildSegments();
        SyncPanelLayout();
    }

    /// <summary>(Re)builds the slot list — reuses existing Segment_i children by name, removes extras.</summary>
    private void RebuildSegments()
    {
        int n = Mathf.Max(1, requiredTotems);
        segmentEmpties = new Image[n];
        segmentFills = new Image[n];
        segmentFillRects = new RectTransform[n];

        if (segmentsRow != null)
        {
            // remove slots beyond the required count (requiredTotems was lowered)
            for (int i = segmentsRow.childCount - 1; i >= 0; i--)
            {
                Transform child = segmentsRow.GetChild(i);
                if (child.name.StartsWith("Segment_") &&
                    int.TryParse(child.name.Substring(8), out int idx) && idx >= n)
                {
                    SafeDestroy(child.gameObject);
                }
            }
        }

        for (int i = 0; i < n; i++)
        {
            Transform existing = segmentsRow != null ? segmentsRow.Find("Segment_" + i) : null;
            RectTransform segRt;
            if (existing != null)
            {
                segRt = existing as RectTransform;
                if (segRt == null)
                {
                    Debug.LogWarning("[TotemCollectionWorldHUD] Segment_" + i + " is not a RectTransform — rebuilding it.", this);
                    SafeDestroy(existing.gameObject);
                    segRt = null;
                }
            }
            else
            {
                segRt = null;
            }

            if (segRt == null)
            {
                GameObject segGO = new GameObject("Segment_" + i, typeof(RectTransform));
                segGO.transform.SetParent(segmentsRow, false);
                segRt = (RectTransform)segGO.transform;
            }

            segmentEmpties[i] = FindOrCreateImage("Empty", segRt);
            segmentFills[i] = FindOrCreateImage("Fill", segRt);
            segmentFillRects[i] = segmentFills[i].rectTransform;
        }

        builtSegmentCount = n;
    }

    /// <summary>Builds the small count text (legacy UI Text + Best Fit — no font assets needed).</summary>
    private void BuildCountText()
    {
        GameObject textGO = new GameObject("CountText", typeof(RectTransform));
        textGO.transform.SetParent(panelRoot, false);

        Text text = textGO.AddComponent<Text>();
        text.font = LoadBuiltinFont();
        text.fontSize = 12;
        text.fontStyle = FontStyle.Bold;
        text.color = textColor;
        text.alignment = TextAnchor.MiddleCenter;
        text.raycastTarget = false;
        text.text = "";
        // Best Fit: the text always fits its strip, whatever its size in metres
        text.resizeTextForBestFit = true;
        text.resizeTextMinSize = 4;
        text.resizeTextMaxSize = 24;

        // black outline so the text stays readable on any background
        // (effectDistance is in canvas units = metres — 1 mm here)
        Shadow shadow = textGO.AddComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
        shadow.effectDistance = new Vector2(0.001f, -0.001f);

        countText = text;
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
            return image;
        }

        GameObject go = new GameObject(childName, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        Image created = go.AddComponent<Image>();
        created.raycastTarget = false;   // pure display — never blocks VR interaction
        return created;
    }

    // ------------------------------------------------------------------ //
    // Layout + visuals
    // ------------------------------------------------------------------ //

    /// <summary>Re-lays-out plate / slots / text from the CURRENT Inspector values (equality-guarded).</summary>
    private void SyncPanelLayout()
    {
        if (panelRoot == null || segmentsRow == null) return;

        int n = Mathf.Max(1, requiredTotems);
        if (segmentFills == null || segmentFills.Length != n || builtSegmentCount != n)
            RebuildSegments();
        if (segmentFills == null || segmentFills.Length != n) return;

        float pad = panelPadding;

        // ---- PLATE ----
        Vector2 plateSize = new Vector2(panelWidth + pad * 2f, panelHeight + pad * 2f);
        if (panelRoot.sizeDelta != plateSize) panelRoot.sizeDelta = plateSize;

        if (plateImage != null)
        {
            Stretch(plateImage.rectTransform, 0f);
            if (plateImage.color != panelBackgroundColor) plateImage.color = panelBackgroundColor;
        }

        // ---- SLOTS ROW: upper strip of the plate (full width when the text is hidden) ----
        float rowHeight = showCountText ? panelHeight * 0.55f : panelHeight;
        if (segmentsRow.anchorMin != new Vector2(0.5f, 0.5f)) segmentsRow.anchorMin = new Vector2(0.5f, 0.5f);
        if (segmentsRow.anchorMax != new Vector2(0.5f, 0.5f)) segmentsRow.anchorMax = new Vector2(0.5f, 0.5f);
        if (segmentsRow.pivot != new Vector2(0.5f, 0.5f)) segmentsRow.pivot = new Vector2(0.5f, 0.5f);
        Vector2 rowSize = new Vector2(panelWidth, rowHeight);
        if (segmentsRow.sizeDelta != rowSize) segmentsRow.sizeDelta = rowSize;
        Vector2 rowPos = new Vector2(0f, showCountText ? panelHeight * 0.185f : 0f);
        if (segmentsRow.anchoredPosition != rowPos) segmentsRow.anchoredPosition = rowPos;

        // ---- EACH SLOT: evenly spaced across the row ----
        float rowWidth = panelWidth;
        float slotW = (rowWidth - segmentGap * (n - 1)) / n;

        for (int i = 0; i < n; i++)
        {
            RectTransform emptyRt = segmentEmpties[i] != null ? segmentEmpties[i].rectTransform : null;
            if (segmentEmpties[i] == null || segmentFills[i] == null) continue;

            RectTransform segRt = emptyRt.parent as RectTransform;
            if (segRt == null) continue;

            if (segRt.anchorMin != new Vector2(0.5f, 0.5f)) segRt.anchorMin = new Vector2(0.5f, 0.5f);
            if (segRt.anchorMax != new Vector2(0.5f, 0.5f)) segRt.anchorMax = new Vector2(0.5f, 0.5f);
            if (segRt.pivot != new Vector2(0.5f, 0.5f)) segRt.pivot = new Vector2(0.5f, 0.5f);
            if (segRt.sizeDelta != new Vector2(slotW, rowHeight)) segRt.sizeDelta = new Vector2(slotW, rowHeight);
            float x = -rowWidth / 2f + slotW / 2f + i * (slotW + segmentGap);
            Vector2 segPos = new Vector2(x, 0f);
            if (segRt.anchoredPosition != segPos) segRt.anchoredPosition = segPos;

            Stretch(segmentEmpties[i].rectTransform, 0f);
            if (segmentEmpties[i].color != segmentEmptyColor) segmentEmpties[i].color = segmentEmptyColor;

            if (segmentFills[i].raycastTarget) segmentFills[i].raycastTarget = false;
        }

        // ---- COUNT TEXT: lower strip ----
        if (countText != null && countTextRect != null)
        {
            if (countTextRect.anchorMin != new Vector2(0.5f, 0.5f)) countTextRect.anchorMin = new Vector2(0.5f, 0.5f);
            if (countTextRect.anchorMax != new Vector2(0.5f, 0.5f)) countTextRect.anchorMax = new Vector2(0.5f, 0.5f);
            if (countTextRect.pivot != new Vector2(0.5f, 0.5f)) countTextRect.pivot = new Vector2(0.5f, 0.5f);
            Vector2 textSize = new Vector2(panelWidth, showCountText ? panelHeight * 0.38f : 0f);
            if (countTextRect.sizeDelta != textSize) countTextRect.sizeDelta = textSize;
            Vector2 textPos = new Vector2(0f, showCountText ? -panelHeight * 0.27f : 0f);
            if (countTextRect.anchoredPosition != textPos) countTextRect.anchoredPosition = textPos;
            if (countText.enabled != showCountText) countText.enabled = showCountText;
            if (countText.raycastTarget) countText.raycastTarget = false;
        }
    }

    /// <summary>Writes the slot fills + count text for a given collected count (shared by play + preview).
    /// The fill rect is owned ENTIRELY here (SyncPanelLayout never touches it): filled = stretched with
    /// the inset, empty = collapsed to a zero-size point in the slot's centre.</summary>
    private void RefreshVisuals(int shown, float pulseTime)
    {
        int n = Mathf.Max(1, requiredTotems);
        shown = Mathf.Clamp(shown, 0, n);

        for (int i = 0; i < segmentFills.Length; i++)
        {
            if (segmentFills[i] == null || segmentFillRects[i] == null) continue;

            bool filled = i < shown;
            RectTransform rt = segmentFillRects[i];

            if (filled)
            {
                // stretched across the slot, inset by the slot border
                if (rt.anchorMin != Vector2.zero) rt.anchorMin = Vector2.zero;
                if (rt.anchorMax != Vector2.one) rt.anchorMax = Vector2.one;
                Vector2 offMin = new Vector2(segmentInset, segmentInset);
                Vector2 offMax = new Vector2(-segmentInset, -segmentInset);
                if (rt.offsetMin != offMin) rt.offsetMin = offMin;
                if (rt.offsetMax != offMax) rt.offsetMax = offMax;
            }
            else
            {
                // collapsed: zero-size, centred (invisible — the dark slot shows through)
                Vector2 centre = new Vector2(0.5f, 0.5f);
                if (rt.anchorMin != centre) rt.anchorMin = centre;
                if (rt.anchorMax != centre) rt.anchorMax = centre;
                if (rt.offsetMin != Vector2.zero) rt.offsetMin = Vector2.zero;
                if (rt.offsetMax != Vector2.zero) rt.offsetMax = Vector2.zero;
            }

            Color slotColour = filled ? segmentFilledColor : new Color(0f, 0f, 0f, 0f);
            if (pulseOnCollect && filled && i == shown - 1 && pulseTime > -900f)
            {
                float since = Time.unscaledTime - pulseTime;
                if (since >= 0f && since < pulseDuration)
                    slotColour = Color.Lerp(pulseColor, segmentFilledColor, since / pulseDuration);
            }
            if (segmentFills[i].color != slotColour) segmentFills[i].color = slotColour;
        }

        if (countText != null)
        {
            string format = string.IsNullOrEmpty(labelFormat) ? "Totems {0}/{1}" : labelFormat;
            string newText = string.Format(format, shown, n);
            if (countText.text != newText) countText.text = newText;

            Color target = (shown >= n) ? textCompleteColor : textColor;
            if (countText.color != target) countText.color = target;
        }
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

    private static void SafeDestroy(GameObject go)
    {
        if (go == null) return;
        if (Application.isPlaying) Destroy(go);
        else DestroyImmediate(go);
    }

    // ------------------------------------------------------------------ //
    // Edit-mode preview (see class doc) — the plate in the Scene view
    // without pressing Play. Never saved into the scene (HideFlags.DontSave).
    // ------------------------------------------------------------------ //

    private void TickEditModePreview()
    {
        if (Application.isPlaying) return;

        if (editModePreview)
        {
            bool malformed = panelRoot == null || panelCanvas == null || segmentsRow == null ||
                             segmentEmpties == null || segmentFills == null ||
                             segmentFills.Length != Mathf.Max(1, requiredTotems);
            if (malformed) EnsurePanel();
            if (panelRoot == null) return;

            ApplyPreviewHideFlags(panelRoot);

            // lock the authored pose (billboard stays OFF in the editor)
            if (panelRoot.localPosition != localOffset) panelRoot.localPosition = localOffset;
            if (panelRoot.localScale != Vector3.one) panelRoot.localScale = Vector3.one;
            if (panelRoot.localRotation != Quaternion.Euler(localEuler))
                panelRoot.localRotation = Quaternion.Euler(localEuler);

            SyncPanelLayout();

            int shown = Mathf.Clamp(Mathf.RoundToInt(editModePreviewLevel * Mathf.Max(1, requiredTotems)),
                                    0, Mathf.Max(1, requiredTotems));
            RefreshVisuals(shown, -999f);

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
        segmentsRow = null;
        segmentEmpties = null;
        segmentFills = null;
        segmentFillRects = null;
        countText = null;
        countTextRect = null;
        builtSegmentCount = -1;
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

    [ContextMenu("Plate: Reset Layout To Defaults")]
    private void ResetLayoutToDefaults()
    {
        localOffset = new Vector3(0f, 0.049f, -0.095f);
        localEuler = new Vector3(-90f, 0f, 90f);
        panelWidth = 0.055f;
        panelHeight = 0.03f;
        panelPadding = 0.005f;
        billboard = false;
    }

    [ContextMenu("Plate: Reset Progress To 0/4")]
    private void ResetProgressMenu()
    {
        ResetProgress();
        Debug.Log("[TotemCollectionWorldHUD] Progress reset to 0/" + Mathf.Max(1, requiredTotems) + ".", this);
    }
}
