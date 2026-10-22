using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// VR HAND POSE DRIVER (v1.1) — moves a hand anchor to the tracked pose of
/// an XR controller.
///
/// Attach this to the GameObject that represents one hand under the XR
/// Origin (VRPlayerSetup creates "VR Left Hand" / "VR Right Hand" under the
/// Camera Offset automatically). Every frame it copies the controller's
/// tracking-space position/rotation onto that anchor's LOCAL transform:
///
///   • the anchor is a child of the XR Origin's Camera Offset — the same
///     parent the XR Origin's camera lives under — so the hand and the
///     head always share the same offset conventions, whatever tracking
///     origin mode (Device / Floor) the rig uses;
///   • poses are written in Update AND re-written in LateUpdate so the
///     warp line, the UI ray and the touch colliders all start the frame
///     from a fresh pose and render with the freshest one;
///   • this driver contains ZERO XR Interaction Toolkit references — it
///     talks straight to UnityEngine.XR, so it works with the XR
///     Device / XR Interaction Simulator in the editor AND with a real
///     headset.
///
/// REST POSE (new in v1.1)
///   Before any controller connects (simulator booting, device asleep),
///   the anchor now sits at a natural "holding a controller, pointing
///     forward-down" REST POSE instead of the tracking origin — so the
///     hands are visible in sane places from the very first frame and the
///     warp ray points at the ground instead of the horizon. The moment a
///     device provides a real pose, it takes over.
///
/// If an existing XRI "LeftHand Controller"/"RightHand Controller" object
/// (with its own TrackedPoseDriver) is already in the rig, VRPlayerSetup
/// reuses it and does NOT add this component — both drive the same device.
///
/// v1.1 — rest pose while no device exists + one-time "tracking OK" log.
/// v1.0 — initial release with the NotDEER VR layer.
/// </summary>
[DisallowMultipleComponent]
public class VRHandPoseDriver : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Tooltip("Which controller this hand follows.")]
    [SerializeField] private XRNode node = XRNode.LeftHand;

    [Tooltip("Write the pose in LateUpdate too, so lines/rays attached to this hand render with the freshest position (disable only if another pose driver already writes LateUpdate).")]
    [SerializeField] private bool alsoDriveInLateUpdate = true;

    [Tooltip("While NO controller exists, hold the hand at this rest pose (relative to the Camera Offset) instead of the tracking origin. Defined for the LEFT hand — the right hand mirrors it automatically.")]
    [SerializeField] private bool useRestPoseWhenNoDevice = true;

    [Tooltip("Rest pose position for the LEFT hand (right hand mirrors X). Looks like a controller held in front of you, pointing forward-down.")]
    [SerializeField] private Vector3 restLocalPosition = new Vector3(-0.22f, -0.18f, 0.32f);

    [Tooltip("Rest pose rotation (Euler degrees) for the LEFT hand — pitched down so the warp ray finds the ground, yawed slightly inward. Right hand mirrors the yaw.")]
    [SerializeField] private Vector3 restLocalEuler = new Vector3(40f, 12f, 0f);

    [Tooltip("Seconds to wait after Play starts before warning that no XR controller was found (the simulator takes a moment to create its devices). 0 disables the warning.")]
    [SerializeField, Min(0f)] private float noDeviceWarningDelay = 5f;

    [Tooltip("Log once when this hand starts tracking a real controller (proof the simulator/headset is feeding poses).")]
    [SerializeField] private bool logWhenTrackingStarts = true;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    private bool warnedNoDevice;
    private string loggedDeviceName;

    /// <summary>The controller this hand follows (settable from code — VRPlayerSetup uses it when it creates hands).</summary>
    public XRNode Node
    {
        get { return node; }
        set { node = value; }
    }

    /// <summary>True while the configured controller device exists.</summary>
    public bool HasDevice
    {
        get { return VRXRInput.HasDevice(node); }
    }

    // ------------------------------------------------------------------ //
    // Unity messages
    // ------------------------------------------------------------------ //

    private void Update()
    {
        Drive();
    }

    private void LateUpdate()
    {
        if (alsoDriveInLateUpdate) Drive();
    }

    // ------------------------------------------------------------------ //
    // Driving
    // ------------------------------------------------------------------ //

    private void Drive()
    {
        if (VRXRInput.TryGetLocalPose(node, out Vector3 localPosition, out Quaternion localRotation))
        {
            transform.localPosition = localPosition;
            transform.localRotation = localRotation;

            if (logWhenTrackingStarts && loggedDeviceName == null)
            {
                loggedDeviceName = VRXRInput.DeviceName(node);
                if (loggedDeviceName != null)
                {
                    Debug.Log("[VRHandPoseDriver] " + HandName + " hand tracking: '" + loggedDeviceName +
                              "'.", this);
                }
            }
            return;
        }

        // No valid device (yet) — hold the rest pose so the hand is in a
        // sane, visible place instead of sitting on the tracking origin.
        // This is a normal state while the simulator boots.
        if (useRestPoseWhenNoDevice)
        {
            bool left = node != XRNode.RightHand;
            transform.localPosition = new Vector3(
                left ? restLocalPosition.x : -restLocalPosition.x,
                restLocalPosition.y,
                restLocalPosition.z);
            transform.localRotation = Quaternion.Euler(new Vector3(
                restLocalEuler.x,
                left ? restLocalEuler.y : -restLocalEuler.y,
                restLocalEuler.z));
        }

        if (!warnedNoDevice && noDeviceWarningDelay > 0f)
        {
            VRXRInput.WarnNoControllersOnce(noDeviceWarningDelay);
            if (VRXRInput.HasNoControllers()) warnedNoDevice = true;
        }
    }

    private string HandName
    {
        get { return node == XRNode.RightHand ? "Right" : "Left"; }
    }
}
