using UnityEngine;

/// <summary>
/// Snaps a corridor (Layer "Hall") to module or hall surfaces at either end.
/// Right-click to detach and move freely.
/// </summary>
[RequireComponent(typeof(DragObject))]
public class HallConnector : MonoBehaviour
{
    [Header("Snap Settings")]
    [SerializeField] float snapGap = 0.02f;

    DragObject _drag;
    Rigidbody _rb;
    bool _snapped;
    Transform _snapParent;
    Vector3 _snapLocalPos;
    Quaternion _snapLocalRot;
    float _halfLengthCached;
    bool _lengthCached;
    struct IgnoreInfo { public float until; public Vector3 refPos; }
    System.Collections.Generic.Dictionary<Transform, IgnoreInfo> _ignore = new System.Collections.Generic.Dictionary<Transform, IgnoreInfo>();

    [Header("Reattach Control")]
    [SerializeField] float reattachCooldown = 0.35f;
    [SerializeField] float reattachSeparation = 0.6f;

    [Header("Contamination")]
    [Tooltip("Enable contamination checking when modules attach to hall.")]
    [SerializeField] bool enableContaminationCheck = true;

    int _dirtyAreaLayer;
    int _cleanAreaLayer;
    int _hallLayer;

    void Awake()
    {
        _drag = GetComponent<DragObject>();
        _rb = GetComponent<Rigidbody>();

        // Initialize layer indices for contamination check
        _dirtyAreaLayer = LayerMask.NameToLayer("DirtyArea");
        _cleanAreaLayer = LayerMask.NameToLayer("CleanArea");
        _hallLayer = LayerMask.NameToLayer("Hall");
    }

    void Update()
    {
        if (_snapped && Input.GetMouseButtonDown(1))
        {
            Detach();
        }
        // purge expired ignores
        if (_ignore.Count > 0)
        {
            var toClear = new System.Collections.Generic.List<Transform>();
            float now = Time.time;
            foreach (var kv in _ignore)
            {
                if (now >= kv.Value.until)
                    toClear.Add(kv.Key);
            }
            for (int i = 0; i < toClear.Count; i++) _ignore.Remove(toClear[i]);
        }
    }

    void OnCollisionStay(Collision collision)
    {
        if (_drag == null) return;
        if (collision.contactCount == 0) return;
        var module = ResolveModuleRoot(collision.collider);
        if (module == null) return;
        if (!CanAttach(module)) return;
        var cp = collision.GetContact(0);
        TryAttachModule(module, cp.point);
    }

    void OnTriggerStay(Collider other)
    {
        if (_drag == null) return;
        var module = ResolveModuleRoot(other);
        if (module == null) return;
        if (!CanAttach(module)) return;
        Vector3 p = other.ClosestPoint(transform.position);
        TryAttachModule(module, p);
    }

    void TryAttachModule(Transform module, Vector3 contactPoint)
    {
        if (module == transform) return;
        if (module.parent == transform) return; // already attached
        // compute world bounds and true end centers along forward axis
        Bounds hb = GetWorldBounds();
        Vector3 fwd = transform.forward;
        float halfLen = ProjectHalfLength(hb, fwd);
        Vector3 center = hb.center;

        // choose end closest to contact
        Vector3 endF = center + fwd * halfLen;
        Vector3 endB = center - fwd * halfLen;
        float df = (endF - contactPoint).sqrMagnitude;
        float db = (endB - contactPoint).sqrMagnitude;
        int s = df <= db ? +1 : -1; // +1 = forward end, -1 = back end

        Vector3 outward = (s > 0) ? fwd : -fwd;
        Quaternion rot = Quaternion.LookRotation(outward, Vector3.up);
        Vector3 endCenter = (s > 0) ? endF : endB;

        // compute module half-depth along outward to place flush to hall end
        float modHalfDepth = GetHalfDepthAlong(module, outward);
        Vector3 desiredPos = endCenter + outward * (snapGap + modHalfDepth);

        // Parent module under hall
        module.SetParent(transform, true); // keep world scale/rotation
        // Preserve module rotation to avoid visual squash; only adjust position
        module.position = desiredPos;

        var md = module.GetComponent<DragObject>();
        if (md != null)
        {
            md.SetRequireRightClickToDetach(true);
            md.DropToBaseHeightNow();
        }

        _snapped = true; // we have at least one module attached

        // Check contamination for all modules attached to this hall
        if (enableContaminationCheck)
        {
            CheckHallContamination();
        }
    }

