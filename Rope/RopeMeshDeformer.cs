using UnityEngine;

[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class RopeMeshDeformer : MonoBehaviour
{
    [Header("Material (Optional)")]
    public Material ropeMaterial;
    public bool autoCreateDefaultMaterial = true;

    private RopeController ropeController;
    private Mesh ropeMesh;
    private Vector3[] originalVertices;
    private Vector3[] deformedVertices;
    private Vector3[] ropePoints;

    void Awake()
    {
        ropeController = GetComponent<RopeController>();
        EnsureAssets();

        // Build a default cylinder-like mesh if empty
        if (ropeMesh.vertexCount == 0)
            GenerateDefaultRopeMesh();

        originalVertices = ropeMesh.vertices;
        deformedVertices = new Vector3[originalVertices.Length];
    }

    void OnEnable()
    {
        EnsureAssets();
    }

    void OnValidate()
    {
        EnsureAssets();
    }

    // Ensures MeshFilter/MeshRenderer have a dynamic Mesh and a Material
   public  void EnsureAssets()
    {
        var mf = GetComponent<MeshFilter>();
        var mr = GetComponent<MeshRenderer>();

        if (ropeMesh == null)
        {
            ropeMesh = new Mesh();
            ropeMesh.name = "RopeTube (Auto)";
            ropeMesh.MarkDynamic();
        }

        // Use sharedMesh/sharedMaterial to avoid extra instances in Edit Mode
        if (mf.sharedMesh != ropeMesh)
            mf.sharedMesh = ropeMesh;

        if (ropeMaterial != null)
        {
            if (mr.sharedMaterial != ropeMaterial)
                mr.sharedMaterial = ropeMaterial;
        }
        else if (autoCreateDefaultMaterial)
        {
            if (mr.sharedMaterial == null || mr.sharedMaterial.name == "Default-Material (Auto)")
            {
                Shader shader =
                    Shader.Find("Universal Render Pipeline/Lit") ??
                    Shader.Find("HDRP/Lit") ??
                    Shader.Find("Standard");

                var mat = new Material(shader) { name = "Default-Material (Auto)" };
                mr.sharedMaterial = mat;
            }
        }
    }

    void Update()
    {
        if (ropeController == null)
            ropeController = GetComponent<RopeController>();

        ropePoints = ropeController != null ? ropeController.RopePoints : null;
        if (ropePoints == null || ropePoints.Length < 2) return;

        if (originalVertices == null || originalVertices.Length == 0)
        {
            originalVertices = ropeMesh.vertices;
            deformedVertices = new Vector3[originalVertices.Length];
        }

        DeformMesh(ropePoints);
        ropeMesh.vertices = deformedVertices;
        ropeMesh.RecalculateNormals();
        ropeMesh.RecalculateBounds();
    }

    void DeformMesh(Vector3[] ropePoints)
    {
        // Simple mapping: treat the mesh's Y (0..1) as arc-length along rope
        for (int i = 0; i < originalVertices.Length; i++)
        {
            Vector3 vertex = originalVertices[i];

            // Assumes GenerateDefaultRopeMesh made height in [0..1]
            float normalizedHeight = Mathf.Clamp01(vertex.y);

            int segCount = ropePoints.Length - 1;
            float scaled = normalizedHeight * segCount;
            int segmentIndex = Mathf.Clamp(Mathf.FloorToInt(scaled), 0, segCount - 1);
            float t = scaled - segmentIndex;

            Vector3 start = ropePoints[segmentIndex];
            Vector3 end   = ropePoints[segmentIndex + 1];

            // Put vertex on the rope centerline; if you want thickness preserved,
            // add local radial offsets in a transported frame here.
            deformedVertices[i] = Vector3.Lerp(start, end, t);
        }
    }

    void GenerateDefaultRopeMesh()
    {
        // Simple cylinder mesh along Y axis with height = 1, radius ~ 0.05
        int segmentsAround = 8;
        int heightSegments = 10;
        float radius = 0.05f;
        float height = 1f;

        Vector3[] verts = new Vector3[(segmentsAround + 1) * (heightSegments + 1)];
        int[] tris = new int[segmentsAround * heightSegments * 6];
        Vector2[] uvs = new Vector2[verts.Length];

        int vertIndex = 0;
        for (int y = 0; y <= heightSegments; y++)
        {
            float v = y / (float)heightSegments;
            float yPos = v * height;
            for (int x = 0; x <= segmentsAround; x++)
            {
                float u = x / (float)segmentsAround;
                float ang = u * Mathf.PI * 2f;
                float xPos = Mathf.Cos(ang) * radius;
                float zPos = Mathf.Sin(ang) * radius;
                verts[vertIndex] = new Vector3(xPos, yPos, zPos);
                uvs[vertIndex] = new Vector2(u, v);
                vertIndex++;
            }
        }

        int triIndex = 0;
        for (int y = 0; y < heightSegments; y++)
        {
            for (int x = 0; x < segmentsAround; x++)
            {
                int i0 = y * (segmentsAround + 1) + x;
                int i1 = i0 + 1;
                int i2 = i0 + (segmentsAround + 1);
                int i3 = i2 + 1;

                tris[triIndex++] = i0;
                tris[triIndex++] = i2;
                tris[triIndex++] = i1;

                tris[triIndex++] = i1;
                tris[triIndex++] = i2;
                tris[triIndex++] = i3;
            }
        }

        ropeMesh.Clear();
        ropeMesh.vertices = verts;
        ropeMesh.triangles = tris;
        ropeMesh.uv = uvs;
        ropeMesh.RecalculateNormals();
        ropeMesh.RecalculateBounds();
    }
}
