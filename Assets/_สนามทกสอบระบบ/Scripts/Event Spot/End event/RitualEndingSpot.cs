using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityEngine.Video;

/// <summary>
/// THE END-GAME RITUAL SPOT (v1.0) — the victory ending sequence.
///
/// WHAT HAPPENS when the player enters the ritual spot with all totems:
///
///   1. FREEZE      — everything in the game freezes in place (like pausing,
///                    but no menu): every script on the player and the camera
///                    is paused, the enemy spawners are switched off, the
///                    pause menu is locked so it can never open on top of the
///                    sequence, and the cursor stays hidden.
///   2. FADE        — after a short beat the whole screen slowly fades to
///                    black. The exact same moment the fade starts, every
///                    enemy in the scene despawns and spawning stops for
///                    good — nothing can ever interrupt the ending.
///   3. WAIT        — the screen stays fully black for a moment (1 s by
///                    default).
///   4. VIDEO       — your ending cutscene plays on the ENTIRE screen. Drag
///                    ANY video clip (.mp4 / .webm / .mov …) into the
///                    "Ending Video" slot — exactly like the jumpscare
///                    system's video slot. Leave it empty while you do not
///                    have the video yet: the sequence still completes
///                    (fade → wait → short hold → game ends), so you can
///                    test the whole flow today and drop the video in later.
///   5. HOLD        — after the video ends the screen stays black for a
///                    final moment (0.5 s by default).
///   6. END         — the game ends cleanly (in the Editor: Play mode stops;
///                    in a build: the application quits). Every value — fade
///                    speed, both waits, video options — is adjustable in
///                    the Inspector. When you build your main menu later,
///                    replace the single QuitGame() call at the bottom of
///                    this file with your menu-loading code.
///
/// ONLY ACTIVE + VISIBLE AFTER ALL 4 TOTEMS:
///   The spot is completely dormant and invisible until the player has
///   collected all required totems ("Totem" items, 4 by default — the same
///   tracking your TotemCollectionHUD does, counted independently so the
///   HUD is optional). At 4/4 the spot appears (marker + your own optional
///   props) and becomes usable. Two extra trigger routes: the HUD's
///   'On All Totems Collected' event can call RevealRitualSpot(), and
///   TotemCollectionHUD.IsComplete is checked automatically as a fallback.
///
/// A PLUG-IN FOR YOUR EVENT SPOTS (zero wiring):
///   This is an InteractionPointListener — the plug-in base class of the
///   InteractionPoint system. Setup:
///     1. Make your ritual spot an event spot: an empty GameObject with an
///        InteractionPoint on it (both activation modes work —
///        "Trigger On Enter" = walking in starts the ending, which matches
///        "once player enters"; "Press Key" = stand inside + press E).
///     2. Add THIS component to the SAME GameObject as the InteractionPoint
///        (a child of it also works — the point finds listeners
///        automatically, nothing to wire).
///     3. Drag your ending video into "Ending Video" (optional).
///   Done. It also works standalone: TriggerEnding() / ForceEnding() are
///   public methods you can call from any UnityEvent or script.
///
/// TOP LAYER — NOTHING CAN BLOCK IT:
///   The fade + video overlay canvas is created the moment the ending
///   starts, at sorting order 32767 (the maximum — above the jumpscare
///   overlay's 32766 and level with the pause menu, which is locked and
///   force-closed so it can never sit on top). A full-screen invisible
///   click-blocker on that canvas swallows all input, and if a jumpscare /
///   Game Over is already running the ritual politely refuses to start
///   (and vice versa — the ending freezes PlayerHitbox, so no jumpscare
///   can begin afterwards).
///
/// TECH NOTES (why it is built this way):
///   • Time.timeScale stays 1 (like the jumpscare sequence) so the fade and
///     video keep running while the WORLD is frozen script-by-script.
///   • The ending runs entirely in this component's Update — the freeze
///     only disables scripts on the PLAYER / CAMERA roots, never here.
///   • The video plays through Unity's built-in VideoPlayer into a
///     RenderTexture shown by a fullscreen RawImage (Aspect-Ratio-Fitter
///     letterboxing) — the same proven pipeline as the jumpscare system.
///   • The video image sits ABOVE the fade image (the fade happens BEFORE
///     the video, opposite order to the jumpscare) — black bars around the
///     video are simply the black fade behind it.
///   • Prepare watchdog + absolute safety timeout guarantee the ending can
///     NEVER get stuck on a slow or broken video file.
///   • Quit path: releases the RenderTexture, stops + unsubscribes the
///     VideoPlayer, removes the overlay, unlocks the pause system and
///     resets its statics FIRST, then quits — no leaked resources, no
///     errors on exit, safe in the Editor and in builds.
///
/// v1.0 — initial release.
/// </summary>
[DisallowMultipleComponent]
public class RitualEndingSpot : InteractionPointListener
{
    /// <summary>The phases of the ending sequence.</summary>
    public enum EndingState
    {
        Idle,               // playing normally (spot may still be gated/hidden)
        FreezeDelay,        // world frozen, brief beat before the fade begins
        Fading,             // slowly fading the screen to black (enemies despawn here)
        WaitingAfterFade,   // fully black, holding before the video
        Video,              // the ending cutscene is playing
        HoldingAfterVideo,  // video over, still black, final hold
        Ended               // the game has been ended
    }

    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Ritual Spot Gate (totems)")]

