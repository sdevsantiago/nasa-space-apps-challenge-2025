using System.Collections.Generic;
using System.Text.RegularExpressions;
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

    [Header("Drag Mode")]
    [Tooltip("When enabled, the object follows the mouse directly using MovePosition for responsive feel.")]
    [SerializeField] bool directFollow = true;
    [Tooltip("Optional smoothing for direct follow (0 = instant). Higher values are snappier.")]
    [SerializeField] float followSharpness = 0f;

    [Header("Tolerance")]
    [SerializeField] float stopEpsilon = 0.0004f;

    [Header("Stacking")]
    [SerializeField] float restackCooldown = 0.6f;
    [SerializeField] float restackSeparation = 1.2f;
    [SerializeField] float maxVerticalNormalForStack = 0.6f;

    [Header("Visual Swap (Optional)")]
    [Tooltip("Swap all Renderer materials when fused to visually represent double module.")]
    [SerializeField] bool swapMaterialsOnFuse = true;
    [SerializeField] Transform visualRoot; // if null, uses this.transform
    [SerializeField] Material fusedMaterial; // material to apply when fused (double)
    [Tooltip("Prefab to instantiate when fused (double model). If assigned, it is preferred over material swap.")]
    [SerializeField] GameObject doubleModelPrefab;
    [SerializeField] Transform doubleVisualParent; // if null, uses this.transform

    Vector3 _dragOffset;
    Plane _dragPlane;
    Rigidbody _rigidbody;
    Collider _collider;
    bool _isDragging;
    Vector3 _targetPosition;
    float _baseLockedY;
    bool _baseHeightInitialized;
    Transform _ignoredStackParent;
    readonly HashSet<Collider> _ignoredParentColliders = new HashSet<Collider>();
    bool _pendingRestoreBaseHeight;
    float _ignoredParentCooldown;
    Transform _defaultParent;
    bool _colliderWasTrigger;
    bool _initialDetectCollisions;
    float _stackedLocalY;
    bool _suppressNextMouseDown;
    Transform _fusedChild; // disabled child while fused
    List<Renderer> _renderers;
    List<Material[]> _originalMaterials;
    bool _visualsCached;
    float _lastClickTime;
    GameObject _doubleInstance;

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
        _colliderWasTrigger = _collider != null && _collider.isTrigger;
        _initialDetectCollisions = _rigidbody != null && _rigidbody.detectCollisions;
        CacheOriginalState();
        ConfigureRigidbody();
        SetIdleMode(true);

        if (visualRoot == null) visualRoot = transform;
        if (doubleVisualParent == null) doubleVisualParent = transform;
    }

    void OnValidate()
    {
        if (restackCooldown < 0f)
            restackCooldown = 0f;

        if (restackSeparation < 0f)
            restackSeparation = 0f;

        if (maxVerticalNormalForStack < 0f)
            maxVerticalNormalForStack = 0f;
        if (maxVerticalNormalForStack > 1f)
            maxVerticalNormalForStack = 1f;
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

        if (_defaultParent == null)
            _defaultParent = FindNonModuleAncestor(transform.parent);
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
        try
        {
            // Double-click to defuse: if this module has a fused (disabled) child, split and drag child
            float now = Time.time;
            bool isDoubleClick = now - _lastClickTime <= 0.3f;
            _lastClickTime = now;

            if (_fusedChild != null && isDoubleClick)
            {
                Debug.Log($"[DragObject] Double-click split on {name}. Detaching child {_fusedChild.name}");
                DefuseAndStartChildDrag();
                return;
            }

            if (_suppressNextMouseDown)
            {
                Debug.Log($"[DragObject] Suppressed mouse down on {name}");
                _suppressNextMouseDown = false;
                return;
            }
            if (!ValidateCamera() || _rigidbody == null)
            {
                Debug.LogWarning($"[DragObject] Missing camera or rigidbody on {name}");
                return;
            }

            Transform previousParent = transform.parent;
            if (previousParent != null && !previousParent.CompareTag("Module"))
            {
                _defaultParent = previousParent;
            }

            if (previousParent != null && previousParent.CompareTag("Module"))
            {
                _ignoredStackParent = previousParent;
                _ignoredParentColliders.Clear();
                _pendingRestoreBaseHeight = true;
                _ignoredParentCooldown = restackCooldown;
                ApplyStackState(false);

                Transform restoreParent = _defaultParent;
                if (restoreParent == null)
                {
                    restoreParent = FindNonModuleAncestor(previousParent.parent);
                    if (restoreParent != null)
                        _defaultParent = restoreParent;
                }
                else if (restoreParent.CompareTag("Module"))
                {
                    restoreParent = FindNonModuleAncestor(restoreParent.parent);
                    if (restoreParent != null)
                        _defaultParent = restoreParent;
                }

                Debug.Log($"[DragObject] Detaching {name} from parent {previousParent.name} -> container {(restoreParent? restoreParent.name: "<none>")}");
                transform.SetParent(restoreParent, true);
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
                Debug.Log($"[DragObject] Begin drag {name}. LockedY={lockedY} Target={_targetPosition}");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[DragObject] Exception in OnMouseDown on {name}: {ex}");
        }
    }

    void OnMouseDrag()
    {
        try
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
        catch (System.Exception ex)
        {
            Debug.LogError($"[DragObject] Exception in OnMouseDrag on {name}: {ex}");
        }
    }

    void OnMouseUp()
    {
        try
        {
            if (!_isDragging)
                return;

            _isDragging = false;
            SetIdleMode(true);
            Debug.Log($"[DragObject] End drag {name}");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[DragObject] Exception in OnMouseUp on {name}: {ex}");
        }
    }

    void FixedUpdate()
    {
        try
        {
            if (!_isDragging || _rigidbody == null)
                return;

            Vector3 current = _rigidbody.position;
            Vector3 planarTarget = new Vector3(_targetPosition.x, lockedY, _targetPosition.z);

            if (directFollow)
            {
                Vector3 next = planarTarget;
                if (followSharpness > 0f)
                {
                    float a = 1f - Mathf.Exp(-followSharpness * Time.fixedDeltaTime);
                    next = Vector3.Lerp(new Vector3(current.x, lockedY, current.z), planarTarget, a);
                }
                _rigidbody.MovePosition(next);
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
                return;
            }

            // Physics spring (legacy mode)
            Vector3 planarCurrent = new Vector3(current.x, lockedY, current.z);
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
        catch (System.Exception ex)
        {
            Debug.LogError($"[DragObject] Exception in FixedUpdate on {name}: {ex}");
        }
    }

    void OnCollisionStay(Collision collision)
    {
        try
        {
            if (!_isDragging || _rigidbody == null)
                return;

            if (collision.contactCount == 0)
                return;

            TrackIgnoredParentContact(collision);

            ContactPoint contact = collision.GetContact(0);
            DragObject targetModule = collision.collider.GetComponentInParent<DragObject>();
            if (targetModule == null)
                return;

            if (!_pendingRestoreBaseHeight && targetModule != null && targetModule != this && !targetModule.transform.IsChildOf(transform))
            {
                Debug.Log($"[DragObject] Collision with module {targetModule.name} from {name}. Trying stack...");
                if (!ShouldIgnoreStacking(targetModule.transform) && TryStackOnModule(contact, collision.collider, targetModule))
                {
                    _isDragging = false;
                    SetIdleMode(true);
                    ApplyStackState(true);
                    Debug.Log($"[DragObject] Stacked {name} on {targetModule.name} via collision");
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
        catch (System.Exception ex)
        {
            Debug.LogError($"[DragObject] Exception in OnCollisionStay on {name}: {ex}");
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
        try
        {
            if (_collider == null || targetCollider == null)
            {
                Debug.LogWarning($"[DragObject] TryStackOnModule aborted on {name}: missing colliders");
                return false;
            }

            if (targetModule.transform == transform || targetModule.transform.IsChildOf(transform))
            {
                Debug.Log($"[DragObject] TryStackOnModule rejected: same/child transform {name} -> {targetModule.name}");
                return false;
            }

            Transform baseModule = GetModuleBase(targetModule.transform);
            if (baseModule != targetModule.transform)
            {
                Debug.Log($"[DragObject] TryStackOnModule rejected: {targetModule.name} is not base (base {baseModule?.name})");
                return false; // no apilar sobre un módulo que ya es hijo de otro
            }

            if (HasModuleChild(baseModule))
            {
                Debug.Log($"[DragObject] TryStackOnModule rejected: base {baseModule.name} already has a child");
                return false;
            }

            float verticalInfluence = Mathf.Abs(contact.normal.y);
            if (verticalInfluence > maxVerticalNormalForStack)
            {
                Debug.Log($"[DragObject] TryStackOnModule rejected: normalY={verticalInfluence} > {maxVerticalNormalForStack}");
                return false;
            }

            // Only same base type modules can stack
            if (!AreSameModuleType(transform, baseModule))
            {
                Debug.Log($"[DragObject] TryStackOnModule rejected: type mismatch {name} vs {baseModule.name}");
                return false;
            }

            Transform previousParent = transform.parent;
            if (previousParent != null && !previousParent.CompareTag("Module"))
                _defaultParent = previousParent;

            float targetTopY = ComputeTopY(baseModule);
            float myBottomOffset = ComputeBottomOffsetFromPosition(transform);
            float newY = targetTopY + myBottomOffset;

            Vector3 basePosition = baseModule.position;
            Vector3 alignedPosition = new Vector3(basePosition.x, newY, basePosition.z);

            transform.SetParent(baseModule, true);
            transform.rotation = baseModule.rotation;

            lockedY = alignedPosition.y;
            MoveTo(alignedPosition);
            ResetVelocities();

            Vector3 local = transform.localPosition;
            local.x = 0f;
            local.z = 0f;
            transform.localPosition = local;
            transform.localRotation = Quaternion.identity;
            _stackedLocalY = transform.localPosition.y;

            ClearIgnoredParentState();

            // Mark fusion on base: disable this as child and swap base visuals
            var baseDrag = baseModule.GetComponent<DragObject>();
            if (baseDrag != null)
                baseDrag.OnChildFused(transform);

            Debug.Log($"[DragObject] TryStackOnModule OK: {name} parent={baseModule.name} y={alignedPosition.y}");
            return true;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[DragObject] Exception in TryStackOnModule on {name}: {ex}");
            return false;
        }
    }

    bool TryStackOnModuleTrigger(Collider targetCollider, DragObject targetModule)
    {
        try
        {
            if (_collider == null || targetCollider == null)
            {
                Debug.LogWarning($"[DragObject] TryStackOnModuleTrigger aborted on {name}: missing colliders");
                return false;
            }

            if (targetModule.transform == transform || targetModule.transform.IsChildOf(transform))
            {
                Debug.Log($"[DragObject] TryStackOnModuleTrigger rejected: same/child transform {name} -> {targetModule.name}");
                return false;
            }

            Transform baseModule = GetModuleBase(targetModule.transform);
            if (baseModule != targetModule.transform)
            {
                Debug.Log($"[DragObject] TryStackOnModuleTrigger rejected: {targetModule.name} is not base (base {baseModule?.name})");
                return false;
            }

            if (HasModuleChild(baseModule))
            {
                Debug.Log($"[DragObject] TryStackOnModuleTrigger rejected: base {baseModule.name} already has a child");
                return false;
            }

            // Only same base type modules can stack
            if (!AreSameModuleType(transform, baseModule))
            {
                Debug.Log($"[DragObject] TryStackOnModuleTrigger rejected: type mismatch {name} vs {baseModule.name}");
                return false;
            }

            Transform previousParent = transform.parent;
            if (previousParent != null && !previousParent.CompareTag("Module"))
                _defaultParent = previousParent;

            float targetTopY = ComputeTopY(baseModule);
            float myBottomOffset = ComputeBottomOffsetFromPosition(transform);
            float newY = targetTopY + myBottomOffset;

            Vector3 basePosition = baseModule.position;
            Vector3 alignedPosition = new Vector3(basePosition.x, newY, basePosition.z);

            transform.SetParent(baseModule, true);
            transform.rotation = baseModule.rotation;

            lockedY = alignedPosition.y;
            MoveTo(alignedPosition);
            ResetVelocities();

            Vector3 local = transform.localPosition;
            local.x = 0f;
            local.z = 0f;
            transform.localPosition = local;
            transform.localRotation = Quaternion.identity;
            _stackedLocalY = transform.localPosition.y;

            ClearIgnoredParentState();

            var baseDrag = baseModule.GetComponent<DragObject>();
            if (baseDrag != null)
                baseDrag.OnChildFused(transform);

            Debug.Log($"[DragObject] TryStackOnModuleTrigger OK: {name} parent={baseModule.name} y={alignedPosition.y}");
            return true;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[DragObject] Exception in TryStackOnModuleTrigger on {name}: {ex}");
            return false;
        }
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

    Transform GetModuleBase(Transform t)
    {
        if (t == null)
            return null;
        while (t.parent != null && t.parent.CompareTag("Module"))
            t = t.parent;
        return t;
    }

    bool ShouldIgnoreStacking(Transform candidate)
    {
        if (_ignoredStackParent == null || candidate != _ignoredStackParent)
            return false;

        if (_pendingRestoreBaseHeight)
            return true;

        if (_ignoredParentCooldown > 0f)
            return true;

        if (!HasClearedIgnoredParentDistance())
            return true;

        ClearIgnoredParentState();
        return false;
    }

    Transform FindNonModuleAncestor(Transform candidate)
    {
        while (candidate != null && candidate.CompareTag("Module"))
            candidate = candidate.parent;

        return candidate;
    }
    public static Transform FindNonModuleAncestor(Transform start, bool includeSelf = false)
    {
        Transform candidate = includeSelf ? start : start?.parent;
        while (candidate != null && candidate.CompareTag("Module"))
            candidate = candidate.parent;
        return candidate;
    }

    void ApplyStackState(bool stacked)
    {
        if (_collider != null)
            _collider.isTrigger = _colliderWasTrigger; // keep original so clicks still hit

        if (_rigidbody != null)
            _rigidbody.detectCollisions = stacked ? true : _initialDetectCollisions;

        ApplyFusedVisual(stacked);
    }

    void RestoreBaseHeight()
    {
        if (!_baseHeightInitialized)
            return;

        lockedY = _baseLockedY;
        Vector3 current = _rigidbody != null ? _rigidbody.position : transform.position;
        current.y = lockedY;
        MoveTo(current);

        _pendingRestoreBaseHeight = false;
        _ignoredParentColliders.Clear();
    }

    void MoveTo(Vector3 position)
    {
        if (_rigidbody != null)
            _rigidbody.position = position;

        transform.position = position;
        _targetPosition = position;
        UpdatePlaneHeight();
    }

    void TrackIgnoredParentContact(Collision collision)
    {
        if (_ignoredStackParent == null || !_pendingRestoreBaseHeight)
            return;

        Transform other = collision.collider.transform;
        if (other == _ignoredStackParent || other.IsChildOf(_ignoredStackParent))
        {
            if (!_ignoredParentColliders.Contains(collision.collider))
                _ignoredParentColliders.Add(collision.collider);
        }
    }

    void Update()
    {
        if (_ignoredStackParent != null && !_pendingRestoreBaseHeight)
        {
            if (_ignoredParentCooldown > 0f)
                _ignoredParentCooldown -= Time.deltaTime;

            if (_ignoredParentCooldown <= 0f && HasClearedIgnoredParentDistance())
                ClearIgnoredParentState();
        }

        // Fallback click: ensure stacked child is clickable even if occluded
        if (!_isDragging && transform.parent != null && transform.parent.CompareTag("Module") && _collider != null)
        {
            if (Input.GetMouseButtonDown(0) && ValidateCamera())
            {
                var ray = dragCamera.ScreenPointToRay(Input.mousePosition);
                var hits = Physics.RaycastAll(ray, 1000f, ~0, QueryTriggerInteraction.Collide);
                for (int i = 0; i < hits.Length; i++)
                {
                    if (hits[i].collider == _collider)
                    {
                        OnMouseDown();
                        break;
                    }
                }
            }
        }
    }

    void TrackIgnoredParentTrigger(Collider other)
    {
        if (_ignoredStackParent == null || !_pendingRestoreBaseHeight)
            return;

        Transform t = other.transform;
        if (t == _ignoredStackParent || t.IsChildOf(_ignoredStackParent))
        {
            if (!_ignoredParentColliders.Contains(other))
                _ignoredParentColliders.Add(other);
        }
    }

    void OnCollisionExit(Collision collision)
    {
        if (_ignoredStackParent == null || !_pendingRestoreBaseHeight)
            return;

        Transform other = collision.collider.transform;
        if (other != _ignoredStackParent && !other.IsChildOf(_ignoredStackParent))
            return;

        _ignoredParentColliders.Remove(collision.collider);

        if (_ignoredParentColliders.Count == 0)
            RestoreBaseHeight();
    }

    void OnTriggerStay(Collider other)
    {
        try
        {
            if (!_isDragging || _rigidbody == null)
                return;

            DragObject targetModule = other.GetComponentInParent<DragObject>();
            if (targetModule == null)
                return;

            // Track trigger contacts with the previous parent while leaving its volume
            if (_ignoredStackParent != null && _pendingRestoreBaseHeight)
            {
                TrackIgnoredParentTrigger(other);
                return;
            }

            Debug.Log($"[DragObject] Trigger with module {targetModule.name} from {name}. Trying stack (trigger)...");
            if (!ShouldIgnoreStacking(targetModule.transform) && TryStackOnModuleTrigger(other, targetModule))
            {
                _isDragging = false;
                SetIdleMode(true);
                ApplyStackState(true);
                Debug.Log($"[DragObject] Stacked {name} on {targetModule.name} via trigger");
                return;
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[DragObject] Exception in OnTriggerStay on {name}: {ex}");
        }
    }

    void OnTriggerExit(Collider other)
    {
        if (_ignoredStackParent == null || !_pendingRestoreBaseHeight)
            return;

        Transform t = other.transform;
        if (t != _ignoredStackParent && !t.IsChildOf(_ignoredStackParent))
            return;

        _ignoredParentColliders.Remove(other);
        if (_ignoredParentColliders.Count == 0)
            RestoreBaseHeight();
    }

    void LateUpdate()
    {
        Transform p = transform.parent;
        if (p != null && p.CompareTag("Module"))
        {
            // Hard lock to parent so there's no lag.
            Vector3 local = transform.localPosition;
            local.x = 0f;
            local.z = 0f;
            local.y = _stackedLocalY;
            transform.localPosition = local;
            transform.localRotation = Quaternion.identity;
        }
    }

    bool HasClearedIgnoredParentDistance()
    {
        if (_ignoredStackParent == null)
            return true;

        if (restackSeparation <= 0f)
            return true;

        Vector3 offset = transform.position - _ignoredStackParent.position;
        offset.y = 0f;
        float minSeparation = restackSeparation;
        return offset.sqrMagnitude >= minSeparation * minSeparation;
    }

    void ClearIgnoredParentState()
    {
        _ignoredStackParent = null;
        _ignoredParentColliders.Clear();
        _pendingRestoreBaseHeight = false;
        _ignoredParentCooldown = 0f;
    }

    // Public helpers for ModuleStackVisuals
    public void SuppressNextMouseDown()
    {
        _suppressNextMouseDown = true;
    }

    public void BeginDragFromCursor()
    {
        OnMouseDown();
    }

    public void StopDrag()
    {
        if (_isDragging)
        {
            _isDragging = false;
            SetIdleMode(true);
        }
    }

    public void PrepareRestoreAfterDetaching(Transform previousParent)
    {
        _ignoredStackParent = previousParent;
        _ignoredParentColliders.Clear();
        _pendingRestoreBaseHeight = true;
        _ignoredParentCooldown = restackCooldown;
        ApplyStackState(false);
    }

    // FUSION/VISUAL SWAP HELPERS
    void OnChildFused(Transform child)
    {
        _fusedChild = child;
        if (_fusedChild != null && _fusedChild.gameObject.activeSelf)
            _fusedChild.gameObject.SetActive(false);
        if (swapMaterialsOnFuse)
            ApplyFusedVisual(true);
    }

    void DefuseAndStartChildDrag()
    {
        if (_fusedChild == null)
            return;

        // Restore visuals
        ApplyFusedVisual(false);

        // Re-enable and detach child
        var child = _fusedChild;
        _fusedChild = null;
        child.gameObject.SetActive(true);
        Transform container = FindNonModuleAncestor(transform.parent);
        if (container == null) container = transform.parent; // fallback
        child.SetParent(container, true);

        // Pass drag to child
        var childDrag = child.GetComponent<DragObject>();
        if (childDrag != null)
        {
            SuppressNextMouseDown();
            childDrag.PrepareRestoreAfterDetaching(transform);
            childDrag.BeginDragFromCursor();
        }
    }

    void EnsureVisualsCache()
    {
        if (_visualsCached)
            return;
        if (visualRoot == null) visualRoot = transform;
        _renderers = new List<Renderer>(visualRoot.GetComponentsInChildren<Renderer>(true));
        _originalMaterials = new List<Material[]>(_renderers.Count);
        for (int i = 0; i < _renderers.Count; i++)
            _originalMaterials.Add(_renderers[i].sharedMaterials);
        _visualsCached = true;
    }

    void ApplyFusedVisual(bool fused)
    {
        // Prefer prefab-based swap if assigned
        if (doubleModelPrefab != null)
        {
            if (fused)
            {
                // Hide single visuals
                SetSingleVisualsEnabled(false);
                // Create/show double instance
                if (_doubleInstance == null)
                {
                    try
                    {
                        _doubleInstance = Instantiate(doubleModelPrefab);
                        _doubleInstance.name = doubleModelPrefab.name + "(inst)";
                        _doubleInstance.transform.SetParent(doubleVisualParent != null ? doubleVisualParent : transform, false);
                        _doubleInstance.transform.localPosition = Vector3.zero;
                        _doubleInstance.transform.localRotation = Quaternion.identity;
                        _doubleInstance.transform.localScale = Vector3.one;
                        // Disable colliders on the visual instance to avoid physics interference
                        foreach (var c in _doubleInstance.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError($"[DragObject] Failed to instantiate double model on {name}: {ex}");
                    }
                }
                else
                {
                    _doubleInstance.SetActive(true);
                }
            }
            else
            {
                // Remove/hide double and show single
                if (_doubleInstance != null)
                {
                    Destroy(_doubleInstance);
                    _doubleInstance = null;
                }
                SetSingleVisualsEnabled(true);
            }
            return;
        }

        // Fallback to material swap
        if (swapMaterialsOnFuse)
        {
            EnsureVisualsCache();
            if (fused)
            {
                if (fusedMaterial == null)
                {
                    Debug.LogWarning($"[DragObject] No fusedMaterial assigned on {name}; visual swap skipped.");
                    return;
                }
                for (int i = 0; i < _renderers.Count; i++)
                {
                    var r = _renderers[i];
                    int len = r.sharedMaterials != null ? r.sharedMaterials.Length : 1;
                    var mats = new Material[len];
                    for (int m = 0; m < len; m++) mats[m] = fusedMaterial;
                    r.sharedMaterials = mats;
                }
            }
            else
            {
                if (!_visualsCached) return;
                for (int i = 0; i < _renderers.Count; i++)
                {
                    _renderers[i].sharedMaterials = _originalMaterials[i];
                }
            }
        }
    }

    void SetSingleVisualsEnabled(bool enabled)
    {
        EnsureVisualsCache();
        for (int i = 0; i < _renderers.Count; i++)
        {
            var r = _renderers[i];
            if (r == null) continue;
            r.enabled = enabled;
        }
    }

    // TYPE CHECK (by name, tolerant to different GO names)
    static bool AreSameModuleType(Transform a, Transform b)
    {
        string ta = DetectModuleTypeName(a);
        string tb = DetectModuleTypeName(b);
        if (string.IsNullOrEmpty(ta) && string.IsNullOrEmpty(tb)) return true; // unknowns allowed
        if (string.IsNullOrEmpty(ta) || string.IsNullOrEmpty(tb)) return false;
        return ta == tb;
    }

    static string DetectModuleTypeName(Transform root)
    {
        if (root == null) return string.Empty;
        // search in hierarchy names
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            var n = t.name.ToLowerInvariant();
            if (n.Contains("icosphere")) return "icosphere";
            if (n.Contains("cylinder")) return "cylinder";
        }
        // fallback to root name
        var rname = root.name.ToLowerInvariant();
        if (rname.Contains("icosphere")) return "icosphere";
        if (rname.Contains("cylinder")) return "cylinder";
        return string.Empty;
    }

    float ComputeTopY(Transform root)
    {
        var colliders = root.GetComponentsInChildren<Collider>();
        bool found = false;
        float top = float.NegativeInfinity;
        for (int i = 0; i < colliders.Length; i++)
        {
            var c = colliders[i];
            if (c == null || !c.enabled || c.isTrigger)
                continue;
            var b = c.bounds;
            if (!found || b.max.y > top)
            {
                top = b.max.y;
                found = true;
            }
        }
        if (!found)
            return root.position.y;
        return top;
    }

    float ComputeBottomOffsetFromPosition(Transform root)
    {
        var colliders = root.GetComponentsInChildren<Collider>();
        bool found = false;
        float bottom = float.PositiveInfinity;
        for (int i = 0; i < colliders.Length; i++)
        {
            var c = colliders[i];
            if (c == null || !c.enabled || c.isTrigger)
                continue;
            var b = c.bounds;
            if (!found || b.min.y < bottom)
            {
                bottom = b.min.y;
                found = true;
            }
        }
        if (!found)
            return 0f;
        return root.position.y - bottom;
    }
}
