using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
#endif

/// <summary>
/// Pauses the ENTIRE game and opens the game menu.
///
/// What "everything pauses" means here:
///   • Time.timeScale = 0   → physics, animations and all deltaTime movement stop
///                            (enemy chase, stun timers, spawner cooldowns…).
///   • AudioListener.pause  → every sound in the game stops.
///   • Input freeze         → the player, camera and flashlight scripts check
///                            GamePauseManager.IsPaused and stop reading input.
///
/// The menu canvas uses sorting order 32767 — Unity's MAXIMUM — so no other
/// canvas or object can ever be drawn above it or block it. A full-screen dim
/// image also swallows every click aimed at anything underneath the menu.
///
/// SETUP: add this component to any GameObject (an empty "GameManagers" object
/// is a good home). The whole menu is built from code at runtime — there is
/// nothing to create in the editor.
///
/// The pause key works on BOTH input backends (legacy Input Manager and the
/// new Input System — keep KeyCodeBridge.cs in the project for the latter).
///
/// v1.1 – PauseLocked: while the jumpscare / Game Over sequence owns the
/// screen (set by JumpscareGameOverManager), the pause key is ignored so the
/// menu can never open on top of the video or the Game Over UI. Cleared
/// automatically when the scene reloads (Restart) or play mode stops.
///
/// v1.2 – compile fix: the EventSystem lookup now uses FindAnyObjectByType
/// (Unity 6.1+ marks FindFirstObjectByType obsolete — CS0618 warning).
/// </summary>
[DisallowMultipleComponent]
public class GamePauseManager : MonoBehaviour
{
    /// <summary>True while the game is paused. Other scripts read this to freeze themselves.</summary>
    public static bool IsPaused { get; private set; }

    /// <summary>
    /// v1.1: while true the pause key is IGNORED — set by JumpscareGameOverManager
    /// for the whole jumpscare video + Game Over screen so the menu can never open
    /// on top of them. Automatically reset when the scene reloads (Restart) or the
    /// manager is destroyed (play-mode stop), so the game can never stay locked.
    /// </summary>
    public static bool PauseLocked { get; set; }

    private static GamePauseManager instance;

    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Pause Key")]

    [Tooltip("Key that opens the menu. Pressing it again (or clicking Resume) returns to the game.")]
    [SerializeField] private KeyCode pauseKey = KeyCode.Escape;

    [Header("Menu Text")]

    [Tooltip("Title shown at the top of the menu.")]
    [SerializeField] private string menuTitle = "GAME MENU";

    [Tooltip("Text on the Resume button.")]
    [SerializeField] private string resumeButtonText = "Resume";

    [Header("Menu Colors")]

    [Tooltip("Full-screen dark overlay behind the panel.")]
    [SerializeField] private Color dimColor = new Color(0f, 0f, 0f, 0.65f);

    [Tooltip("Background color of the centered panel.")]
    [SerializeField] private Color panelColor = new Color(0.08f, 0.08f, 0.08f, 0.95f);

    [Tooltip("Thin border color around the panel.")]
    [SerializeField] private Color panelBorderColor = new Color(1f, 1f, 1f, 0.25f);

    [Tooltip("Normal color of the Resume button.")]
    [SerializeField] private Color buttonColor = new Color(0.85f, 0.85f, 0.85f, 1f);

    [Tooltip("Text color on the Resume button.")]
    [SerializeField] private Color buttonTextColor = Color.black;

    [Tooltip("Color of the menu title.")]
    [SerializeField] private Color titleColor = Color.white;

    [Header("Menu Size (pixels)")]

    [Tooltip("Width of the centered panel.")]
    [SerializeField] private float panelWidth = 440f;

    [Tooltip("Height of the centered panel.")]
    [SerializeField] private float panelHeight = 280f;

    [Tooltip("Width of the Resume button.")]
    [SerializeField] private float buttonWidth = 260f;

