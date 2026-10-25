using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Video;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
#endif

/// <summary>
/// THE JUMPSCARE → GAME OVER → RESTART SEQUENCE (one-hit-kill enemy contact).
///
/// What happens when a live enemy touches the player's hitbox (PlayerHitbox):
///
///   1. FREEZE      — the player (cannot move or rotate — every script on the
///                    player and the camera is paused), the enemy (same) and
///                    the game stay exactly at the contact position. The pause
///                    menu is locked so it can never open on top of the
///                    sequence. Time.timeScale stays 1 so the video can play.
///   2. VIDEO       — your jumpscare video plays on the ENTIRE screen. Drag
///                    ANY video clip (.mp4 / .webm / .mov …) into the
///                    "Jumpscare Video" slot in the Inspector — no code, no
///                    extra setup (Unity's built-in VideoPlayer renders into
///                    a RenderTexture shown by a fullscreen UI RawImage).
///   3. FADE        — the screen quickly fades to black (0.2s by default).
///   4. GAME OVER   — white "Game Over" text pops up in the screen center.
///   5. RESTART     — after a 1s delay the Restart button appears UNDER the
///                    text and the cursor automatically becomes visible and
///                    free at that same moment.
///   6. Clicking Restart reloads the scene: the player is placed back at the
///      Starting Point (facing the marker's forward arrow), control is fully
///      regained, the cursor is hidden again, the spawner/enemy/flashlight
///      all start fresh — the game is 100% replayable, kill after kill.
///
/// THE STARTING POINT (respawn marker, visible in the scene):
///   • Option A (recommended): right-click the component header (gear icon) →
///     "Create Starting Point At Player" — creates a "PlayerSpawnPoint"
///     GameObject at the player's current position, already wired up. Move it
///     anywhere and rotate its blue arrow to set where the player respawns
///     and which way it faces.
///   • Option B: create an empty GameObject named exactly "PlayerSpawnPoint"
///     — it is found and wired automatically.
///   • Option C: drag any Transform into the Starting Point slot.
///   A green marker gizmo shows the point whenever one is assigned.
///
/// SETUP: add this component to any GameObject (an empty "GameManagers"
/// object is a good home — next to GamePauseManager). Add PlayerHitbox to
/// the player. Tag the enemy prefab "Enemy". Everything else is built from
/// code at runtime; every value is adjustable in the Inspector.
///
/// NOTE (one-time): the scene must be in the Build Settings for Restart to
/// reload it (File > Build Settings > drag your scene into "Scenes In Build").
/// The manager checks this and tells you if it is missing.
///
/// PATCH v1.1 (compile fix): all Unity APIs now match the official Scripting
/// API exactly — VideoAudioOutputMode (there is no "AudioOutputMode" type),
/// VideoPlayer.prepareCompleted (there is no "prepared" member), the official
/// AspectRatioFitter for the letterbox (RawImage has no preserveAspect — that
/// is an Image-only member) and FindAnyObjectByType (FindFirstObjectByType is
/// obsolete in Unity 6.1+).
///
/// PATCH v1.3 (VR support):
///   • The player resolves through the shared PlayerHeadResolver
///     (XR Origin first, then the 'Player' tag) — so the freeze and the
///     respawn also work when the rig root is not tagged (or an old desktop
///     player still carries the tag in a VR scene).
///   • PlacePlayerAtStartingPoint() teleports an XR rig the VR-correct way:
///     the HEADSET lands exactly on the marker (not the rig's floor point,
///     which would leave the player standing offset from it), the rig then
///     yaw-rotates around the head so the body faces the marker's forward
///     arrow. The CharacterController on the rig is disabled during the
///     teleport and re-enabled right after — Unity's official teleport
///     pattern. Non-VR players keep the original behaviour unchanged.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-100)] // respawn placement runs before the camera controller's Start
public class JumpscareGameOverManager : MonoBehaviour
{
    /// <summary>The phases of the sequence.</summary>
    public enum SequenceState { Idle, Video, Fading, GameOver, RestartReady, Restarting }

    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Jumpscare Video (drag any video file here)")]

