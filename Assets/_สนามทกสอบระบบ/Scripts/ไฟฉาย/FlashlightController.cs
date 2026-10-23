using System.Collections;
using UnityEngine;
using UnityEngine.Events;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Aims the player's flashlight (a Spot Light) wherever the camera is looking —
/// like pointing a gun in a third-person shooter.
///
/// Attach this component to the Spot Light GameObject that is tagged
/// "Flashlight". The light can be parented ANYWHERE (the player's body, the
/// camera, a hand bone …): this script drives the light's world rotation so it
/// always matches the camera's view direction, no matter how it is attached.
///
/// Completely independent from the enemy scripts — this component never
/// references any enemy code. The enemies find this light by its "Flashlight"
/// tag instead (see FlashlightDetector), so you can add, move or remove this
/// component freely without touching the enemy prefab.
///
/// The toggle key works on BOTH input backends (legacy Input Manager and the
/// new Input System) — keep KeyCodeBridge.cs in the project for the latter.
///
/// v1.2 – frozen while the pause menu is open (GamePauseManager): the aim
///        locks and the toggle key is ignored until the game is resumed.
///
/// v2.0 – BATTERY SYSTEM:
///        The flashlight now has a battery that drains while the light is on.
///        A green bar with a white frame appears in the BOTTOM-RIGHT corner of
///        the screen (FlashlightBatteryHUD.cs — keep that file in the project
///        too; the bar builds itself, no scene setup needed).
///        - The bar shrinks toward the right side as power is used.
///        - When the battery runs out, the light in the player's hand TURNS
///          OFF by itself and pressing F only produces a weak dying sputter.
///        - The light flickers when the battery gets low.
///        - Call RechargeBattery() / RechargeBatteryFull() from a pickup item
///          to refill it (also available as Context Menu test actions).
///        - Events: On Battery Low / On Battery Empty (hook sounds here).
///        Turn "Use Battery" off to get the old unlimited-light behaviour.
///
/// v2.1 – EDITOR-EDITABLE BATTERY UI (user request: "I want to move and scale
///        the battery UI without touching code"): right-click this
///        component's header in the Inspector and pick
///        "Battery: Create Editable HUD In Scene (editor)" — the battery bar
///        becomes a permanent scene object you can MOVE / RESIZE / SCALE with
///        the normal Scene view tools, and the layout is saved with the
///        scene. At Play a HUD that already exists in the scene is reused
///        automatically (never duplicated). All battery values (duration,
///        start charge, low threshold, flicker …) are Inspector fields.
///
/// v2.2 – CHARACTER ANIMATION SUPPORT, ZERO SETUP, ZERO WARNINGS.
///        Replaces the version that printed
///        "Character Animator is not assigned. Assign the character's
///        Animator in the Inspector." every time the game started.
///        - The Animator is found AUTOMATICALLY (no Inspector assignment
///          needed): first on the flashlight's parents (the usual setup —
///          the flashlight sits inside the character model), then anywhere
///          in the player hierarchy.
///        - NO WARNINGS, EVER. A missing Animator or missing parameters are
///          a normal, supported state: the flashlight (aim, toggle, battery,
///          HUD) works exactly the same with or without animations. When no
///          Animator exists you get ONE white info hint in the editor —
///          never a yellow warning, never spam, nothing in builds.
///        - HOW ANIMATIONS ARE DRIVEN (ask your animator to add ONE of
///          these to the character's Animator Controller — the names are
///          also editable fields in the Inspector):
///            • a BOOL parameter named "FlashlightOn" (true = light on,
///              false = light off) — recommended, mirrors the light state,
///              survives re-entrance and scene changes; or
///            • TRIGGER parameters "FlashlightOn" / "FlashlightOff" — fired
///              on each switch (used only when the bool does not exist).
///          Parameters that don't exist are simply not driven — no setup is
///          wrong, nothing can break. Right-click the component header and
///          pick "Animation: Show Detected Animator & Parameters" to see
///          exactly what was found.
///        - New events On Turned On / On Turned Off (hook click sounds).
///        - SetOn() is now idempotent (switching to the state it already has
///          does not replay the animation).
///
/// v2.3 – UNITY 6.6 FIX: "Animator is not playing an AnimatorController".
///        When the character model carries an Animator component WITHOUT an
///        Animator Controller assigned (a half-integrated model import — WIP
///        hand-me-downs from an animator friend look exactly like this),
///        v2.2's parameter scan still read that Animator, and Unity logged
///        "Animator is not playing an AnimatorController" (error + warning)
///        from FlashlightController.Awake every run. v2.3 treats a
///        controller-less Animator as "no animations": the parameter scan and
///        every Set/SetTrigger are skipped cleanly — ONE white editor info
///        hint explains the fix (assign the controller, or remove the
///        Animator). The moment a controller IS assigned — even mid-Play —
///        the next flashlight toggle picks the animations up automatically.
///        The right-click "Animation: Show Detected Animator & Parameters"
///        action now reports the missing-controller case explicitly.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Light))]
[DefaultExecutionOrder(100)] // Run after the camera controllers each frame, so the light copies this frame's FINAL camera pose (no 1-frame aim lag).
public class FlashlightController : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Camera To Follow")]

    [Tooltip("The camera whose view direction the flashlight copies. " +
             "Auto-found via Camera.main when left empty (camera must be tagged 'MainCamera').")]
    [SerializeField] private Transform cameraTransform;

    [Header("Aiming")]

    [Tooltip("How fast the light rotates to match the camera. " +
             "0 = instant snap (tightest aim), higher = smoother with a little lag.")]
    [SerializeField] private float rotationSmoothing = 18f;

    public enum AimMode { Camera, Hand }

    [Tooltip("CAMERA = beam aims where the HEAD looks (original design). " +
             "HAND = beam points wherever the flashlight MODEL points (true handheld VR).")]
    [SerializeField] private AimMode aimMode = AimMode.Camera;

    [Header("Position (optional)")]

    [Tooltip("Follow the camera's position as well? OFF = the light stays wherever you parented it " +
             "(e.g. held at the player's chest) and only its AIM follows the camera. " +
             "ON = the light floats with the camera plus the offset below.")]
    [SerializeField] private bool followCameraPosition = false;

    [Tooltip("Offset from the camera position when Follow Camera Position is on " +
             "(camera-local space, e.g. slightly right and low like a held torch).")]
    [SerializeField] private Vector3 positionOffset = new Vector3(0.3f, -0.2f, 0.15f);

    [Tooltip("How fast the light moves to the target position. 0 = instant.")]
    [SerializeField] private float positionSmoothing = 15f;

    [Header("On / Off")]

    [Tooltip("Allow switching the flashlight off/on with a key. " +
             "A switched-off light can never stun enemies (see FlashlightDetector).")]
    [SerializeField] private bool canToggle = true;

    [Tooltip("Key that toggles the flashlight. Works on BOTH input backends " +
             "(keep KeyCodeBridge.cs in the project when using the new Input System).")]
    [SerializeField] private KeyCode toggleKey = KeyCode.F;

    [Tooltip("Flashlight state when the game starts.")]
    [SerializeField] private bool startOn = true;

    [Header("Character Animation (v2.2 — optional)")]

    [Tooltip("The character's Animator, used for optional flashlight on/off animations. " +
             "AUTO-FOUND when left empty: parents first (the usual setup — the flashlight " +
             "sits inside the character model), then the whole player hierarchy. " +
             "Animations are fully optional — the flashlight works exactly the same without any Animator.")]
    [SerializeField] private Animator characterAnimator;

    [Tooltip("Look for the Animator automatically when the field above is empty. " +
             "Untick ONLY to force 'no animations' on a character that has an unrelated Animator.")]
    [SerializeField] private bool autoFindAnimator = true;

    [Tooltip("BOOL parameter in the character's Animator Controller that mirrors the flashlight " +
             "state (true = on, false = off). Recommended for your animator: one bool named " +
             "'FlashlightOn' with transitions both ways. Ignored when it does not exist.")]
    [SerializeField] private string boolParameterName = "FlashlightOn";

    [Tooltip("TRIGGER fired when the flashlight switches ON (used only when the bool parameter above does not exist).")]
    [SerializeField] private string onTriggerName = "FlashlightOn";

    [Tooltip("TRIGGER fired when the flashlight switches OFF (used only when the bool parameter above does not exist).")]
    [SerializeField] private string offTriggerName = "FlashlightOff";

    [Header("Battery (v2.0)")]

    [Tooltip("The flashlight has a limited battery that drains while the light is on. " +
             "OFF = unlimited light (the old behaviour, no HUD either).")]
    [SerializeField] private bool useBattery = true;

    [Tooltip("How many seconds of light one full battery holds while the light is on. " +
             "180 = 3 minutes. The HUD bar drains from full to empty over exactly this time.")]
    [SerializeField, Min(1f)] private float batteryDuration = 180f;

    [Tooltip("Battery charge (0-1) when the game starts. 1 = full. " +
             "Set it lower to test what a dying battery looks like.")]
    [SerializeField, Range(0f, 1f)] private float startingCharge = 1f;

    [Tooltip("Battery charge (0-1) below which the 'On Battery Low' event fires and the light starts flickering.")]
    [SerializeField, Range(0.02f, 0.9f)] private float lowBatteryThreshold = 0.25f;

    [Header("Low Battery Flicker")]

    [Tooltip("The light flickers while the battery is below the Low Battery Threshold — a classic horror warning.")]
    [SerializeField] private bool flickerWhenLow = true;

    [Tooltip("Lowest intensity the flicker dips to, as a fraction of the light's normal intensity.")]
    [SerializeField, Range(0f, 1f)] private float flickerMinimumIntensity = 0.45f;

    [Header("Dead Battery")]

    [Tooltip("Pressing the toggle key with an empty battery makes the light give a weak dying sputter " +
             "(so the player understands the battery is dead, not the key).")]
    [SerializeField] private bool deadFlickerOnToggle = true;

    [Header("Battery Events (hook sounds here)")]

    [Tooltip("Fires once when the battery drops below the Low Battery Threshold (and resets after a recharge).")]
    public UnityEvent onBatteryLow;

    [Tooltip("Fires once when the battery fully runs out and the light dies.")]
    public UnityEvent onBatteryEmpty;

    [Header("Toggle Events (v2.2 — hook sounds here)")]

    [Tooltip("Fires every time the flashlight switches ON (key, code, or pickup).")]
    public UnityEvent onTurnedOn;

    [Tooltip("Fires every time the flashlight switches OFF (key, code, or a dead battery).")]
    public UnityEvent onTurnedOff;

    [Header("Battery HUD")]

    [Tooltip("Automatically create the battery bar UI (green bar, white frame, bottom-right screen corner) " +
             "when the game starts — but only if the scene does not already contain one (a HUD placed with " +
             "the editor action below is reused, never duplicated).")]
    [SerializeField] private bool autoCreateBatteryHUD = true;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private Light spotlight;
    private bool loggedCameraWarning;
    private int cameraSearchFrames;

    // battery runtime
    private float batteryLevel;            // 0..1
    private bool batteryEmpty;
    private bool lowBatteryAnnounced;
    private float baseIntensity;           // the light's authored intensity (flicker restores to this)
    private float flickerTimer;
    private float flickerTarget = 1f;
    private Coroutine deadFlickerRoutine;

    // character animation runtime (v2.2; v2.3 adds the controller gate)
    private int animBoolId;
    private int animOnTriggerId;
    private int animOffTriggerId;
    private bool animBoolValid;
    private bool animOnTriggerValid;
    private bool animOffTriggerValid;
    private bool animParametersCached;   // v2.3: parameters were read from an Animator that HAS a controller

    // Editor-only hint latches (v2.3): everything that touches these lives
    // inside #if UNITY_EDITOR, so the fields themselves are compiled out of
    // builds — no "field is never used" warning can ever appear there.
