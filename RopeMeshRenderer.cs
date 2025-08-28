// ---------------------------------------------
// RopeMeshRenderer.cs  (visibility & space fixes)
// ---------------------------------------------
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
[ExecuteAlways]
[RequireComponent(typeof(LineRenderer))]
[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(UnityEngine.MeshRenderer))]
public class RopeMeshRenderer : MonoBehaviour
{
    public enum RenderMode
    {
        LineRenderer,
        Mesh3D
    }

    [Header("Mode")] public RenderMode renderMode = RenderMode.Mesh3D;

    [Header("Faces")] [Tooltip("Force reverse face winding (useful if rope looks inside-out).")]
    public bool flipFaces = false;

  //  [Tooltip("Automatically flip based on transform sign (handles negative parent scales).")]
   // public bool autoFixInsideOut = true;

    [Header("Mesh (3D mode)")] [Min(0.0001f)]
    public float radius = 0.015f;

    [Range(3, 64)] public int radialSegments = 16;

    [Tooltip("UV repeats per meter along rope")]
    public float uvPerMeter = 1.0f;

    [Tooltip("Flat caps on ends")] public bool capEnds = true;

    [Tooltip("Parallel-transport frames (reduces twisting)")]
    public bool useParallelTransport = true;
    
    [Header("Tear Visuals")]
    [Tooltip("Add flat caps where a tear happens")]
    public bool capAtBreaks = true;
    
    [Header("Source")] [Tooltip("If not set, uses the LineRenderer on the same GameObject for points.")]
    public LineRenderer sourceLine;

    [Header("Material")] [Tooltip("If null, a default Standard/URP Lit material is created.")]
    public Material ropeMaterial;

    [Header("Debug")] [Tooltip("Draw with Graphics.DrawMeshNow to bypass some culling/material issues.")]
    public bool alwaysDrawDebug = false;

    [Tooltip("Extra padding (meters) added to bounds to avoid over-culling.")]
    public float boundsPadding = 0.1f;

    [Header("Performance/LOD")]
    [Tooltip("Sample every Nth ring when building the mesh (1 = all rings)")] public int renderRingStride = 1;
    [Tooltip("Rebuild mesh every N frames (1 = every frame)")] 
    public int rebuildEveryNFrames = 1;
    
    private MeshFilter _mf;
    private UnityEngine.MeshRenderer _mr;
    private LineRenderer _lr;
    private Mesh _mesh;
    private bool[] _connectMask; // length = rings-1; true = connect, false = torn
    private readonly List<Vector3> _centersWorld = new List<Vector3>(256);
    private readonly List<Vector3> _centersLocal = new List<Vector3>(256);
    private int _frameCounter = 0;
    void Reset()
    {
        EnsureComponents();
        if (_lr != null)
        {
            _lr.alignment = LineAlignment.View;
            _lr.textureMode = LineTextureMode.Stretch;
            _lr.numCornerVertices = 4;
            _lr.numCapVertices = 4;
            _lr.widthMultiplier = 0.01f;
        }

        renderMode = RenderMode.Mesh3D;
    }

    void OnEnable()
    {
        EnsureComponents();
        ApplyMode();
        RebuildMesh();
    }

    void OnValidate()
    {
        EnsureComponents();
        ApplyMode();
        RebuildMesh();
    }

    void LateUpdate()
    {
        if (renderMode == RenderMode.Mesh3D)
            RebuildMesh();
    }

    public void SetMode(RenderMode mode)
    {
        renderMode = mode;
        ApplyMode();
        RebuildMesh();
    }

    private void ApplyMode()
    {
        EnsureComponents();

        bool meshMode = (renderMode == RenderMode.Mesh3D);
        if (_mr)
        {
            _mr.enabled = meshMode;
            _mr.forceRenderingOff = false; // <- ensure not forced off
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            _mr.receiveShadows = true;
        }

        if (_lr) _lr.enabled = !meshMode; // we still read its points when hidden
    }