    [Tooltip("The jumpscare video shown on the whole screen. Drag ANY video clip from your " +
             "Project window into this slot (.mp4 / .webm / .mov …). Leave empty to skip " +
             "straight to the fade to black (useful while you do not have the video yet).")]
    [SerializeField] private VideoClip jumpscareVideo;

    [Tooltip("Volume of the jumpscare video's own audio track (0..1).")]
    [Range(0f, 1f)][SerializeField] private float videoVolume = 1f;

    [Tooltip("Fit the video inside the screen keeping its proportions (black bars where it " +
             "does not fill). OFF = stretch edge-to-edge to the whole screen.")]
    [SerializeField] private bool keepVideoAspectRatio = true;

    [Tooltip("Wait until the first frame is decoded before showing the video (no one-frame flash).")]
    [SerializeField] private bool waitForFirstFrame = true;

    [Tooltip("Seconds to wait for the video to prepare before playing it anyway (slow disks).")]
    [SerializeField] private float videoPrepareTimeout = 2f;

    [Tooltip("ABSOLUTE safety cap: if the video has not ended after this many seconds " +
             "(corrupt / unsupported file), the Game Over screen shows anyway — the game " +
             "can never get stuck on a broken video.")]
    [SerializeField] private float videoSafetyTimeout = 60f;

    [Header("Game Over Screen")]

    [Tooltip("Seconds the fade to black takes after the video ends.")]
    [SerializeField] private float fadeToBlackDuration = 0.2f;

    [Tooltip("The big white text shown in the middle of the screen.")]
    [SerializeField] private string gameOverText = "Game Over";

    [Tooltip("Delay between the Game Over text appearing and the Restart button showing. " +
             "The cursor becomes visible at the exact same moment as the button.")]
    [SerializeField] private float restartButtonDelay = 1f;

    [Tooltip("Text on the Restart button.")]
    [SerializeField] private string restartButtonText = "Restart";

    [Header("Game Over Style")]

    [Tooltip("Color of the Game Over text (white by default).")]
    [SerializeField] private Color gameOverTextColor = Color.white;

    [Tooltip("Font size of the Game Over text.")]
    [SerializeField] private int gameOverFontSize = 64;

    [Tooltip("Normal color of the Restart button.")]
    [SerializeField] private Color buttonColor = new Color(0.85f, 0.85f, 0.85f, 1f);

    [Tooltip("Text color on the Restart button.")]
    [SerializeField] private Color buttonTextColor = Color.black;

    [Tooltip("Width of the Restart button (pixels).")]
    [SerializeField] private float buttonWidth = 240f;

    [Tooltip("Height of the Restart button (pixels).")]
    [SerializeField] private float buttonHeight = 64f;

    [Tooltip("Font size of the Restart button label.")]
    [SerializeField] private int buttonFontSize = 26;

    [Tooltip("Vertical gap between the Game Over text and the Restart button (pixels).")]
    [SerializeField] private float buttonGap = 120f;

    [Header("Restart / Respawn")]

    [Tooltip("Where the player respawns after Restart (and where it starts when the scene loads). " +
             "Use the component's context menu 'Create Starting Point At Player', or drag any " +
             "Transform here. A GameObject named 'PlayerSpawnPoint' is found automatically. " +
             "The player faces this marker's forward (blue) arrow.")]
    [SerializeField] private Transform startingPoint;

    [Tooltip("Place the player at the Starting Point when the scene starts (this is what " +
             "happens right after Restart reloads the scene). Turn OFF to keep the player " +
             "wherever it is placed in the editor instead.")]
    [SerializeField] private bool placePlayerAtStart = true;

    [Header("Player Reference")]

    [Tooltip("Optional: the player to freeze / respawn. Auto-found via the 'Player' tag when empty.")]
    [SerializeField] private Transform playerOverride;

    [Header("Layer")]

