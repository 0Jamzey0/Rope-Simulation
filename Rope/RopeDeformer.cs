
// ---------------------------------------------
// FILE: RopeDeformer.cs
// (No mesh field needed anymore; optional utility to push points directly to the 3D renderer.)
// ---------------------------------------------
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class RopeDeformer : MonoBehaviour
{
    [Tooltip("Optional: assign if you want to feed deformed points straight to the mesh (bypassing LineRenderer). Leave empty to let RopeMeshRenderer read from the LineRenderer instead.")]
    public RopeMeshRenderer ropeRenderer;

    // Example hook: call this when your deformation step finishes.
    public void ApplyDeformation(IList<Vector3> deformedPoints)
    {
        if (ropeRenderer && ropeRenderer.enabled)
        {
            // Feed points directly into the 3D rope (only used when in Mesh3D mode)
            ropeRenderer.SetPoints(deformedPoints);
        }
        else
        {
            // Otherwise your existing code can keep pushing positions to LineRenderer
            var lr = GetComponent<LineRenderer>();
            if (lr && deformedPoints != null)
            {
                lr.positionCount = deformedPoints.Count;
                for (int i = 0; i < deformedPoints.Count; i++) lr.SetPosition(i, deformedPoints[i]);
            }
        }
    }
}