    [Tooltip("Which items count towards opening the ritual spot: only ItemPickup components with this Item Id are counted (same value as your TotemCollectionHUD uses).")]
    [SerializeField] private string trackedItemId = "Totem";

    [Tooltip("How many totems the player must collect before the ritual spot appears and becomes usable (4 in your game).")]
    [SerializeField, Min(1)] private int requiredTotems = 4;

    [Tooltip("Count collected items automatically and reveal the spot when the required number is reached. OFF = only RevealRitualSpot() or the HUD route can open it.")]
    [SerializeField] private bool listenForPickups = true;

    [Tooltip("Also watch the TotemCollectionHUD (when one exists): when the HUD reports 4/4 the spot reveals itself. A zero-wiring fallback — works even if this script was added after the totems were already collected.")]
    [SerializeField] private bool checkTotemHUD = true;

    [Tooltip("Hide the spot completely (marker + extra visuals, interaction disabled) until all totems are collected. OFF = the spot is always visible, but still only ACTIVATES after all totems.")]
    [SerializeField] private bool hideUntilRevealed = true;

    [Tooltip("Your own ritual props (candles, circle mesh, particles …): hidden together with the marker until the totems are complete, revealed together with it. Optional.")]
    [SerializeField] private GameObject[] extraVisuals = new GameObject[0];

    [Header("Ending Video (drag any video file here)")]

    [Tooltip("The ending cutscene shown on the whole screen. Drag ANY video clip from your Project window into this slot (.mp4 / .webm / .mov …) — same as the jumpscare system's video slot. Leave empty to skip the video (useful while you do not have it yet: fade → wait → hold → end).")]
    [SerializeField] private VideoClip endingVideo = null;

    [Tooltip("Volume of the ending video's own audio track (0..1).")]
    [Range(0f, 1f)][SerializeField] private float videoVolume = 1f;

    [Tooltip("Fit the video inside the screen keeping its proportions (black bars where it does not fill). OFF = stretch edge-to-edge to the whole screen.")]
    [SerializeField] private bool keepVideoAspectRatio = true;

    [Tooltip("Wait until the first frame is decoded before playing the video (no one-frame flash).")]
    [SerializeField] private bool waitForFirstFrame = true;

    [Tooltip("Seconds to wait for the video to prepare before playing it anyway (slow disks).")]
    [SerializeField] private float videoPrepareTimeout = 2f;

    [Tooltip("ABSOLUTE safety cap: if the video has not ended after this many seconds (corrupt / unsupported file), the ending continues anyway — the sequence can never get stuck on a broken video.")]
    [SerializeField] private float videoSafetyTimeout = 60f;

    [Header("Ending Timing (all adjustable)")]

    [Tooltip("Seconds the world stays frozen before the fade to black begins (a short beat so the freeze reads as a moment).")]
    [SerializeField] private float freezeDelay = 0.5f;

    [Tooltip("Seconds the slow fade to black takes.")]
    [SerializeField] private float fadeToBlackDuration = 3f;

    [Tooltip("Seconds the screen stays fully black AFTER the fade and BEFORE the video starts.")]
    [SerializeField] private float waitAfterFade = 1f;

    [Tooltip("Seconds the screen stays black AFTER the video ends and BEFORE the game quits.")]
    [SerializeField] private float postVideoHold = 0.5f;

    [Header("World At Trigger")]

    [Tooltip("Despawn every enemy in the scene and stop all spawning when the fade to black begins (enemies vanish behind the growing black screen).")]
    [SerializeField] private bool despawnEnemies = true;

    [Tooltip("Optional: the player to freeze when the ending starts. Auto-resolved from the interaction (or the 'Player' tag) when empty.")]
    [SerializeField] private Transform playerOverride = null;

    [Header("Events")]

    [Tooltip("Fired ONCE when the ritual spot is revealed (all totems collected) — hook your own reveal sound / effect here.")]
    [SerializeField] private UnityEvent onRevealed = new UnityEvent();

    [Tooltip("Fired once when the ending sequence starts (the moment the player enters the spot with all totems).")]
    [SerializeField] private UnityEvent onEndingStarted = new UnityEvent();

    [Header("Screen Layer")]

    [Tooltip("Sorting order of the fade + video overlay canvas. 32767 = the absolute maximum — above the jumpscare overlay (32766) and level with the pause menu (which is locked and force-closed so it can never block the ending).")]
    [SerializeField] private int canvasSortingOrder = 32767;

    [Header("Debug")]

    [Tooltip("Write every phase change, reveal and enemy despawn to the Console.")]
    [SerializeField] private bool debugLogs = false;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private InteractionPoint point;            // the event spot this plugs into
    private Transform markerVisual;            // the spot's "Interaction Marker Visual" child
    private bool warnedNoPoint;

