using UnityEngine;

/// <summary>
/// Une el hall a módulos usando los colliders de sus extremos, no el pivot.
/// Click derecho para soltar el grupo y mover libremente.
/// </summary>
[RequireComponent(typeof(DragObject))]
public class HallConnector : MonoBehaviour
{
    [Header("Snap Settings")]
    [SerializeField] float snapGap = 0.02f;

    [Header("Ends (Auto detect if empty)")]
    [Tooltip("Collider del extremo frontal (+Z local). Si no se asigna, se detecta automáticamente.")]
    [SerializeField] Collider frontEnd;
    [Tooltip("Collider del extremo trasero (-Z local). Si no se asigna, se detecta automáticamente.")]
    [SerializeField] Collider backEnd;
    [Tooltip("Detectar extremos según el eje Z local. Cambiar si el largo del hall apunta en otro eje.")]
    [SerializeField] Vector3 detectionLocalAxis = Vector3.forward;

    [Header("Reattach Control")]
    [SerializeField] float reattachCooldown = 0.35f;
    [SerializeField] float reattachSeparation = 0.6f;

    DragObject _drag;
    bool _snapped;

    struct IgnoreInfo { public float until; public Vector3 refPos; }
    System.Collections.Generic.Dictionary<Transform, IgnoreInfo> _ignore = new System.Collections.Generic.Dictionary<Transform, IgnoreInfo>();

    // Cache de colliders propios del hall para cálculos y para ignorar colisiones con módulos acoplados
    Collider[] _ownCollidersSnapshot;           // Incluye triggers (p.e. extremos)
    Collider[] _ownSolidCollidersSnapshot;      // Solo no-triggers (cuerpo del hall)

    struct ColliderPair { public Collider a; public Collider b; }
    System.Collections.Generic.Dictionary<Transform, System.Collections.Generic.List<ColliderPair>> _ignoredCollisionPairs
        = new System.Collections.Generic.Dictionary<Transform, System.Collections.Generic.List<ColliderPair>>();
    System.Collections.Generic.Dictionary<Transform, System.Collections.Generic.List<ColliderPair>> _ignoredHallPairs
        = new System.Collections.Generic.Dictionary<Transform, System.Collections.Generic.List<ColliderPair>>();

    // Ocupación y propiedad
    bool _frontBusy;
    bool _backBusy;
    System.Collections.Generic.Dictionary<Transform, bool> _attachedModules = new System.Collections.Generic.Dictionary<Transform, bool>(); // module -> true(front)
    static System.Collections.Generic.Dictionary<Transform, HallConnector> s_ownerByModule = new System.Collections.Generic.Dictionary<Transform, HallConnector>();

    [Header("Contamination")]
    [Tooltip("Enable contamination checking when modules attach to hall.")]
    [SerializeField] bool enableContaminationCheck = true;

    int _dirtyAreaLayer;
    int _cleanAreaLayer;
    int _hallLayer;

    void Awake()
    {
        _drag = GetComponent<DragObject>();
        AutoDetectEndsIfNeeded();
        CacheOwnCollidersSnapshot();
        // Initialize layer indices for contamination check
        _dirtyAreaLayer = LayerMask.NameToLayer("DirtyArea");
        _cleanAreaLayer = LayerMask.NameToLayer("CleanArea");
        _hallLayer = LayerMask.NameToLayer("Hall");
    }

