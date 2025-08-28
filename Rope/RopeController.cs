// ---------------------------------------------
// FILE: RopeController.cs
// ---------------------------------------------
using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(LineRenderer))]
public class RopeController : MonoBehaviour
{
    // -----------------------------
    // Rendering (enum toggle)
    // -----------------------------
    public enum RenderMode { LineRenderer, Mesh3D }

    [Header("Rendering")]
    public RenderMode renderMode = RenderMode.Mesh3D;

    // Expose core 3D rope look from controller
    [Min(0.0001f)] public float ropeRadius = 0.015f;
    [Range(3, 64)] public int radialSegments = 16;
    [Tooltip("UV repeats per meter along rope")] public float uvPerMeter = 1.0f;
    [Tooltip("Flat caps on rope ends")] public bool capEnds = true;
    [Tooltip("Parallel-transport frames to reduce twisting")] public bool useParallelTransport = true;

    // -----------------------------
    // Rope Simulation (Verlet)
    // -----------------------------
    [Header("Rope Simulation")]
    [Min(2)] public int numSegments = 16;
    [Min(0.001f)] public float segmentLength = 0.2f;
    public Vector3 gravity = new Vector3(0, -9.81f, 0);

    [Min(1)] public int solverIterations = 4;
    [Range(1, 8)] public int substeps = 1;

    [Header("Anchors")]
    [Tooltip("If not set, the first point is pinned to this transform")]
    public Transform anchorPoint;
    [Tooltip("Optional: pin the last point to this transform")]
    public Transform endPoint;

    // -----------------------------
    // Multi-attachment support (NEW)
    // -----------------------------
    [Serializable]
    public class RopeAttachment
    {
        [Tooltip("RigidBody to pin to the rope")]
        public Rigidbody body;

        [Tooltip("Local offset on the rigidbody used as the attach point")]
        public Vector3 localOffset = Vector3.zero;

        [Tooltip("Rope point index to pin to (0..numSegments-1). Ignored when Auto Update Index is ON.")]
        public int index = 1;

        [Tooltip("When ON, the index is updated every frame to the nearest rope point to the RB anchor")]
        public bool autoUpdateIndex = true;

        // Debug: last computed world anchor (helpful in Scene view)
        [HideInInspector] public Vector3 lastAnchorWorld;
    }

    [Header("Attachments (up to numSegments)")]
    public List<RopeAttachment> attachments = new List<RopeAttachment>();

    // -----------------------------
    // Tearing-SubSystem Constraint
    // -----------------------------
    [Header("Tearing")]
    public RopeTearConstraint tearing = new RopeTearConstraint
    {
        active = false,              
        tearStretchRatio = 1.6f,
        minOverstretchTime = 0.05f,
        recoverRate = 4.0f
    };
    
    // -----------------------------
    // Interaction / Collisions
    // -----------------------------
    [Header("Interaction")]
    public bool interactive = false;
    public LayerMask interactionLayer;
    [Tooltip("Legacy/simple radius (also used as default for advanced)")]
    [Range(0.01f, 0.3f)] public float interactionRadius = 0.1f;

    public enum CollisionType { Simple, Accurate }
    public CollisionType interactionType = CollisionType.Simple;
    // -------- Advanced collision tuning --------
    [Header("Advanced Collision")]
    [Tooltip("Sphere radius for particle collision (defaults to interactionRadius if <= 0)")]
    public float collisionRadius = -1f;
    [Range(0f, 1f), Tooltip("How bouncy the rope is on impact")]
    public float restitution = 0.05f;
    [Range(0f, 1f), Tooltip("Tangential energy removed on contact (0 = ice, 1 = sticky)")]
    public float friction = 0.6f;
    [Tooltip("Small padding added when resolving contacts")]
    public float contactOffset = 0.001f;
    [Min(1), Tooltip("Collision iterations per substep (stability under stacking)")]
    public int collisionSolverIterations = 2;

    [Header("Continuous Collision Detection")]
    [Tooltip("Use SphereCast to prevent tunneling for each particle")]
    public bool useParticleCCD = true;
    [Tooltip("Use CapsuleCast along rope edges to improve contact on fast motion")]
    public bool useEdgeCCD = true;
    [Range(1, 8), Tooltip("Subdivides long edges for CCD (higher = more robust, slower)")]
    public int edgeCCDSubsteps = 2;

    // --- Performance Profile ---
    [Header("Performance Profile")]
    public RopePerformanceProfile performance = new RopePerformanceProfile();
    