    private Canvas overlayCanvas;              // built when the ending starts
    private Image fadeImage;                   // the slow fade to black (also the video's black bars)
    private RawImage videoImage;               // the ending video (ABOVE the fade)
    private AspectRatioFitter videoAspectFitter;

    private VideoPlayer videoPlayer;
    private RenderTexture videoTexture;

    private float stateTimer;
    private bool videoStarted;
    private bool videoEnded;
    private bool prepareFallbackUsed;
    private bool warnedNoVideo;
    private bool endingOwnedByMe;              // this instance started the active ending

    // ------------------------------------------------------------------ //
    // Public API
    // ------------------------------------------------------------------ //

    /// <summary>Current phase of the ending sequence (Idle = playing normally).</summary>
    public EndingState State { get; private set; }

    /// <summary>True once the required totems are collected and the spot has appeared.</summary>
    public bool IsRevealed { get; private set; }

    /// <summary>How many tracked totems this spot has counted so far (clamped at the requirement).</summary>
    public int CollectedTotems { get; private set; }

    /// <summary>True from the moment the player enters the spot with all totems until the game ends.</summary>
    public static bool IsEndingActive { get; private set; }

    /// <summary>True after the ending fully completed (the frame the game was ended). Resets on scene reload.</summary>
    public static bool HasEnded { get; private set; }

    /// <summary>
    /// Reveals the ritual spot immediately (idempotent — safe to call any number
    /// of times, from UnityEvents, code or the HUD's 'On All Totems Collected'
    /// event). Does nothing once revealed or once the ending has started.
    /// </summary>
    public void RevealRitualSpot()
    {
        if (IsRevealed || State != EndingState.Idle) return;

        IsRevealed = true;
        ApplyGateVisuals();

        if (debugLogs)
            Debug.Log("[RitualEndingSpot] Revealed — all " + requiredTotems + " totems collected. The ritual spot is now active.", this);

        if (onRevealed != null) onRevealed.Invoke();
    }

    /// <summary>
    /// Starts the ending sequence NOW (respects the totem gate — the same
    /// thing that happens when the player enters the spot). Returns true
    /// when the sequence actually started. Wire this to any UnityEvent.
    /// </summary>
    public bool TriggerEnding()
    {
        return StartEnding(ResolvePlayerRoot());
    }

    /// <summary>
    /// Starts the ending sequence NOW, BYPASSING the totem gate (for testing
    /// or alternate endings). Returns true when the sequence actually started.
    /// </summary>
    public bool ForceEnding()
    {
        if (State != EndingState.Idle) return false;
        IsRevealed = true;
        return StartEnding(ResolvePlayerRoot());
    }

