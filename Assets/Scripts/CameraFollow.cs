using UnityEngine;

public class CameraFollow : MonoBehaviour
{
    public Transform target;

    public Vector3 offset = new(0, 5, -12);
    public int distance = 12;
    public int height = 5;
    public int followSpeed = 10;

    void LateUpdate()
    {
        Vector3 desired = target.position - target.forward * distance + Vector3.up * height;
        transform.position = Vector3.Lerp(transform.position, desired, Time.deltaTime * followSpeed);
        transform.LookAt(target);
    }
}