    // -----------------------------
    // Runtime / internals
    // -----------------------------
    private LineRenderer _line;
    private RopeMeshRenderer _meshRenderer;

    private Vector3[] _current;
    private Vector3[] _prev;
    private Collider[] _bp; // broadphase NonAlloc buffer
    private int _bpCount;
    private float _lastDt;

    /// <summary>Exposes rope points for other systems.</summary>
    public Vector3[] RopePoints => _current;

    // Inspector debug (read-only mirror of rope points)
    [Header("Debug View (read-only)")]
    [SerializeField] private Vector3[] inspectorRopePoints;

    private RenderMode _lastAppliedMode = (RenderMode)(-1);

    // -----------------------------
    // Lifecycle
    // -----------------------------
    private void Awake()
    {
        EnsureComponents();
        AllocateAndInitializePoints();
        ApplyRenderModeIfChanged();
        PushRenderSettingsToMesh();
        UpdateVisuals(); // initial
        MirrorPointsToInspector();
        ClampAllAttachmentIndices();
        tearing.Initialize(numSegments);
        _bp = new Collider[Mathf.Max(8, performance.maxBroadphaseColliders)];
    }

    private void OnValidate()
    {
        EnsureComponents();
        ClampArraysToSegmentCount();
        ClampAllAttachmentIndices();
        ApplyRenderModeIfChanged();
        PushRenderSettingsToMesh();
        UpdateVisuals();
        MirrorPointsToInspector();
        if (_bp == null || _bp.Length != Mathf.Max(8, performance.maxBroadphaseColliders))
            _bp = new Collider[Mathf.Max(8, performance.maxBroadphaseColliders)];
    }

    private void Update()
    {
        // Auto-update indices BEFORE sim so constraints pin the correct points this step
        AutoUpdateAttachmentIndices();

        float dt = (substeps > 0) ? Time.deltaTime / substeps : Time.deltaTime;

        _lastDt = dt;


        // Adaptive decisions based on previous frame state
        int effSub = substeps;
        int effIters = solverIterations;
        if (performance.adaptiveSubsteps)
        {
            float vMax = RopePerformanceProfile.RopePerfUtil.ComputeMaxSpeed(_current, _prev, Mathf.Max(1e-6f, dt));
            if (vMax > performance.speedThreshold + performance.speedThresholdHysteresis) effSub = performance.substepsMax;
            else if (vMax < performance.speedThreshold - performance.speedThresholdHysteresis) effSub = performance.substepsMin;
            effSub = Mathf.Clamp(effSub, performance.substepsMin, performance.substepsMax);
        }
        if (performance.adaptiveIterations)
        {
            float avgStretch = RopePerformanceProfile.RopePerfUtil.ComputeAvgStretch(_current, segmentLength);
            effIters = (avgStretch > performance.stretchTolerance) ? performance.iterationsMax : performance.iterationsMin;
            effIters = Mathf.Clamp(effIters, performance.iterationsMin, performance.iterationsMax);
        }
        
        AutoUpdateAttachmentIndices();


        float subDt = dt / Mathf.Max(1, effSub);
        for (int s = 0; s < Mathf.Max(1, effSub); s++)
        {
            if (tearing.active) tearing.Step(_current, segmentLength, subDt);
            
            IntegrateVerlet(subDt);
            
            for (int i = 0;i < Mathf.Max(1, effIters); i++)
                SatisfyConstraints();

            if (interactive)
            {
                // Broadphase cache (once per substep)
                if (performance.useBroadphaseCache)
                {
                    var b = RopePerformanceProfile.RopePerfUtil.ComputeBounds(_current);
                    Vector3 half = b.extents + Vector3.one * (GetCollisionRadius() + performance.broadphasePadding);
                    _bpCount = Physics.OverlapBoxNonAlloc(b.center, half, _bp, Quaternion.identity, interactionLayer, QueryTriggerInteraction.Ignore);
                }
                if (interactionType == CollisionType.Simple)
                    HandleCollisionsSimple();
                else
                    HandleCollisionsAdvanced(dt);
            }
        }

        UpdateVisuals();
        MirrorPointsToInspector();
        if (_meshRenderer != null && tearing.active) _meshRenderer.SetConstraintMask(tearing.GetConnectMask());
        if (_meshRenderer && performance.dynamicRendererLOD)
        {
            float dist = DistanceToCamera(_meshRenderer.transform.position);
            Debug.Log("Distance to Camera is " + dist);
            int targetRingsStride = (dist >= performance.farDistance) ? Mathf.Max(1, performance.ringStrideFar) : 1;
            int targetRadial = (dist >= performance.farDistance) ? performance.radialSegmentsFar : (dist <= performance.nearDistance ? performance.radialSegmentsNear : radialSegments);
            // _meshRenderer.renderRingStride = targetRingsStride;
            // _meshRenderer.radialSegments = targetRadial;
            radialSegments=targetRadial;
            _meshRenderer.rebuildEveryNFrames = Mathf.Max(1, performance.rebuildEveryNFrames);
        }
    
        //tearing.Step(_current, segmentLength, (substeps > 0 ? Time.deltaTime / substeps : Time.deltaTime));
    }

