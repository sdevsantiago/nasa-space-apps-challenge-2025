using UnityEngine;

/// <summary>
/// Simple free-fly camera controller that moves with WASD and rotates with the mouse or Q/E keys.
/// Hold the right mouse button (configurable) to enable mouse look.
/// </summary>
public class CameraController : MonoBehaviour
{
    [Header("Movement")]
    [SerializeField] float moveSpeed = 6f;
    [SerializeField] float sprintMultiplier = 1.8f;
    [SerializeField] bool keepMovementOnPlane = true;

    [Header("Rotation")]
    [SerializeField] float mouseSensitivity = 2.5f;
    [SerializeField] float keyRotationSpeed = 90f;
    [SerializeField] float pitchMin = -80f;
    [SerializeField] float pitchMax = 80f;
    [SerializeField] bool requireRightMouseButton = true;

    float _yaw;
    float _pitch;

    void Awake()
    {
        var angles = transform.eulerAngles;
        _yaw = angles.y;
        _pitch = angles.x;
        ClampPitch();
    }

    void Update()
    {
        RotateCamera();
        MoveCamera();
    }

    void RotateCamera()
    {
        float yawInput = 0f;
        if (Input.GetKey(KeyCode.Q))
            yawInput -= 1f;
        if (Input.GetKey(KeyCode.E))
            yawInput += 1f;

        if (Mathf.Abs(yawInput) > Mathf.Epsilon)
            _yaw += yawInput * keyRotationSpeed * Time.deltaTime;

        bool mouseActive = !requireRightMouseButton || Input.GetMouseButton(1);
        if (mouseActive)
        {
            float mouseX = Input.GetAxisRaw("Mouse X");
            float mouseY = Input.GetAxisRaw("Mouse Y");

            _yaw += mouseX * mouseSensitivity;
            _pitch -= mouseY * mouseSensitivity;
            ClampPitch();
        }

        transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
    }

    void MoveCamera()
    {
        float horizontal = Input.GetAxisRaw("Horizontal");
        float vertical = Input.GetAxisRaw("Vertical");

        Vector3 forward = transform.forward;
        Vector3 right = transform.right;

        if (keepMovementOnPlane)
        {
            forward.y = 0f;
            right.y = 0f;
            forward.Normalize();
            right.Normalize();
        }

        Vector3 moveDirection = forward * vertical + right * horizontal;
        if (moveDirection.sqrMagnitude > 1f)
            moveDirection.Normalize();

        float speed = moveSpeed;
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            speed *= sprintMultiplier;

        transform.position += moveDirection * speed * Time.deltaTime;
    }

    void ClampPitch()
    {
        _pitch = Mathf.Clamp(_pitch, pitchMin, pitchMax);
    }
}
