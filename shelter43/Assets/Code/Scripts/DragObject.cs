using UnityEngine;

/// <summary>
/// Allows dragging any object tagged "Module" across a horizontal plane while keeping Y locked.
/// Uses a spring-damper force applied to the object's Rigidbody for smooth motion while preventing runaway impulses.
/// </summary>
[RequireComponent(typeof(Collider))]
[RequireComponent(typeof(Rigidbody))]
public class DragObject : MonoBehaviour
{
    [Header("Dragging")]
    [SerializeField] Camera dragCamera;
    [SerializeField] float lockedY = 1f;
    [SerializeField] bool useInitialHeight = true;

    [Header("Physics")]
    [SerializeField] float springForce = 260f;
    [SerializeField] float damping = 40f;
    [SerializeField] float maxHorizontalSpeed = 18f;
    [SerializeField] float maxAcceleration = 70f;
    [SerializeField] float maxTargetDistance = 20f;

    [Header("Tolerance")]
    [SerializeField] float stopEpsilon = 0.0004f;

    Vector3 _dragOffset;
    Plane _dragPlane;
    Rigidbody _rigidbody;
    Collider _collider;
    bool _isDragging;
    Vector3 _targetPosition;
    float _baseLockedY;
    bool _baseHeightInitialized;

    RigidbodyConstraints _originalConstraints;
    bool _originalUseGravity;
    bool _originalIsKinematic;
    float _originalDrag;
    float _originalAngularDrag;
    RigidbodyInterpolation _originalInterpolation;
    CollisionDetectionMode _originalCollisionDetection;

    void Awake()
    {
        if (!CompareTag("Module"))
        {
            enabled = false;
            return;
        }

        if (dragCamera == null)
            dragCamera = Camera.main;

        _rigidbody = GetComponent<Rigidbody>();
        _collider = GetComponent<Collider>();
        CacheOriginalState();
        ConfigureRigidbody();
        SetIdleMode(true);
    }

    void Start()
    {
        if (_rigidbody == null)
            return;

        if (useInitialHeight)
            lockedY = _rigidbody.position.y;

        Vector3 start = _rigidbody.position;
        start.y = lockedY;
        _rigidbody.position = start;

        _dragPlane = new Plane(Vector3.up, new Vector3(0f, lockedY, 0f));
        _targetPosition = _rigidbody.position;

        if (!_baseHeightInitialized)
        {
            _baseLockedY = lockedY;
            _baseHeightInitialized = true;
        }
    }

    void OnDestroy()
    {
        if (_rigidbody == null)
            return;

        _rigidbody.constraints = _originalConstraints;
        _rigidbody.useGravity = _originalUseGravity;
        _rigidbody.isKinematic = _originalIsKinematic;
        _rigidbody.linearDamping = _originalDrag;
        _rigidbody.angularDamping = _originalAngularDrag;
        _rigidbody.interpolation = _originalInterpolation;
        _rigidbody.collisionDetectionMode = _originalCollisionDetection;
    }