    // -----------------------------
    // Setup / Utilities
    // -----------------------------
    private void EnsureComponents()
    {
        if (!_line)
        {
            _line = GetComponent<LineRenderer>();
            if (_line == null) _line = gameObject.AddComponent<LineRenderer>();
            // sensible defaults
            _line.alignment = LineAlignment.View;
            _line.textureMode = LineTextureMode.Stretch;
            _line.numCornerVertices = 2;
            _line.numCapVertices = 2;
            _line.widthMultiplier = 0.01f;
            _line.receiveShadows = false;
            _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        if (!_meshRenderer)
        {
            _meshRenderer = GetComponent<RopeMeshRenderer>();
            if (_meshRenderer == null) _meshRenderer = gameObject.AddComponent<RopeMeshRenderer>();
        }

        if (!anchorPoint) anchorPoint = transform;

        if (_current == null || _prev == null || _current.Length != numSegments)
        {
            AllocateAndInitializePoints();
        }
    }

    private void AllocateAndInitializePoints()
    {
        _current = new Vector3[numSegments];
        _prev = new Vector3[numSegments];

        Vector3 start = (anchorPoint ? anchorPoint.position : transform.position);
        Vector3 dir = Vector3.down;

        if (endPoint)
        {
            Vector3 end = endPoint.position;
            float totalLen = Vector3.Distance(start, end);
            segmentLength = Mathf.Max(1e-4f, totalLen / (numSegments - 1));
            dir = (end - start).normalized;
        }

        for (int i = 0; i < numSegments; i++)
        {
            _current[i] = start + dir * (segmentLength * i);
            _prev[i] = _current[i];
        }

        if (_line) _line.positionCount = numSegments;
    }

    private void ClampArraysToSegmentCount()
    {
        if (_current == null || _current.Length != numSegments ||
            _prev == null || _prev.Length != numSegments)
        {
            AllocateAndInitializePoints();
        }
        else if (_line && _line.positionCount != numSegments)
        {
            _line.positionCount = numSegments;
        }
    }

    private void ApplyRenderModeIfChanged()
    {
        if (_meshRenderer == null) return;
        if (_lastAppliedMode == renderMode) return;

        _lastAppliedMode = renderMode;
        _meshRenderer.SetMode(renderMode == RenderMode.Mesh3D
            ? RopeMeshRenderer.RenderMode.Mesh3D
            : RopeMeshRenderer.RenderMode.LineRenderer);

        if (_line)
        {
            _line.enabled = (renderMode == RenderMode.LineRenderer);
        }
    }

    private void PushRenderSettingsToMesh()
    {
        if (_meshRenderer == null) return;

        _meshRenderer.radius = ropeRadius;
        _meshRenderer.radialSegments = radialSegments;
        _meshRenderer.uvPerMeter = uvPerMeter;
        _meshRenderer.capEnds = capEnds;
        _meshRenderer.useParallelTransport = useParallelTransport;

        if (_meshRenderer.sourceLine != _line) _meshRenderer.sourceLine = _line;
    }

    private void ClampAllAttachmentIndices()
    {
        if (attachments == null) return;
        for (int i = 0; i < attachments.Count; i++)
        {
            if (attachments[i] == null) continue;
            attachments[i].index = Mathf.Clamp(attachments[i].index, 0, Mathf.Max(0, numSegments - 1));
        }
    }

    // -----------------------------
    // Simulation
    // -----------------------------
    private void IntegrateVerlet(float dt)
    {
        float dt2 = dt * dt;

        for (int i = 0; i < numSegments; i++)
        {
            if (IsPinned(i)) continue;

            Vector3 cur = _current[i];
            Vector3 prev = _prev[i];

            Vector3 vel = cur - prev;
            Vector3 acc = gravity;

            Vector3 next = cur + vel + acc * dt2;

            _prev[i] = cur;
            _current[i] = next;
        }
    }

    private void SatisfyConstraints()
    {
        // Hard pins: anchors
        if (anchorPoint) _current[0] = anchorPoint.position;
        if (endPoint) _current[numSegments - 1] = endPoint.position;

        // Hard pins: attachments (set desired target once per iteration block)
        PinAttachmentPoints();

        // Distance constraints
        for (int i = 0; i < numSegments - 1; i++)
        {
            if (tearing.active && !tearing.IsConnected(i)) continue;
            Vector3 p1 = _current[i];
            Vector3 p2 = _current[i + 1];

            Vector3 delta = p2 - p1;
            float dist = delta.magnitude;
            if (dist < 1e-7f) continue;

            float diff = (dist - segmentLength) / dist;
            Vector3 correction = delta * 0.5f * diff;

            bool p1Pinned = IsPinned(i);
            bool p2Pinned = IsPinned(i + 1);

            if (!p1Pinned && !p2Pinned)
            {
                _current[i] += correction;
                _current[i + 1] -= correction;
            }
            else if (p1Pinned && !p2Pinned)
            {
                _current[i + 1] -= delta * diff;
            }
            else if (!p1Pinned && p2Pinned)
            {
                _current[i] += delta * diff;
            }
        }

        // Re-pin after solving to enforce exact constraints
        if (anchorPoint) { _current[0] = anchorPoint.position; _prev[0] = _current[0]; }
        if (endPoint) { _current[numSegments - 1] = endPoint.position; _prev[numSegments - 1] = _current[numSegments - 1]; }
        if (_meshRenderer != null && tearing.active)
        {
            _meshRenderer.SetConstraintMask(tearing.GetConnectMask()); // see renderer patch below
        }
        PinAttachmentPoints();
    }

    private void PinAttachmentPoints()
    {
        if (attachments == null) return;
        int cap = Mathf.Min(attachments.Count, numSegments); // up to k where k = numSegments

        for (int a = 0; a < cap; a++)
        {
            var att = attachments[a];
            if (att == null || att.body == null) continue;

            Vector3 anchorPos = att.body.transform.TransformPoint(att.localOffset);
            att.lastAnchorWorld = anchorPos;

            int idx = Mathf.Clamp(att.index, 0, Mathf.Max(0, numSegments - 1));
            _current[idx] = anchorPos;
            _prev[idx] = anchorPos;
        }
    }

    private bool IsPinned(int index)
    {
        if (index == 0 && anchorPoint) return true;
        if (index == numSegments - 1 && endPoint) return true;

        if (attachments != null)
        {
            int cap = Mathf.Min(attachments.Count, numSegments);
            for (int a = 0; a < cap; a++)
            {
                var att = attachments[a];
                if (att == null || att.body == null) continue;
                if (att.index == index) return true;
            }
        }
        return false;
    }

    private void AutoUpdateAttachmentIndices()
    {
        if (attachments == null) return;

        int cap = Mathf.Min(attachments.Count, numSegments);
        for (int a = 0; a < cap; a++)
        {
            var att = attachments[a];
            if (att == null || att.body == null) continue;
            if (!att.autoUpdateIndex) continue;

            Vector3 anchorPos = att.body.transform.TransformPoint(att.localOffset);
            att.index = GetClosestSegmentIndex(anchorPos);
        }
    }

    private void HandleCollisionsSimple()
    {
        if (!interactive) return;

        for (int i = 0; i < numSegments; i++)
        {
            if (IsPinned(i)) continue;

            Collider[] hits = Physics.OverlapSphere(_current[i], interactionRadius, interactionLayer);
            foreach (var h in hits)
            {
                if (!h) continue;
                Vector3 closest = h.ClosestPoint(_current[i]);
                Vector3 push = _current[i] - closest;
                float d = push.magnitude;

                if (d > 0f && d < interactionRadius)
                {
                    _current[i] += push.normalized * (interactionRadius - d);
                }
            }
        }
    }

     // -----------------------------
    // AAA-style collision handling
    // -----------------------------
    private void HandleCollisionsAdvanced(float dt)
    {
        float r = (collisionRadius > 0f) ? collisionRadius : interactionRadius;
        if (r <= 0f) return;

        // Multiple passes improve stability under stacking
        int passes = Mathf.Max(1, collisionSolverIterations);

        for (int pass = 0; pass < passes; pass++)
        {
            // 1) Per-particle continuous collision (SphereCast)
            if (useParticleCCD)
            {
                for (int i = 0; i < numSegments; i++)
                {
                    if (IsPinned(i)) continue;

                    Vector3 prev = _prev[i];
                    Vector3 cur  = _current[i];

                    Vector3 v = cur - prev;
                    float dist = v.magnitude;
                    if (dist <= 1e-8f) continue;

                    if (Physics.SphereCast(prev, r, v.normalized, out RaycastHit hit, dist + contactOffset, interactionLayer, QueryTriggerInteraction.Ignore))
                    {
                        // Place on surface + offset
                        Vector3 newPos = hit.point + hit.normal * (r + contactOffset);

                        // Compute velocity at impact & apply bounce/friction
                        Vector3 vel = cur - prev;
                        Vector3 n = hit.normal;
                        Vector3 vn = Vector3.Project(vel, n);
                        Vector3 vt = vel - vn;

                        Vector3 velOut = (-restitution) * vn + (1f - friction) * vt;

                        _current[i] = newPos;
                        _prev[i] = newPos - velOut;
                    }
                }
            }

            // 2) Post-solve overlap depenetration (robust resting contact)
            for (int i = 0; i < numSegments; i++)
            {
                if (IsPinned(i)) continue;

                // Gather overlaps
                Collider[] overlaps = Physics.OverlapSphere(_current[i], r, interactionLayer, QueryTriggerInteraction.Ignore);
                if (overlaps == null || overlaps.Length == 0) continue;

                Vector3 correction = Vector3.zero;
                foreach (var col in overlaps)
                {
                    if (!col) continue;

                    // Push out along the shortest direction using ClosestPoint
                    Vector3 cp = col.ClosestPoint(_current[i]);
                    Vector3 dir = _current[i] - cp;
                    float d = dir.magnitude;
                    if (d <= 1e-6f) continue;

                    float penetration = Mathf.Max(0f, r - d);
                    if (penetration > 0f)
                        correction += dir.normalized * (penetration + contactOffset);
                }

                if (correction.sqrMagnitude > 0f)
                {
                    // Apply correction and damp tangential velocity for friction
                    Vector3 cur = _current[i] + correction;
                    Vector3 vel = cur - _prev[i];

                    // Approximate contact normal as correction direction
                    Vector3 n = correction.normalized;
                    Vector3 vn = Vector3.Project(vel, n);
                    Vector3 vt = vel - vn;
                    Vector3 velOut = (1f - friction) * vt + (1f - restitution) * vn;

                    _current[i] = cur;
                    _prev[i] = cur - velOut;
                }
            }

            // 3) Edge-level CCD (CapsuleCast) — helps when sliding along surfaces
            if (useEdgeCCD)
            {
                int edges = numSegments - 1;
                for (int e = 0; e < edges; e++)
                {
                    // Skip torn edges
                    if (tearing.active && !tearing.IsConnected(e)) continue;

                    int steps = Mathf.Max(1, edgeCCDSubsteps);
                    for (int s = 0; s < steps; s++)
                    {
                        float t0 = (float)s / steps;
                        float t1 = (float)(s + 1) / steps;

                        Vector3 p0a = Vector3.Lerp(_prev[e],     _current[e],     t0);
                        Vector3 p0b = Vector3.Lerp(_prev[e + 1], _current[e + 1], t0);
                        Vector3 p1a = Vector3.Lerp(_prev[e],     _current[e],     t1);
                        Vector3 p1b = Vector3.Lerp(_prev[e + 1], _current[e + 1], t1);

                        Vector3 dir = ((p1a + p1b) * 0.5f) - ((p0a + p0b) * 0.5f);
                        float d = dir.magnitude;
                        if (d <= 1e-8f) continue;

                        if (Physics.CapsuleCast(p0a, p0b, r, dir.normalized, out RaycastHit hit, d + contactOffset, interactionLayer, QueryTriggerInteraction.Ignore))
                        {
                            // Move the pair out along the normal evenly
                            Vector3 n = hit.normal;
                            Vector3 push = n * (contactOffset + 0.001f);

                            _current[e]     += push * 0.5f;
                            _current[e + 1] += push * 0.5f;

                            // Dampen tangential motion for both
                            Vector3 va = _current[e]     - _prev[e];
                            Vector3 vb = _current[e + 1] - _prev[e + 1];

                            Vector3 vna = Vector3.Project(va, n);
                            Vector3 vta = va - vna;
                            Vector3 vnb = Vector3.Project(vb, n);
                            Vector3 vtb = vb - vnb;

                            _prev[e]     = _current[e]     - ((-restitution) * vna + (1f - friction) * vta);
                            _prev[e + 1] = _current[e + 1] - ((-restitution) * vnb + (1f - friction) * vtb);
                        }
                    }
                }
            }

            // Re-enforce pins after collision pass (keeps anchors rigid)
            if (anchorPoint) { _current[0] = anchorPoint.position; _prev[0] = _current[0]; }
            if (endPoint)    { _current[numSegments - 1] = endPoint.position; _prev[numSegments - 1] = _current[numSegments - 1]; }
            PinAttachmentPoints();
        }
    }

    private float GetCollisionRadius() => (collisionRadius > 0f) ? collisionRadius : interactionRadius;
    private float DistanceToCamera(Vector3 world)
    {
        var cam = Camera.main; if (!cam) return 0f; return Vector3.Distance(cam.transform.position, world);
    }

    // -----------------------------
    // Rendering
    // -----------------------------
    private void UpdateVisuals()
    {
        if (_line)
        {
            _line.positionCount = numSegments;
            _line.SetPositions(_current);
        }

        ApplyRenderModeIfChanged();
        PushRenderSettingsToMesh();
        // Mesh rebuild happens in RopeMeshRenderer (LateUpdate).
    }

    // -----------------------------
    // Public helpers
    // -----------------------------
    public void InitializeRope(Vector3 start, Vector3 end)
    {
        if (!anchorPoint)
        {
            anchorPoint = new GameObject("AnchorPoint").transform;
            anchorPoint.SetParent(transform);
        }
        anchorPoint.position = start;

        if (!endPoint)
        {
            endPoint = new GameObject("EndPoint").transform;
            endPoint.SetParent(transform);
        }
        endPoint.position = end;

        float totalLen = Vector3.Distance(start, end);
        segmentLength = Mathf.Max(1e-4f, totalLen / (numSegments - 1));

        AllocateAndInitializePoints();
        UpdateVisuals();
        MirrorPointsToInspector();
    }

    public void ApplyForceToSegment(int index, Vector3 force)
    {
        if (index < 0 || index >= numSegments) return;
        if (IsPinned(index)) return;
        _current[index] += force;
    }

    public int GetClosestSegmentIndex(Vector3 position)
    {
        // Simple: nearest rope point (fast & stable). Good enough for auto-follow.
        int idx = -1;
        float best = float.MaxValue;
        for (int i = 0; i < numSegments; i++)
        {
            float d = Vector3.SqrMagnitude(position - _current[i]); // sqr for speed
            if (d < best) { best = d; idx = i; }
        }
        return Mathf.Clamp(idx, 0, Mathf.Max(0, numSegments - 1));
    }

    public Vector3 GetSegmentPosition(int index)
    {
        if (index < 0 || index >= numSegments) return Vector3.zero;
        return _current[index];
    }

    // -----------------------------
    // Debug / Inspector mirroring
    // -----------------------------
    private void MirrorPointsToInspector()
    {
        if (_current == null) return;
        if (inspectorRopePoints == null || inspectorRopePoints.Length != _current.Length)
            inspectorRopePoints = new Vector3[_current.Length];

        for (int i = 0; i < _current.Length; i++) inspectorRopePoints[i] = _current[i];
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (_current == null || _current.Length == 0) return;

        // Rope points
        Gizmos.color = new Color(0.2f, 0.9f, 0.2f, 1f);
        for (int i = 0; i < _current.Length; i++)
        {
            Gizmos.DrawWireSphere(_current[i], interactionRadius * 0.6f);
        }

        // Attachments
        if (attachments != null)
        {
            Gizmos.color = Color.cyan;
            int cap = Mathf.Min(attachments.Count, numSegments);
            for (int a = 0; a < cap; a++)
            {
                var att = attachments[a];
                if (att == null || att.body == null) continue;
                Vector3 anchor = att.body.transform.TransformPoint(att.localOffset);
                Gizmos.DrawSphere(anchor, interactionRadius * 0.6f);

                int idx = Mathf.Clamp(att.index, 0, Mathf.Max(0, numSegments - 1));
                if (_current != null && _current.Length > idx)
                {
                    Gizmos.DrawLine(_current[idx], anchor);
                }
            }
        }
    }
#endif
}