    /// <summary>
    /// The InteractionPoint plug-in hook: the player activated the spot.
    /// The ending starts only when the totems are complete; the press is
    /// consumed (point follows its Usage Mode) so nothing fires twice.
    /// </summary>
    public override bool OnPointActivated(InteractionPoint activatedPoint, GameObject player)
    {
        // Never compete with a running jumpscare / Game Over — death wins.
        if (JumpscareGameOverManager.IsJumpscareActive || JumpscareGameOverManager.IsGameOver) return false;

        // The totem gate: before 4/4 the ritual is inert (the point stays
        // ready and untouched — its marker is hidden anyway).
        if (!IsRevealed) return false;

        return StartEnding(player);
    }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        // Statics survive scene reloads — a freshly loaded scene always
        // starts clean (same rule as the jumpscare manager). Guarded so a
        // duplicate component added at runtime mid-ending can never stomp
        // the running sequence's static state.
        if (!IsEndingActive)
        {
            IsEndingActive = false;
            HasEnded = false;
        }
    }

    private void Start()
    {
        // All Awakes (including the InteractionPoint's, which builds the
        // marker visual) have run by now — safe to cache and to hide.
        point = GetComponentInParent<InteractionPoint>();
        if (point == null)
        {
            if (!warnedNoPoint)
            {
                warnedNoPoint = true;
                Debug.LogWarning("[RitualEndingSpot] No InteractionPoint found on '" + name +
                                 "' or its parents. This plug-in is designed for event spots — add an " +
                                 "InteractionPoint to the same GameObject (or a parent) so the player can " +
                                 "trigger the ending. You can still start it from code via TriggerEnding().", this);
            }
        }
        else
        {
            markerVisual = FindMarkerVisual(point.transform);
        }

        // Fresh scene, everything complete already? (script added late / a
        // saved 4/4 state) — reveal right away instead of waiting forever.
        if (!IsRevealed && checkTotemHUD && AnyHUDComplete()) RevealRitualSpot();

        ApplyGateVisuals();
    }

    private void OnEnable()
    {
        // The ONLY link to the pickup system: the same static event the HUD
        // listens to. Subscribe/unsubscribe symmetrically — no leaks.
        if (listenForPickups) ItemPickup.AnyItemPickedUp += HandleItemPickedUp;
    }

    private void OnDisable()
    {
        ItemPickup.AnyItemPickedUp -= HandleItemPickedUp;
    }

    private void Update()
    {
        // ---- gate enforcement: keep the spot perfectly hidden/inert while
        //      locked (cheap flag writes — Unity equality-guarded, no churn).
        if (State == EndingState.Idle && !IsRevealed) ApplyGateVisuals();

        // ---- the ending sequence state machine ----
        if (State == EndingState.Idle || State == EndingState.Ended) return;

        stateTimer += Time.deltaTime;

        switch (State)
        {
            case EndingState.FreezeDelay:
                if (stateTimer >= Mathf.Max(0f, freezeDelay)) EnterFading();
                break;

            case EndingState.Fading:
                {
                    // The fade itself (slowly, over the adjustable duration).
                    float t = Mathf.Clamp01(stateTimer / Mathf.Max(0.01f, fadeToBlackDuration));
                    SetFadeAlpha(t);
                    if (t >= 1f) EnterWaitingAfterFade();
                }
                break;

            case EndingState.WaitingAfterFade:
                if (stateTimer >= Mathf.Max(0f, waitAfterFade)) EnterVideo();
                break;

            case EndingState.Video:
                UpdateVideoPhase();
                break;

            case EndingState.HoldingAfterVideo:
                if (stateTimer >= Mathf.Max(0f, postVideoHold)) EnterEnded();
                break;
        }
    }

    private void LateUpdate()
    {
        if (State == EndingState.Idle || State == EndingState.Ended) return;

        // SAFETY NET 1 — the pause menu must never sit on top of the ending
        // (and Time.timeScale = 0 would freeze the fade). If a pause keypress
        // landed in the same frame the ending started, force the game back to
        // running right here. Same self-healing style the jumpscare sequence
        // uses for the cursor.
        if (GamePauseManager.IsPaused) ForceResumeGame();

        // SAFETY NET 2 — the cursor stays hidden and locked for the whole
        // ending (there is nothing to click; it is released by the editor /
        // application when the game ends).
        if (Cursor.lockState != CursorLockMode.Locked || Cursor.visible)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    private void OnDestroy()
    {
        ItemPickup.AnyItemPickedUp -= HandleItemPickedUp;

        if (endingOwnedByMe)
        {
            IsEndingActive = false;
            endingOwnedByMe = false;
        }

        // Never leave the pause system permanently locked (scene reload /
        // play-mode stop / quit teardown).
        GamePauseManager.PauseLocked = false;

        ReleaseVideoTexture();

        if (videoPlayer != null)
        {
            videoPlayer.prepareCompleted -= OnVideoPrepared;
            videoPlayer.loopPointReached -= OnVideoLoopPointReached;
            Destroy(videoPlayer);
            videoPlayer = null;
        }

        if (overlayCanvas != null)
        {
            Destroy(overlayCanvas.gameObject);
            overlayCanvas = null;
        }
    }

    /// <summary>Clamps Inspector values so the sequence can never receive nonsense input.</summary>
    private void OnValidate()
    {
        requiredTotems = Mathf.Max(1, requiredTotems);
        freezeDelay = Mathf.Max(0f, freezeDelay);
        fadeToBlackDuration = Mathf.Max(0f, fadeToBlackDuration);
        waitAfterFade = Mathf.Max(0f, waitAfterFade);
        postVideoHold = Mathf.Max(0f, postVideoHold);
        videoPrepareTimeout = Mathf.Max(0.1f, videoPrepareTimeout);
        videoSafetyTimeout = Mathf.Max(1f, videoSafetyTimeout);
        canvasSortingOrder = Mathf.Clamp(canvasSortingOrder, 0, 32767);
        if (string.IsNullOrEmpty(trackedItemId)) trackedItemId = "Totem";
    }

    // ------------------------------------------------------------------ //
    // The totem gate
    // ------------------------------------------------------------------ //

    private void HandleItemPickedUp(ItemPickup item)
    {
        if (State != EndingState.Idle) return;                          // an ending in progress ignores pickups
        if (item == null) return;
        if (item.ItemId != trackedItemId) return;                       // other item types are none of our business
        if (IsRevealed || CollectedTotems >= requiredTotems) return;    // clamped — a 5th totem changes nothing

        CollectedTotems++;

        if (debugLogs)
            Debug.Log("[RitualEndingSpot] '" + item.name + "' collected — " +
                      CollectedTotems + "/" + requiredTotems + " towards the ritual.", this);

        if (CollectedTotems >= requiredTotems)
        {
            RevealRitualSpot();
        }
        else if (checkTotemHUD && AnyHUDComplete())
        {
            // The HUD (which counts independently) may already be complete —
            // its word is enough. Zero-wiring fallback.
            RevealRitualSpot();
        }
    }

    /// <summary>True when any TotemCollectionHUD in the scene reports 4/4.</summary>
    private static bool AnyHUDComplete()
    {
        TotemCollectionHUD[] huds = FindAllHUDs();
        for (int i = 0; i < huds.Length; i++)
        {
            TotemCollectionHUD hud = huds[i];
            if (hud != null && hud.IsComplete) return true;
        }
        return false;
    }

    private static TotemCollectionHUD[] FindAllHUDs()
    {
#if UNITY_6000_0_OR_NEWER
        return FindObjectsByType<TotemCollectionHUD>(FindObjectsInactive.Exclude);
#elif UNITY_2023_1_OR_NEWER
        return FindObjectsByType<TotemCollectionHUD>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
        return FindObjectsOfType<TotemCollectionHUD>();
#endif
    }

    // ------------------------------------------------------------------ //
    // Gate visuals (hidden ↔ revealed)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Applies the hidden/revealed state to the event spot and the extra
    /// visuals. Equality-guarded — calling it every frame while gated costs
    /// a few flag reads, and it also covers spots that build their marker
    /// visual late (Awake order independence).
    /// </summary>
    private void ApplyGateVisuals()
    {
        bool show = IsRevealed || !hideUntilRevealed;

        // ---- the event spot itself: interaction on/off ----
        if (point != null && point.enabled != show)
        {
            point.enabled = show;
            if (debugLogs)
                Debug.Log("[RitualEndingSpot] InteractionPoint '" + point.name + "' " +
                          (show ? "enabled — the ritual can now be triggered." : "disabled — the spot is inert."), this);
        }

        // ---- the glowing marker child (built by the point's Awake) ----
        if (markerVisual == null && point != null) markerVisual = FindMarkerVisual(point.transform);
        if (markerVisual != null && markerVisual.gameObject.activeSelf != show)
            markerVisual.gameObject.SetActive(show);

        // ---- the user's own ritual props ----
        if (extraVisuals != null)
        {
            for (int i = 0; i < extraVisuals.Length; i++)
            {
                GameObject visual = extraVisuals[i];
                if (visual != null && visual.activeSelf != show) visual.SetActive(show);
            }
        }
    }

    /// <summary>Finds the point's marker visual child (current + legacy v2 name).</summary>
    private static Transform FindMarkerVisual(Transform pointTransform)
    {
        Transform visual = pointTransform.Find("Interaction Marker Visual");
        if (visual == null) visual = pointTransform.Find("Interaction Ring Visual");   // v1/v2 scenes
        return visual;
    }

    // ------------------------------------------------------------------ //
    // The ending sequence
    // ------------------------------------------------------------------ //

    /// <summary>Starts the sequence (returns false and does nothing when not allowed).</summary>
    private bool StartEnding(GameObject playerRoot)
    {
        if (State != EndingState.Idle) return false;                            // already running / ended
        if (!IsRevealed) return false;                                          // totem gate
        if (JumpscareGameOverManager.IsJumpscareActive || JumpscareGameOverManager.IsGameOver) return false;

        IsEndingActive = true;
        endingOwnedByMe = true;
        stateTimer = 0f;
        videoStarted = false;
        videoEnded = false;
        prepareFallbackUsed = false;

        FreezeWorld(playerRoot);

        // The spot has done its job — stop the point's own updates (prompt,
        // respawn timers) behind the coming black screen.
        if (point != null && point.enabled) point.enabled = false;

        BuildOverlay();
        BuildVideoPlayer();

        State = EndingState.FreezeDelay;

        if (debugLogs)
            Debug.Log("[RitualEndingSpot] Ending started — world frozen. Fade begins in " +
                      Mathf.Max(0f, freezeDelay) + "s.", this);

        if (onEndingStarted != null) onEndingStarted.Invoke();

        return true;
    }

    /// <summary>FREEZE — everything stands perfectly still (like pausing, no menu).</summary>
    private void FreezeWorld(GameObject playerRoot)
    {
        // PLAYER: no movement, no rotation, no hitbox, no flashlight — every
        // script on the player root is paused, the physics body is stopped.
        if (playerRoot == null) playerRoot = ResolvePlayerRoot();
        if (playerRoot != null)
        {
            DisableAllScripts(playerRoot);
            SetVelocity(playerRoot.GetComponent<Rigidbody>(), Vector3.zero);
        }

        // CAMERA: pause its scripts so the view stays exactly where it was
        // (the Camera itself keeps rendering — it is not a MonoBehaviour).
        Camera cam = Camera.main;
        if (cam != null) DisableAllScripts(cam.gameObject);

        // SPAWNERS OFF: from this exact moment no new enemy can ever appear.
        EnemySpawner[] spawners = FindAllSpawners();
        for (int i = 0; i < spawners.Length; i++)
        {
            EnemySpawner spawner = spawners[i];
            if (spawner != null && spawner.enabled) spawner.enabled = false;
        }

        // The pause menu must never open on top of the ending.
        GamePauseManager.PauseLocked = true;

        // Cursor stays hidden for the whole sequence (LateUpdate keeps it).
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    // ---- phase transitions ----

    private void EnterFading()
    {
        State = EndingState.Fading;
        stateTimer = 0f;

        overlayCanvas.gameObject.SetActive(true);
        videoImage.gameObject.SetActive(false);
        SetFadeAlpha(0f);

        // "As the same time the player's screen starts fading black" — the
        // enemies vanish and spawning stops for good, behind the fade.
        if (despawnEnemies) DespawnAllEnemies();
    }

    private void EnterWaitingAfterFade()
    {
        State = EndingState.WaitingAfterFade;
        stateTimer = 0f;
        SetFadeAlpha(1f);    // belt & braces — fully black now
    }

    private void EnterVideo()
    {
        State = EndingState.Video;
        stateTimer = 0f;

        if (endingVideo != null)
        {
            EnsureVideoTexture();
            videoImage.gameObject.SetActive(true);
            videoPlayer.clip = endingVideo;     // re-read in case it was set after Awake
            videoPlayer.Prepare();              // OnVideoPrepared starts playback
        }
        else
        {
            WarnNoVideoOnce();
            EnterHoldingAfterVideo();           // no video yet: hold, then end
        }
    }

    private void EnterHoldingAfterVideo()
    {
        State = EndingState.HoldingAfterVideo;
        stateTimer = 0f;

        HideVideoImage();
        SetFadeAlpha(1f);                       // the screen stays black
    }

    private void EnterEnded()
    {
        State = EndingState.Ended;
        HasEnded = true;

        if (debugLogs) Debug.Log("[RitualEndingSpot] Ending complete — the game is ending now.", this);

        QuitGame();
    }

    // ---- the video phase (same watchdog pattern as the jumpscare sequence) ----

    private void UpdateVideoPhase()
    {
        if (videoEnded) return;

        if (videoPlayer.isPlaying)
        {
            videoStarted = true;
        }
        else if (videoStarted)
        {
            // Playback stopped without the end event (end of clip or error) —
            // treat it as ended so the sequence can never stall.
            OnVideoEnd();
            return;
        }
        else if (!prepareFallbackUsed && stateTimer > videoPrepareTimeout)
        {
            // Prepare watchdog: Play() also prepares implicitly — never wait forever.
            prepareFallbackUsed = true;
            videoPlayer.Play();
        }

        if (stateTimer > videoSafetyTimeout) OnVideoEnd();   // absolute cap
    }

    private void OnVideoPrepared(VideoPlayer source)
    {
        if (State != EndingState.Video || videoEnded) return;

        videoPlayer.SetDirectAudioVolume(0, videoVolume);
        videoPlayer.Play();
    }

    private void OnVideoLoopPointReached(VideoPlayer source)
    {
        OnVideoEnd();
    }

    private void OnVideoEnd()
    {
        if (videoEnded) return;
        videoEnded = true;

        if (videoPlayer != null && videoPlayer.isPlaying) videoPlayer.Stop();

        EnterHoldingAfterVideo();
    }

    // ------------------------------------------------------------------ //
    // Despawning (at fade start)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Removes every enemy from the game: all spawner-owned enemies
    /// (DespawnAll), every remaining EnemyController root (manually placed
    /// ones included) and every EnemyFleeController-only body.
    /// </summary>
    private void DespawnAllEnemies()
    {
        int despawned = 0;

        // 1) spawner-owned enemies + their pending respawn timers
        EnemySpawner[] spawners = FindAllSpawners();
        for (int i = 0; i < spawners.Length; i++)
        {
            EnemySpawner spawner = spawners[i];
            if (spawner == null) continue;
            despawned += spawner.AliveCount;    // about to vanish — count before
            spawner.DespawnAll();
        }

        // 2) every remaining enemy body (spawner-listed ones are already gone;
        //    Destroy marks them dead, so the fake-null check skips doubles)
        EnemyController[] controllers = FindAllEnemyControllers();
        for (int i = 0; i < controllers.Length; i++)
        {
            EnemyController controller = controllers[i];
            if (controller == null || controller.gameObject == null) continue;
            Destroy(controller.gameObject);
            despawned++;
        }

        // 3) flee-only bodies (enemies mid "run away" without a controller)
        EnemyFleeController[] fleers = FindAllFleeControllers();
        for (int i = 0; i < fleers.Length; i++)
        {
            EnemyFleeController fleer = fleers[i];
            if (fleer == null || fleer.gameObject == null) continue;
            Destroy(fleer.gameObject);
            despawned++;
        }

        if (debugLogs)
            Debug.Log("[RitualEndingSpot] " + despawned + " enem" + (despawned == 1 ? "y" : "ies") +
                      " despawned — spawning stopped for good.", this);
    }

    private static EnemySpawner[] FindAllSpawners()
    {
#if UNITY_6000_0_OR_NEWER
        return FindObjectsByType<EnemySpawner>(FindObjectsInactive.Exclude);
#elif UNITY_2023_1_OR_NEWER
        return FindObjectsByType<EnemySpawner>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
        return FindObjectsOfType<EnemySpawner>();
#endif
    }

    private static EnemyController[] FindAllEnemyControllers()
    {
#if UNITY_6000_0_OR_NEWER
        return FindObjectsByType<EnemyController>(FindObjectsInactive.Exclude);
#elif UNITY_2023_1_OR_NEWER
        return FindObjectsByType<EnemyController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
        return FindObjectsOfType<EnemyController>();
#endif
    }

    private static EnemyFleeController[] FindAllFleeControllers()
    {
#if UNITY_6000_0_OR_NEWER
        return FindObjectsByType<EnemyFleeController>(FindObjectsInactive.Exclude);
#elif UNITY_2023_1_OR_NEWER
        return FindObjectsByType<EnemyFleeController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
        return FindObjectsOfType<EnemyFleeController>();
#endif
    }

    // ------------------------------------------------------------------ //
    // Freeze helpers (same proven pattern as the jumpscare sequence)
    // ------------------------------------------------------------------ //

    private static void DisableAllScripts(GameObject root)
    {
        if (root == null) return;

        MonoBehaviour[] scripts = root.GetComponents<MonoBehaviour>();
        for (int i = 0; i < scripts.Length; i++)
        {
            if (scripts[i] != null && scripts[i].enabled)
                scripts[i].enabled = false;
        }
    }

    private static void SetVelocity(Rigidbody body, Vector3 value)
    {
        // Kinematic bodies ignore velocity writes (and Unity warns about them).
        if (body == null || body.isKinematic) return;

#if UNITY_6000_0_OR_NEWER
        body.linearVelocity = value;
#else
        body.velocity = value;
#endif
    }

    private GameObject ResolvePlayerRoot()
    {
        if (playerOverride != null) return playerOverride.gameObject;
        GameObject found = GameObject.FindGameObjectWithTag("Player");
        return found;
    }

    /// <summary>Closes the pause menu and un-pauses, whatever paused it (defensive self-healing).</summary>
    private static void ForceResumeGame()
    {
#if UNITY_6000_0_OR_NEWER
        GamePauseManager manager = FindAnyObjectByType<GamePauseManager>();
#else
        GamePauseManager manager = FindObjectOfType<GamePauseManager>();
#endif
        if (manager != null) manager.ResumeGame();
    }

    // ------------------------------------------------------------------ //
    // Overlay + video building (fully from code — nothing to set up)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Builds the ending overlay: a fullscreen black fade image (the slow
    /// fade AND the video's letterbox bars — it sits UNDER the video) with
    /// the fullscreen video above it. The fade image also blocks all clicks
    /// for the whole sequence — nothing under it is reachable.
    /// </summary>
    private void BuildOverlay()
    {
        if (overlayCanvas != null) return;    // already built (safety on double triggers)

        GameObject canvasGO = new GameObject("RitualEndingCanvas");
        overlayCanvas = canvasGO.AddComponent<Canvas>();
        overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        overlayCanvas.sortingOrder = canvasSortingOrder;
        CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        canvasGO.AddComponent<GraphicRaycaster>();   // makes the fade image's click-blocker work

        // ---- the fade to black (under the video: also its black bars) ----
        GameObject fadeGO = new GameObject("Fade");
        fadeImage = fadeGO.AddComponent<Image>();
        fadeImage.raycastTarget = true;              // swallows every click while visible
        StretchFullScreen(fadeImage.rectTransform, canvasGO.transform);

        // ---- the video itself (ABOVE the fade — the fade happens first) ----
        GameObject videoGO = new GameObject("VideoImage");
        videoImage = videoGO.AddComponent<RawImage>();
        videoImage.raycastTarget = false;
        videoAspectFitter = videoGO.AddComponent<AspectRatioFitter>();   // official uGUI letterbox
        StretchFullScreen(videoImage.rectTransform, canvasGO.transform);

        // hidden until the sequence starts
        canvasGO.SetActive(false);
    }

    private void BuildVideoPlayer()
    {
        if (videoPlayer != null) return;

        videoPlayer = gameObject.AddComponent<VideoPlayer>();
        videoPlayer.playOnAwake = false;
        videoPlayer.waitForFirstFrame = waitForFirstFrame;
        videoPlayer.isLooping = false;
        videoPlayer.renderMode = VideoRenderMode.RenderTexture;
        videoPlayer.audioOutputMode = VideoAudioOutputMode.Direct;   // official enum: UnityEngine.Video.VideoAudioOutputMode
        videoPlayer.controlledAudioTrackCount = 1;
        videoPlayer.clip = endingVideo;

        videoPlayer.prepareCompleted += OnVideoPrepared;             // official event
        videoPlayer.loopPointReached += OnVideoLoopPointReached;
    }

    private void HideVideoImage()
    {
        if (videoImage != null) videoImage.gameObject.SetActive(false);
    }

    private void SetFadeAlpha(float alpha)
    {
        if (fadeImage == null) return;
        Color c = fadeImage.color;
        c.r = c.g = c.b = 0f;   // always pure black; the alpha is driven by the sequence
        c.a = alpha;
        fadeImage.color = c;
    }

    private void EnsureVideoTexture()
    {
        int width = endingVideo != null ? (int)endingVideo.width : 0;
        int height = endingVideo != null ? (int)endingVideo.height : 0;
        if (width <= 0 || height <= 0) { width = 1280; height = 720; }

        if (videoTexture == null || videoTexture.width != width || videoTexture.height != height)
        {
            ReleaseVideoTexture();
            videoTexture = new RenderTexture(width, height, 0);
            videoTexture.Create();
        }

        videoPlayer.targetTexture = videoTexture;
        videoImage.texture = videoTexture;

        ApplyVideoAspect();    // a different clip may have a different aspect ratio
    }

    private void ReleaseVideoTexture()
    {
        if (videoTexture != null)
        {
            videoTexture.Release();
            videoTexture = null;
        }
    }

    /// <summary>Fits the fullscreen video to its proportions using Unity's official AspectRatioFitter (letterbox).</summary>
    private void ApplyVideoAspect()
    {
        if (videoImage == null || videoAspectFitter == null) return;

        if (keepVideoAspectRatio)
        {
            float aspect = 16f / 9f;    // fallback until the clip's size is known
            if (videoTexture != null && videoTexture.height > 0)
                aspect = (float)videoTexture.width / videoTexture.height;
            else if (endingVideo != null && endingVideo.height > 0)
                aspect = (float)endingVideo.width / endingVideo.height;

            if (videoAspectFitter.aspectMode != AspectRatioFitter.AspectMode.FitInParent)
                videoAspectFitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            if (Mathf.Abs(videoAspectFitter.aspectRatio - aspect) > 0.0001f)
                videoAspectFitter.aspectRatio = aspect;
        }
        else
        {
            if (videoAspectFitter.aspectMode != AspectRatioFitter.AspectMode.None)
                videoAspectFitter.aspectMode = AspectRatioFitter.AspectMode.None;
            StretchFullScreen(videoImage.rectTransform, overlayCanvas.transform);   // edge-to-edge
        }
    }

    private static void StretchFullScreen(RectTransform rt, Transform parent)
    {
        rt.SetParent(parent, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private void WarnNoVideoOnce()
    {
        if (warnedNoVideo) return;
        warnedNoVideo = true;

        Debug.LogWarning("[RitualEndingSpot] No ending video assigned — continuing without it " +
                         "(fade → hold → end). Drag a video clip into the 'Ending Video' slot when " +
                         "your cutscene is ready (works without any code changes).", this);
    }

    // ------------------------------------------------------------------ //
    // Ending the game
    // ------------------------------------------------------------------ //

    /// <summary>
    /// The final step: cleans everything up FIRST (no leaked resources, no
    /// teardown errors), then ends the game — Play mode stops in the Editor,
    /// the application quits in a build. Replace this method's last two
    /// lines with your main-menu load when you build it.
    /// </summary>
    private void QuitGame()
    {
        // 1) stop + detach the video (unsubscribe first — no callbacks during teardown)
        if (videoPlayer != null)
        {
            videoPlayer.prepareCompleted -= OnVideoPrepared;
            videoPlayer.loopPointReached -= OnVideoLoopPointReached;
            if (videoPlayer.isPlaying) videoPlayer.Stop();
        }

        // 2) release the render texture BEFORE quitting
        ReleaseVideoTexture();

        // 3) remove the overlay (it would otherwise flash for one teardown frame)
        if (overlayCanvas != null)
        {
            Destroy(overlayCanvas.gameObject);
            overlayCanvas = null;
        }

        // 4) hand the pause system back (in the Editor, Play mode can be
        //    restarted instantly without a stuck lock)
        GamePauseManager.PauseLocked = false;

        IsEndingActive = false;
        // State stays Ended; HasEnded stays true — both reset on the next
        // scene load by Awake.

        // 5) end the game — current APIs for both environments:
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;   // stops Play mode in the Editor
#else
        Application.Quit();                                // ends the process in a build
#endif
    }

    // ------------------------------------------------------------------ //
    // Editor helpers
    // ------------------------------------------------------------------ //

    /// <summary>Context menu (gear icon): runs the whole sequence immediately, bypassing the totem gate — for testing the ending without collecting the totems.</summary>
    [ContextMenu("Test Ending Now (bypass totems)")]
    private void TestEndingNow()
    {
        ForceEnding();
    }

    /// <summary>Always-on gizmo so the gated ritual spot is still visible (and findable) in the Scene view.</summary>
    private void OnDrawGizmos()
    {
        // A small golden "shrine" marker: ring + pillar — distinct from the
        // point's own green gizmos, and drawn even while the spot is gated.
        Vector3 basePos = transform.position;
        Gizmos.color = new Color(1f, 0.75f, 0.15f, 0.9f);
        Gizmos.DrawWireSphere(basePos + Vector3.up * 0.1f, 0.45f);
        Gizmos.DrawLine(basePos, basePos + Vector3.up * 2f);
        Gizmos.DrawLine(basePos + Vector3.up * 1.8f, basePos + Vector3.up * 1.8f + transform.right * 0.35f);
        Gizmos.DrawLine(basePos + Vector3.up * 1.8f, basePos + Vector3.up * 1.8f - transform.right * 0.35f);

        // when gated, cross the marker out — "not yet available"
        if (Application.isPlaying && !IsRevealed && hideUntilRevealed)
        {
            Gizmos.color = new Color(1f, 0.25f, 0.15f, 0.9f);
            Gizmos.DrawLine(basePos + Vector3.up * 0.7f + transform.right * 0.5f,
                            basePos + Vector3.up * 1.4f - transform.right * 0.5f);
            Gizmos.DrawLine(basePos + Vector3.up * 0.7f - transform.right * 0.5f,
                            basePos + Vector3.up * 1.4f + transform.right * 0.5f);
        }
    }
}