    [Tooltip("Height of the Resume button.")]
    [SerializeField] private float buttonHeight = 70f;

    [Tooltip("Font size of the title.")]
    [SerializeField] private int titleFontSize = 36;

    [Tooltip("Font size of the Resume button label.")]
    [SerializeField] private int buttonFontSize = 26;

    [Header("Layer (always on top)")]

    [Tooltip("Canvas sorting order of the menu. 32767 is Unity's maximum — nothing can be drawn above it.")]
    [SerializeField] private int menuSortingOrder = 32767;

    [Header("Cursor After Resume")]

    [Tooltip("Cursor mode when returning to gameplay. Keep this in sync with the Player Camera's " +
             "'Lock Cursor' setting (Locked + invisible is the normal gameplay state).")]
    [SerializeField] private CursorLockMode cursorModeAfterResume = CursorLockMode.Locked;

    [Tooltip("Show the cursor after resuming? Normally false (invisible during gameplay).")]
    [SerializeField] private bool cursorVisibleAfterResume = false;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private Canvas menuCanvas;
    private Image dimImage;
    private Image panelImage;
    private Outline panelOutline;
    private Text titleLabel;
    private Image buttonImage;
    private Text buttonLabel;
    private Button resumeButton;

    private float timeScaleBeforePause = 1f;

