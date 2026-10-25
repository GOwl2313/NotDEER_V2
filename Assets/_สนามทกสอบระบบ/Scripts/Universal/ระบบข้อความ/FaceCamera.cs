using UnityEngine;

/// <summary>
/// Keeps a floating text / world-space canvas upright and facing the camera
/// (billboard).
///
/// v2.0 — VR-SAFE + NULL-SAFE REWRITE (the original threw every frame):
///   • The original cached Camera.main once in Start() and dereferenced it
///     unguarded in LateUpdate — if the camera did not exist yet (the XR
///     rig's Main Camera can appear after scene objects start) it threw a
///     NullReferenceException EVERY FRAME, and once the rig appeared the
///     billboard kept facing a stale/dead camera.
///   • NOW: the camera is re-resolved every 0.5 s while missing, a destroyed
///     camera is handled safely, and a missing parent no longer throws.
///   • The billboard always faces the live Main Camera — in XR Interaction
///     Toolkit rigs that is the headset's centre-eye camera, so the text
///     faces the actual player (not the rig's floor origin).
/// </summary>
public class FaceCamera : MonoBehaviour
{
    private Camera mainCamera;
    private Vector3 offsetFromParent;
    private float cameraSearchTimer;

    private const float CameraRescanInterval = 0.5f;

    private void Start()
    {
        // Calculate the initial offset within the parent
        offsetFromParent = transform.localPosition;

        mainCamera = Camera.main;
        if (mainCamera == null) cameraSearchTimer = CameraRescanInterval; // retry on the first LateUpdate
    }

    private void LateUpdate()
    {
        if (mainCamera == null)
        {
            cameraSearchTimer += Time.unscaledDeltaTime;
            if (cameraSearchTimer >= CameraRescanInterval)
            {
                cameraSearchTimer = 0f;
                mainCamera = Camera.main;
            }

            if (mainCamera == null) return; // no camera yet — try again next frame
        }

        // Maintain the position offset within the parent
        if (transform.parent != null)
        {
            transform.position = transform.parent.position + offsetFromParent;
        }

        // Make the canvas face the camera (kept upright)
        Vector3 directionToCamera = mainCamera.transform.position - transform.position;
        directionToCamera.y = 0f;  // Keep the text upright

        // Apply rotation to face the camera
        if (directionToCamera.sqrMagnitude < 0.0001f) return;
        transform.rotation = Quaternion.LookRotation(-directionToCamera, Vector3.up);
    }
}
