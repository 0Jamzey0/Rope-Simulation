using UnityEngine;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class RopeMeshDeformer : MonoBehaviour
{
    public RopeController ropeController; 
    private Mesh ropeMesh;                
    private Vector3[] originalVertices;  
    private Vector3[] deformedVertices;
    private Vector3[] ropePoints;

    void Start()
    {
       
        ropeMesh = GetComponent<MeshFilter>().mesh;
        originalVertices = ropeMesh.vertices;
        deformedVertices = new Vector3[originalVertices.Length];
        ropePoints = ropeController.GetRopePoints;
    }

    void Update()
    {
        if(ropePoints==null)
        ropePoints = ropeController.GetRopePoints;
        
        if (ropePoints.Length < 2) return; 

       
        DeformMesh(ropePoints);

        
        ropeMesh.vertices = deformedVertices;
        ropeMesh.RecalculateNormals();
    }

    void DeformMesh(Vector3[] ropePoints)
    {
        for (int i = 0; i < originalVertices.Length; i++)
        {
            Vector3 vertex = originalVertices[i];
            float normalizedHeight = Mathf.Clamp01(vertex.y); 

            
            int segmentIndex = Mathf.FloorToInt(normalizedHeight * (ropePoints.Length - 1));
            segmentIndex = Mathf.Clamp(segmentIndex, 0, ropePoints.Length - 2); // Ensure valid index

          
            Vector3 start = ropePoints[segmentIndex];
            Vector3 end = ropePoints[segmentIndex + 1];
            deformedVertices[i] = Vector3.Lerp(start, end, normalizedHeight * (ropePoints.Length - 1) - segmentIndex);
        }
    }
}