#if UNITY_EDITOR
    private bool animatorHintLogged;
    private bool controllerHintLogged;
#endif

    /// <summary>True while the flashlight is switched on (the light component is enabled).</summary>
    public bool IsOn
    {
        get { return spotlight != null && spotlight.enabled; }
    }

    /// <summary>Current battery charge from 0 (empty) to 1 (full). Always 1 when the battery system is off.</summary>
    public float BatteryLevel01
    {
        get { return useBattery ? Mathf.Clamp01(batteryLevel) : 1f; }
    }

    /// <summary>True when the battery has fully run out (the light cannot be switched on until recharged).</summary>
    public bool IsBatteryEmpty
    {
        get { return useBattery && batteryEmpty; }
    }

    /// <summary>True when this flashlight uses the battery system at all.</summary>
    public bool UsesBattery
    {
        get { return useBattery; }
    }

    /// <summary>
    /// The Animator currently driving the character animations (v2.2). Null when
    /// none was found — a perfectly valid, warning-free state.
    /// </summary>
    public Animator CharacterAnimator
    {
        get { return characterAnimator; }
    }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        spotlight = GetComponent<Light>();

        if (spotlight.type != LightType.Spot)
        {
            Debug.LogWarning("[FlashlightController] The Light on '" + name + "' is not a Spot Light. " +
                             "Flashlight detection expects a spotlight cone — set its Type to 'Spot' in the Inspector.", this);
        }

        // ---- battery runtime setup ----
        baseIntensity = spotlight.intensity;
        batteryLevel = Mathf.Clamp01(startingCharge);
        batteryEmpty = useBattery && batteryLevel <= 0f;

        // do not re-announce "low battery" when the game already starts low
        lowBatteryAnnounced = useBattery && batteryLevel <= lowBatteryThreshold;

        // a dead battery means the light cannot start on
        spotlight.enabled = startOn && !batteryEmpty;

        // ---- optional character animations (v2.2: never warns) ----
        InitializeFlashlightAnimation();
    }

    private void Start()
    {
        // Build the battery HUD automatically — a green bar with a white frame
        // in the bottom-right screen corner (see FlashlightBatteryHUD.cs).
        // v2.1: a HUD that already exists in the scene (e.g. created with the
        // "Create Editable HUD In Scene" editor action) is reused, never
        // duplicated.
        if (autoCreateBatteryHUD && FindExistingHud() == null)
        {
            GameObject hudGO = new GameObject("Flashlight Battery HUD");
            FlashlightBatteryHUD hud = hudGO.AddComponent<FlashlightBatteryHUD>();
            hud.Bind(this);
        }
    }

    /// <summary>Any battery HUD in this scene (active or not)? Used so auto-creation never duplicates one.</summary>
    private FlashlightBatteryHUD FindExistingHud()
    {
#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
        return FindAnyObjectByType<FlashlightBatteryHUD>(FindObjectsInactive.Include);
#else
        return FindObjectOfType<FlashlightBatteryHUD>();
#endif
    }

    private void Update()
    {
        // v1.2: frozen with the rest of the game while the pause menu is open.
        // (While paused, timeScale = 0, so deltaTime is 0 anyway — the explicit
        // gate just makes that guarantee obvious and absolute.)
        if (GamePauseManager.IsPaused) return;

        TickBattery();
    }

    // LateUpdate (not Update): the camera controllers place the camera in Update/LateUpdate;
    // running here plus DefaultExecutionOrder(100) guarantees we always copy the freshest pose.
    private void LateUpdate()
    {
        FindCameraIfNeeded();
        if (cameraTransform == null) return;

        // v1.2: frozen with the rest of the game while the pause menu is open.
        if (GamePauseManager.IsPaused) return;

        HandleToggleInput();

        // v2.4 — HAND mode: the light lives under the flashlight model;
        // don't touch the transform, so it fully follows the hand.
        if (aimMode != AimMode.Camera) return;

        // ---- Aim where the camera looks (world-space, parent-independent) ----
        if (rotationSmoothing <= 0f)
        {
            transform.rotation = cameraTransform.rotation;
        }
        else
        {
            // Frame-rate independent smoothing: catches up quickly after big
            // turns, but never fights the camera at high frame rates.
            float blend = 1f - Mathf.Exp(-rotationSmoothing * Time.deltaTime);
            transform.rotation = Quaternion.Slerp(transform.rotation, cameraTransform.rotation, blend);
        }

        // ---- Optional position follow ----
        if (followCameraPosition)
        {
            Vector3 targetPosition = cameraTransform.position + cameraTransform.rotation * positionOffset;

            if (positionSmoothing <= 0f)
            {
                transform.position = targetPosition;
            }
            else
            {
                float blend = 1f - Mathf.Exp(-positionSmoothing * Time.deltaTime);
                transform.position = Vector3.Lerp(transform.position, targetPosition, blend);
            }
        }
    }

    /// <summary>Clamps Inspector values so the controller can never receive nonsense input.</summary>
    private void OnValidate()
    {
        rotationSmoothing = Mathf.Max(0f, rotationSmoothing);
        positionSmoothing = Mathf.Max(0f, positionSmoothing);
        batteryDuration = Mathf.Max(1f, batteryDuration);
        lowBatteryThreshold = Mathf.Clamp(lowBatteryThreshold, 0.02f, 0.9f);
        flickerMinimumIntensity = Mathf.Clamp01(flickerMinimumIntensity);

        // v2.2: re-scan the Animator parameters when the names are edited
        // during Play (edit-mode changes re-run Awake anyway).
        if (Application.isPlaying && characterAnimator != null)
        {
            CacheAnimationParameters();
        }
    }

    // ------------------------------------------------------------------ //
    // Character animation (v2.2 — fully optional, never warns)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Resolves the character Animator and caches which of its parameters match
    /// the configured names. v2.2 design goal: NEVER nag. A missing Animator or
    /// missing parameters simply mean "no animations" — the flashlight itself
    /// (aim, toggle, battery, HUD) works exactly the same either way.
    /// </summary>
    private void InitializeFlashlightAnimation()
    {
        ResolveAnimator();

        if (characterAnimator == null)
        {
#if UNITY_EDITOR
            // ONE white info hint (editor only) — never a yellow warning, never spam.
            if (!animatorHintLogged)
            {
                animatorHintLogged = true;
                Debug.Log("[FlashlightController] No Animator found — the flashlight works fine without " +
                          "animations. (Auto-find checks the parents and the player hierarchy. Assign " +
                          "'Character Animator' in the Inspector to enable on/off animations.)", this);
            }
#endif
            return;
        }

        CacheAnimationParameters();

        // Push the starting state WITHOUT firing triggers — an already-on light
        // must not replay a raise animation at startup.
        if (animBoolValid)
        {
            characterAnimator.SetBool(animBoolId, spotlight.enabled);
        }
    }

    /// <summary>
    /// Finds the character's Animator without any manual Inspector setup.
    /// </summary>
    private void ResolveAnimator()
    {
        if (characterAnimator != null || !autoFindAnimator) return;

        // 1) The usual setup: the flashlight is parented inside the character
        //    model, so the nearest Animator up the chain is the character's.
        characterAnimator = GetComponentInParent<Animator>();

        // 2) Otherwise search the whole player hierarchy from the root (covers
        //    an Animator placed on a child, e.g. a hand bone).
        if (characterAnimator == null)
        {
            Transform root = transform.root;
            if (root != null)
            {
                characterAnimator = root.GetComponentInChildren<Animator>();
            }
        }
    }

    /// <summary>
    /// Scans the Animator's parameter list ONCE and remembers which of the three
    /// configured names actually exist (and are the right type). Reading
    /// animator.parameters allocates, so this runs once here — never per frame.
    /// v2.3: an Animator with no RuntimeAnimatorController assigned is not
    /// scanned at all (reading it would log "Animator is not playing an
    /// AnimatorController") — that state simply means "no animations".
    /// </summary>
    private void CacheAnimationParameters()
    {
        animBoolValid = false;
        animOnTriggerValid = false;
        animOffTriggerValid = false;
        animParametersCached = false;

        if (characterAnimator == null) return;

        // v2.3: an Animator WITHOUT a RuntimeAnimatorController cannot be
        // inspected or driven — reading .parameters on it makes Unity log
        // "Animator is not playing an AnimatorController" (as an error AND a
        // warning). This is the half-integrated-model case (model imported
        // with an Animator but no controller assigned yet): treat it as "no
        // animations" and say so once, helpfully, in the editor only.
        if (characterAnimator.runtimeAnimatorController == null)
        {
#if UNITY_EDITOR
            if (!controllerHintLogged)
            {
                controllerHintLogged = true;
                Debug.Log("[FlashlightController] Found an Animator on '" + characterAnimator.name +
                          "' but it has no Animator Controller assigned — its animations are skipped. " +
                          "Assign your animator's controller to that Animator component (or remove the " +
                          "Animator if the model should not animate). The flashlight itself works " +
                          "exactly the same either way.", this);
            }
#endif
            return;
        }

        animParametersCached = true;
        animBoolId = Animator.StringToHash(boolParameterName);
        animOnTriggerId = Animator.StringToHash(onTriggerName);
        animOffTriggerId = Animator.StringToHash(offTriggerName);

        AnimatorControllerParameter[] parameters = characterAnimator.parameters;
        for (int i = 0; i < parameters.Length; i++)
        {
            AnimatorControllerParameter parameter = parameters[i];

            if (parameter.type == AnimatorControllerParameterType.Bool && parameter.nameHash == animBoolId)
            {
                animBoolValid = true;
            }
            else if (parameter.type == AnimatorControllerParameterType.Trigger && parameter.nameHash == animOnTriggerId)
            {
                animOnTriggerValid = true;
            }
            else if (parameter.type == AnimatorControllerParameterType.Trigger && parameter.nameHash == animOffTriggerId)
            {
                animOffTriggerValid = true;
            }
        }
    }

    /// <summary>
    /// Drives the character animation to the given flashlight state: the bool
    /// parameter (preferred) is set true/false; otherwise the matching trigger
    /// fires. Silently does nothing when no Animator or no matching parameters
    /// exist — missing animation can never break the light.
    /// </summary>
    private void PushAnimationState(bool on, bool fireTriggers)
    {
        if (characterAnimator == null) return;

        // v2.3: never drive a controller-less Animator — SetBool/SetTrigger
        // would log "Animator is not playing an AnimatorController" too.
        if (characterAnimator.runtimeAnimatorController == null)
        {
            animBoolValid = false;
            animOnTriggerValid = false;
            animOffTriggerValid = false;
            return;
        }

        // v2.3 self-healing: if a controller appeared AFTER the parameters
        // were last cached (e.g. the missing piece of the model integration
        // was assigned mid-Play), pick the parameters up on this toggle.
        if (!animParametersCached)
        {
            CacheAnimationParameters();
        }

        if (animBoolValid)
        {
            characterAnimator.SetBool(animBoolId, on);
        }
        else if (fireTriggers)
        {
            if (on && animOnTriggerValid)
            {
                characterAnimator.SetTrigger(animOnTriggerId);
            }
            else if (!on && animOffTriggerValid)
            {
                characterAnimator.SetTrigger(animOffTriggerId);
            }
        }
    }

    // ------------------------------------------------------------------ //
    // Battery
    // ------------------------------------------------------------------ //

    private void TickBattery()
    {
        if (!useBattery) return;

        if (spotlight.enabled && !batteryEmpty)
        {
            batteryLevel -= Time.deltaTime / Mathf.Max(0.01f, batteryDuration);

            if (batteryLevel <= 0f)
            {
                batteryLevel = 0f;
                OnBatteryDepleted();
            }
            else if (!lowBatteryAnnounced && batteryLevel <= lowBatteryThreshold)
            {
                lowBatteryAnnounced = true;
                if (onBatteryLow != null) onBatteryLow.Invoke();
            }
        }

        UpdateFlicker();
    }

    /// <summary>The battery ran out: the light in the player's hand turns off by itself.</summary>
    private void OnBatteryDepleted()
    {
        batteryEmpty = true;

        if (deadFlickerRoutine != null)
        {
            StopCoroutine(deadFlickerRoutine);
            deadFlickerRoutine = null;
        }

        spotlight.enabled = false;   // <- THE dead-battery rule: the light goes out
        PushAnimationState(false, true);   // the arm lowers when the light dies (if animated)
        RestoreIntensity();

        if (onBatteryEmpty != null) onBatteryEmpty.Invoke();
        if (onTurnedOff != null) onTurnedOff.Invoke();

        Debug.Log("[FlashlightController] The flashlight battery is empty — the light is out until it is recharged.", this);
    }

    private void UpdateFlicker()
    {
        if (spotlight == null) return;

        bool flickering = flickerWhenLow &&
                          !batteryEmpty &&
                          spotlight.enabled &&
                          batteryLevel <= lowBatteryThreshold;

        if (!flickering)
        {
            RestoreIntensity();
            return;
        }

        // pick a new random sputter every few hundred milliseconds —
        // frame-rate independent and a lot calmer than per-frame noise
        flickerTimer -= Time.deltaTime;
        if (flickerTimer <= 0f)
        {
            flickerTimer = Random.Range(0.04f, 0.11f);
            flickerTarget = Random.Range(flickerMinimumIntensity, 1f);
        }

        spotlight.intensity = baseIntensity * flickerTarget;
    }

    private void RestoreIntensity()
    {
        if (spotlight != null && spotlight.intensity != baseIntensity)
            spotlight.intensity = baseIntensity;
    }

    /// <summary>Three weak sputters, then darkness — pressing F on a dead battery.</summary>
    private IEnumerator DeadFlickerRoutine()
    {
        for (int i = 0; i < 3; i++)
        {
            spotlight.enabled = true;
            spotlight.intensity = baseIntensity * 0.35f;
            yield return new WaitForSeconds(0.05f);

            spotlight.enabled = false;
            yield return new WaitForSeconds(0.09f);
        }

        RestoreIntensity();
        deadFlickerRoutine = null;
    }

    // ------------------------------------------------------------------ //
    // Public API
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Switches the flashlight on/off from code (e.g. a scripted event).
    /// Enemies react immediately because the FlashlightDetector reads the
    /// light's enabled state every check. Note: switching ON does nothing
    /// while the battery is empty, and switching to the state the light is
    /// already in does nothing (v2.2 — animations don't replay either).
    /// </summary>
    public void SetOn(bool on)
    {
        if (spotlight == null) return;
        if (on && IsBatteryEmpty) return;   // a dead battery cannot be switched on
        if (spotlight.enabled == on) return; // already in that state

        spotlight.enabled = on;
        PushAnimationState(on, true);

        if (on)
        {
            if (onTurnedOn != null) onTurnedOn.Invoke();
        }
        else
        {
            if (onTurnedOff != null) onTurnedOff.Invoke();
        }
    }

    /// <summary>
    /// Recharges the battery by a fraction of its full capacity
    /// (e.g. 0.25f = a battery pickup worth 25%). Does nothing while the
    /// battery system is off.
    /// </summary>
    public void RechargeBattery(float fractionOfFull)
    {
        if (!useBattery) return;

        batteryLevel = Mathf.Clamp01(batteryLevel + fractionOfFull);
        batteryEmpty = batteryLevel <= 0f;

        // above the threshold again -> the low-battery warning can re-fire on the next drain
        if (batteryLevel > lowBatteryThreshold)
            lowBatteryAnnounced = false;

        RestoreIntensity();
    }

    /// <summary>Recharges the battery to full (e.g. picking up a fresh battery).</summary>
    public void RechargeBatteryFull()
    {
        if (!useBattery) return;

        batteryLevel = 1f;
        batteryEmpty = false;
        lowBatteryAnnounced = false;

        RestoreIntensity();
    }

    // ------------------------------------------------------------------ //
    // Editor test actions (right-click the component header in the Inspector)
    // ------------------------------------------------------------------ //

    [ContextMenu("Battery: Recharge Full (test)")]
    private void TestRechargeFull()
    {
        RechargeBatteryFull();
    }

    [ContextMenu("Battery: Add 25% (pickup test)")]
    private void TestRechargePickup()
    {
        RechargeBattery(0.25f);
    }

    [ContextMenu("Battery: Drain To Empty (test)")]
    private void TestDrain()
    {
        if (!useBattery) return;
        batteryLevel = 0.0001f;   // empties within a frame while the light is on
    }

    [ContextMenu("Animation: Show Detected Animator & Parameters")]
    private void DebugAnimationSetup()
    {
        ResolveAnimator();
        CacheAnimationParameters();

        if (characterAnimator == null)
        {
            Debug.Log("[FlashlightController] Animation check: no Animator found (the flashlight still " +
                      "works — animations are optional). Assign 'Character Animator', or make sure an " +
                      "Animator exists on the player, to enable on/off animations.", this);
            return;
        }

        // v2.3: report the controller-less Animator explicitly — otherwise
        // every parameter would misleadingly show up as "not found".
        if (characterAnimator.runtimeAnimatorController == null)
        {
            Debug.Log("[FlashlightController] Animation check: found an Animator on '" + characterAnimator.name +
                      "' but it has NO Animator Controller assigned — animations are skipped (this is " +
                      "the 'Animator is not playing an AnimatorController' situation). Assign a " +
                      "controller to that Animator to enable animations; the flashlight itself is " +
                      "unaffected.", this);
            return;
        }

        Debug.Log("[FlashlightController] Animation check: using the Animator on '" + characterAnimator.name +
                  "'. Bool '" + boolParameterName + "': " + (animBoolValid ? "FOUND" : "not found") +
                  ". Trigger '" + onTriggerName + "': " + (animOnTriggerValid ? "FOUND" : "not found") +
                  ". Trigger '" + offTriggerName + "': " + (animOffTriggerValid ? "FOUND" : "not found") +
                  ". Not-found parameters simply stay un-animated — rename the fields above to match " +
                  "your Animator Controller's parameter names.", this);

        // push the current state so the result is visible immediately
        if (animBoolValid && spotlight != null)
        {
            characterAnimator.SetBool(animBoolId, spotlight.enabled);
        }
    }

