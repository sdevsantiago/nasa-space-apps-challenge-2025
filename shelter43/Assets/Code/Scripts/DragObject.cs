using UnityEngine;

/// <summary>
/// Allows dragging an object across a horizontal plane while keeping Y locked.
/// Uses Rigidbody forces for smooth motion and stops instantly when released.
/// </summary>
[RequireComponent(typeof(Collider))]
public class DragObject : MonoBehaviour
{
    [Header("Dragging")]
    [SerializeField] Camera dragCamera;
    [SerializeField] float lockedY = 1f;
    [SerializeField] bool useInitialHeight = true;

    [Header("Physics")]
    [SerializeField] Rigidbody targetBody;
    [SerializeField] float springForce = 120f;
    [SerializeField] float damping = 24f;
    [SerializeField] float maxHorizontalSpeed = 10f;

    Vector3 _dragOffset;
    Plane _dragPlane;
    Rigidbody _rigidbody;
    bool _isDragging;
    Vector3 _targetPosition;
    RigidbodyConstraints _originalConstraints;

    void Awake()
    {
        if (dragCamera == null)
            dragCamera = Camera.main;

        _rigidbody = targetBody != null ? targetBody : GetComponent<Rigidbody>();
        if (_rigidbody == null)
        {
            _rigidbody = gameObject.AddComponent<Rigidbody>();
        }

        ConfigureRigidbody();
    }

    void Start()
    {
        if (useInitialHeight)
            lockedY = _rigidbody.position.y;

        Vector3 start = _rigidbody.position;
        start.y = lockedY;
        _rigidbody.position = start;

        _dragPlane = new Plane(Vector3.up, new Vector3(0f, lockedY, 0f));
        _targetPosition = _rigidbody.position;
    }

    void OnDestroy()
    {
        if (_rigidbody != null)
        {
            _rigidbody.constraints = _originalConstraints;
        }
    }

    void OnMouseDown()
    {
        if (!ValidateCamera())
            return;

        UpdatePlaneHeight();

        var ray = dragCamera.ScreenPointToRay(Input.mousePosition);
        if (_dragPlane.Raycast(ray, out float enter))
        {
            Vector3 hitPoint = ray.GetPoint(enter);
            Vector3 currentPosition = _rigidbody.position;
            _dragOffset = currentPosition - hitPoint;
            _dragOffset.y = 0f;
            _targetPosition = currentPosition;
            _isDragging = true;
            ResetVelocities();
        }
    }

    void OnMouseDrag()
    {
        if (!_isDragging || !ValidateCamera())
            return;

        UpdatePlaneHeight();

        var ray = dragCamera.ScreenPointToRay(Input.mousePosition);
        if (_dragPlane.Raycast(ray, out float enter))
        {
            Vector3 target = ray.GetPoint(enter) + _dragOffset;
            target.y = lockedY;
            _targetPosition = target;
        }
    }

    void OnMouseUp()
    {
        if (!_isDragging)
            return;

        _isDragging = false;
        ResetVelocities();
    }

    void FixedUpdate()
    {
        if (!_isDragging)
            return;

        Vector3 current = _rigidbody.position;
        Vector3 planarCurrent = new Vector3(current.x, lockedY, current.z);
        Vector3 planarTarget = new Vector3(_targetPosition.x, lockedY, _targetPosition.z);
        Vector3 delta = planarTarget - planarCurrent;

        if (delta.sqrMagnitude <= 0.0001f)
        {
            _rigidbody.position = planarTarget;
            ResetVelocities();
            return;
        }

        Vector3 horizontalVelocity = new Vector3(_rigidbody.linearVelocity.x, 0f, _rigidbody.linearVelocity.z);
        Vector3 desiredAcceleration = delta * springForce - horizontalVelocity * damping;

        desiredAcceleration.y = 0f;
        _rigidbody.AddForce(desiredAcceleration, ForceMode.Acceleration);

        horizontalVelocity = new Vector3(_rigidbody.linearVelocity.x, 0f, _rigidbody.linearVelocity.z);
        horizontalVelocity = Vector3.ClampMagnitude(horizontalVelocity, maxHorizontalSpeed);
        _rigidbody.linearVelocity = new Vector3(horizontalVelocity.x, 0f, horizontalVelocity.z);
    }

    void ConfigureRigidbody()
    {
        _originalConstraints = _rigidbody.constraints;
        _rigidbody.useGravity = false;
        _rigidbody.linearDamping = 0f;
        _rigidbody.angularDamping = 0.05f;
        _rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
        _rigidbody.constraints = _originalConstraints | RigidbodyConstraints.FreezeRotation | RigidbodyConstraints.FreezePositionY;
    }

    void ResetVelocities()
    {
        _rigidbody.linearVelocity = Vector3.zero;
        _rigidbody.angularVelocity = Vector3.zero;
    }

    bool ValidateCamera()
    {
        if (dragCamera != null)
            return true;

        dragCamera = Camera.main;
        return dragCamera != null;
    }

    void UpdatePlaneHeight()
    {
        _dragPlane.SetNormalAndPosition(Vector3.up, new Vector3(0f, lockedY, 0f));
    }
}
