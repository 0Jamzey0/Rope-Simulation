using UnityEngine;

public class RopeMeshRenderer
{
	private Mesh ropeMesh;
	private Vector3[] vertices;
	private int[] triangles;
	private Vector2[] uvs;

	private float ropeRadius;
	private int segmentsAround;
	private int numSegments;

	private MeshFilter meshFilter;

	public RopeMeshRenderer(MeshFilter meshFilter, float ropeRadius, int segmentsAround, int numSegments)
	{
		this.meshFilter = meshFilter;
		this.ropeRadius = ropeRadius;
		this.segmentsAround = segmentsAround;
		this.numSegments = numSegments;

		InitializeMesh();
	}

	private void InitializeMesh()
	{
		ropeMesh = new Mesh();
		meshFilter.mesh = ropeMesh;

		int vertexCount = numSegments * segmentsAround;
		int triangleCount = (numSegments - 1) * segmentsAround * 6;

		vertices = new Vector3[vertexCount];
		triangles = new int[triangleCount];
		uvs = new Vector2[vertexCount];

		for (int i = 0; i < numSegments - 1; i++)
		{
			for (int j = 0; j < segmentsAround; j++)
			{
				// Triangle indices
				int baseIndex = (i * segmentsAround + j) * 6;
				int nextSegment = (j + 1) % segmentsAround;

				int currentIndex = i * segmentsAround + j;
				int nextIndex = currentIndex + segmentsAround;

				triangles[baseIndex] = currentIndex;
				triangles[baseIndex + 1] = nextIndex;
				triangles[baseIndex + 2] = i * segmentsAround + nextSegment;

				triangles[baseIndex + 3] = nextIndex;
				triangles[baseIndex + 4] = nextSegment + (i + 1) * segmentsAround;
				triangles[baseIndex + 5] = i * segmentsAround + nextSegment;
			}
		}

		ropeMesh.vertices = vertices;
		ropeMesh.triangles = triangles;
		ropeMesh.uv = uvs;
	}

	public void UpdateMesh(Vector3[] ropePoints)
	{
		Transform parentTransform = meshFilter.transform;

		for (int i = 0; i < numSegments; i++)
		{
			// Convert rope points to local space
			Vector3 localPosition = parentTransform.InverseTransformPoint(ropePoints[i]);
			Vector3 direction = i < numSegments - 1
				? (parentTransform.InverseTransformPoint(ropePoints[i + 1]) - localPosition).normalized
				: (localPosition - parentTransform.InverseTransformPoint(ropePoints[i - 1])).normalized;

			Quaternion rotation = Quaternion.LookRotation(direction);

			for (int j = 0; j < segmentsAround; j++)
			{
				float angle = (float)j / segmentsAround * Mathf.PI * 2;
				Vector3 offset = rotation * new Vector3(Mathf.Cos(angle) * ropeRadius, Mathf.Sin(angle) * ropeRadius, 0);
				vertices[i * segmentsAround + j] = localPosition + offset;
			}
		}

		ropeMesh.vertices = vertices;
		ropeMesh.RecalculateNormals();
		ropeMesh.RecalculateBounds();
	}

}