#if UNITY_EDITOR
    // ------------------------------------------------------------------ //
    // Editor action: place a PERMANENT, fully editable battery HUD object
    // into the open scene — then move / scale it with the normal editor
    // tools (no code, no Play mode needed). See FlashlightBatteryHUD v1.1.
    // ------------------------------------------------------------------ //

    [ContextMenu("Battery: Create Editable HUD In Scene (editor)")]
    private void CreateEditableHudInScene()
    {
        if (Application.isPlaying)
        {
            Debug.LogWarning("[FlashlightController] Exit Play mode first — this action places a permanent, " +
                             "editable HUD object into the scene.", this);
            return;
        }

        if (!gameObject.scene.IsValid())
        {
            Debug.LogWarning("[FlashlightController] This is a prefab asset, not a scene instance. Open the " +
                             "scene and run this action on the flashlight instance inside it.", this);
            return;
        }

        if (UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage() != null)
        {
            Debug.LogWarning("[FlashlightController] Prefab editing mode is open. Close it, then run this " +
                             "action from the flashlight instance in the scene itself.", this);
            return;
        }

        // already one in the scene? just select it — never duplicate
        FlashlightBatteryHUD existing = FindExistingHud();
        if (existing != null)
        {
            UnityEditor.Selection.activeGameObject = existing.gameObject;
            Debug.Log("[FlashlightController] This scene already contains a battery HUD ('" + existing.name +
                      "') — selecting it. Move/scale its 'BatteryBar' child with the Scene view tools.", this);
            return;
        }

        GameObject hudGO = new GameObject("Flashlight Battery HUD");
        FlashlightBatteryHUD hud = hudGO.AddComponent<FlashlightBatteryHUD>();
        hud.Bind(this);

        UnityEditor.Undo.RegisterCreatedObjectUndo(hudGO, "Create Flashlight Battery HUD");
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
        UnityEditor.Selection.activeGameObject = hudGO;

        Debug.Log("[FlashlightController] Battery HUD created in the scene (selected now). In the Hierarchy " +
                  "select 'Flashlight Battery HUD' → 'BatteryBar' and MOVE / SCALE it with the Scene view " +
                  "Rect and Scale tools (or the RectTransform fields in the Inspector). Save the scene to keep " +
                  "the layout — at Play it is reused automatically (no duplicate is created).", this);
    }