    void OnValidate()
    {
#if UNITY_EDITOR
        // Evitar tocar SerializedObjects mientras el Inspector/SceneView se están cerrando
        if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode)
            return;
        UnityEditor.EditorApplication.delayCall += () =>
        {
            if (this == null) return;
            if (!isActiveAndEnabled) return;
            AutoDetectEndsIfNeeded();
            CacheOwnCollidersSnapshot();
        };
#else
        // En ejecución, no hacer nada (Awake ya lo hace)
#endif
    }

    void Update()
    {
        if (_snapped && Input.GetMouseButtonDown(1))
        {
            Detach();
        }

        // Limpia bloqueos de reatachado caducados
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

        // Solo aceptamos colisiones provenientes de los colliders de extremo
        var cp = collision.GetContact(0);
        Collider our = cp.thisCollider != null ? cp.thisCollider : null;
        
        // Determine which end this collision is closest to
        Vector3 contactPoint = cp.point;
        bool isFrontContact = false;
        bool isBackContact = false;
        
        if (frontEnd != null && backEnd != null)
        {
            float distToFront = (contactPoint - frontEnd.bounds.center).sqrMagnitude;
            float distToBack = (contactPoint - backEnd.bounds.center).sqrMagnitude;
            
            // Assign to the closest end
            if (distToFront < distToBack)
            {
                isFrontContact = true;
            }
            else
            {
                isBackContact = true;
            }
        }
        else if (frontEnd != null)
        {
            isFrontContact = true;
        }
        else if (backEnd != null)
        {
            isBackContact = true;
        }
        else
        {
            Debug.LogWarning($"[HallConnector] No end colliders configured!");
            return;
        }
        
        Debug.Log($"[HallConnector] Collision from {module.name}: isFrontContact={isFrontContact}, isBackContact={isBackContact}, _frontBusy={_frontBusy}, _backBusy={_backBusy}");
        
        if (isFrontContact && _frontBusy)
        {
            Debug.Log($"[HallConnector] Front end is busy, ignoring collision from {module.name}");
            return;
        }
        if (isBackContact && _backBusy)
        {
            Debug.Log($"[HallConnector] Back end is busy, ignoring collision from {module.name}");
            return;
        }
        
        Vector3 refPoint = cp.point;
        
        // Pass which end detected the collision
        Collider preferredEnd = isFrontContact ? frontEnd : backEnd;
        TryAttachModule(module, refPoint, preferredEnd);
    }

    void OnTriggerStay(Collider other)
    {
        if (_drag == null) return;
        var module = ResolveModuleRoot(other);
        if (module == null) return;
        if (!CanAttach(module)) return;
        
        Vector3 p = other.ClosestPoint(transform.position);
        
        // Determine which end is closest
        Collider preferredEnd = null;
        if (frontEnd != null && backEnd != null)
        {
            float distToFront = (p - frontEnd.bounds.center).sqrMagnitude;
            float distToBack = (p - backEnd.bounds.center).sqrMagnitude;
            preferredEnd = (distToFront < distToBack) ? frontEnd : backEnd;
            
            // Check if preferred end is busy
            if (preferredEnd == frontEnd && _frontBusy) return;
            if (preferredEnd == backEnd && _backBusy) return;
        }
        else if (frontEnd != null)
        {
            if (_frontBusy) return;
            preferredEnd = frontEnd;
        }
        else if (backEnd != null)
        {
            if (_backBusy) return;
            preferredEnd = backEnd;
        }
        
        TryAttachModule(module, p, preferredEnd);
    }

    void TryAttachModule(Transform module, Vector3 refPoint, Collider ourColliderHint)
    {
        if (module == null) return;
        if (module == transform) return;
        if (module.parent == transform) return; // ya es hijo

        if (frontEnd == null || backEnd == null)
            AutoDetectEndsIfNeeded();

        // Selecciona extremo preferido
        Collider endCol = null;
        Vector3 outward = Vector3.forward;
        bool picked = false;
        
        Debug.Log($"[HallConnector] TryAttachModule: module={module.name}, ourColliderHint={ourColliderHint?.name}");
        
        // If we have a hint about which end collided, use that directly
        if (ourColliderHint == frontEnd || ourColliderHint == backEnd)
        {
            endCol = ourColliderHint;
            Vector3 fwd = transform.TransformDirection(detectionLocalAxis.sqrMagnitude > 0.0001f ? detectionLocalAxis.normalized : Vector3.forward);
            outward = (ourColliderHint == frontEnd) ? fwd.normalized : (-fwd).normalized;
            picked = true;
            Debug.Log($"[HallConnector] Using collision hint: endCol={endCol?.name}, isFront={ourColliderHint == frontEnd}");
        }
        
        if (!picked && ourColliderHint == null)
        {
            // 1) Si hay solape real con un extremo, usa ese
            picked = SelectEndByOverlap(module, out endCol, out outward);
            Debug.Log($"[HallConnector] SelectEndByOverlap: picked={picked}, endCol={endCol?.name}");
            // 2) Si no, decide por la posición del módulo proyectada en el eje local
            if (!picked)
            {
                picked = SelectEndByAxis(module, out endCol, out outward);
                Debug.Log($"[HallConnector] SelectEndByAxis: picked={picked}, endCol={endCol?.name}");
            }
        }
        // 3) Fallback: decide por distancia al punto de referencia
        if (!picked)
        {
            SelectBestEnd(refPoint, ourColliderHint, out endCol, out outward);
            Debug.Log($"[HallConnector] SelectBestEnd: endCol={endCol?.name}");
        }
        
        if (endCol == null)
        {
            Debug.LogWarning($"[HallConnector] Could not select end collider for module {module.name}");
            return;
        }
        
        bool selectedFront = (endCol == frontEnd);
        Debug.Log($"[HallConnector] Selected end: {(selectedFront ? "FRONT" : "BACK")}, _frontBusy={_frontBusy}, _backBusy={_backBusy}");
        
        // Check if selected end is already busy
        if (selectedFront && _frontBusy)
        {
            Debug.Log($"[HallConnector] Selected FRONT end is already busy, aborting attach for {module.name}");
            return;
        }
        if (!selectedFront && _backBusy)
        {
            Debug.Log($"[HallConnector] Selected BACK end is already busy, aborting attach for {module.name}");
            return;
        }

        // Punto del plano exterior del extremo del hall (proyectado en XZ)
        Vector3 outwardPlanar = new Vector3(outward.x, 0f, outward.z);
        if (outwardPlanar.sqrMagnitude < 0.0001f) outwardPlanar = outward.normalized;
        else outwardPlanar.Normalize();
        Vector3 endPlane = ComputeHallEndPlane(outwardPlanar, endCol);

        // Profundidad del módulo hacia fuera
        float modHalfDepth = GetUnionHalfDepthAlong(module, outward);

        // Mueve este pasillo para alinear su extremo con el plano del módulo (no mover el módulo)
        Bounds mb = GetWorldBoundsNonTrigger(module);
        Vector3 modulePlane = mb.center - outwardPlanar * ProjectHalfDepth(mb, outwardPlanar);
        Vector3 targetPlane = modulePlane - outwardPlanar * snapGap;
        Vector3 hallEndPlane = endPlane;
        Vector3 delta = targetPlane - hallEndPlane;
        // Solo corrige en el plano XZ para no variar altura
        delta.y = 0f;
        transform.position += delta;

        // Reestructurar jerarquía si el módulo ya está colgado de otro pasillo
        var previousHall = GetNearestHallAncestor(module);
        if (previousHall != null && previousHall != transform)
        {
            // El nuevo pasillo pasa a ser hijo del pasillo previo
            transform.SetParent(previousHall, true);
            // Evitar colisiones entre halls de la misma cadena para no generar vibración
            IgnoreCollisionsWithHall(previousHall, true);
        }

        // Establecer el módulo como hijo de este pasillo (mantener posición mundial)
        module.SetParent(transform, true);
        // Ocupa extremo y registra propietario
        bool isFront = (endCol == frontEnd);
        if (isFront) _frontBusy = true; else _backBusy = true;
        _attachedModules[module] = isFront;
        s_ownerByModule[module] = this;
        
        Debug.Log($"[HallConnector] Module {module.name} attached to {(isFront ? "FRONT" : "BACK")} end. _frontBusy={_frontBusy}, _backBusy={_backBusy}");

        var md = module.GetComponent<DragObject>();
        var hd = _drag;
        if (md != null)
        {
            md.StopDrag();
            md.SetRequireRightClickToDetach(true);
            // No tocar su altura aquí para no hundirlo
        }

        _snapped = true;

        // Si el acople ocurre mientras el usuario mantiene click izquierdo, transfiere el arrastre
        // al pasillo raíz (el primero de la cadena) para mover todo el conjunto
        if (Input.GetMouseButton(0))
        {
            if (hd != null) hd.StopDrag();
            var rootHall = GetTopHallAncestor(transform);
            var rootDrag = rootHall != null ? rootHall.GetComponent<DragObject>() : _drag;
            if (rootDrag != null)
                rootDrag.BeginDragFromCursor();
        }

        // Ignorar colisiones entre el hall y el módulo acoplado para evitar vibraciones
        IgnoreCollisionsWithModule(module, true);
        
        // Chequear contaminación tras acoplar (opcional)
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
        // Desvincula todos los hijos tipo Módulo y restaura materiales si aplica
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
            if (md != null)
            {
                // Permitir que el módulo vuelva a moverse individualmente
                md.SetRequireRightClickToDetach(false);
                md.DropToBaseHeightNow();
            }
            _ignore[c] = new IgnoreInfo { until = Time.time + reattachCooldown, refPos = c.position };

            // Restablecer colisiones ignoradas
            IgnoreCollisionsWithModule(c, false);

            // Liberar propietario y ocupación
            if (s_ownerByModule.ContainsKey(c)) s_ownerByModule.Remove(c);
            bool wasFront;
            if (_attachedModules.TryGetValue(c, out wasFront))
            {
                if (wasFront) _frontBusy = false; else _backBusy = false;
                _attachedModules.Remove(c);
            }
        }
    }

    // Public: usado por DragObject al hacer click derecho en un hijo
    public void DetachGroup()
    {
        Detach();
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
        // Si ya tiene un hall "dueño" que no es el hall ancestro directo, no competir
        HallConnector currentOwner;
        if (s_ownerByModule.TryGetValue(module, out currentOwner) && currentOwner != null && currentOwner != this)
        {
            var nearest = GetNearestHallAncestor(module);
            if (nearest != currentOwner)
                return false;
        }
        // Nota: permitimos que otro pasillo reasigne el dueño del módulo;
        // la ocupación por extremo y la reasignación de jerarquía evita vibraciones.
        IgnoreInfo info;
        if (_ignore.TryGetValue(module, out info))
        {
            Vector3 d = module.position - info.refPos;
            d.y = 0f;
            bool timeOk = Time.time >= info.until;
            bool distOk = d.sqrMagnitude >= reattachSeparation * reattachSeparation;
            if (!timeOk || !distOk)
                return false; // ambas condiciones obligatorias
            _ignore.Remove(module);
        }
        return true;
    }

    void AutoDetectEndsIfNeeded()
    {
        // Si ambos están asignados, respeta la configuración manual
        if (frontEnd != null && backEnd != null) return;

        var all = GetComponentsInChildren<Collider>(true);
        if (all == null || all.Length == 0) return;

        // Preferir triggers (suelen ser los extremos); si no hay, usar todos
        System.Collections.Generic.List<Collider> candidates = new System.Collections.Generic.List<Collider>();
        for (int i = 0; i < all.Length; i++)
        {
            var c = all[i];
            if (c == null || !c.enabled) continue;
            // No considerar colliders de módulos que ya estén como hijos
            if (c.GetComponentInParent<HallConnector>() != this) continue;
            candidates.Add(c);
        }
        if (candidates.Count < 2) return;

        // Si hay triggers, filtra a triggers
        int triggerCount = 0;
        for (int i = 0; i < candidates.Count; i++) if (candidates[i].isTrigger) triggerCount++;
        if (triggerCount >= 2)
        {
            candidates.RemoveAll(c => !c.isTrigger);
        }

        // Ordena por posición proyectada en el eje local de detección
        Vector3 axis = detectionLocalAxis.sqrMagnitude > 0.0001f ? detectionLocalAxis.normalized : Vector3.forward;
        float bestMin = float.PositiveInfinity, bestMax = float.NegativeInfinity;
        Collider minC = null, maxC = null;
        foreach (var c in candidates)
        {
            Vector3 local = transform.InverseTransformPoint(c.bounds.center);
            float p = Vector3.Dot(local, axis);
            if (p < bestMin) { bestMin = p; minC = c; }
            if (p > bestMax) { bestMax = p; maxC = c; }
        }

        // Asegura que sean distintos
        if (minC != null && maxC != null && minC != maxC)
        {
            backEnd = minC;
            frontEnd = maxC;
            // Ajusta el eje de detección según la disposición real de extremos
            Vector3 lb = transform.InverseTransformPoint(backEnd.bounds.center);
            Vector3 lf = transform.InverseTransformPoint(frontEnd.bounds.center);
            Vector3 axisDelta = (lf - lb);
            if (axisDelta.sqrMagnitude > 0.0001f)
                detectionLocalAxis = axisDelta.normalized;
        }
    }

    void SelectBestEnd(Vector3 refPoint, Collider ourColliderHint, out Collider endCollider, out Vector3 outward)
    {
        // Por defecto: decidir por proximidad al punto de referencia
        Collider a = frontEnd, b = backEnd;
        Vector3 fwd = transform.TransformDirection(detectionLocalAxis.sqrMagnitude > 0.0001f ? detectionLocalAxis.normalized : Vector3.forward);
        if (ourColliderHint != null)
        {
            // Si el contacto proviene de uno de nuestros colliders, respétalo
            if (IsSameOrChild(ourColliderHint, a)) { endCollider = a; outward = fwd.normalized; return; }
            if (IsSameOrChild(ourColliderHint, b)) { endCollider = b; outward = (-fwd).normalized; return; }
        }

        // Decide por distancia al centro del collider
        float da = float.PositiveInfinity, db = float.PositiveInfinity;
        if (a != null)
        {
            Vector3 pa = a.ClosestPoint(refPoint);
            da = (pa - refPoint).sqrMagnitude;
        }
        if (b != null)
        {
            Vector3 pb = b.ClosestPoint(refPoint);
            db = (pb - refPoint).sqrMagnitude;
        }

        bool useA = (da <= db);
        endCollider = useA ? a : b;
        outward = useA ? fwd.normalized : (-fwd).normalized;
    }

    static bool IsSameOrChild(Collider c, Collider target)
    {
        if (c == null || target == null) return false;
        if (c == target) return true;
        return c.transform.IsChildOf(target.transform) || target.transform.IsChildOf(c.transform);
    }

    bool SelectEndByOverlap(Transform module, out Collider endCollider, out Vector3 outward)
    {
        endCollider = null;
        outward = Vector3.zero;
        if (module == null) return false;
        Collider a = frontEnd, b = backEnd;
        if (a == null && b == null) return false;

        var moduleCols = module.GetComponentsInChildren<Collider>(true);
        if (moduleCols == null || moduleCols.Length == 0) return false;

        Vector3 fwd = transform.TransformDirection(detectionLocalAxis.sqrMagnitude > 0.0001f ? detectionLocalAxis.normalized : Vector3.forward);
        float penA = 0f, penB = 0f;
        Vector3 dir; float dist;
        for (int i = 0; i < moduleCols.Length; i++)
        {
            var mc = moduleCols[i];
            if (mc == null || !mc.enabled) continue;
            if (a != null && Physics.ComputePenetration(a, a.transform.position, a.transform.rotation, mc, mc.transform.position, mc.transform.rotation, out dir, out dist))
                penA += dist;
            if (b != null && Physics.ComputePenetration(b, b.transform.position, b.transform.rotation, mc, mc.transform.position, mc.transform.rotation, out dir, out dist))
                penB += dist;
        }
        if (penA <= 0f && penB <= 0f)
            return false;

        bool useA = penA >= penB;
        endCollider = useA ? a : b;
        outward = useA ? fwd.normalized : (-fwd).normalized;
        return true;
    }

    bool SelectEndByAxis(Transform module, out Collider endCollider, out Vector3 outward)
    {
        endCollider = null;
        outward = Vector3.zero;
        if (module == null) return false;
        if (frontEnd == null && backEnd == null) return false;

        // Centro del módulo (colliders sólidos) en local del hall
        Bounds mb = GetWorldBoundsNonTrigger(module);
        Vector3 localCenter = transform.InverseTransformPoint(mb.center);

        // Eje local de detección (normalizado)
        Vector3 localAxis = detectionLocalAxis.sqrMagnitude > 0.0001f ? detectionLocalAxis.normalized : Vector3.forward;
        float s = Vector3.Dot(localCenter, localAxis);

        // Mapear a extremo y dirección hacia fuera (en mundo)
        Vector3 worldAxis = transform.TransformDirection(localAxis);
        bool useFront = s >= 0f; // centro a favor del eje => frente
        endCollider = useFront ? frontEnd : backEnd;
        outward = useFront ? worldAxis.normalized : (-worldAxis).normalized;

        // Si el extremo seleccionado no existe, intenta el otro
        if (endCollider == null)
        {
            endCollider = useFront ? backEnd : frontEnd;
            outward = -outward;
        }
        return endCollider != null;
    }

    static float ProjectHalfDepth(Bounds wb, Vector3 dir)
    {
        Vector3 f = dir.normalized;
        Vector3 e = wb.extents;
        Vector3 ad = new Vector3(Mathf.Abs(f.x), Mathf.Abs(f.y), Mathf.Abs(f.z));
        float proj = ad.x * e.x + ad.y * e.y + ad.z * e.z;
        return Mathf.Max(0.0001f, proj);
    }

    static float GetUnionHalfDepthAlong(Transform root, Vector3 dir)
    {
        if (root == null) return 0.5f;
        var cols = root.GetComponentsInChildren<Collider>(true);
        if (cols == null || cols.Length == 0) return 0.5f;
        Bounds wb = new Bounds(root.position, Vector3.zero);
        bool init = false;
        foreach (var c in cols)
        {
            if (c == null || !c.enabled || c.isTrigger) continue;
            if (!init) { wb = c.bounds; init = true; }
            else wb.Encapsulate(c.bounds);
        }
        if (!init) return 0.5f;
        return ProjectHalfDepth(wb, dir);
    }

    static Bounds GetWorldBoundsNonTrigger(Transform root)
    {
        var cols = root != null ? root.GetComponentsInChildren<Collider>(true) : null;
        if (cols == null || cols.Length == 0)
            return new Bounds(root != null ? root.position : Vector3.zero, Vector3.zero);
        bool init = false;
        Bounds wb = new Bounds(root.position, Vector3.zero);
        for (int i = 0; i < cols.Length; i++)
        {
            var c = cols[i];
            if (c == null || !c.enabled || c.isTrigger) continue;
            if (!init) { wb = c.bounds; init = true; }
            else wb.Encapsulate(c.bounds);
        }
        if (!init)
            wb = new Bounds(root.position, Vector3.zero);
        return wb;
    }

    static Transform GetNearestModuleAncestor(Transform t)
    {
        var p = t;
        while (p != null && !p.CompareTag("Module")) p = p.parent;
        return p;
    }

    static Transform GetTopModuleAncestor(Transform t)
    {
        if (t == null) return null;
        Transform m = null;
        var p = t;
        while (p != null)
        {
            if (p.CompareTag("Module")) m = p;
            p = p.parent;
        }
        return m;
    }

    static Transform GetNearestHallAncestor(Transform t)
    {
        var p = t != null ? t.parent : null;
        while (p != null)
        {
            if (p.GetComponent<HallConnector>() != null) return p;
            p = p.parent;
        }
        return null;
    }

    static Transform GetTopHallAncestor(Transform t)
    {
        Transform h = null;
        var p = t;
        while (p != null)
        {
            if (p.GetComponent<HallConnector>() != null) h = p;
            p = p.parent;
        }
        return h;
    }

    void CacheOwnCollidersSnapshot()
    {
        var all = GetComponentsInChildren<Collider>(true);
        if (all == null) { _ownCollidersSnapshot = new Collider[0]; _ownSolidCollidersSnapshot = new Collider[0]; return; }
        var tmp = new System.Collections.Generic.List<Collider>(all.Length);
        var tmpSolid = new System.Collections.Generic.List<Collider>(all.Length);
        for (int i = 0; i < all.Length; i++)
        {
            var c = all[i];
            if (c == null || !c.enabled) continue;
            // Snapshot inicial del prefab del hall (antes de acoples); válido para ignorar futuras colisiones
            tmp.Add(c);
            if (!c.isTrigger) tmpSolid.Add(c);
        }
        _ownCollidersSnapshot = tmp.ToArray();
        _ownSolidCollidersSnapshot = tmpSolid.ToArray();
    }

    Vector3 ComputeHallEndPlane(Vector3 outward, Collider fallbackEnd)
    {
        // Usa colliders sólidos del cuerpo; si hay varios, elige el más extremo hacia 'outward'
        // y cercano al centro del collider de extremo para evitar coger el lado opuesto.
        if (_ownSolidCollidersSnapshot != null && _ownSolidCollidersSnapshot.Length > 0)
        {
            Vector3 outN = outward.normalized;
            float bestProj = float.NegativeInfinity;
            for (int i = 0; i < _ownSolidCollidersSnapshot.Length; i++)
            {
                var c = _ownSolidCollidersSnapshot[i];
                var b = c.bounds;
                Vector3 planePt = b.center + outN * ProjectHalfDepth(b, outN);
                float proj = Vector3.Dot(planePt, outN);
                if (proj > bestProj) bestProj = proj;
            }

            Vector3 refCenter = fallbackEnd != null ? fallbackEnd.bounds.center : transform.position;
            float bestDist = float.PositiveInfinity;
            Vector3 bestPt = refCenter;
            for (int i = 0; i < _ownSolidCollidersSnapshot.Length; i++)
            {
                var c = _ownSolidCollidersSnapshot[i];
                var b = c.bounds;
                Vector3 planePt = b.center + outN * ProjectHalfDepth(b, outN);
                float proj = Vector3.Dot(planePt, outN);
                if (bestProj - proj <= 0.001f)
                {
                    float d = (planePt - refCenter).sqrMagnitude;
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestPt = planePt;
                    }
                }
            }
            return bestPt;
        }
        // Fallback al propio collider de extremo (por si el cuerpo no tiene colliders sólidos)
        var eb = fallbackEnd.bounds;
        return eb.center + outward.normalized * ProjectHalfDepth(eb, outward);
    }

    void IgnoreCollisionsWithModule(Transform module, bool ignore)
    {
        if (module == null) return;
        if (_ownCollidersSnapshot == null || _ownCollidersSnapshot.Length == 0) return;
        var moduleCols = module.GetComponentsInChildren<Collider>(true);
        if (moduleCols == null || moduleCols.Length == 0) return;

        System.Collections.Generic.List<ColliderPair> pairs;
        if (ignore)
        {
            if (!_ignoredCollisionPairs.TryGetValue(module, out pairs))
            {
                pairs = new System.Collections.Generic.List<ColliderPair>(moduleCols.Length * _ownCollidersSnapshot.Length);
                _ignoredCollisionPairs[module] = pairs;
            }
        }
        else
        {
            if (!_ignoredCollisionPairs.TryGetValue(module, out pairs)) return;
        }

        for (int i = 0; i < _ownCollidersSnapshot.Length; i++)
        {
            var hc = _ownCollidersSnapshot[i];
            if (hc == null) continue;
            for (int j = 0; j < moduleCols.Length; j++)
            {
                var mc = moduleCols[j];
                if (mc == null) continue;
                if (ignore)
                {
                    Physics.IgnoreCollision(hc, mc, true);
                    pairs.Add(new ColliderPair { a = hc, b = mc });
                }
                else
                {
                    Physics.IgnoreCollision(hc, mc, false);
                }
            }
        }

        if (!ignore)
        {
            _ignoredCollisionPairs.Remove(module);
        }
    }

    void IgnoreCollisionsWithHall(Transform otherHall, bool ignore)
    {
        if (otherHall == null) return;
        if (_ownCollidersSnapshot == null || _ownCollidersSnapshot.Length == 0) return;
        var otherCols = otherHall.GetComponentsInChildren<Collider>(true);
        if (otherCols == null || otherCols.Length == 0) return;

        System.Collections.Generic.List<ColliderPair> pairs;
        if (ignore)
        {
            if (!_ignoredHallPairs.TryGetValue(otherHall, out pairs))
            {
                pairs = new System.Collections.Generic.List<ColliderPair>(otherCols.Length * _ownCollidersSnapshot.Length);
                _ignoredHallPairs[otherHall] = pairs;
            }
        }
        else
        {
            if (!_ignoredHallPairs.TryGetValue(otherHall, out pairs)) return;
        }

        for (int i = 0; i < _ownCollidersSnapshot.Length; i++)
        {
            var hc = _ownCollidersSnapshot[i];
            if (hc == null) continue;
            for (int j = 0; j < otherCols.Length; j++)
            {
                var oc = otherCols[j];
                if (oc == null) continue;
                if (ignore)
                {
                    Physics.IgnoreCollision(hc, oc, true);
                    pairs.Add(new ColliderPair { a = hc, b = oc });
                }
                else
                {
                    Physics.IgnoreCollision(hc, oc, false);
                }
            }
        }

        if (!ignore)
        {
            _ignoredHallPairs.Remove(otherHall);
        }
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
