
using UnityEngine;

public class PickupAnimationEventReceiver : MonoBehaviour
{
    [SerializeField] private PickupAnimationController controller;

    public void SetController(PickupAnimationController target)
    {
        controller = target;
    }

    // Called by the Grab Animation Event.
    public void StopPickupAnimation()
    {
        if (controller != null)
        {
            controller.StopPickupAnimation();
        }
        else
        {
            Debug.LogWarning(
                "[PickupAnimationEventReceiver] " +
                "PickupAnimationController is not assigned.",
                this
            );
        }
    }
}