#endif

    // ------------------------------------------------------------------ //
    // Input & camera lookup
    // ------------------------------------------------------------------ //

    private void HandleToggleInput()
    {
        if (!canToggle || spotlight == null) return;

        if (ReadToggleKeyPressed())
        {
            if (spotlight.enabled)
            {
                spotlight.enabled = false;          // switching OFF is always allowed
                PushAnimationState(false, true);
                if (onTurnedOff != null) onTurnedOff.Invoke();
            }
            else if (batteryEmpty)
            {
                // dead battery: a weak dying sputter instead of silence
                if (deadFlickerOnToggle && deadFlickerRoutine == null)
                    deadFlickerRoutine = StartCoroutine(DeadFlickerRoutine());
            }
            else
            {
                spotlight.enabled = true;
                PushAnimationState(true, true);
                if (onTurnedOn != null) onTurnedOn.Invoke();
            }
        }
    }

    private bool ReadToggleKeyPressed()
    {
#if ENABLE_INPUT_SYSTEM
        // KeyCodeBridge maps the Inspector KeyCode to the new Input System key,
        // so the Toggle Key field genuinely works on both input backends.
        return KeyCodeBridge.WasKeyPressedThisFrame(Keyboard.current, toggleKey);
#else
        return Input.GetKeyDown(toggleKey);
#endif
    }

    private void FindCameraIfNeeded()
    {
        if (cameraTransform != null) return;

        // Camera.main only finds ENABLED cameras tagged 'MainCamera'.
        Camera main = Camera.main;
        if (main != null)
        {
            cameraTransform = main.transform;
            return;
        }

        // Keep retrying every frame (the camera might be enabled later or the
        // scene might still be loading). Only nag the user once about it.
        cameraSearchFrames++;
        if (!loggedCameraWarning && cameraSearchFrames > 120)
        {
            loggedCameraWarning = true;
            Debug.LogWarning("[FlashlightController] No camera tagged 'MainCamera' found. " +
                             "Tag your camera 'MainCamera' or assign it to the 'Camera Transform' field. " +
                             "Retrying silently every frame until one exists.", this);
        }
    }
    public bool IsFlashlightOn
    {
        get { return IsOn; }
    }

    public void SetFlashlightForPickup(bool isOn)
    {
        SetOn(isOn);
    }
}