    private void EnsureComponents()
    {
        if (!_mf) _mf = GetComponent<MeshFilter>();
        if (!_mr) _mr = GetComponent<UnityEngine.MeshRenderer>();
        if (!_lr) _lr = GetComponent<LineRenderer>();
        if (!sourceLine) sourceLine = _lr;

        if (_mesh == null)
        {
            _mesh = new Mesh { name = "RopeMesh (procedural)" };
            _mesh.MarkDynamic();
            // Use sharedMesh for edit-mode persistence without instance duplication
            _mf.sharedMesh = _mesh;
        }

        if (ropeMaterial == null)
        {
            // Prefer URP Lit if present, fall back to Standard
            Shader sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) sh = Shader.Find("Standard");
            ropeMaterial = new Material(sh) { name = "Default Rope Material" };
            ropeMaterial.enableInstancing = true;

            // Ensure fully opaque by default
            if (sh != null && sh.name.Contains("Universal"))
            {
#if UNITY_EDITOR
                // URP Lit uses _Surface keyword for Opaque/Transparent
                ropeMaterial.SetFloat("_Surface", 0f); // 0 = Opaque
#endif
            }
        }

        if (_mr && _mr.sharedMaterial == null) _mr.sharedMaterial = ropeMaterial;

        // Safety: if any parent scales to 0, push to 1 to avoid invisibility
        var ls = transform.localScale;
        if (Mathf.Approximately(ls.x, 0f) || Mathf.Approximately(ls.y, 0f) || Mathf.Approximately(ls.z, 0f))
            transform.localScale = Vector3.one;
    }

    // ---------- Public: feed world-space points (optional) ----------
    public void SetPoints(IList<Vector3> worldPoints)
    {
        if (renderMode != RenderMode.Mesh3D) return;
        if (worldPoints == null || worldPoints.Count < 2)
        {
            ClearMesh();
            return;
        }

        _centersLocal.Clear();
        for (int i = 0; i < worldPoints.Count; i++)
            _centersLocal.Add(transform.InverseTransformPoint(worldPoints[i]));

        BuildAndAssign(_centersLocal);
    }
    
    /// <summary>Provide an edge-connection mask (true=connect faces between ring i and i+1)</summary>
    public void SetConstraintMask(bool[] connectMask)
    {
        _connectMask = connectMask;
    }

    // ---------- Core Rebuild ----------
    private void RebuildMesh()
    {
        if (renderMode != RenderMode.Mesh3D) return;

        if (!sourceLine)
        {
            ClearMesh();
            return;
        }

        int count = sourceLine.positionCount;
        if (count < 2)
        {
            ClearMesh();
            return;
        }

        _centersWorld.Clear();
        for (int i = 0; i < count; i++) _centersWorld.Add(sourceLine.GetPosition(i));

        // Convert to local space
        _centersLocal.Clear();
        if (sourceLine.useWorldSpace)
        {
            for (int i = 0; i < _centersWorld.Count; i++)
                _centersLocal.Add(transform.InverseTransformPoint(_centersWorld[i]));
        }
        else
        {
            _centersLocal.AddRange(_centersWorld); // already local to this transform
        }

        BuildAndAssign(_centersLocal);
    }

    private void BuildAndAssign(List<Vector3> centersLocal)
    {
        if (centersLocal == null || centersLocal.Count < 2)
        {
            ClearMesh();
            return;
        }

        bool mirrored = transform.lossyScale.x * transform.lossyScale.y * transform.lossyScale.z < 0f;
        bool flipWinding = (/*autoFixInsideOut && */mirrored) ^ flipFaces;
        // BuildTube(centersLocal, radius, radialSegments, uvPerMeter, capEnds, useParallelTransport, _mesh);

        BuildTube(_centersLocal, radius, radialSegments, uvPerMeter, capEnds, useParallelTransport, flipWinding, _mesh);
        // Bounds inflate to avoid thin-rope culling at distance
        var b = _mesh.bounds;
        b.Expand(boundsPadding * 2f);
        _mesh.bounds = b;

        // As a last-resort debug path: draw even if renderer/material is odd
        if (alwaysDrawDebug)
        {
            if (ropeMaterial) ropeMaterial.SetPass(0);
            Graphics.DrawMeshNow(_mesh, transform.localToWorldMatrix);
        }
    }

    private void ClearMesh()
    {
        if (_mesh != null) _mesh.Clear();
    }

    // ---------- Tube Builder (unchanged core) ----------
    private static readonly List<Vector3> s_vertices = new List<Vector3>(8192);
    private static readonly List<Vector3> s_normals = new List<Vector3>(8192);
    private static readonly List<Vector2> s_uvs = new List<Vector2>(8192);
    private static readonly List<int> s_tris = new List<int>(12288);

    private void BuildTube(List<Vector3> centers, float radius, int radialSegments, float uvPerMeter,
        bool capEnds, bool usePT, bool flipWinding, Mesh mesh)
    {
        if (centers == null || centers.Count < 2)
        {
            mesh.Clear();
            return;
        }

        int rings = centers.Count;
        int vertsPerRing = Mathf.Max(3, radialSegments);

        s_vertices.Clear();
        s_normals.Clear();
        s_uvs.Clear();
        s_tris.Clear();

        // Tangents
        Vector3[] tangents = new Vector3[rings];
        for (int i = 0; i < rings; i++)
        {
            if (i == 0) tangents[i] = (centers[1] - centers[0]).normalized;
            else if (i == rings - 1) tangents[i] = (centers[i] - centers[i - 1]).normalized;
            else
            {
                Vector3 t1 = (centers[i] - centers[i - 1]).normalized;
                Vector3 t2 = (centers[i + 1] - centers[i]).normalized;
                tangents[i] = (t1 + t2).normalized;
                if (tangents[i].sqrMagnitude < 1e-6f) tangents[i] = t2;
            }
        }

        // Initial frame
        Vector3 refN = Vector3.up;
        if (Mathf.Abs(Vector3.Dot(refN, tangents[0])) > 0.95f) refN = Vector3.right;
        Vector3 prevT = tangents[0];
        Vector3 n = Vector3.ProjectOnPlane(refN, prevT).normalized;
        if (n.sqrMagnitude < 1e-6f) n = Vector3.Cross(prevT, Vector3.right).normalized;
        Vector3 b = Vector3.Cross(prevT, n).normalized;

        float vAccum = 0f;
        for (int i = 0; i < rings; i++)
        {
            Vector3 t = tangents[i];

            if (usePT && i > 0)
            {
                float dot = Mathf.Clamp(Vector3.Dot(prevT, t), -1f, 1f);
                float ang = Mathf.Acos(dot);
                if (ang > 1e-5f)
                {
                    Vector3 axis = Vector3.Normalize(Vector3.Cross(prevT, t));
                    Quaternion q = Quaternion.AngleAxis(ang * Mathf.Rad2Deg, axis);
                    n = (q * n).normalized;
                    b = Vector3.Cross(t, n).normalized;
                }
            }
            else if (!usePT)
            {
                n = Vector3.ProjectOnPlane(n, t).normalized;
                if (n.sqrMagnitude < 1e-6f) n = Vector3.ProjectOnPlane(Vector3.up, t).normalized;
                b = Vector3.Cross(t, n).normalized;
            }

            if (i > 0) vAccum += (centers[i] - centers[i - 1]).magnitude * uvPerMeter;

            float step = Mathf.PI * 2f / vertsPerRing;
            for (int j = 0; j < vertsPerRing; j++)
            {
                float a = j * step;
                Vector3 offset = n * Mathf.Cos(a) * radius + b * Mathf.Sin(a) * radius;
                Vector3 pos = centers[i] + offset;
                s_vertices.Add(pos);
                s_normals.Add(offset.normalized);
                s_uvs.Add(new Vector2((float)j / vertsPerRing, vAccum));
            }

            prevT = t;
        }

        // sides
        for (int i = 0; i < rings - 1; i++)
        {
            // If a mask is provided and this edge is torn, skip connecting faces
            bool connect = (_connectMask == null || i < 0 || i >= _connectMask.Length) ? true : _connectMask[i];
            if (!connect)
            {
                // cap both exposed rings at the break to make ends closed
                if (capAtBreaks)
                {
                    // Cap ring i (end cap facing prev tangent)
                    int ringStart = i * vertsPerRing;
                    int centerIdx = s_vertices.Count;
                    s_vertices.Add(centers[i]);
                    s_normals.Add(-tangents[i]);
                    s_uvs.Add(new Vector2(0.5f, 0.5f));
                    for (int j = 0; j < vertsPerRing; j++)
                    {
                        int b0 = ringStart + ((j + 1) % vertsPerRing);
                        int c0 = ringStart + j;
                        s_tris.Add(centerIdx); s_tris.Add(b0); s_tris.Add(c0);
                    }

                    // Cap ring i+1 (end cap facing next tangent)
                    ringStart = (i + 1) * vertsPerRing;
                    centerIdx = s_vertices.Count;
                    s_vertices.Add(centers[i + 1]);
                    s_normals.Add(tangents[i + 1]);
                    s_uvs.Add(new Vector2(0.5f, 0.5f));
                    for (int j = 0; j < vertsPerRing; j++)
                    {
                        int b2 = ringStart + j;
                        int c2 = ringStart + ((j + 1) % vertsPerRing);
                        s_tris.Add(centerIdx); s_tris.Add(b2); s_tris.Add(c2);
                    }
                }
                continue;
            }
            
            int i0 = i * vertsPerRing;
            int i1 = (i + 1) * vertsPerRing;
            for (int j = 0; j < vertsPerRing; j++)
            {
                int j0 = j;
                int j1 = (j + 1) % vertsPerRing;

                int a = i0 + j0;
                int bidx = i1 + j0;
                int c = i1 + j1;
                int d = i0 + j1;

                if (!flipWinding)
                {
                    s_tris.Add(a);
                    s_tris.Add(bidx);
                    s_tris.Add(c);
                    s_tris.Add(a);
                    s_tris.Add(c);
                    s_tris.Add(d);
                }
                else
                {
                    s_tris.Add(a);
                    s_tris.Add(c);
                    s_tris.Add(bidx);
                    s_tris.Add(a);
                    s_tris.Add(d);
                    s_tris.Add(c);
                }
            }

            // caps
            if (capEnds && rings >= 2)
            {
                // start
                int startBase = 0;
                Vector3 startCenter = centers[0];
                Vector3 startNormal = -tangents[0];
                int centerStartIndex = s_vertices.Count;
                s_vertices.Add(startCenter);
                s_normals.Add(startNormal);
                s_uvs.Add(new Vector2(0.5f, 0.5f));
                for (int j = 0; j < vertsPerRing; j++)
                {
                    int a = centerStartIndex;
                    int b0 = startBase + ((j + 1) % vertsPerRing);
                    int c0 = startBase + j;
                    if (!flipWinding)
                    {
                        s_tris.Add(centerStartIndex);
                        s_tris.Add(b0);
                        s_tris.Add(c0);
                    }
                    else
                    {
                        s_tris.Add(centerStartIndex);
                        s_tris.Add(c0);
                        s_tris.Add(b0);
                    }
                }

                // end
                int endBase = (rings - 1) * vertsPerRing;
                Vector3 endCenter = centers[rings - 1];
                Vector3 endNormal = tangents[rings - 1];
                int centerEndIndex = s_vertices.Count;
                s_vertices.Add(endCenter);
                s_normals.Add(endNormal);
                s_uvs.Add(new Vector2(0.5f, 0.5f));
                for (int j = 0; j < vertsPerRing; j++)
                {
                    int a2 = centerEndIndex;
                    int b2 = endBase + j;
                    int c2 = endBase + ((j + 1) % vertsPerRing);
                    if (!flipWinding)
                    {
                        s_tris.Add(centerEndIndex);
                        s_tris.Add(b2);
                        s_tris.Add(c2);
                    }
                    else
                    {
                        s_tris.Add(centerEndIndex);
                        s_tris.Add(c2);
                        s_tris.Add(b2);
                    }
                }
            }

            // assign
            mesh.Clear();
            mesh.SetVertices(s_vertices);
            mesh.SetNormals(s_normals);
            mesh.SetUVs(0, s_uvs);
            mesh.SetTriangles(s_tris, 0);
            mesh.RecalculateBounds();
        }

        // Optional: visual sanity check in Scene view for the centerline
        void OnDrawGizmos()
        {
            if (_centersLocal == null || _centersLocal.Count < 2) return;
            Gizmos.color = Color.yellow;
            var M = transform.localToWorldMatrix;
            for (int i = 0; i < _centersLocal.Count - 1; i++)
                Gizmos.DrawLine(M.MultiplyPoint3x4(_centersLocal[i]), M.MultiplyPoint3x4(_centersLocal[i + 1]));
        }
    }
}
