using System.Numerics;
//using InfinityCode.UltimateEditorEnhancer.Attributes;
using Sirenix.Utilities;
using UnityEngine;
using Vector3 = UnityEngine.Vector3;
using Sirenix.OdinInspector;

public class RopeController : MonoBehaviour
{
    // [Header("Player Interactions")]
    // public float DetectionRadius;
    // public string PlayerTag;
    // public Transform PlayerTransform;

    private enum CollisionType
    {
        Simple,Advanced
    }

    [Title("Performance",bold:true)]
    [GUIColor("#FF0000")]
    [MinValue(1)]
    public int Iterations=3;
    [Range(1,2)]
    [Tooltip("Represents number of Substeps per frame")]
    public int Substeps=1;
    [Title("Rope Mesh Settings",bold:true)]
    public MeshFilter meshFilter; //--> mesh to be deformed into rope , here we can use any mesh but for best results --> use a rope model mesh
    public float ropeRadius = 0.05f;
    public int segmentsAround = 8;
    private RopeMeshRenderer ropeMeshRenderer;
    
    [Title("Rope Settings",bold:true)] 
    public int numSegments=3;
    public float segementLenght=0.2f;
    public Vector3 Gravity = new Vector3(0, -9.8f, 0);

    [Title("Rope Visualization",bold:true)]
    public LineRenderer lineRenderer;
    
    
    private Vector3[] CurrentPoints;
    private Vector3[] PrevPoints;
    public Vector3[] RopePoints {
        get
        {
            return CurrentPoints;
        }
     }

    [Tooltip("By Default if not added to the Inspector , the Anchor will would be this Transform's position")]
    [SerializeField] private Transform anchorPoint;
    [Tooltip("End Anchor Point In case of wanting to add the rope between two anchor points")]
    [SerializeField] private Transform EndPoint;
    
    [Title("Interaction",bold:true)]
    [SerializeField] private bool Interactive;
    [EnableIf("Interactive")]
    [SerializeField] private LayerMask InteractionLayer;
    [Range(0.05f,0.3f)]
    [EnableIf("Interactive")]
    [SerializeField] private float InteractiveRadius=.1f;
    [EnableIf("Interactive")]
    [SerializeField] private CollisionType InteractionType= CollisionType.Simple;

    public void Start()
    {
        if (anchorPoint == null) anchorPoint = transform;
        CurrentPoints = new Vector3[numSegments];
        PrevPoints = new Vector3[numSegments];
        Vector3 startPos = anchorPoint.position;
        for (int i = 0; i < numSegments; i++)
        {
            CurrentPoints[i] = startPos+ Vector3.down * segementLenght * i;
            PrevPoints[i] = CurrentPoints[i];
        }
        if (lineRenderer != null)
            lineRenderer.positionCount = numSegments;

        if(meshFilter != null)
        ropeMeshRenderer = new RopeMeshRenderer(meshFilter, ropeRadius, segmentsAround, numSegments);

    }

    public void Update()
    {
        // if (DetectPlayer())
        // {
        //     Debug.Log("Player detected");
        //     AttachPlayertoRope(GetClosestPoint(PlayerTransform.position));
        //     Debug.Log("Closest point to player is \t" + GetClosestPoint(PlayerTransform.position));
        //     DetachPlayer();
        // }

        for (int i = 0; i < Substeps; i++)
        {
            if (ropeMeshRenderer != null)
                ropeMeshRenderer.UpdateMesh(CurrentPoints);
            else
                RenderRopeUsingLineRenderer();

            StartSimulation(Time.deltaTime);
            ApplyConstraints();
            
            if(InteractionType==CollisionType.Simple)
                
                HandleCollisionsSimple();
            else
                HandleCollisionsAccurate(Time.deltaTime);
        }
    }

    private void StartSimulation(float Time)
    {
        for (int i = 1; i < numSegments; i++)
        {
            Vector3 newPoint = 2*CurrentPoints[i] - PrevPoints[i] + Gravity * Mathf.Pow(Time,2);
            PrevPoints[i] = CurrentPoints[i];
            CurrentPoints[i] = newPoint;
            
        }

    }

    private void ApplyConstraints()
    {

        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            for (int i = 0; i < numSegments - 1; i++)
            {
                Vector3 Dir = CurrentPoints[i+1] - CurrentPoints[i];
                float error = Dir.magnitude- segementLenght;
                
                Vector3 Correction =Dir.normalized * (error* .5f);
                
                if(i>0) CurrentPoints[i] +=Correction;
                CurrentPoints[i+1] -= Correction;
            }
        }
        if(anchorPoint)
        CurrentPoints[0]= anchorPoint.position;
       
