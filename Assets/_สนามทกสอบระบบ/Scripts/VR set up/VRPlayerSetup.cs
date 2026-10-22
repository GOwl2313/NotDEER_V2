using UnityEngine;

/// <summary>
/// VR PLAYER SETUP (v1.1) — the one component that turns the copied scene
/// into the VR scene. Put it on the PLAYER ROOT (the GameObject tagged
/// "Player"); on Play (or via right-click → "Setup VR Now") it wires the
/// whole NotDEER VR layer automatically:
///
///   1. XR RIG   — finds the "XR Origin (XR Rig/VR)" — under the player OR
///                 anywhere else in the scene (Unity's menu drops it at the
///                 scene root) — re-parents it under the Player root at
///                 local zero, so WARPS MOVE THE PLAYER ROOT and the
///                 hitbox, event points, enemies and prompts all keep
///                 following like in the flat game. The rig's camera is
///                 remembered in the static <see cref="XRCamera"/> so every
///                 VR component uses THE VR CAMERA as "the head", never a
///                 random Camera.main.
///   2. CAMERAS  — takes over rendering for the WHOLE SCENE, not just the
///                 player: every camera outside the XR rig (the flat game's
///                 "Main Camera (1)" usually sits at the SCENE ROOT, not
///                 under the Player) is deactivated and untagged. Fixes the
///                 duplicate "MainCamera" tag warning, the double rendering
///                 and the double AudioListener. Also disables the flat
///                 PlayerMovementController + PlayerCameraController
///                 WHEREVER they are (the orbit camera script usually lives
///                 on the flat camera object, not on the player).
///   3. HANDS    — looks for existing XR Interaction Toolkit hand
///                 controllers under the rig; when none exist it creates
///                 "VR Left Hand" / "VR Right Hand" anchors under the
///                 Camera Offset, each with a VRHandPoseDriver (reads the
///                 controller straight from UnityEngine.XR — works with the
///                 XR Device/XR Interaction Simulator in the editor AND
///                 with a real headset) and a VRHandVisual so you can
///                 actually SEE the hand.
///   4. VR LAYER — installs the components, idempotently (never duplicates
///                 on re-runs):
///                   LEFT  hand: VRWarpLocomotion (white line + ring warp),
///                               VRUIPointer, VRTouchPickup, VRHandVisual
///                   RIGHT hand: VRFlashlightMount (beam expels enemies),
///                               VRUIPointer, VRTouchPickup, VRHandVisual
///                   PLAYER root: VRPauseMenu (menu spawns from left hand)
///   5. AUDIO    — every AudioListener outside the XR rig is removed; the
///                 XR camera is guaranteed exactly one.
///
/// WHAT YOU STILL DO MANUALLY (see NotDEER_VRSetup.md):
///   • create the XR Origin via GameObject > XR > XR Origin (VR) and drag
///     it under the Player (anywhere works — this script finds it),
///   • install the XR Device/XR Interaction Simulator (editor testing).
///
/// v1.1 — scene-wide camera takeover (the flat camera can be at the scene
///        root — v1.0 only looked under the Player and left a second
///        MainCamera-tagged camera rendering), scene-wide flat-controller
///        disabling, AudioListener cleanup, visible hands, static XRCamera
///        for every head-relative calculation, Find-API deprecation fix.
/// v1.0 — initial release with the NotDEER VR layer.
/// </summary>
[DisallowMultipleComponent]
public class VRPlayerSetup : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Setup")]

    [Tooltip("Run the whole setup automatically on Play (recommended). Manual: right-click this component → 'Setup VR Now'.")]
    [SerializeField] private bool autoSetupOnPlay = true;

    [Tooltip("Disable the flat-game PlayerMovementController + PlayerCameraController wherever they are in the scene (VR warping replaces walking; the XR camera replaces mouse look).")]
    [SerializeField] private bool disableFlatGameplay = true;

    [Tooltip("Deactivate every camera outside the XR rig, scene-wide (fixes the duplicate MainCamera tag warning, double rendering and double AudioListener). The XR Origin camera takes over.")]
    [SerializeField] private bool takeOverAllCameras = true;

    [Tooltip("Create 'VR Left Hand' / 'VR Right Hand' anchors when no XR Interaction Toolkit controllers exist under the rig.")]
    [SerializeField] private bool createHandsIfMissing = true;

    [Header("Diagnostics")]

    [Tooltip("Log every setup step to the Console.")]
    [SerializeField] private bool verboseLogging = true;

    // ------------------------------------------------------------------ //
    // Shared head reference — every VR component resolves "the head"
    // through this first (it is ALWAYS the XR rig's camera, never a
    // leftover flat-game camera that happens to be tagged MainCamera).
    // ------------------------------------------------------------------ //

    private static Camera s_XRCamera;

    /// <summary>
    /// The XR rig's camera found by the last setup run (null before setup).
    /// Lifetime-checked like any Unity object — a destroyed camera reads
    /// as null here, and callers fall back to Camera.main.
    /// </summary>
    public static Camera XRCamera
    {
        get { return s_XRCamera; }
    }

    /// <summary>Convenience: the XR camera's transform (null when unknown).</summary>
    public static Transform XRHead
    {
        get { return s_XRCamera != null ? s_XRCamera.transform : null; }
    }

    /// <summary>Play-mode entry reset — correct even with Domain Reload disabled.</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        s_XRCamera = null;
    }

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private bool setupDone;

    /// <summary>True once the VR layer is fully installed (this play session).</summary>
    public bool IsSetupComplete
    {
        get { return setupDone; }
    }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        if (autoSetupOnPlay) SetupIfNeeded();
    }

    /// <summary>Manual trigger: right-click the component header → "Setup VR Now".</summary>
    [ContextMenu("Setup VR Now")]
    private void SetupNowFromContextMenu()
    {
        setupDone = false;
        SetupIfNeeded();
    }

    // ------------------------------------------------------------------ //
    // The setup
    // ------------------------------------------------------------------ //

    private void SetupIfNeeded()
    {
        if (setupDone) return;

        // ---- 1. the XR rig ----
        Transform xrOrigin = FindXROrigin();
        if (xrOrigin == null)
        {
            Debug.LogError("[VRPlayerSetup] No 'XR Origin' found under the player. Create it via " +
                           "GameObject > XR > XR Origin (VR), drag it under the Player root, then press " +
                           "Play again (or right-click here → Setup VR Now). The flat game keeps running " +
                           "unchanged until then.", this);
            enabled = false;
            return;
        }

        if (xrOrigin.parent != transform)
        {
            xrOrigin.SetParent(transform, false);
            Log("XR Origin '" + xrOrigin.name + "' re-parented under '" + name + "'.");
        }
        xrOrigin.localPosition = Vector3.zero;
        xrOrigin.localRotation = Quaternion.identity;
        xrOrigin.localScale = Vector3.one;

        // ---- 2. cameras + flat gameplay (scene-wide!) ----
        if (!ClaimXRCamera(xrOrigin)) return;

        // Order matters: the flat controllers are disabled FIRST (while
        // their objects are still active — the legacy Find path on older
        // Unity only sees active objects), then every foreign camera is
        // deactivated + untagged.
        if (disableFlatGameplay) DisableFlatGameplay();
        if (takeOverAllCameras) TakeOverSceneCameras(xrOrigin);

        // ---- 3. hands ----
        Transform leftHand, rightHand;
        if (createHandsIfMissing)
        {
            leftHand = FindOrCreateHand(xrOrigin, XRNodeLike.Left, "LeftHand Controller", "VR Left Hand");
            rightHand = FindOrCreateHand(xrOrigin, XRNodeLike.Right, "RightHand Controller", "VR Right Hand");
        }
        else
        {
            leftHand = FindExistingHand(xrOrigin, "LeftHand Controller", "VR Left Hand");
            rightHand = FindExistingHand(xrOrigin, "RightHand Controller", "VR Right Hand");
        }

        if (leftHand == null || rightHand == null)
        {
            Debug.LogError("[VRPlayerSetup] Could not resolve hand anchors under the XR Origin — " +
                           "VR components were NOT installed.", this);
            enabled = false;
            return;
        }

        // ---- 4. the VR layer (idempotent) ----
        InstallHandComponents(leftHand, XRNodeLike.Left);
        InstallHandComponents(rightHand, XRNodeLike.Right);
        InstallOrAdd<VRPauseMenu>(gameObject);

        // ---- 5. audio ----
        EnsureSingleAudioListener(xrOrigin);

        setupDone = true;
        Log("VR layer installed: left hand = warp + UI pointer + touch pickup + visual, right hand = flashlight + UI pointer + touch pickup + visual, pause menu on the player root.");
    }

    // ------------------------------------------------------------------ //
    // Steps
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Finds the XR Origin: first under the player (the documented setup),
    /// then anywhere in the scene (Unity's menu drops it at the scene root —
    /// often far away at its default position — which is fine: the setup
    /// re-parents it under the player at local zero either way).
    /// </summary>
    private Transform FindXROrigin()
    {
        Transform[] children = GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < children.Length; i++)
        {
            if (children[i] != transform && children[i].name.IndexOf("XR Origin", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return children[i];
            }
        }

        // Scene-wide fallback (matches the freshly created "XR Origin (VR)").
        Transform[] all = FindAllScene<Transform>();
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null && all[i] != transform &&
                all[i].name.IndexOf("XR Origin", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return all[i];
            }
        }
        return null;
    }

    /// <summary>Remembers the XR rig's camera as THE head camera for the whole VR layer.</summary>
    private bool ClaimXRCamera(Transform xrOrigin)
    {
        Camera rigCamera = xrOrigin.GetComponentInChildren<Camera>(true);
        if (rigCamera == null)
        {
            Debug.LogError("[VRPlayerSetup] The XR Origin has no camera — the VR layer cannot run. " +
                           "Recreate it via GameObject > XR > XR Origin (VR).", this);
            enabled = false;
            return false;
        }

        rigCamera.gameObject.SetActive(true);
        s_XRCamera = rigCamera;
        Log("VR camera: '" + rigCamera.name + "' under '" + xrOrigin.name + "' — every VR component uses it as the head.");
        return true;
    }

    /// <summary>
    /// Deactivates every camera GameObject outside the XR rig and clears
    /// their MainCamera tag — scene-wide. The flat game's camera is often a
    /// scene-root object (NOT under the Player), so looking under the player
    /// alone is not enough: two MainCamera-tagged cameras both render and
    /// Camera.main becomes a coin flip. After this there is exactly one
    /// enabled camera in the scene: the XR rig's.
    /// </summary>
    private void TakeOverSceneCameras(Transform xrOrigin)
    {
        Camera[] cameras = FindAllScene<Camera>();
        for (int i = 0; i < cameras.Length; i++)
        {
            Camera camera = cameras[i];
            if (camera == null) continue;
            if (camera == s_XRCamera) continue;

            // The XR rig's own extra cameras (none in the standard prefab)
            // stay with the rig.
            if (camera.transform.IsChildOf(xrOrigin)) continue;

            string cameraName = camera.gameObject.name;
            camera.gameObject.SetActive(false);
            camera.gameObject.tag = "Untagged";
            Log("Camera '" + cameraName + "' deactivated + untagged (the XR Origin camera takes over; this also fixes the duplicate MainCamera tag warning and the double AudioListener).");
        }
    }

    private void DisableFlatGameplay()
    {
        // Scene-wide on purpose: PlayerCameraController usually sits on the
        // flat camera object (scene root), not on the player.
        PlayerMovementController[] movements = FindAllScene<PlayerMovementController>();
        for (int i = 0; i < movements.Length; i++)
        {
            movements[i].enabled = false;
            Log("PlayerMovementController on '" + movements[i].name + "' disabled (warping replaces walking).");
        }

        PlayerCameraController[] looks = FindAllScene<PlayerCameraController>();
        for (int i = 0; i < looks.Length; i++)
        {
            looks[i].enabled = false;
            Log("PlayerCameraController on '" + looks[i].name + "' disabled (the XR headset replaces mouse look).");
        }
    }

    private Transform FindOrCreateHand(Transform xrOrigin, XRNodeLike node, string xriName, string createdName)
    {
        Transform existing = FindExistingHand(xrOrigin, xriName, createdName);
        if (existing != null) return existing;

        Transform cameraOffset = FindCameraOffset(xrOrigin);

        GameObject handGO = new GameObject(createdName);
        handGO.transform.SetParent(cameraOffset, false);

        VRHandPoseDriver driver = handGO.AddComponent<VRHandPoseDriver>();
        driver.Node = node == XRNodeLike.Left ? UnityEngine.XR.XRNode.LeftHand : UnityEngine.XR.XRNode.RightHand;

        Log("Created '" + createdName + "' under '" + cameraOffset.name + "' (pose driven from UnityEngine.XR — works with the XR simulator and real headsets).");
        return handGO.transform;
    }

    private Transform FindExistingHand(Transform xrOrigin, string xriName, string createdName)
    {
        // Existing XRI controllers may sit under the rig root or the camera
        // offset — accept both placements.
        Transform[] children = xrOrigin.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < children.Length; i++)
        {
            string childName = children[i].name;
            if (childName.IndexOf(xriName, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                childName.IndexOf(createdName, System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return children[i];
            }
        }
        return null;
    }

    /// <summary>The XRI "Camera Offset" child of the XR Origin (falls back to the origin itself).</summary>
    private static Transform FindCameraOffset(Transform xrOrigin)
    {
        for (int i = 0; i < xrOrigin.childCount; i++)
        {
            Transform child = xrOrigin.GetChild(i);
            if (child.name.IndexOf("Camera Offset", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return child;
            }
        }
        return xrOrigin;
    }

    private void InstallHandComponents(Transform hand, XRNodeLike node)
    {
        // LEFT hand: warp + UI pointer + touch pickup.
        // RIGHT hand: flashlight + UI pointer + touch pickup.
        if (node == XRNodeLike.Left)
        {
            InstallOrAdd<VRWarpLocomotion>(hand.gameObject);
        }
        else
        {
            InstallOrAdd<VRFlashlightMount>(hand.gameObject);
        }

        InstallOrAdd<VRUIPointer>(hand.gameObject);
        InstallOrAdd<VRTouchPickup>(hand.gameObject);
        InstallOrAdd<VRHandVisual>(hand.gameObject);
    }

    private T InstallOrAdd<T>(GameObject host) where T : Component
    {
        T existing = host.GetComponent<T>();
        if (existing != null) return existing;

        T added = host.AddComponent<T>();
        Log("Added " + typeof(T).Name + " to '" + host.name + "'.");
        return added;
    }

    /// <summary>
    /// Exactly one AudioListener survives: the XR camera's. Listeners
    /// outside the rig (e.g. on the deactivated flat camera) are removed.
    /// </summary>
    private void EnsureSingleAudioListener(Transform xrOrigin)
    {
        AudioListener rigListener = xrOrigin.GetComponentInChildren<AudioListener>(true);
        if (rigListener == null && s_XRCamera != null)
        {
            s_XRCamera.gameObject.AddComponent<AudioListener>();
            Log("AudioListener added to the XR camera.");
        }

        AudioListener[] listeners = FindAllScene<AudioListener>();
        for (int i = 0; i < listeners.Length; i++)
        {
            AudioListener listener = listeners[i];
            if (listener == null) continue;
            if (listener.transform.IsChildOf(xrOrigin)) continue;

            string listenerName = listener.name;
            Destroy(listener);
            Log("AudioListener on '" + listenerName + "' removed (exactly one listener — the XR camera's — survives).");
        }
    }

    private void Log(string message)
    {
        if (verboseLogging) Debug.Log("[VRPlayerSetup] " + message, this);
    }

    // ------------------------------------------------------------------ //
    // Internals
    // ------------------------------------------------------------------ //

    /// <summary>Local left/right marker (avoids referencing UnityEngine.XR in every signature).</summary>
    private enum XRNodeLike
    {
        Left,
        Right
    }

    /// <summary>
    /// Version-correct "find every instance in the scene" helper:
    /// Unity 6.4+ (6000.4) deprecated every FindObjectsSortMode-taking and
    /// FindFirstObjectByType overload — this uses the current API there,
    /// the 2023 form on 2023–6.3, and the legacy form before that.
    /// </summary>
    private static T[] FindAllScene<T>() where T : Component
    {
#if UNITY_6000_4_OR_NEWER
        return Object.FindObjectsByType<T>(FindObjectsInactive.Include);
#elif UNITY_2023_1_OR_NEWER
        return Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
        return Object.FindObjectsOfType<T>();
#endif
    }
}