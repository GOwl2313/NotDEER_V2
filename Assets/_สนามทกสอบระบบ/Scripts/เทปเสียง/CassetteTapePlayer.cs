using UnityEngine;
using UnityEngine.UI;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// CASSETTE TAPE PLAYER (v1.0) — story tapes for event spots.
///
/// WHAT IT DOES
///   Put this component on an event spot (an empty object, a tape recorder
///   prop, an InteractionPoint — anything). When the PLAYER walks into its
///   trigger collider, a cassette tape recording starts playing FROM THE
///   PLAYER: the sound is 2D, so it is heard at full clarity no matter where
///   the player character is standing or walking while it plays.
///
///   While the tape plays, a "Press [Q] to skip" prompt shows at the middle
///   of the screen, a little below the crosshair — same style and layer as
///   the other prompts in the project (bold white text, black outline,
///   canvas sorting 250, far below the pause menu). Pressing the skip key,
///   or the tape reaching its end, stops the sound and removes the prompt.
///
/// ZERO SETUP
///   • Drag ANY audio file Unity imports (.mp3, .ogg, .wav) into the
///     "Tape Clip" field — that is the entire setup.
///   • No trigger collider on the object? A BoxCollider walk-in zone
///     (Center 0,1,0 / Size 2,2,2) is created automatically on Play, exactly
///     like PlayerHitbox's dead zone — add your own trigger collider in edit
///     mode to control the zone and silence the info message.
///   • The skip prompt canvas builds itself from code the first time a tape
///     plays (same conventions as your other HUDs: Screen Space Overlay,
///     sorting 250, 1920x1080 reference scaling). Nothing to wire.
///   • Works on BOTH input backends (legacy Input Manager and the new Input
///     System — keep KeyCodeBridge.cs in the project).
///   • The skip key shares the project's key arbitration (KeyCodeBridge):
///     one press performs one action, even if other systems listen to Q.
///
/// TRIGGER MODES
///   OnPlayerEnter — the tape starts the moment the player walks in (default).
///   ManualOnly   — this script never starts the tape by itself. Call
///                  PlayTape() from any UnityEvent (an InteractionPoint's
///                  activation event, a UI button, another script...) — the
///                  public API is listed below.
///
/// RULES IT FOLLOWS
///   • Only ONE tape plays at a time: a new tape starting stops the previous
///     one (the previous tape's prompt disappears with it).
///   • Walking into the SAME spot again while its tape is already playing
///     does not restart it; after it ended, walking in again replays it
///     (turn "Replay On Re-enter" off to make a tape play only once).
///   • The tape keeps playing while the player walks anywhere (2D audio) —
///     it only stops on SKIP, on reaching its end, when this component is
///     disabled/destroyed, or when the pause menu is open (it freezes with
///     the rest of the game and resumes after unpausing — same behaviour as
///     every other gameplay system here).
///   • Keep this component on an object that stays ACTIVE during playback
///     (an own empty "Event Spot" object is perfect): when its object turns
///     off, the tape and the prompt stop with it.
/// </summary>
[DisallowMultipleComponent]
public class CassetteTapePlayer : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Cassette Tape")]

    [Tooltip("The recording played when this spot triggers. Any audio file Unity imports — .mp3, .ogg or .wav.")]
    [SerializeField] private AudioClip tapeClip;

    [Tooltip("How loud the tape plays (1 = full volume).")]
    [SerializeField, Range(0f, 1f)] private float volume = 1f;

    [Tooltip("OnPlayerEnter = the tape starts the moment the player walks into this object's trigger collider. " +
             "ManualOnly = this script never starts the tape itself — call PlayTape() from any UnityEvent " +
             "(InteractionPoint activation, UI button, another script).")]
    [SerializeField] private CassetteTriggerMode triggerMode = CassetteTriggerMode.OnPlayerEnter;

    [Header("Walk-In Zone (OnPlayerEnter mode)")]

    [Tooltip("Tag that identifies the player. Checked on the entering collider AND on all of its parents, " +
             "so the player's collider may sit anywhere in the player hierarchy.")]
    [SerializeField] private string playerTag = "Player";

    [Tooltip("Replay the tape when the player walks in again after it ended. " +
             "Turn OFF to make the tape play only once (until the scene is reloaded).")]
    [SerializeField] private bool replayOnReenter = true;

    [Tooltip("Center of the auto-created walk-in zone (used ONLY when the object has no trigger collider of its own).")]
    [SerializeField] private Vector3 triggerZoneCenter = new Vector3(0f, 1f, 0f);

    [Tooltip("Size of the auto-created walk-in zone (used ONLY when the object has no trigger collider of its own).")]
    [SerializeField] private Vector3 triggerZoneSize = new Vector3(2f, 2f, 2f);

    [Header("Skip")]

    [Tooltip("Allow the player to stop the tape early with the skip key.")]
    [SerializeField] private bool allowSkip = true;

    [Tooltip("Key that skips the tape. Works on both the legacy Input Manager and the new Input System " +
             "(keep KeyCodeBridge.cs in the project for the new Input System).")]
    [SerializeField] private KeyCode skipKey = KeyCode.Q;

    [Header("Skip Prompt UI")]

    [Tooltip("Colour of the 'Press [Q] to skip' text.")]
    [SerializeField] private Color promptColor = Color.white;

    [Tooltip("Font size of the skip prompt text.")]
    [SerializeField, Range(10, 80)] private int promptFontSize = 30;

    [Tooltip("How far below the middle of the screen the skip prompt sits (pixels — clear of the crosshair).")]
    [SerializeField, Min(0f)] private float promptOffsetY = 160f;

    [Tooltip("Sorting order of the prompt canvas. 250 = the same layer as the InteractionPoints' prompts " +
             "(above the HUD, far below the pause menu).")]
    [SerializeField] private int canvasSortingOrder = 250;

    [Tooltip("Custom prompt text. Leave EMPTY for the automatic 'Press [Q] to skip' (follows the skip key).")]
    [SerializeField] private string customPromptText = "";

    [Header("Behaviour")]

    [Tooltip("Freeze the tape while the pause menu is open and resume it after unpausing (same behaviour as the rest of the game).")]
    [SerializeField] private bool respectPause = true;

    [Tooltip("Write tape start/stop events to the Console.")]
    [SerializeField] private bool debugLogs = false;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    /// <summary>The one tape that is currently playing anywhere (null while none plays).</summary>
    private static CassetteTapePlayer activeTape;

    /// <summary>True while this tape is playing (frozen tapes count as playing).</summary>
    public bool IsPlaying { get; private set; }

    /// <summary>True once this tape has played at least once this session.</summary>
    public bool HasPlayedThisSession { get; private set; }

    private AudioSource tapeSource;        // dedicated source — never fights other AudioSources
    private Transform playerRoot;          // cached from the trigger event / tag lookup
    private bool warnedNoPlayer;
    private bool warnedNoClip;
    private bool pausedByPauseMenu;

    private Canvas promptCanvas;           // built lazily on first play
    private Text promptLabel;

    // cached values — every UI write below happens ONLY on a real change
    // (the label itself holds the current size/colour/offset, so those are
    // compared against the label directly instead of caching a second copy)
    private string lastPromptText;
    private KeyCode lastPromptKey;
    private string lastCustomText;

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        if (triggerMode == CassetteTriggerMode.OnPlayerEnter) EnsureTriggerCollider();
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!enabled) return;
        if (triggerMode != CassetteTriggerMode.OnPlayerEnter) return;

        GameObject playerRootGO;
        if (!IsPlayerCollider(other, out playerRootGO)) return;   // anything that is not the player passes silently

        playerRoot = playerRootGO.transform;                      // free player reference for the audio host

        if (IsPlaying) return;                                    // already playing this tape — no restart
        if (!replayOnReenter && HasPlayedThisSession)
        {
            if (debugLogs) Debug.Log("[CassetteTapePlayer] '" + name + "': already played this session — ignoring re-enter.", this);
            return;
        }

        PlayTape();
    }

    private void Update()
    {
        if (!IsPlaying) return;                                   // idle: zero per-frame cost

        // ---- pause freeze (the tape resumes exactly where it froze) ---- //
        if (respectPause && GamePauseManager.IsPaused)
        {
            if (!pausedByPauseMenu && tapeSource != null)
            {
                tapeSource.Pause();
                pausedByPauseMenu = true;
                if (debugLogs) Debug.Log("[CassetteTapePlayer] '" + name + "': frozen by the pause menu.", this);
            }
            return;                                              // frozen: no skip, no end detection this frame
        }
        if (pausedByPauseMenu)
        {
            if (tapeSource != null) tapeSource.UnPause();
            pausedByPauseMenu = false;
        }

        // ---- skip (shares the project's one-press-one-action arbitration) ---- //
        if (allowSkip && SkipPressedThisFrame() && !KeyCodeBridge.WasKeyConsumedThisFrame(skipKey))
        {
            KeyCodeBridge.ConsumeKey(skipKey);
            StopTape();
            return;
        }

        // ---- natural end (player destroyed mid-play counts as ended too) ---- //
        if (tapeSource == null || !tapeSource.isPlaying)
        {
            StopTape();
            return;
        }

        // ---- live tuning while playing (guarded — writes only on change) ---- //
        if (tapeSource.volume != volume) tapeSource.volume = volume;
        SyncPromptVisuals();
    }

    private void OnDisable()
    {
        // component off or GameObject deactivated — the tape and the prompt
        // stop with it (documented rule: never a stuck prompt, never a
        // runaway tape nobody can skip).
        if (IsPlaying) StopTape();
    }

    private void OnDestroy()
    {
        if (IsPlaying) StopTape();
        if (activeTape == this) activeTape = null;

        // the prompt canvas was built as a ROOT object — it must never
        // outlive this component (same rule as ProximityPromptUI).
        if (promptCanvas != null)
        {
            Destroy(promptCanvas.gameObject);
            promptCanvas = null;
            promptLabel = null;
        }
    }

    /// <summary>Play-mode entry reset — correct even with Domain Reload disabled.</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        activeTape = null;
    }

    // ------------------------------------------------------------------ //
    // Public API (UnityEvent-wireable)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Starts the tape (does nothing if it is already playing, if no clip is
    /// assigned, or if a once-per-session tape already played). Stops any
    /// other tape first — only one tape plays at a time.
    /// </summary>
    public void PlayTape()
    {
        if (IsPlaying) return;

        if (tapeClip == null)
        {
            if (!warnedNoClip)
            {
                warnedNoClip = true;
                Debug.LogWarning("[CassetteTapePlayer] '" + name + "': no Tape Clip assigned — nothing to play. " +
                                 "Drag an audio file (.mp3/.ogg/.wav) into the 'Tape Clip' field.", this);
            }
            return;
        }
        if (!replayOnReenter && HasPlayedThisSession) return;

        // one tape at a time — the new tape replaces the old one
        // (fake-null safe: a destroyed previous tape simply skips this)
        if (activeTape != null && activeTape != this) activeTape.StopTape();

        EnsureTapeSource();

        tapeSource.clip = tapeClip;
        tapeSource.volume = volume;
        tapeSource.spatialBlend = 0f;     // 2D: full clarity anywhere in the scene
        tapeSource.loop = false;
        tapeSource.playOnAwake = false;
        tapeSource.Play();

        IsPlaying = true;
        HasPlayedThisSession = true;
        pausedByPauseMenu = false;
        activeTape = this;

        ShowPrompt();
        SyncPromptVisuals();

        if (debugLogs) Debug.Log("[CassetteTapePlayer] '" + name + "': playing '" + tapeClip.name + "' (" + tapeClip.length.ToString("0.#") + " s).", this);
    }

    /// <summary>Stops the tape and removes the skip prompt.</summary>
    public void StopTape()
    {
        if (tapeSource != null) tapeSource.Stop();

        IsPlaying = false;
        if (activeTape == this) activeTape = null;
        HidePrompt();

        if (debugLogs) Debug.Log("[CassetteTapePlayer] '" + name + "': tape stopped.", this);
    }

    /// <summary>Plays the tape when idle, stops it when playing (UI-button friendly).</summary>
    public void ToggleTape()
    {
        if (IsPlaying) StopTape();
        else PlayTape();
    }

    // ------------------------------------------------------------------ //
    // Input (both backends, through the project's KeyCodeBridge)
    // ------------------------------------------------------------------ //

    private bool SkipPressedThisFrame()
    {
#if ENABLE_INPUT_SYSTEM
        return KeyCodeBridge.WasKeyPressedThisFrame(Keyboard.current, skipKey);
#else
        return Input.GetKeyDown(skipKey);
#endif
    }

    // ------------------------------------------------------------------ //
    // Audio host — a dedicated AudioSource ON the player
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Makes sure a dedicated AudioSource exists (reused for every replay).
    /// It lives ON THE PLAYER when the player is known — the playing sound
    /// is 2D either way, so it is heard at full clarity everywhere.
    /// </summary>
    private void EnsureTapeSource()
    {
        if (tapeSource != null) return;   // also survives destroy: Unity fake-null

        Transform host = ResolvePlayerRoot();
        if (host == null) host = transform;   // no player found — the sound is 2D anyway

        GameObject go = new GameObject("Cassette Tape Audio (auto)");
        go.transform.SetParent(host, false);
        tapeSource = go.AddComponent<AudioSource>();
        tapeSource.playOnAwake = false;
        tapeSource.spatialBlend = 0f;
    }

    private Transform ResolvePlayerRoot()
    {
        if (playerRoot != null) return playerRoot;   // Unity fake-null: survives destroy

        GameObject player = FindPlayerByTag();
        if (player != null)
        {
            playerRoot = player.transform;
            return playerRoot;
        }

        if (!warnedNoPlayer)
        {
            warnedNoPlayer = true;
            Debug.LogWarning("[CassetteTapePlayer] '" + name + "': no GameObject tagged '" + playerTag +
                             "' found — the tape plays from this spot instead (still 2D, still clearly audible).", this);
        }
        return null;
    }

    /// <summary>Tag lookup that survives a tag that does not exist in the project yet.</summary>
    private GameObject FindPlayerByTag()
    {
        if (string.IsNullOrEmpty(playerTag)) return null;
        try
        {
            return GameObject.FindGameObjectWithTag(playerTag);
        }
        catch
        {
            return null;   // the tag is not defined — the warning above already said it
        }
    }

    // ------------------------------------------------------------------ //
    // Player detection (same walk-up-the-hierarchy rule as InteractionPoint)
    // ------------------------------------------------------------------ //

    private bool IsPlayerCollider(Collider other, out GameObject playerRootGO)
    {
        playerRootGO = null;
        if (other == null || string.IsNullOrEmpty(playerTag)) return false;

        Transform t = other.transform;
        while (t != null)
        {
            bool match = false;
            try { match = t.CompareTag(playerTag); } catch { }
            if (match)
            {
                playerRootGO = t.gameObject;
                return true;
            }
            t = t.parent;
        }
        return false;
    }

    // ------------------------------------------------------------------ //
    // Walk-in zone (auto-created ONLY when the object has no trigger —
    // exactly the PlayerHitbox pattern)
    // ------------------------------------------------------------------ //

    private void EnsureTriggerCollider()
    {
        Collider[] colliders = GetComponents<Collider>();
        for (int i = 0; i < colliders.Length; i++)
        {
            if (colliders[i] != null && colliders[i].isTrigger) return;   // already has a walk-in zone
        }

        BoxCollider zone = gameObject.AddComponent<BoxCollider>();
        zone.isTrigger = true;
        zone.center = triggerZoneCenter;
        zone.size = triggerZoneSize;

        Debug.Log("[CassetteTapePlayer] No trigger collider found on '" + name + "' — created a BoxCollider walk-in zone " +
                  "(Center " + triggerZoneCenter + ", Size " + triggerZoneSize + "). Add your own trigger collider in edit mode " +
                  "to control the zone and silence this message.", this);
    }

    // ------------------------------------------------------------------ //
    // Skip prompt UI (built from code — no assets, house conventions)
    // ------------------------------------------------------------------ //

    private void ShowPrompt()
    {
        EnsureBuilt();
        if (promptCanvas == null || promptLabel == null) return;

        if (!promptCanvas.enabled) promptCanvas.enabled = true;
    }

    private void HidePrompt()
    {
        if (promptCanvas != null && promptCanvas.enabled) promptCanvas.enabled = false;
    }

    private void EnsureBuilt()
    {
        if (promptCanvas != null) return;   // also rebuilds if someone deleted the canvas
        BuildPrompt();
    }

    /// <summary>Builds the overlay canvas with one outlined text — same style as the InteractionPoints' prompts.</summary>
    private void BuildPrompt()
    {
        GameObject root = new GameObject("Cassette Skip Prompt (auto)");
        root.AddComponent<RectTransform>();
        promptCanvas = root.AddComponent<Canvas>();
        promptCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        promptCanvas.sortingOrder = canvasSortingOrder;
        promptCanvas.enabled = false;

        CanvasScaler scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        GameObject labelGO = new GameObject("Skip Prompt");
        labelGO.AddComponent<RectTransform>();
        labelGO.transform.SetParent(root.transform, false);

        promptLabel = labelGO.AddComponent<Text>();
        promptLabel.font = LoadBuiltinFont();
        promptLabel.fontSize = promptFontSize;
        promptLabel.fontStyle = FontStyle.Bold;
        promptLabel.color = promptColor;
        promptLabel.alignment = TextAnchor.MiddleCenter;
        promptLabel.horizontalOverflow = HorizontalWrapMode.Overflow;   // any key name — never clips
        promptLabel.verticalOverflow = VerticalWrapMode.Overflow;
        promptLabel.raycastTarget = false;                              // never blocks clicks
        promptLabel.text = "";

        RectTransform rt = promptLabel.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);   // the screen middle…
        rt.anchoredPosition = new Vector2(0f, -promptOffsetY);               // …a little below it
        rt.sizeDelta = new Vector2(800f, 80f);

        // black outline so the text stays readable on any background
        Shadow shadow = labelGO.AddComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
        shadow.effectDistance = new Vector2(2f, -2f);

        // seed the change detectors so the first Sync writes everything once
        lastPromptText = null;
        lastPromptKey = KeyCode.None;
        lastCustomText = null;
    }

    /// <summary>Live, equality-guarded sync — nothing is written while nothing changed.</summary>
    private void SyncPromptVisuals()
    {
        if (promptLabel == null) return;

        if (promptLabel.fontSize != promptFontSize)
        {
            promptLabel.fontSize = promptFontSize;
        }

        if (promptLabel.color != promptColor)
        {
            promptLabel.color = promptColor;
        }

        float offset = -promptOffsetY;
        Vector2 pos = promptLabel.rectTransform.anchoredPosition;
        if (pos.y != offset)
        {
            promptLabel.rectTransform.anchoredPosition = new Vector2(pos.x, offset);
        }

        string text = EffectivePromptText();
        if (promptLabel.text != text)
        {
            promptLabel.text = text;
        }
    }

    /// <summary>
    /// The prompt text: the custom text when one is set, otherwise the
    /// automatic "Press [&lt;key&gt;] to skip" (rebuilt ONLY when the key or
    /// the custom text actually changed — zero steady-state allocations).
    /// </summary>
    private string EffectivePromptText()
    {
        if (!string.IsNullOrEmpty(customPromptText)) return customPromptText;

        if (lastPromptKey != skipKey || lastCustomText != customPromptText)
        {
            lastPromptKey = skipKey;
            lastCustomText = customPromptText;
            lastPromptText = "Press [" + skipKey + "] to skip";
        }
        return lastPromptText;
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

    // ------------------------------------------------------------------ //
    // Inspector validation
    // ------------------------------------------------------------------ //

    /// <summary>Clamps Inspector values so the tape can never receive nonsense input.</summary>
    private void OnValidate()
    {
        volume = Mathf.Clamp01(volume);
        promptFontSize = Mathf.Clamp(promptFontSize, 10, 80);
        promptOffsetY = Mathf.Max(0f, promptOffsetY);
        canvasSortingOrder = Mathf.Clamp(canvasSortingOrder, 0, 32767);
        if (string.IsNullOrEmpty(playerTag)) playerTag = "Player";
        triggerZoneSize = new Vector3(
            Mathf.Max(0.1f, triggerZoneSize.x),
            Mathf.Max(0.1f, triggerZoneSize.y),
            Mathf.Max(0.1f, triggerZoneSize.z));
    }
}

/// <summary>How the tape gets started.</summary>
public enum CassetteTriggerMode
{
    /// <summary>The tape starts the moment the player walks into the trigger collider.</summary>
    OnPlayerEnter,

    /// <summary>Never starts by itself — call PlayTape() from any UnityEvent.</summary>
    ManualOnly
}