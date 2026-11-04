// TotemEventProbe.cs - TEMPORARY diagnostic, "Totem D (1)" only.
// Read-only: logs facts, changes nothing. Delete it after the pickup works.
using UnityEngine;
using UnityEngine.EventSystems;

public class TotemEventProbe : MonoBehaviour, IPointerDownHandler, IPointerClickHandler
{
    private void Start()
    {
        EventSystem es = Object.FindFirstObjectByType<EventSystem>();
        Camera cam = Camera.main;
        bool hasRaycaster = cam != null && cam.GetComponent<PhysicsRaycaster>() != null;
        Collider col = GetComponentInChildren<Collider>();
        bool sameGo = col != null && col.gameObject == gameObject;

        Debug.Log("[Probe] EventSystem: " + (es ? "found" : "MISSING (none active)")
            + " | MainCamera: " + (cam ? cam.name : "MISSING")
            + " | PhysicsRaycaster on it: " + hasRaycaster
            + " | Collider: " + (col ? col.GetType().Name + (col.isTrigger ? " (isTrigger)" : "") : "MISSING")
            + " | on this GameObject: " + sameGo);
    }

    public void OnPointerDown(PointerEventData d)
    {
        Debug.Log("[Probe] POINTER DOWN reached " + name + " -> chain OK, break is after the event (Section B)");
    }

    public void OnPointerClick(PointerEventData d)
    {
        Debug.Log("[Probe] CLICK reached " + name);
    }

    private void OnMouseDown()
    {
        Debug.Log("[Probe] OnMouseDown (mouse ray hit this GameObject - no EventSystem needed)");
    }
}