    [Tooltip("Canvas sorting order — just below the pause menu's maximum (32767) and above " +
             "everything else, so the video and Game Over screen always cover the scene.")]
    [SerializeField] private int canvasSortingOrder = 32766;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private static JumpscareGameOverManager instance;

    /// <summary>The scene's manager (null when none exists).</summary>
    public static JumpscareGameOverManager Instance
    {
        get { return instance != null ? instance : null; }
    }

    /// <summary>True from the moment the enemy touches the player until the scene restarts.</summary>
    public static bool IsJumpscareActive { get; private set; }

    /// <summary>True while the Game Over screen (text/button) is up.</summary>
    public static bool IsGameOver { get; private set; }

    /// <summary>Current phase of the sequence (Idle = playing normally).</summary>
    public SequenceState State { get; private set; }

    private Canvas overlayCanvas;
    private Image backdropImage;
    private RawImage videoImage;
    private AspectRatioFitter videoAspectFitter;
    private Image fadeImage;
    private Text gameOverLabel;
    private Image buttonImage;
    private Text buttonLabel;
    private Button restartButton;

    private VideoPlayer videoPlayer;
    private RenderTexture videoTexture;

    private float stateTimer;
    private bool videoStarted;
    private bool videoEnded;
    private bool prepareFallbackUsed;
    private bool warnedNoVideo;
    private bool warnedSceneNotInBuild;

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Debug.LogWarning("[JumpscareGameOverManager] Only one jumpscare manager is allowed — " +
                             "destroying the duplicate on '" + name + "'.", this);
            Destroy(gameObject);
            return;
        }
        instance = this;

        // Statics survive scene reloads — a freshly loaded scene always starts clean.
        IsJumpscareActive = false;
        IsGameOver = false;
        State = SequenceState.Idle;

        EnsureEventSystem();
        BuildOverlay();
        BuildVideoPlayer();

        ResolveStartingPoint();
        if (placePlayerAtStart) PlacePlayerAtStartingPoint();

        WarnIfSceneNotInBuildSettings();
    }

    private void Update()
    {
        if (State == SequenceState.Idle || State == SequenceState.Restarting) return;

        stateTimer += Time.deltaTime;

        switch (State)
        {
            case SequenceState.Video:
                UpdateVideoPhase();
                break;

            case SequenceState.Fading:
                {
                    float t = Mathf.Clamp01(stateTimer / Mathf.Max(0.01f, fadeToBlackDuration));
                    SetFadeAlpha(t);
                    if (t >= 1f)
                    {
                        HideVideoImage();
                        EnterGameOver();
                    }
                }
                break;

            case SequenceState.GameOver:
                if (stateTimer >= Mathf.Max(0f, restartButtonDelay)) EnterRestartReady();
                break;
        }

        // Keep the overlay looking like the Inspector says (live tweaking).
        ApplyOverlayStyle();
    }

    private void LateUpdate()
    {
        if (State == SequenceState.Idle || State == SequenceState.Restarting) return;

        if (State == SequenceState.RestartReady)
        {
            // The Restart button needs a free, visible cursor — nothing may hide it.
            if (Cursor.lockState != CursorLockMode.None || !Cursor.visible)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }
        else
        {
            // During the video / fade / Game Over text the cursor stays hidden.
            // (The camera controller releases the cursor when it is disabled —
            // this safety net puts it right back, same trick GamePauseManager
            // uses in the opposite direction.)
            if (Cursor.lockState != CursorLockMode.Locked || Cursor.visible)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }
    }

    private void OnDestroy()
    {
        if (instance != this) return;
        instance = null;

        IsJumpscareActive = false;
        IsGameOver = false;

        // Never leave the pause menu permanently locked (scene reload / play-mode stop).
        GamePauseManager.PauseLocked = false;

        ReleaseVideoTexture();
    }

    /// <summary>Clamps Inspector values so the sequence can never receive nonsense input.</summary>
    private void OnValidate()
    {
        fadeToBlackDuration = Mathf.Max(0f, fadeToBlackDuration);
        restartButtonDelay = Mathf.Max(0f, restartButtonDelay);
        videoPrepareTimeout = Mathf.Max(0.1f, videoPrepareTimeout);
        videoSafetyTimeout = Mathf.Max(1f, videoSafetyTimeout);
        gameOverFontSize = Mathf.Clamp(gameOverFontSize, 8, 200);
        buttonFontSize = Mathf.Clamp(buttonFontSize, 8, 128);
        buttonWidth = Mathf.Max(60f, buttonWidth);
        buttonHeight = Mathf.Max(30f, buttonHeight);
        buttonGap = Mathf.Max(0f, buttonGap);
        canvasSortingOrder = Mathf.Clamp(canvasSortingOrder, 0, 32767);
    }

    // ------------------------------------------------------------------ //
    // Public API
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Starts the jumpscare sequence (called by PlayerHitbox when a live enemy
    /// touches the player's dead zone). Returns true when the sequence started.
    /// </summary>
    public bool TriggerJumpscare(Collider enemyCollider)
    {
        if (State != SequenceState.Idle || enemyCollider == null) return false;

        IsJumpscareActive = true;
        stateTimer = 0f;
        videoStarted = false;
        videoEnded = false;
        prepareFallbackUsed = false;

        FreezeWorld(enemyCollider);

        if (jumpscareVideo != null)
        {
            State = SequenceState.Video;
            ShowVideoOverlay();
            videoPlayer.clip = jumpscareVideo; // re-read in case it was set after Awake
            videoPlayer.Prepare(); // OnVideoPrepared starts playback
        }
        else
        {
            WarnNoVideoOnce();
            EnterFading(); // no video yet: straight to the fade to black
        }

        return true;
    }

    /// <summary>Wired to the Restart button — reloads the scene from the beginning.</summary>
    public void RestartScene()
    {
        if (State != SequenceState.RestartReady) return; // guards double clicks

        State = SequenceState.Restarting;

        Scene current = SceneManager.GetActiveScene();
        if (current.buildIndex >= 0)
            SceneManager.LoadScene(current.buildIndex);
        else
            SceneManager.LoadScene(current.name);
    }

    // ------------------------------------------------------------------ //
    // Freeze — everything stands perfectly still at the contact position
    // ------------------------------------------------------------------ //

    private void FreezeWorld(Collider enemyCollider)
    {
        // PLAYER: no movement, no rotation — pause every script on the player
        // root (movement, crosshair, hitbox …). The physics body is stopped too.
        GameObject playerRoot = ResolvePlayerRoot();
        if (playerRoot != null)
        {
            DisableAllScripts(playerRoot);
            SetVelocity(playerRoot.GetComponent<Rigidbody>(), Vector3.zero);
        }

        // CAMERA: pause its scripts so the view stays exactly where it was
        // (Camera itself is not a MonoBehaviour — the view keeps rendering).
        Camera cam = Camera.main;
        if (cam != null) DisableAllScripts(cam.gameObject);

        // ENEMY: pause every script on its root (controller, detector,
        // stabilizer …) and stop its physics body — frozen at the contact spot.
        GameObject enemyRoot = ResolveEnemyRoot(enemyCollider);
        if (enemyRoot != null) DisableAllScripts(enemyRoot);
        SetVelocity(enemyCollider.attachedRigidbody, Vector3.zero);

        // The pause menu must never open on top of the jumpscare / Game Over.
        GamePauseManager.PauseLocked = true;

        // Cursor stays hidden until the Restart button appears.
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

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

    // v1.3: XR Origin first, then the classic 'Player' tag (shared helper).
    private GameObject ResolvePlayerRoot()
    {
        if (playerOverride != null) return playerOverride.gameObject;

        Transform resolved = PlayerHeadResolver.ResolveRoot(null);
        return resolved != null ? resolved.gameObject : null;
    }

    private static GameObject ResolveEnemyRoot(Collider enemyCollider)
    {
        EnemyController controller = enemyCollider.GetComponentInParent<EnemyController>();
        if (controller != null) return controller.gameObject;

        Rigidbody body = enemyCollider.attachedRigidbody;
        if (body != null) return body.gameObject;

        return enemyCollider.gameObject;
    }

    // ------------------------------------------------------------------ //
    // Phase transitions
    // ------------------------------------------------------------------ //

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

        if (stateTimer > videoSafetyTimeout) OnVideoEnd(); // absolute cap
    }

    private void OnVideoPrepared(VideoPlayer source)
    {
        if (State != SequenceState.Video || videoEnded) return;
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

        EnterFading();
    }

    private void EnterFading()
    {
        State = SequenceState.Fading;
        stateTimer = 0f;

        overlayCanvas.gameObject.SetActive(true);
        backdropImage.gameObject.SetActive(jumpscareVideo != null); // black bars behind a video only
        fadeImage.gameObject.SetActive(true);
        gameOverLabel.gameObject.SetActive(false);
        restartButton.gameObject.SetActive(false);
        SetFadeAlpha(0f);
    }

    private void EnterGameOver()
    {
        State = SequenceState.GameOver;
        stateTimer = 0f;
        IsGameOver = true;

        gameOverLabel.gameObject.SetActive(true);
    }

    private void EnterRestartReady()
    {
        State = SequenceState.RestartReady;

        restartButton.gameObject.SetActive(true);

        // The cursor appears at the same moment as the Restart button.
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    // ------------------------------------------------------------------ //
    // Respawn / starting point
    // ------------------------------------------------------------------ //

    private void ResolveStartingPoint()
    {
        if (startingPoint != null) return;
        GameObject found = GameObject.Find("PlayerSpawnPoint");
        if (found != null) startingPoint = found.transform;
    }

    private void PlacePlayerAtStartingPoint()
    {
        if (startingPoint == null) return;

        Transform player = playerOverride != null ? playerOverride : ResolvePlayerTransform();
        if (player == null) return;

        // v1.3 VR: an XR rig is teleported so the HEADSET lands exactly on the
        // marker (not the rig's floor point — the two can be meters apart) and
        // the body faces the marker's forward arrow.
        Unity.XR.CoreUtils.XROrigin xrOrigin = player.GetComponent<Unity.XR.CoreUtils.XROrigin>();
        if (xrOrigin == null) xrOrigin = player.GetComponentInChildren<Unity.XR.CoreUtils.XROrigin>();

        CharacterController character = player.GetComponent<CharacterController>();
        bool characterWasEnabled = character != null && character.enabled;
        if (characterWasEnabled) character.enabled = false; // Unity's official teleport pattern

        if (xrOrigin != null && xrOrigin.Camera != null)
        {
            Transform head = xrOrigin.Camera.transform;
            Vector3 headPos = head.position;

            // 1) Slide the rig so the headset ends up over the marker, and take
            //    the marker's floor height for the rig itself.
            player.position += new Vector3(
                startingPoint.position.x - headPos.x,
                startingPoint.position.y - player.position.y,
                startingPoint.position.z - headPos.z);

            // 2) Yaw the whole rig around the HEAD so the rig's forward matches
            //    the marker's forward — the head stays on the marker while it
            //    pivots with the rig.
            float yawDelta = startingPoint.eulerAngles.y - head.eulerAngles.y;
            player.RotateAround(head.position, Vector3.up, yawDelta);
        }
        else
        {
            // Non-VR (desktop) — the original behaviour, unchanged.
            player.position = startingPoint.position;
            player.rotation = startingPoint.rotation; // faces the marker's forward arrow
        }

        if (characterWasEnabled) character.enabled = true;

        Rigidbody body = player.GetComponentInParent<Rigidbody>();
        SetVelocity(body, Vector3.zero);
    }

    // v1.3: XR Origin first, then the classic 'Player' tag (shared helper).
    private Transform ResolvePlayerTransform()
    {
        if (playerOverride != null) return playerOverride;
        return PlayerHeadResolver.ResolveRoot(null);
    }

    /// <summary>Context menu (gear icon): creates the respawn marker at the player's position.</summary>
    [ContextMenu("Create Starting Point At Player")]
    private void CreateStartingPoint()
    {
        Transform player = ResolvePlayerTransform();

        GameObject spawn = new GameObject("PlayerSpawnPoint");
        spawn.transform.position = player != null ? player.position : transform.position;
        spawn.transform.rotation = player != null ? player.rotation : transform.rotation;

        startingPoint = spawn.transform;

        Debug.Log("[JumpscareGameOverManager] Created 'PlayerSpawnPoint'. Move it anywhere and " +
                  "rotate its blue arrow to choose where — and which way — the player respawns.", this);
    }

    private void WarnIfSceneNotInBuildSettings()
    {
        if (warnedSceneNotInBuild) return;

        Scene scene = SceneManager.GetActiveScene();
        if (scene.buildIndex >= 0) return;

        warnedSceneNotInBuild = true;
        Debug.LogWarning("[JumpscareGameOverManager] Scene '" + scene.name + "' is not in the Build " +
                         "Settings, so Restart cannot reload it yet. One-time setup: File > Build " +
                         "Settings, drag your scene into 'Scenes In Build'. (Works in the editor " +
                         "after that — no need to actually build the game.)", this);
    }

    // ------------------------------------------------------------------ //
    // Video / overlay building (fully from code — nothing to set up in the editor)
    // ------------------------------------------------------------------ //

    private void BuildVideoPlayer()
    {
        videoPlayer = gameObject.AddComponent<VideoPlayer>();
        videoPlayer.playOnAwake = false;
        videoPlayer.waitForFirstFrame = waitForFirstFrame;
        videoPlayer.isLooping = false;
        videoPlayer.renderMode = VideoRenderMode.RenderTexture;
        videoPlayer.audioOutputMode = VideoAudioOutputMode.Direct; // official enum: UnityEngine.Video.VideoAudioOutputMode
        videoPlayer.controlledAudioTrackCount = 1;
        videoPlayer.clip = jumpscareVideo;

        videoPlayer.prepareCompleted += OnVideoPrepared; // official event (there is no "prepared" member)
        videoPlayer.loopPointReached += OnVideoLoopPointReached;
    }

    private void ShowVideoOverlay()
    {
        EnsureVideoTexture();

        overlayCanvas.gameObject.SetActive(true);
        backdropImage.gameObject.SetActive(true);
        videoImage.gameObject.SetActive(true);
        fadeImage.gameObject.SetActive(true);
        gameOverLabel.gameObject.SetActive(false);
        restartButton.gameObject.SetActive(false);
        SetFadeAlpha(0f);
    }

    private void HideVideoImage()
    {
        if (videoImage != null) videoImage.gameObject.SetActive(false);
    }

    private void SetFadeAlpha(float alpha)
    {
        if (fadeImage == null) return;
        Color c = fadeImage.color;
        c.a = alpha;
        fadeImage.color = c;
    }

    private void EnsureVideoTexture()
    {
        int width = jumpscareVideo != null ? (int)jumpscareVideo.width : 0;
        int height = jumpscareVideo != null ? (int)jumpscareVideo.height : 0;
        if (width <= 0 || height <= 0) { width = 1280; height = 720; }

        if (videoTexture == null || videoTexture.width != width || videoTexture.height != height)
        {
            ReleaseVideoTexture();
            videoTexture = new RenderTexture(width, height, 0);
            videoTexture.Create();
        }

        videoPlayer.targetTexture = videoTexture;
        videoImage.texture = videoTexture;

        ApplyVideoAspect(); // a different clip may have a different aspect ratio
    }

    private void ReleaseVideoTexture()
    {
        if (videoTexture != null)
        {
            videoTexture.Release();
            videoTexture = null;
        }
    }

    private void BuildOverlay()
    {
        Font font = GetBuiltInFont();

        GameObject canvasGO = new GameObject("JumpscareCanvas");
        overlayCanvas = canvasGO.AddComponent<Canvas>();
        overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        overlayCanvas.sortingOrder = canvasSortingOrder;
        CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        canvasGO.AddComponent<GraphicRaycaster>();

        // ---- black backdrop (behind the video: letterbox bars; blocks clicks) ----
        GameObject backdropGO = new GameObject("Backdrop");
        backdropImage = backdropGO.AddComponent<Image>();
        backdropImage.raycastTarget = true;
        StretchFullScreen(backdropImage.rectTransform, canvasGO.transform);

        // ---- the video itself ----
        GameObject videoGO = new GameObject("VideoImage");
        videoImage = videoGO.AddComponent<RawImage>();
        videoImage.raycastTarget = false;
        videoAspectFitter = videoGO.AddComponent<AspectRatioFitter>(); // official uGUI "letterbox" component
        StretchFullScreen(videoImage.rectTransform, canvasGO.transform);

        // ---- fade to black (runs on top of the video's last frame) ----
        GameObject fadeGO = new GameObject("Fade");
        fadeImage = fadeGO.AddComponent<Image>();
        fadeImage.raycastTarget = false;
        StretchFullScreen(fadeImage.rectTransform, canvasGO.transform);

        // ---- Game Over text ----
        GameObject textGO = new GameObject("GameOverText");
        gameOverLabel = textGO.AddComponent<Text>();
        gameOverLabel.font = font;
        gameOverLabel.fontStyle = FontStyle.Bold;
        gameOverLabel.alignment = TextAnchor.MiddleCenter;
        gameOverLabel.raycastTarget = false;
        RectTransform textRt = gameOverLabel.rectTransform;
        textRt.SetParent(canvasGO.transform, false);
        textRt.anchorMin = textRt.anchorMax = textRt.pivot = new Vector2(0.5f, 0.5f);
        textRt.anchoredPosition = new Vector2(0f, buttonGap * 0.5f);
        textRt.sizeDelta = new Vector2(700f, 140f);

        // ---- Restart button ----
        GameObject buttonGO = new GameObject("RestartButton");
        buttonImage = buttonGO.AddComponent<Image>();
        buttonImage.raycastTarget = true;
        RectTransform buttonRt = buttonImage.rectTransform;
        buttonRt.SetParent(canvasGO.transform, false);
        buttonRt.anchorMin = buttonRt.anchorMax = buttonRt.pivot = new Vector2(0.5f, 0.5f);
        buttonRt.anchoredPosition = new Vector2(0f, -buttonGap * 0.5f);

        restartButton = buttonGO.AddComponent<Button>();
        restartButton.transition = Button.Transition.ColorTint;
        restartButton.onClick.AddListener(RestartScene);

        // ---- button label ----
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

        // everything hidden until the jumpscare starts
        canvasGO.SetActive(false);
    }

    private static void StretchFullScreen(RectTransform rt, Transform parent)
    {
        rt.SetParent(parent, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private void ApplyOverlayStyle()
    {
        if (overlayCanvas == null) return;

        overlayCanvas.sortingOrder = canvasSortingOrder;

        Color black = Color.black;
        backdropImage.color = black;

        Color fade = fadeImage.color;
        fade.r = fade.g = fade.b = 0f; // always pure black; alpha is driven by the sequence
        fadeImage.color = fade;

        ApplyVideoAspect();

        gameOverLabel.text = gameOverText;
        gameOverLabel.color = gameOverTextColor;
        gameOverLabel.fontSize = gameOverFontSize;
        gameOverLabel.rectTransform.anchoredPosition = new Vector2(0f, buttonGap * 0.5f);

        buttonLabel.text = restartButtonText;
        buttonLabel.color = buttonTextColor;
        buttonLabel.fontSize = buttonFontSize;

        buttonImage.rectTransform.sizeDelta = new Vector2(buttonWidth, buttonHeight);
        buttonImage.rectTransform.anchoredPosition = new Vector2(0f, -buttonGap * 0.5f);

        ColorBlock colors = restartButton.colors;
        colors.normalColor = buttonColor;
        colors.highlightedColor = Color.Lerp(buttonColor, Color.white, 0.25f);
        colors.pressedColor = Color.Lerp(buttonColor, Color.black, 0.25f);
        colors.selectedColor = Color.Lerp(buttonColor, Color.white, 0.25f);
        colors.disabledColor = buttonColor * 0.5f;
        colors.fadeDuration = 0.08f;
        restartButton.colors = colors;
    }

    /// <summary>
    /// Fits the fullscreen video RawImage to the video's proportions using Unity's
    /// official AspectRatioFitter (uGUI layout component). "Keep Video Aspect Ratio"
    /// ON  = letterbox: FitInParent sizes the rect inside the fullscreen canvas, the
    ///        black backdrop image shows where the video does not fill (black bars).
    /// OFF = AspectMode.None + the rect is stretched edge-to-edge again.
    /// </summary>
    private void ApplyVideoAspect()
    {
        if (videoImage == null || videoAspectFitter == null) return;

        if (keepVideoAspectRatio)
        {
            float aspect = 16f / 9f; // fallback until the clip's size is known
            if (videoTexture != null && videoTexture.height > 0)
                aspect = (float)videoTexture.width / videoTexture.height;
            else if (jumpscareVideo != null && jumpscareVideo.height > 0)
                aspect = (float)jumpscareVideo.width / jumpscareVideo.height;

            if (videoAspectFitter.aspectMode != AspectRatioFitter.AspectMode.FitInParent)
                videoAspectFitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            if (Mathf.Abs(videoAspectFitter.aspectRatio - aspect) > 0.0001f)
                videoAspectFitter.aspectRatio = aspect;
        }
        else
        {
            if (videoAspectFitter.aspectMode != AspectRatioFitter.AspectMode.None)
                videoAspectFitter.aspectMode = AspectRatioFitter.AspectMode.None;
            StretchFullScreen(videoImage.rectTransform, overlayCanvas.transform); // edge-to-edge
        }
    }

    private void WarnNoVideoOnce()
    {
        if (warnedNoVideo) return;
        warnedNoVideo = true;

        Debug.LogWarning("[JumpscareGameOverManager] No jumpscare video assigned — skipping " +
                         "straight to the fade to black. Drag a video clip into the 'Jumpscare " +
                         "Video' slot to add it (works without any code changes).", this);
    }

    // ------------------------------------------------------------------ //
    // Editor helpers
    // ------------------------------------------------------------------ //

    /// <summary>Always-on gizmo so the starting point is easy to see and adjust in the scene.</summary>
    private void OnDrawGizmos()
    {
        if (startingPoint == null) return;

        Vector3 basePos = startingPoint.position;
        Vector3 head = basePos + Vector3.up * 2f;

        Gizmos.color = new Color(0f, 1f, 0.5f, 0.9f);
        Gizmos.DrawWireSphere(basePos + Vector3.up * 0.15f, 0.3f);
        Gizmos.DrawLine(basePos, head);

        // little "flag" crossbar so it reads as a marker, not a stray sphere
        Gizmos.DrawLine(basePos + Vector3.up * 1.85f, basePos + Vector3.up * 1.85f + startingPoint.right * 0.4f);

        // forward arrow — the direction the player will face after respawning
        Gizmos.color = Color.cyan;
        Gizmos.DrawRay(head, startingPoint.forward * 1.2f);
    }

    // ------------------------------------------------------------------ //
    // Shared plumbing (same pattern as GamePauseManager — kept private so the
    // two managers stay fully independent of each other)
    // ------------------------------------------------------------------ //

    /// <summary>Makes sure a clickable UI exists (creates the right input module for the project's input backend).</summary>
    private static void EnsureEventSystem()
    {
#if UNITY_2023_1_OR_NEWER
        EventSystem existing = FindAnyObjectByType<EventSystem>(); // FindFirstObjectByType is obsolete in Unity 6.1+
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