        if(EndPoint)
        CurrentPoints[numSegments - 1] = EndPoint.position;

    }
    
    
    private void HandleCollisionsSimple()
    {
        if(!Interactive) return;
        
        for (int i = 0; i < numSegments; i++)
        {
            Collider[] hitColliders = Physics.OverlapSphere(CurrentPoints[i], InteractiveRadius, InteractionLayer);

            foreach (var hit in hitColliders)
            {
                if (hit != null)
                {
                    Vector3 closestPoint = hit.ClosestPoint(CurrentPoints[i]);
                    Vector3 correction = CurrentPoints[i] - closestPoint;
                    float distance = correction.magnitude;

                    if (distance < InteractiveRadius && distance > 0)
                    {
                        CurrentPoints[i] += correction.normalized * (InteractiveRadius - distance);
                    }
                }
            }
        }
    }

    private void HandleCollisionsAccurate(float deltaTime)
    {
        for (int i = 0; i < numSegments; i++)
        {
            Vector3 velocity = (CurrentPoints[i] - PrevPoints[i]) / deltaTime;
            float distance = velocity.magnitude * deltaTime;

            RaycastHit hit;
            if (Physics.SphereCast(PrevPoints[i], InteractiveRadius, velocity.normalized, out hit, distance, InteractionLayer))
            {
                // Correct position to avoid tunneling
                CurrentPoints[i] = hit.point + hit.normal * InteractiveRadius;

                // Apply a friction-like impulse to dampen the movement
                Vector3 reflectedVelocity = Vector3.Reflect(velocity, hit.normal) * 0.5f; // Dampen factor
                PrevPoints[i] = CurrentPoints[i] - reflectedVelocity * deltaTime;

                // Apply forces to the hit rigidbody (experimental)
                if (hit.rigidbody != null)
                {
                    hit.rigidbody.AddForce(reflectedVelocity * 5f, ForceMode.Impulse);
                }
            }
        }
    }
    
    public int GetClosestSegmentIndex(Vector3 position)
    {
        float shortestDistance = float.MaxValue;
        int closestIndex = -1;

        for (int i = 0; i < CurrentPoints.Length; i++)
        {
            float distance = Vector3.Distance(position, CurrentPoints[i]);
            if (distance < shortestDistance)
            {
                shortestDistance = distance;
                closestIndex = i;
            }
        }

        return closestIndex;
    }
    
    public void ApplyForceToSegment(int index, Vector3 force)
    {
        if (index < 0 || index >= CurrentPoints.Length) return;

        CurrentPoints[index] += force;
    }

    public Vector3 GetSegmentPosition(int index)
    {
        if (index < 0 || index >= CurrentPoints.Length) return Vector3.zero;
        return CurrentPoints[index];
    }

    public Vector3[] GetRopePoints => CurrentPoints; //used for exposing the RopePoints for Further Integrations (with other systems like the Swinging Mechanic ...etc)
    
    #region Rendering
    private void RenderRopeUsingLineRenderer()
    {
        if (lineRenderer != null)
        {
            lineRenderer.SetPositions(CurrentPoints); // Update LineRenderer positions
        }
    }
    public void InitializeRope(Vector3 anchorPos, Vector3 endPos)
    {
        // Set anchor and endpoint positions
        if (anchorPoint == null)
        {
            anchorPoint = new GameObject("AnchorPoint").transform;
            anchorPoint.SetParent(transform);
        }
        anchorPoint.position = anchorPos;

        if (EndPoint == null)
        {
            EndPoint = new GameObject("EndPoint").transform;
            EndPoint.SetParent(transform);
        }
        EndPoint.position = endPos;

        // Calculate segment length based on distance
        Vector3 ropeDirection = (endPos - anchorPos).normalized;
        float totalLength = Vector3.Distance(anchorPos, endPos);
        segementLenght = totalLength / (numSegments - 1);

       
        CurrentPoints = new Vector3[numSegments];
        PrevPoints = new Vector3[numSegments];
        for (int i = 0; i < numSegments; i++)
        {
            CurrentPoints[i] = anchorPos + ropeDirection * segementLenght * i;
            PrevPoints[i] = CurrentPoints[i];
        }

      
        if (lineRenderer != null)
        {
            lineRenderer.positionCount = numSegments;
            lineRenderer.useWorldSpace = true; 
            lineRenderer.SetPositions(CurrentPoints);
        }

        // Start simulation
        ApplyConstraints(); // Apply initial constraints
        RenderRopeUsingLineRenderer(); // Render the rope
        Debug.Log("Rope initialized");
    }

    #endregion

    void OnDrawGizmos()
    {
        if (Application.isPlaying)
        {
            Gizmos.color = Color.green;
            for (int i = 0; i < numSegments-1; i++)
            {
                Gizmos.DrawSphere(CurrentPoints[i], 0.05f);
                Gizmos.DrawWireSphere(CurrentPoints[i],InteractiveRadius);
            }

        }
    }

    #region PlayerInteraction
    //
    // private bool DetectPlayer()
    // {
    //     Collider [] colliders = Physics.OverlapSphere(anchorPoint.position,DetectionRadius);
    //     foreach (Collider col in colliders)
    //     {
    //         if (col.CompareTag(PlayerTag))
    //         {
    //            // _playerTransform = col.transform.position;
    //             return true;
    //         }
    //     }
    //     return false;
    // }
    //
    // private Vector3 GetClosestPoint(Vector3 Position)
    // {
    //      float ShortestDistance = float.MaxValue;
    //      Vector3 ClosestPoint = Vector3.zero;
    //     for (int i = 0; i < numSegments; i++)
    //     {
    //         if (Vector3.Distance(Position, CurrentPoints[i]) < ShortestDistance)
    //         {
    //             ShortestDistance = Vector3.Distance(Position,CurrentPoints[i]);
    //             ClosestPoint = CurrentPoints[i];
    //         }
    //     }
    //     return ClosestPoint;
    //
    // }
    //
    // private void AttachPlayertoRope(Vector3 Position)
    // {
    //     if (Input.GetButtonDown("Jump") && !AttachPlayer)
    //     {
    //         PlayerTransform.parent.position = Position;
    //         AttachPlayer = true;
    //     }
    //
    //
    // }
    //
    // private void DetachPlayer()
    // {
    //     if (Input.GetButtonDown("Jump") && AttachPlayer)
    //     {
    //         AttachPlayer = false;
    //         PlayerTransform.parent = null;
    //     }
    // }
    #endregion
}