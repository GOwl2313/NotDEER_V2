using UnityEngine;

/// <summary>
/// VR PLAYER SETUP (v1.0) — the one component that turns the copied scene
/// into the VR scene. Put it on the PLAYER ROOT (the GameObject tagged
/// "Player"); on Play (or via right-click → "Setup VR Now") it wires the
/// whole NotDEER VR layer automatically:
///
///   1. XR RIG   — finds the "XR Origin (XR Rig/VR)" under the player,
///                 re-parents it under the Player root at local zero (so
///                 WARPS MOVE THE PLAYER ROOT and the hitbox, event points,
///                 enemies and prompts all keep following like in the flat
///                 game).
///   2. CAMERAS  — deactivates the ORIGINAL flat-game camera GameObject
///                 (fixes Unity's duplicate "MainCamera" tag warning and
///                 the double AudioListener) and disables the flat-game
///                 PlayerMovementController + PlayerCameraController.
///                 Everything else (hitbox, footsteps, flashlight, HUDs…)
///                 stays untouched — "the rest like the original one".
///   3. HANDS    — looks for existing XR Interaction Toolkit hand
///                 controllers under the rig; when none exist it creates
///                 "VR Left Hand" / "VR Right Hand" anchors under the
///                 Camera Offset, each with a VRHandPoseDriver that reads
///                 the controller straight from UnityEngine.XR (works with
///                 the XR Interaction Simulator in the editor AND with a
///                 real headset — no XRI code dependency at all).
///   4. VR LAYER — installs the components, idempotently (never duplicates
///                 on re-runs):
///                   LEFT  hand: VRWarpLocomotion (white line + ring warp),
///                               VRUIPointer, VRTouchPickup
///                   RIGHT hand: VRFlashlightMount (beam expels enemies),
///                               VRUIPointer, VRTouchPickup
///                   PLAYER root: VRPauseMenu (menu spawns from left hand)
///   5. AUDIO    — makes sure exactly one AudioListener survives (the XR
///                 camera's) after the original camera is deactivated.
///
/// WHAT YOU STILL DO MANUALLY (see NotDEER_VRSetup.md):
///   • create the XR Origin via GameObject > XR > XR Origin (VR) and drag
///     it under the Player (or anywhere — this script finds it under the
///     player; keep it there),
///   • install the XR Interaction Simulator (editor testing).
///
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

    [Tooltip("Disable the flat-game PlayerMovementController + PlayerCameraController (VR warping replaces walking; the XR camera replaces mouse look).")]
    [SerializeField] private bool disableFlatGameplay = true;

    [Tooltip("Deactivate the original flat-game camera GameObject (fixes the duplicate MainCamera tag warning + double AudioListener). The XR Origin camera takes over.")]
    [SerializeField] private bool disableOriginalCamera = true;

    [Tooltip("Create 'VR Left Hand' / 'VR Right Hand' anchors when no XR Interaction Toolkit controllers exist under the rig.")]
    [SerializeField] private bool createHandsIfMissing = true;

    [Header("Diagnostics")]

    [Tooltip("Log every setup step to the Console.")]
    [SerializeField] private bool verboseLogging = true;

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

        // ---- 2. cameras + flat gameplay ----
        if (disableOriginalCamera) DeactivateOriginalCameras(xrOrigin);
        if (disableFlatGameplay) DisableFlatGameplay();

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
        EnsureAudioListener(xrOrigin);

        setupDone = true;
        Log("VR layer installed: left hand = warp + UI pointer + touch pickup, right hand = flashlight + UI pointer + touch pickup, pause menu on the player root.");
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

        // Scene-root fallback (matches the freshly created "XR Origin (VR)").
        Transform[] all = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null && all[i].parent == null &&
                all[i].name.IndexOf("XR Origin", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return all[i];
            }
        }
        return null;
    }

    /// <summary>Deactivates every camera GameObject under the player that is NOT part of the XR rig.</summary>
    private void DeactivateOriginalCameras(Transform xrOrigin)
    {
        Camera[] cameras = GetComponentsInChildren<Camera>(true);
        for (int i = 0; i < cameras.Length; i++)
        {
            Camera camera = cameras[i];
            if (camera == null) continue;

            // The XR rig's own camera stays (it becomes the player's eyes).
            if (camera.transform.IsChildOf(xrOrigin)) continue;

            string cameraName = camera.gameObject.name;
            camera.gameObject.SetActive(false);
            Log("Original camera '" + cameraName + "' deactivated (the XR Origin camera takes over; this also fixes the duplicate MainCamera tag warning).");
        }
    }

    private void DisableFlatGameplay()
    {
        PlayerMovementController movement = GetComponentInChildren<PlayerMovementController>();
        if (movement != null)
        {
            movement.enabled = false;
            Log("PlayerMovementController disabled (warping replaces walking).");
        }

        PlayerCameraController look = GetComponentInChildren<PlayerCameraController>();
        if (look != null)
        {
            look.enabled = false;
            Log("PlayerCameraController disabled (the XR headset replaces mouse look).");
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

        Log("Created '" + createdName + "' under '" + cameraOffset.name + "' (pose driven from UnityEngine.XR — works with the XR Interaction Simulator and real headsets).");
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
    }

    private T InstallOrAdd<T>(GameObject host) where T : Component
    {
        T existing = host.GetComponent<T>();
        if (existing != null) return existing;

        T added = host.AddComponent<T>();
        Log("Added " + typeof(T).Name + " to '" + host.name + "'.");
        return added;
    }

    private void EnsureAudioListener(Transform xrOrigin)
    {
        AudioListener listener = xrOrigin.GetComponentInChildren<AudioListener>();
        if (listener != null) return;

        Camera xrCamera = xrOrigin.GetComponentInChildren<Camera>();
        if (xrCamera != null)
        {
            xrCamera.gameObject.AddComponent<AudioListener>();
            Log("AudioListener added to the XR camera (the original camera's listener is gone with it).");
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
}
