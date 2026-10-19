using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// TOTEM COLLECTION HUD (v1.1) — the "Totems collected: 0/4" progress tracker.
///
/// WHAT IT DOES
///   Upper-left screen text that counts how many totems the player has
///   collected: "Totems collected: 0/4" → "1/4" → … → "4/4". It also fires a
///   single UnityEvent the moment the LAST totem is collected — the hook for
///   the future ritual spot ("open when all 4 are found").
///
/// FULLY SEPARATE FROM THE PICKUP SYSTEM (by design):
///   This script never references ItemPickup instances. It only LISTENS to
///   the static ItemPickup.AnyItemPickedUp announcement and counts the items
///   whose Item Id matches ('Totem' by default). The pickup system works with
///   or without this HUD; the HUD works without knowing where any item is.
///
/// TWO WAYS TO PROVIDE THE TEXT (both fully supported, both drag-and-drop):
///   A. YOUR OWN TEXT (what you asked for): create a text on your 'HUD'
///      canvas (the renamed battery-bar canvas), position/style it however
///      you like, then drag its GameObject into the 'Text Object' field.
///      BOTH text types work — legacy UI Text AND TextMeshPro
///      (TextMeshProUGUI / TextMeshPro — v1.1: the old 'Totem Text' field
///      only accepted legacy Text, which is why a TextMeshPro text could not
///      be dragged in). The script writes ONLY the text itself — your font,
///      size, colour and position are never touched.
///   B. ZERO SETUP: leave 'Text Object' empty and the script builds a default
///      upper-left text automatically (white, bold, outlined, margin 24 px).
///      If a child named 'TotemsCollectedText' was saved with the scene it is
///      reused instead — your edits to it are kept, never reset.
///
///   You can put this component directly on the 'HUD' canvas object (its
///   Canvas is reused untouched) or on any other GameObject (it then creates
///   its own overlay canvas, sorting order 201 — just above the battery HUD).
///
/// RESETTING
///   • Player death → your Restart button reloads the scene: everything is
///     rebuilt fresh, so the count is back to 0/4 automatically (same as the
///     items, the flashlight, everything else).
///   • Building a respawn WITHOUT a scene reload? Call ResetProgress()
///     (public method) — count back to 0, text back to "0/4", and the
///     'On All Totems Collected' event can fire again on the next full set.
///
/// v1.0 — initial release.
/// v1.1 — 'Totem Text' (legacy-Text-only) became 'Text Object' (GameObject):
///        drag ANY text object — legacy UI Text or TextMeshPro, both work.
///        Fixes "can't put my own text in the field".
/// </summary>
[DisallowMultipleComponent]
public class TotemCollectionHUD : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Text (leave empty = built automatically)")]

    [Tooltip("YOUR text object: drag the GameObject with your text on it (on the 'HUD' canvas). Legacy UI Text AND TextMeshPro (TextMeshProUGUI) both work — the type is detected automatically. ONLY its text is ever written — your font, size, colour and position stay yours. Left empty: a default upper-left text is built automatically.")]
    [SerializeField] private GameObject totemTextObject = null;

    [Header("Tracking")]

    [Tooltip("Which items count: only ItemPickup components with this Item Id are counted (others are ignored).")]
    [SerializeField] private string trackedItemId = "Totem";

    [Tooltip("How many totems the player has to collect (4 in your game).")]
    [SerializeField, Min(1)] private int requiredTotems = 4;

    [Tooltip("Text shown: {0} = collected count, {1} = required count.")]
    [SerializeField] private string labelFormat = "Totems collected: {0}/{1}";

    [Header("Event")]

    [Tooltip("Fired ONCE when the last required totem is collected (4/4) — wire the future ritual spot opening here.")]
    [SerializeField] private UnityEvent onAllTotemsCollected = new UnityEvent();

    [Header("Auto-Built Default Text (only used when 'Text Object' is left empty)")]

    [Tooltip("Distance of the auto-built text from the screen's top-left corner (pixels).")]
    [SerializeField, Min(0f)] private float edgeMargin = 24f;

    [Tooltip("Font size of the auto-built text.")]
    [SerializeField, Range(8, 128)] private int fontSize = 28;

    [Tooltip("Colour of the auto-built text.")]
    [SerializeField] private Color textColor = Color.white;

    [Tooltip("Sorting order of the auto-created canvas (201 = just above the battery HUD's 200, below the prompts' 250). Ignored when sitting on an existing canvas like your 'HUD'.")]
    [SerializeField] private int canvasSortingOrder = 201;

    [Header("Debug")]

    [Tooltip("Write every pickup and reset to the Console.")]
    [SerializeField] private bool debugLogs = false;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private Canvas ownCanvas;        // only non-null when WE created the canvas
    private Text legacyText;         // resolved from totemTextObject — legacy UI Text
    private TMP_Text tmpText;        // resolved from totemTextObject — TextMeshPro text
    private bool warnedNoTextComponent;

    // ------------------------------------------------------------------ //
    // Public API
    // ------------------------------------------------------------------ //

    /// <summary>How many totems have been collected so far.</summary>
    public int CollectedTotems { get; private set; }

    /// <summary>How many totems are required in total.</summary>
    public int RequiredTotems
    {
        get { return requiredTotems; }
    }

    /// <summary>True once all required totems are collected (4/4).</summary>
    public bool IsComplete
    {
        get { return CollectedTotems >= requiredTotems; }
    }

    /// <summary>Progress as 0..1 (0 at 0/4, 1 at 4/4) — handy for bars or the ritual spot.</summary>
    public float Progress01
    {
        get { return requiredTotems > 0 ? Mathf.Clamp01((float)CollectedTotems / requiredTotems) : 0f; }
    }

    /// <summary>
    /// Resets the count to 0/4 (and re-arms the completion event). The normal
    /// player-death restart (scene reload) resets everything automatically —
    /// call this only if you ever build a respawn without a scene reload.
    /// </summary>
    public void ResetProgress()
    {
        if (debugLogs) Debug.Log("[TotemCollectionHUD] Progress reset (was " + CollectedTotems + "/" + requiredTotems + ").", this);

        CollectedTotems = 0;
        RefreshText();
    }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void OnEnable()
    {
        // The ONLY link to the pickup system: a static event. Subscribe/
        // unsubscribe symmetrically so nothing ever leaks across reloads.
        ItemPickup.AnyItemPickedUp += HandleItemPickedUp;

        RefreshText();   // shows "Totems collected: 0/4" immediately
    }

    private void OnDisable()
    {
        ItemPickup.AnyItemPickedUp -= HandleItemPickedUp;
    }

    private void OnDestroy()
    {
        // belt-and-braces: never leave a dead HUD subscribed to a static event
        ItemPickup.AnyItemPickedUp -= HandleItemPickedUp;

        // a canvas WE created is removed with us; a reused canvas ('HUD') stays
        if (ownCanvas != null)
        {
            Destroy(ownCanvas.gameObject);
            ownCanvas = null;
        }
    }

    /// <summary>Clamps Inspector values so the tracker can never receive nonsense input.</summary>
    private void OnValidate()
    {
        requiredTotems = Mathf.Max(1, requiredTotems);
        edgeMargin = Mathf.Max(0f, edgeMargin);
        fontSize = Mathf.Clamp(fontSize, 8, 128);
        if (string.IsNullOrEmpty(trackedItemId)) trackedItemId = "Totem";
        if (string.IsNullOrEmpty(labelFormat)) labelFormat = "Totems collected: {0}/{1}";

        if (Application.isPlaying) RefreshText();   // live editing while the game runs
    }

    // ------------------------------------------------------------------ //
    // The listener (the whole link to the pickup system)
    // ------------------------------------------------------------------ //

    private void HandleItemPickedUp(ItemPickup item)
    {
        if (item == null) return;
        if (item.ItemId != trackedItemId) return;             // other item types are none of our business
        if (CollectedTotems >= requiredTotems) return;        // clamped at 4/4 — a 5th totem never shows 5/4

        CollectedTotems++;
        RefreshText();

        if (debugLogs)
            Debug.Log("[TotemCollectionHUD] '" + item.name + "' collected — " +
                      CollectedTotems + "/" + requiredTotems + ".", this);

        if (CollectedTotems == requiredTotems)
        {
            if (onAllTotemsCollected != null) onAllTotemsCollected.Invoke();
        }
    }

    // ------------------------------------------------------------------ //
    // Text
    // ------------------------------------------------------------------ //

    private void RefreshText()
    {
        EnsureText();

        string format = string.IsNullOrEmpty(labelFormat) ? "Totems collected: {0}/{1}" : labelFormat;
        string newText = string.Format(format, CollectedTotems, requiredTotems);

        // equality-guarded writes — only the TEXT is ever touched, never the style
        if (tmpText != null)
        {
            if (tmpText.text != newText) tmpText.text = newText;
        }
        else if (legacyText != null)
        {
            if (legacyText.text != newText) legacyText.text = newText;
        }
    }

    /// <summary>
    /// Finds the text to write to: the user's assigned object (legacy Text or
    /// TextMeshPro — detected automatically), a child saved with the scene
    /// ('TotemsCollectedText'), or a freshly built default. User-made objects
    /// are ALWAYS reused exactly as they are — never reset.
    /// </summary>
    private void EnsureText()
    {
        // A) the user's own text object — works for legacy Text AND TextMeshPro
        if (totemTextObject != null)
        {
            legacyText = totemTextObject.GetComponent<Text>();
            tmpText = legacyText == null ? totemTextObject.GetComponent<TMP_Text>() : null;

            if (legacyText == null && tmpText == null && !warnedNoTextComponent)
            {
                warnedNoTextComponent = true;
                Debug.LogWarning("[TotemCollectionHUD] The assigned 'Text Object' '" + totemTextObject.name +
                                 "' has neither a UI Text nor a TextMeshPro component on it — nothing to write to. " +
                                 "Drag the GameObject that carries your text component.", this);
            }
            return;
        }

        // B) a child saved with the scene — reused as-is (legacy or TMP)
        Transform existing = transform.Find("TotemsCollectedText");
        if (existing != null)
        {
            legacyText = existing.GetComponent<Text>();
            tmpText = legacyText == null ? existing.GetComponent<TMP_Text>() : null;
            if (legacyText != null || tmpText != null) return;
        }

        // C) build the default
        BuildDefaultText();
    }

    /// <summary>Builds the default upper-left text (and a canvas first, when this object has none).</summary>
    private void BuildDefaultText()
    {
        Canvas canvas = GetComponent<Canvas>();
        if (canvas == null)
        {
            GameObject canvasGO = new GameObject("TotemCollectionHUD Canvas (auto)");
            canvasGO.transform.SetParent(transform, false);
            canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = canvasSortingOrder;
            ownCanvas = canvas;

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
        }
        // When this component sits on an existing canvas (your 'HUD'), it is
        // reused untouched — no sorting order stomping, no extra scaler.

        GameObject textGO = new GameObject("TotemsCollectedText");
        textGO.AddComponent<RectTransform>();
        textGO.transform.SetParent(canvas.transform, false);

        Text text = textGO.AddComponent<Text>();
        text.font = LoadBuiltinFont();
        text.fontSize = fontSize;
        text.fontStyle = FontStyle.Bold;
        text.color = textColor;
        text.alignment = TextAnchor.UpperLeft;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;   // never clips a growing count
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;                              // never blocks clicks
        text.text = "";
        legacyText = text;
        tmpText = null;

        RectTransform rt = text.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);            // top-left corner of the canvas
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(edgeMargin, -edgeMargin);
        rt.sizeDelta = new Vector2(600f, 48f);

        // black outline so the text stays readable on any background
        Shadow shadow = textGO.AddComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
        shadow.effectDistance = new Vector2(2f, -2f);
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