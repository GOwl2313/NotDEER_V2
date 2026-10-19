
using UnityEngine;

public class PickupAnimationController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Animator animator;

    [Tooltip("Optional: GameObject that receives the Grab Animation Event.")]
    [SerializeField] private PickupAnimationEventReceiver eventReceiver;

    [Header("Animation")]
    [SerializeField] private string pickupTrigger = "Pickup";
    [SerializeField] private FlashlightController flashlightController;
    [SerializeField] private string pickupLayerName = "PickupLayer";

    private int pickupLayerIndex = -1;
    private bool flashlightWasOn;
    public bool IsPickingUp { get; private set; }

    private void Awake()
    {
        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        if (animator != null)
            pickupLayerIndex = animator.GetLayerIndex(pickupLayerName);

        if (eventReceiver == null && animator != null)
            eventReceiver = animator.GetComponent<PickupAnimationEventReceiver>();

        if (eventReceiver != null)
            eventReceiver.SetController(this);
    }

    public void PlayPickupAnimation()
    {
        if (animator == null || IsPickingUp)
            return;

        IsPickingUp = true;

        // จำสถานะไฟฉายก่อนเริ่มเก็บ
        flashlightWasOn = flashlightController != null
            && flashlightController.IsFlashlightOn;

        // ปิดไฟฉาย
        if (flashlightController != null)
            flashlightController.SetFlashlightForPickup(false);

        animator.SetTrigger(pickupTrigger);
    }

    public void StopPickupAnimation()
    {
        if (!IsPickingUp)
            return;

        // เปิดไฟฉายกลับตามสถานะก่อนเริ่มเก็บ
        if (flashlightController != null)
            flashlightController.SetFlashlightForPickup(flashlightWasOn);

        IsPickingUp = false;
    }
}