    void OnMouseDown()
    {
        if (!ValidateCamera() || _rigidbody == null)
            return;

        if (transform.parent != null && transform.parent.CompareTag("Module"))
        {
            transform.SetParent(null, true);
            RestoreBaseHeight();
        }

        SetIdleMode(false);
        ResetVelocities();

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
        }
    }

    void OnMouseDrag()
    {
        if (!_isDragging || !ValidateCamera() || _rigidbody == null)
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
        SetIdleMode(true);
    }

    void FixedUpdate()
    {
        if (!_isDragging || _rigidbody == null || _rigidbody.isKinematic)
            return;

        Vector3 current = _rigidbody.position;
        Vector3 planarCurrent = new Vector3(current.x, lockedY, current.z);
        Vector3 planarTarget = new Vector3(_targetPosition.x, lockedY, _targetPosition.z);
        Vector3 delta = planarTarget - planarCurrent;

        if (delta.sqrMagnitude <= stopEpsilon)
        {
            _rigidbody.position = planarTarget;
            ResetVelocities();
            return;
        }

        float maxDistanceSqr = maxTargetDistance * maxTargetDistance;
        if (delta.sqrMagnitude > maxDistanceSqr)
        {
            delta = delta.normalized * maxTargetDistance;
            planarTarget = planarCurrent + delta;
            _targetPosition = new Vector3(planarTarget.x, lockedY, planarTarget.z);
        }

        Vector3 velocity = _rigidbody.linearVelocity;
        Vector3 horizontalVelocity = new Vector3(velocity.x, 0f, velocity.z);
        Vector3 desiredAcceleration = delta * springForce - horizontalVelocity * damping;
        desiredAcceleration.y = 0f;

        float maxAccelSqr = maxAcceleration * maxAcceleration;
        if (desiredAcceleration.sqrMagnitude > maxAccelSqr)
            desiredAcceleration = desiredAcceleration.normalized * maxAcceleration;

        _rigidbody.AddForce(desiredAcceleration, ForceMode.Acceleration);

        velocity = _rigidbody.linearVelocity;
        horizontalVelocity = new Vector3(velocity.x, 0f, velocity.z);
        float maxSpeedSqr = maxHorizontalSpeed * maxHorizontalSpeed;
        if (horizontalVelocity.sqrMagnitude > maxSpeedSqr)
            horizontalVelocity = horizontalVelocity.normalized * maxHorizontalSpeed;

        _rigidbody.linearVelocity = new Vector3(horizontalVelocity.x, 0f, horizontalVelocity.z);
    }

    void OnCollisionStay(Collision collision)
    {
        if (!_isDragging || _rigidbody == null)
            return;

        if (!collision.collider.CompareTag("Module"))
            return;

        if (collision.contactCount == 0)
            return;

        ContactPoint contact = collision.GetContact(0);
        DragObject targetModule = collision.collider.GetComponentInParent<DragObject>();
        if (targetModule != null && targetModule != this && !targetModule.transform.IsChildOf(transform))
        {
            if (TryStackOnModule(contact, collision.collider, targetModule))
            {
                _isDragging = false;
                SetIdleMode(true);
                return;
            }
        }

        Vector3 normal = contact.normal;
        normal.y = 0f;
        if (normal.sqrMagnitude <= 0.0001f)
            return;

        normal.Normalize();

        Vector3 velocity = _rigidbody.linearVelocity;
        float into = Vector3.Dot(velocity, -normal);
        if (into > 0f)
        {
            velocity += normal * into;
            velocity.y = 0f;
            float maxSpeedSqr = maxHorizontalSpeed * maxHorizontalSpeed;
            Vector3 clamped = velocity.sqrMagnitude > maxSpeedSqr
                ? velocity.normalized * maxHorizontalSpeed
                : velocity;
            _rigidbody.linearVelocity = new Vector3(clamped.x, 0f, clamped.z);
        }
    }

    void CacheOriginalState()
    {
        _originalConstraints = _rigidbody.constraints;
        _originalUseGravity = _rigidbody.useGravity;
        _originalIsKinematic = _rigidbody.isKinematic;
        _originalDrag = _rigidbody.linearDamping;
        _originalAngularDrag = _rigidbody.angularDamping;
        _originalInterpolation = _rigidbody.interpolation;
        _originalCollisionDetection = _rigidbody.collisionDetectionMode;
    }

    void ConfigureRigidbody()
    {
        _rigidbody.useGravity = false;
        _rigidbody.isKinematic = false;
        _rigidbody.linearDamping = 0f;
        _rigidbody.angularDamping = 0.05f;
        _rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
        _rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        _rigidbody.constraints = RigidbodyConstraints.FreezeRotation | RigidbodyConstraints.FreezePositionY;
    }

    void SetIdleMode(bool idle)
    {
        if (_rigidbody == null)
            return;

        if (idle)
        {
            ResetVelocities();
            _rigidbody.isKinematic = true;
            _rigidbody.constraints = RigidbodyConstraints.FreezeAll;
            _targetPosition = _rigidbody.position;
        }
        else
        {
            _rigidbody.isKinematic = false;
            _rigidbody.constraints = RigidbodyConstraints.FreezeRotation | RigidbodyConstraints.FreezePositionY;
        }
    }

    void ResetVelocities()
    {
        if (_rigidbody == null)
            return;

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

    bool TryStackOnModule(ContactPoint contact, Collider targetCollider, DragObject targetModule)
    {
        if (_collider == null || targetCollider == null)
            return false;

        if (targetModule.transform == transform || targetModule.transform.IsChildOf(transform))
            return false;

        if (HasModuleChild(targetModule.transform))
            return false;

        float verticalInfluence = Mathf.Abs(contact.normal.y);
        if (verticalInfluence > 0.3f)
            return false;

        Bounds myBounds = _collider.bounds;
        Bounds targetBounds = targetCollider.bounds;

        float targetY = targetBounds.max.y + myBounds.extents.y;
        Vector3 basePosition = targetModule.transform.position;
        Vector3 alignedPosition = new Vector3(basePosition.x, targetY, basePosition.z);

        transform.SetParent(targetModule.transform, true);
        transform.rotation = targetModule.transform.rotation;

        lockedY = alignedPosition.y;
        MoveTo(alignedPosition);
        ResetVelocities();

        return true;
    }

    bool HasModuleChild(Transform parent)
    {
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child == transform)
                continue;

            if (child.CompareTag("Module"))
                return true;
        }

        return false;
    }

    void RestoreBaseHeight()
    {
        if (!_baseHeightInitialized)
            return;

        lockedY = _baseLockedY;
        Vector3 current = _rigidbody != null ? _rigidbody.position : transform.position;
        current.y = lockedY;
        MoveTo(current);
    }

    void MoveTo(Vector3 position)
    {
        if (_rigidbody != null)
            _rigidbody.position = position;

        transform.position = position;
        _targetPosition = position;
        UpdatePlaneHeight();
    }
}