    void Detach()
    {
        if (!_snapped) return;
        _snapped = false;
        _drag.SetRequireRightClickToDetach(false);
        // Detach all child modules and restore their original materials
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var c = transform.GetChild(i);
            if (!c.CompareTag("Module")) continue;
            var md = c.GetComponent<DragObject>();
            
            // Restore original material before detaching
            if (md != null && enableContaminationCheck)
            {
                md.RestoreOriginalMaterial();
            }
            
            Transform container = DragObject.FindNonModuleAncestor(c, true);
            if (container == null) container = transform.parent;
            c.SetParent(container, true);
            if (md != null) md.DropToBaseHeightNow();
            _ignore[c] = new IgnoreInfo { until = Time.time + reattachCooldown, refPos = c.position };
        }
        _snapParent = null;
    }

    // Public API to detach via right-click on any child module
    public void DetachGroup()
    {
        Detach();
    }

    void LateUpdate()
    {
        if (_snapped && _snapParent != null)
        {
            // Reassert exact local pose every frame to avoid any drift or reorder jitter
            transform.localPosition = _snapLocalPos;
            transform.localRotation = _snapLocalRot;
        }
    }

    Transform ResolveSnapParent(Transform target)
    {
        if (target == null) return null;
        int hall = LayerMask.NameToLayer("Hall");
        // If target is a Module, find its module root
        Transform t = target;
        if (!t.CompareTag("Module"))
        {
            var p = t;
            while (p != null && !p.CompareTag("Module")) p = p.parent;
            if (p != null) t = p;
        }
        // If it's not a Module, and it's a Hall, anchor to its DragObject root
        if (!t.CompareTag("Module") && hall >= 0)
        {
            var otherDrag = target.GetComponentInParent<DragObject>();
            if (otherDrag != null) t = otherDrag.transform;
        }
        return t;
    }

    Transform ResolveModuleRoot(Component c)
    {
        if (c == null) return null;
        var t = c.GetComponentInParent<Transform>();
        if (t == null) return null;
        var p = t;
        while (p != null && !p.CompareTag("Module")) p = p.parent;
        return p;
    }

    bool CanAttach(Transform module)
    {
        if (module == null) return false;
        IgnoreInfo info;
        if (_ignore.TryGetValue(module, out info))
        {
            Vector3 d = module.position - info.refPos;
            d.y = 0f;
            bool timeOk = Time.time >= info.until;
            bool distOk = d.sqrMagnitude >= reattachSeparation * reattachSeparation;
            if (!timeOk || !distOk)
                return false; // both conditions must be met
            _ignore.Remove(module);
        }
        return true;
    }

    Bounds GetWorldBounds()
    {
        var rends = GetComponentsInChildren<Renderer>(true);
        Bounds wb = new Bounds(transform.position, Vector3.zero);
        bool init = false;
        foreach (var r in rends)
        {
            if (r == null) continue;
            if (!init) { wb = r.bounds; init = true; }
            else wb.Encapsulate(r.bounds);
        }
        if (!init)
            wb = new Bounds(transform.position, Vector3.one);
        return wb;
    }

    float ProjectHalfLength(Bounds wb, Vector3 dir)
    {
        Vector3 f = dir.normalized;
        Vector3 size = wb.size;
        float len = Mathf.Abs(Vector3.Dot(f, Vector3.right)) * size.x
                  + Mathf.Abs(Vector3.Dot(f, Vector3.up)) * size.y
                  + Mathf.Abs(Vector3.Dot(f, Vector3.forward)) * size.z;
        return Mathf.Max(0.01f, len * 0.5f);
    }

    float GetHalfDepthAlong(Transform module, Vector3 dir)
    {
        if (module == null) return 0.5f;
        var cols = module.GetComponentsInChildren<Collider>(true);
        if (cols == null || cols.Length == 0) return 0.5f;
        Bounds wb = new Bounds(module.position, Vector3.zero);
        bool init = false;
        foreach (var c in cols)
        {
            if (c == null || !c.enabled) continue;
            if (!init) { wb = c.bounds; init = true; }
            else wb.Encapsulate(c.bounds);
        }
        if (!init) return 0.5f;
        Vector3 e = wb.extents;
        Vector3 ad = new Vector3(Mathf.Abs(dir.x), Mathf.Abs(dir.y), Mathf.Abs(dir.z));
        // projection of AABB extents along dir
        float proj = ad.x * e.x + ad.y * e.y + ad.z * e.z;
        return Mathf.Max(0.01f, proj);
    }

    /// <summary>
    /// Check if modules connected to this hall have mixed clean/dirty layers.
    /// If so, contaminate them by applying contaminated material to their siblings.
    /// </summary>
    void CheckHallContamination()
    {
        bool hasDirty = false;
        bool hasClean = false;

        Debug.Log($"[HallConnector] Checking contamination for hall {name}");

        // Check all child modules for their layers
        for (int i = 0; i < transform.childCount; i++)
        {
            var child = transform.GetChild(i);
            if (!child.CompareTag("Module")) continue;

            int childLayer = child.gameObject.layer;
            Debug.Log($"[HallConnector] Child {child.name} layer: {childLayer} (LayerName: {LayerMask.LayerToName(childLayer)})");

            if (childLayer == _dirtyAreaLayer)
                hasDirty = true;
            else if (childLayer == _cleanAreaLayer)
                hasClean = true;
        }

        Debug.Log($"[HallConnector] Hall contamination result: hasDirty={hasDirty}, hasClean={hasClean}");

        // If both dirty and clean modules are present, contaminate all modules
        if (hasDirty && hasClean)
        {
            Debug.Log($"[HallConnector] Contamination detected! Applying contaminated material to all modules.");

            for (int i = 0; i < transform.childCount; i++)
            {
                var child = transform.GetChild(i);
                if (!child.CompareTag("Module")) continue;

                var dragObj = child.GetComponent<DragObject>();
                if (dragObj != null)
                {
                    dragObj.FindAndCacheSibling();
                    dragObj.CheckAndApplyContamination();
                }
            }
        }
        else
        {
            Debug.Log($"[HallConnector] No contamination detected. Modules are clean.");
        }
    }
}