    /// <summary>
    /// The key that opens/closes the pause menu (KeyCode.None when no manager
    /// exists in the scene). PlayerCameraController reads this so it never
    /// double-handles the pause key with its own cursor toggle.
    /// </summary>
    public static KeyCode PauseKey
    {
        get { return instance != null ? instance.pauseKey : KeyCode.None; }
    }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Debug.LogWarning("[GamePauseManager] Only one pause manager is allowed — destroying the " +
                             "duplicate on '" + name + "'.", this);
            Destroy(gameObject);
            return;
        }
        instance = this;

        // Safety: if the scene was reloaded while paused, make sure the game runs again.
        if (IsPaused)
        {
            IsPaused = false;
            Time.timeScale = 1f;
            AudioListener.pause = false;
        }

        EnsureEventSystem();
        BuildMenu();
        ApplyMenuStyle();
        SetMenuVisible(false);
    }

    private void Update()
    {
        // v1.1: the jumpscare / Game Over sequence owns the screen — the pause
        // key must never open the menu on top of the video or the Game Over UI.
        if (PauseLocked) return;

        if (ReadPauseKeyPressed())
        {
            if (!IsPaused) PauseGame();
            else ResumeGame();
            return;
        }

        // Keep the menu looking like the Inspector says (live tweaking while paused).
        if (IsPaused) ApplyMenuStyle();
    }

    private void LateUpdate()
    {
        // Safety net: while paused, keep the cursor free even if some other script
        // tries to re-lock it — a locked cursor would make the menu unclickable.
        if (IsPaused && (Cursor.lockState != CursorLockMode.None || !Cursor.visible))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    private void OnDestroy()
    {
        if (instance != this) return;
        instance = null;

        // v1.1: never leave the pause key permanently locked (scene reload /
        // play-mode stop while a jumpscare was running).
        PauseLocked = false;

        // Never leave the game frozen if this object is destroyed.
        if (IsPaused)
        {
            IsPaused = false;
            Time.timeScale = 1f;
            AudioListener.pause = false;
        }
    }

    /// <summary>Clamps Inspector values so the menu can never receive nonsense input.</summary>
    private void OnValidate()
    {
        menuSortingOrder = Mathf.Clamp(menuSortingOrder, 0, 32767);
        panelWidth = Mathf.Max(160f, panelWidth);
        panelHeight = Mathf.Max(120f, panelHeight);
        buttonWidth = Mathf.Max(60f, buttonWidth);
        buttonHeight = Mathf.Max(30f, buttonHeight);
        titleFontSize = Mathf.Clamp(titleFontSize, 8, 128);
        buttonFontSize = Mathf.Clamp(buttonFontSize, 8, 128);
    }

    // ------------------------------------------------------------------ //
    // Public API
    // ------------------------------------------------------------------ //

    /// <summary>Pauses the entire game and opens the menu.</summary>
    public void PauseGame()
    {
        if (IsPaused) return;
        IsPaused = true;

        timeScaleBeforePause = Time.timeScale;
        Time.timeScale = 0f;              // physics, animations, deltaTime movement — frozen
        AudioListener.pause = true;       // every audio source — silent

        Cursor.lockState = CursorLockMode.None;   // free the mouse for the menu
        Cursor.visible = true;

        SetMenuVisible(true);
        ApplyMenuStyle();
    }

    /// <summary>Closes the menu and un-pauses. Wired to the Resume button; Esc also calls this.</summary>
    public void ResumeGame()
    {
        if (!IsPaused) return;
        IsPaused = false;

        Time.timeScale = timeScaleBeforePause;   // normally back to 1
        AudioListener.pause = false;

        Cursor.lockState = cursorModeAfterResume;
        Cursor.visible = cursorVisibleAfterResume;

        SetMenuVisible(false);
    }

    // ------------------------------------------------------------------ //
    // Input (supports both the legacy Input Manager and the new Input System)
    // ------------------------------------------------------------------ //

    private bool ReadPauseKeyPressed()
    {
#if ENABLE_INPUT_SYSTEM
        // KeyCodeBridge maps the Inspector KeyCode to the new Input System key,
        // so the Pause Key field genuinely works on both input backends.
        return KeyCodeBridge.WasKeyPressedThisFrame(Keyboard.current, pauseKey);
#else
        return Input.GetKeyDown(pauseKey);
#endif
    }

    // ------------------------------------------------------------------ //
    // Menu building (fully from code — nothing to set up in the editor)
    // ------------------------------------------------------------------ //

    private void BuildMenu()
    {
        Font font = GetBuiltInFont();

        GameObject canvasGO = new GameObject("PauseMenuCanvas");
        menuCanvas = canvasGO.AddComponent<Canvas>();
        menuCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        menuCanvas.sortingOrder = menuSortingOrder;              // max value — always on top
        CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        canvasGO.AddComponent<GraphicRaycaster>();               // required for clicking the button

        // ---- Full-screen dim (also blocks clicks on anything under the menu) ----
        GameObject dimGO = new GameObject("Dim");
        dimImage = dimGO.AddComponent<Image>();
        dimImage.raycastTarget = true;
        RectTransform dim = dimImage.rectTransform;
        dim.SetParent(canvasGO.transform, false);
        dim.anchorMin = Vector2.zero;
        dim.anchorMax = Vector2.one;
        dim.offsetMin = Vector2.zero;
        dim.offsetMax = Vector2.zero;

        // ---- Centered panel ----
        GameObject panelGO = new GameObject("Panel");
        panelImage = panelGO.AddComponent<Image>();
        panelImage.raycastTarget = true;
        RectTransform panel = panelImage.rectTransform;
        panel.SetParent(dim, false);
        panel.anchorMin = panel.anchorMax = panel.pivot = Vector2.one * 0.5f;
        panel.anchoredPosition = Vector2.zero;

        panelOutline = panelGO.AddComponent<Outline>();
        panelOutline.effectColor = panelBorderColor;
        panelOutline.effectDistance = new Vector2(2f, -2f);

        // ---- Title ----
        GameObject titleGO = new GameObject("Title");
        titleLabel = titleGO.AddComponent<Text>();
        titleLabel.font = font;
        titleLabel.fontStyle = FontStyle.Bold;
        titleLabel.alignment = TextAnchor.MiddleCenter;
        titleLabel.raycastTarget = false;
        RectTransform titleRt = titleLabel.rectTransform;
        titleRt.SetParent(panel, false);
        titleRt.anchorMin = titleRt.anchorMax = new Vector2(0.5f, 1f);
        titleRt.pivot = new Vector2(0.5f, 1f);
        titleRt.anchoredPosition = new Vector2(0f, -28f);

        // ---- Resume button ----
        GameObject buttonGO = new GameObject("ResumeButton");
        buttonImage = buttonGO.AddComponent<Image>();
        buttonImage.raycastTarget = true;
        RectTransform buttonRt = buttonImage.rectTransform;
        buttonRt.SetParent(panel, false);
        buttonRt.anchorMin = buttonRt.anchorMax = buttonRt.pivot = Vector2.one * 0.5f;
        buttonRt.anchoredPosition = new Vector2(0f, -14f);

        resumeButton = buttonGO.AddComponent<Button>();
        resumeButton.transition = Button.Transition.ColorTint;
        resumeButton.onClick.AddListener(ResumeGame);

        // ---- Button label ----
        GameObject labelGO = new GameObject("Label");
        buttonLabel = labelGO.AddComponent<Text>();
        buttonLabel.font = font;
        buttonLabel.alignment = TextAnchor.MiddleCenter;
        buttonLabel.raycastTarget = false;
        RectTransform labelRt = buttonLabel.rectTransform;
        labelRt.SetParent(buttonRt, false);
        labelRt.anchorMin = Vector2.zero;
        labelRt.anchorMax = Vector2.one;
        labelRt.offsetMin = Vector2.zero;
        labelRt.offsetMax = Vector2.zero;
    }

    private void ApplyMenuStyle()
    {
        if (menuCanvas == null) return;

        menuCanvas.sortingOrder = menuSortingOrder;

        dimImage.color = dimColor;
        panelImage.color = panelColor;
        panelOutline.effectColor = panelBorderColor;
        panelImage.rectTransform.sizeDelta = new Vector2(panelWidth, panelHeight);

        titleLabel.text = menuTitle;
        titleLabel.color = titleColor;
        titleLabel.fontSize = titleFontSize;
        titleLabel.rectTransform.sizeDelta = new Vector2(panelWidth - 40f, 60f);

        buttonLabel.text = resumeButtonText;
        buttonLabel.color = buttonTextColor;
        buttonLabel.fontSize = buttonFontSize;

        buttonImage.rectTransform.sizeDelta = new Vector2(buttonWidth, buttonHeight);

        // Button hover/press tints derived from the base button color.
        ColorBlock colors = resumeButton.colors;
        colors.normalColor = buttonColor;
        colors.highlightedColor = Color.Lerp(buttonColor, Color.white, 0.25f);
        colors.pressedColor = Color.Lerp(buttonColor, Color.black, 0.25f);
        colors.selectedColor = Color.Lerp(buttonColor, Color.white, 0.25f);
        colors.disabledColor = buttonColor * 0.5f;
        colors.fadeDuration = 0.08f;
        resumeButton.colors = colors;
    }

    private void SetMenuVisible(bool visible)
    {
        if (menuCanvas != null)
        {
            menuCanvas.gameObject.SetActive(visible);
        }
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    /// <summary>Makes sure a clickable UI exists (creates the right input module for the project's input backend).</summary>
    private static void EnsureEventSystem()
    {
#if UNITY_2023_1_OR_NEWER
        EventSystem existing = FindAnyObjectByType<EventSystem>(); // FindFirstObjectByType is deprecated since Unity 6.4 — FindAnyObjectByType is the current API
#else
        EventSystem existing = FindObjectOfType<EventSystem>();
#endif
        if (existing != null) return;

        GameObject esGO = new GameObject("EventSystem");
        esGO.AddComponent<EventSystem>();

#if ENABLE_INPUT_SYSTEM
        esGO.AddComponent<InputSystemUIInputModule>();
#else
        esGO.AddComponent<StandaloneInputModule>();
#endif
    }

    /// <summary>Built-in font lookup that works across Unity versions (LegacyRuntime since 2022, Arial before).</summary>
    private static Font GetBuiltInFont()
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