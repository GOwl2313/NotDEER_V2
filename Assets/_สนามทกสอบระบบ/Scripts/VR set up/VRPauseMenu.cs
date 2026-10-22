using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityEngine.XR;

/// <summary>
/// VR PAUSE MENU (v1.0) — pausing spawns the game menu FROM THE LEFT HAND.
///
/// Put this component on the Player root (VRPlayerSetup does that for you).
/// It has two halves:
///
/// 1) THE VR PAUSE BUTTON
///    Pressing the LEFT controller's Menu button (or the keyboard Escape —
///    unchanged from the flat game) pauses through the REAL
///    GamePauseManager.PauseGame()/ResumeGame(). Time scale, audio and the
///    pause lock all behave exactly like the original; nothing about the
///    pause system is duplicated.
///
/// 2) THE SPAWNED MENU
///    The moment the game pauses, a world-space menu panel appears AT THE
///    LEFT HAND (slightly above it, tilted toward the player's face) and
///    stays anchored in the world while you use it. Reach out with either
///    hand: the VRUIPointer's white line appears and its trigger clicks the
///    big Resume button. If you physically step away and the menu ends up
///    out of reach, it quietly re-anchors at the hand again.
///    While paused, this component also hides the ORIGINAL screen-space
///    "PauseMenuCanvas" that GamePauseManager builds — in VR you interact
///    with the hand menu, not a flat overlay (a mouse can't reach a screen
///    that's inside your head).
///
/// MENU CONTENT mirrors the original: a title and a Resume button. Both
/// texts and all colors are configurable in the Inspector.
///
/// FROZEN RULES: the menu button is ignored while the jumpscare is active
/// or the Game Over screen is up (GamePauseManager.PauseLocked also guards
/// this — belt and braces).
///
/// v1.0 — initial release with the NotDEER VR layer.
/// </summary>
[DisallowMultipleComponent]
public class VRPauseMenu : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Pause Button (left controller)")]

    [Tooltip("Button on the LEFT controller that opens/closes the menu. The keyboard pause key (Escape) keeps working too.")]
    [SerializeField] private VRXRInput.HandButton menuButton = VRXRInput.HandButton.Menu;

    [Header("Spawn (from the left hand)")]

    [Tooltip("How high above the left hand the menu spawns (meters).")]
    [SerializeField, Range(0f, 1f)] private float spawnHeightOffset = 0.22f;

    [Tooltip("How far from the left hand toward your face the menu spawns (meters).")]
    [SerializeField, Range(0f, 1f)] private float spawnForwardOffset = 0.35f;

    [Tooltip("If the menu ends up farther than this from the left hand while paused, it re-anchors there (meters).")]
    [SerializeField, Range(0.3f, 3f)] private float reAnchorDistance = 0.9f;

    [Header("Original Screen Menu")]

    [Tooltip("Hide GamePauseManager's screen-space menu while paused (in VR the hand menu replaces it).")]
    [SerializeField] private bool hideScreenPauseMenu = true;

    [Header("Menu Text")]

    [Tooltip("Title shown at the top of the menu.")]
    [SerializeField] private string menuTitle = "GAME MENU";

    [Tooltip("Text on the Resume button.")]
    [SerializeField] private string resumeButtonText = "Resume";

    [Header("Menu Colors")]

    [Tooltip("Background color of the panel.")]
    [SerializeField] private Color panelColor = new Color(0.08f, 0.08f, 0.08f, 0.95f);

    [Tooltip("Thin border color around the panel.")]
    [SerializeField] private Color panelBorderColor = new Color(1f, 1f, 1f, 0.25f);

    [Tooltip("Normal color of the Resume button.")]
    [SerializeField] private Color buttonColor = new Color(0.85f, 0.85f, 0.85f, 1f);

    [Tooltip("Text color on the Resume button.")]
    [SerializeField] private Color buttonTextColor = Color.black;

    [Tooltip("Color of the title.")]
    [SerializeField] private Color titleColor = Color.white;

    [Header("Events")]

    [Tooltip("Fired when the VR menu is spawned from the hand.")]
    [SerializeField] private UnityEvent onMenuShown = new UnityEvent();

    [Tooltip("Fired when the VR menu is hidden (game resumed).")]
    [SerializeField] private UnityEvent onMenuHidden = new UnityEvent();

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private GamePauseManager pauseManager;
    private Transform leftHand;
    private GameObject screenMenuCache;
    private bool screenMenuHideLogged;
    private Transform headCamera;

    private Canvas menuCanvas;
    private Text titleLabel;
    private Text buttonLabel;
    private Image buttonImage;
    private Image panelImage;
    private Button resumeButton;
    private Outline panelOutline;

    private float handSearchTimer = 1f;   // >= retry interval: the FIRST search happens immediately
    private bool wasPaused;
    private bool warnedNoManager;

    /// <summary>True while the VR menu panel is showing.</summary>
    public bool IsMenuVisible
    {
        get { return menuCanvas != null && menuCanvas.gameObject.activeSelf; }
    }

    /// <summary>The menu panel's canvas (built lazily on first show).</summary>
    public Canvas MenuCanvas
    {
        get { return menuCanvas; }
    }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Update()
    {
        // ---- the VR pause button (Escape keeps working via GamePauseManager) ----
        if (VRXRInput.GetButtonDown(XRNode.LeftHand, menuButton))
        {
            if (!JumpscareGameOverManager.IsJumpscareActive && !JumpscareGameOverManager.IsGameOver)
            {
                GamePauseManager manager = ResolvePauseManager();
                if (manager != null)
                {
                    if (!GamePauseManager.IsPaused) manager.PauseGame();
                    else manager.ResumeGame();
                }
            }
        }

        // ---- react to pause changes, no matter who caused them ----
        if (GamePauseManager.IsPaused && !wasPaused) ShowMenu();
        else if (!GamePauseManager.IsPaused && wasPaused) HideMenu();
        wasPaused = GamePauseManager.IsPaused;
    }

    private void LateUpdate()
    {
        if (!IsMenuVisible) return;

        // Keep the menu within reach: re-anchor at the hand if the player
        // physically wandered away from it (room-scale reality).
        Transform hand = ResolveLeftHand();
        if (hand != null && reAnchorDistance > 0f)
        {
            float distance = (menuCanvas.transform.position - hand.position).magnitude;
            if (distance > reAnchorDistance) PlaceAtHand(hand);
        }

        // Hide the original screen-space menu every late frame while paused
        // (GamePauseManager activates it once; this wins after that).
        if (hideScreenPauseMenu) HideScreenPauseMenu();
    }

    private void OnDisable()
    {
        // Scene reloads / play-mode stops with the menu open — drop it.
        if (IsMenuVisible) HideMenu();
    }

    /// <summary>Clamps Inspector values so the menu can never receive nonsense input.</summary>
    private void OnValidate()
    {
        if (reAnchorDistance < spawnForwardOffset) reAnchorDistance = spawnForwardOffset;
        if (string.IsNullOrEmpty(menuTitle)) menuTitle = "GAME MENU";
        if (string.IsNullOrEmpty(resumeButtonText)) resumeButtonText = "Resume";
    }

    // ------------------------------------------------------------------ //
    // Show / hide
    // ------------------------------------------------------------------ //

    private void ShowMenu()
    {
        EnsureMenuBuilt();
        if (menuCanvas == null) return;

        Transform hand = ResolveLeftHand();
        if (hand != null) PlaceAtHand(hand);

        menuCanvas.gameObject.SetActive(true);

        if (onMenuShown != null) onMenuShown.Invoke();
    }

    private void HideMenu()
    {
        if (menuCanvas != null) menuCanvas.gameObject.SetActive(false);

        if (onMenuHidden != null) onMenuHidden.Invoke();
    }

    /// <summary>Places the menu at the left hand: a touch above it, pulled toward the player's face, facing them.</summary>
    private void PlaceAtHand(Transform hand)
    {
        Transform head = ResolveHeadCamera();
        if (head == null) return;

        Vector3 towardHead = head.position - hand.position;
        towardHead.y = 0f;
        towardHead = towardHead.sqrMagnitude > 0.0001f ? towardHead.normalized : Flatten(head.forward);

        Vector3 target = hand.position + Vector3.up * spawnHeightOffset + towardHead * spawnForwardOffset;
        menuCanvas.transform.position = target;

        Vector3 toPanel = target - head.position;
        toPanel.y = 0f;
        menuCanvas.transform.rotation = toPanel.sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation(toPanel.normalized, Vector3.up)
            : Quaternion.LookRotation(Flatten(head.forward), Vector3.up);
    }

    private static Vector3 Flatten(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude > 0.0001f ? v.normalized : Vector3.forward;
    }

    // ------------------------------------------------------------------ //
    // The original screen menu (hidden while paused in VR)
    // ------------------------------------------------------------------ //

    private void HideScreenPauseMenu()
    {
        if (screenMenuCache == null)
        {
            screenMenuCache = GameObject.Find("PauseMenuCanvas");
            if (screenMenuCache == null) return;   // maybe not built yet — retry next frame
        }

        if (screenMenuCache.activeSelf)
        {
            screenMenuCache.SetActive(false);
            if (!screenMenuHideLogged)
            {
                screenMenuHideLogged = true;
                Debug.Log("[VRPauseMenu] Screen-space pause menu hidden while paused (the VR hand menu replaces it).", this);
            }
        }
    }

    // ------------------------------------------------------------------ //
    // Menu building (fully from code — same style as GamePauseManager)
    // ------------------------------------------------------------------ //

    private void EnsureMenuBuilt()
    {
        if (menuCanvas != null) return;

        Font font = GetBuiltInFont();

        GameObject canvasGO = new GameObject("VR Pause Menu Canvas");
        menuCanvas = canvasGO.AddComponent<Canvas>();
        menuCanvas.renderMode = RenderMode.WorldSpace;
        menuCanvas.sortingOrder = 30000;

        RectTransform canvasRect = canvasGO.GetComponent<RectTransform>();
        canvasRect.sizeDelta = new Vector2(640f, 380f);
        canvasGO.transform.localScale = new Vector3(0.001f, 0.001f, 0.001f);

        // Panel
        GameObject panelGO = new GameObject("Panel");
        panelImage = panelGO.AddComponent<Image>();
        panelImage.raycastTarget = true;
        panelImage.color = panelColor;
        RectTransform panel = panelImage.rectTransform;
        panel.SetParent(canvasGO.transform, false);
        panel.anchorMin = panel.anchorMax = panel.pivot = Vector2.one * 0.5f;
        panel.anchoredPosition = Vector2.zero;
        panel.sizeDelta = new Vector2(640f, 380f);

        panelOutline = panelGO.AddComponent<Outline>();
        panelOutline.effectColor = panelBorderColor;
        panelOutline.effectDistance = new Vector2(2f, -2f);

        // Title
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
        titleRt.anchoredPosition = new Vector2(0f, -40f);
        titleRt.sizeDelta = new Vector2(600f, 70f);

        // Resume button
        GameObject buttonGO = new GameObject("ResumeButton");
        buttonImage = buttonGO.AddComponent<Image>();
        buttonImage.raycastTarget = true;
        RectTransform buttonRt = buttonImage.rectTransform;
        buttonRt.SetParent(panel, false);
        buttonRt.anchorMin = buttonRt.anchorMax = buttonRt.pivot = Vector2.one * 0.5f;
        buttonRt.anchoredPosition = new Vector2(0f, -40f);
        buttonRt.sizeDelta = new Vector2(460f, 110f);

        resumeButton = buttonGO.AddComponent<Button>();
        resumeButton.transition = Button.Transition.ColorTint;
        resumeButton.onClick.AddListener(ResumeGame);

        // Button label
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

        // Style pass
        ApplyMenuStyle();

        // Register as a world UI: arm's-length size + white-line pointers.
        VRWorldUICanvas worldUI = canvasGO.AddComponent<VRWorldUICanvas>();
        worldUI.Mode = VRWorldUICanvas.FollowMode.None;   // it lives at the hand, not in front of the face
        worldUI.RefreshGraphicCache();

        menuCanvas.gameObject.SetActive(false);
    }

    private void ApplyMenuStyle()
    {
        if (menuCanvas == null) return;

        titleLabel.text = menuTitle;
        titleLabel.color = titleColor;
        titleLabel.fontSize = 44;

        buttonLabel.text = resumeButtonText;
        buttonLabel.color = buttonTextColor;
        buttonLabel.fontSize = 32;

        panelImage.color = panelColor;
        panelOutline.effectColor = panelBorderColor;

        ColorBlock colors = resumeButton.colors;
        colors.normalColor = buttonColor;
        colors.highlightedColor = Color.Lerp(buttonColor, Color.white, 0.25f);
        colors.pressedColor = Color.Lerp(buttonColor, Color.black, 0.25f);
        colors.selectedColor = Color.Lerp(buttonColor, Color.white, 0.25f);
        colors.disabledColor = buttonColor * 0.5f;
        colors.fadeDuration = 0.08f;
        resumeButton.colors = colors;
    }

    /// <summary>Wired to the Resume button — closes the menu through the real pause manager.</summary>
    public void ResumeGame()
    {
        GamePauseManager manager = ResolvePauseManager();
        if (manager != null) manager.ResumeGame();
    }

    // ------------------------------------------------------------------ //
    // Lazy references
    // ------------------------------------------------------------------ //

    private GamePauseManager ResolvePauseManager()
    {
        if (pauseManager != null) return pauseManager;

#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
        pauseManager = FindFirstObjectByType<GamePauseManager>();
#else
        pauseManager = FindObjectOfType<GamePauseManager>();
#endif
        if (pauseManager == null && !warnedNoManager)
        {
            warnedNoManager = true;
            Debug.LogWarning("[VRPauseMenu] No GamePauseManager in the scene — the VR menu button can " +
                             "do nothing. Add the GamePauseManager component to any GameObject.", this);
        }
        return pauseManager;
    }

    private Transform ResolveLeftHand()
    {
        if (leftHand != null) return leftHand;

        handSearchTimer += Time.unscaledDeltaTime;
        if (handSearchTimer < 0.25f) return null;
        handSearchTimer = 0f;

        VRHandPoseDriver[] drivers = GetComponentsInChildren<VRHandPoseDriver>();
        for (int i = 0; i < drivers.Length; i++)
        {
            if (drivers[i].Node == XRNode.LeftHand)
            {
                leftHand = drivers[i].transform;
                return leftHand;
            }
        }

        return null;
    }

    private Transform ResolveHeadCamera()
    {
        if (headCamera != null) return headCamera;

        Camera main = Camera.main;
        if (main != null) headCamera = main.transform;
        return headCamera;
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
