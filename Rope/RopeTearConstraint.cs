// ---------------------------------------------
// FILE: RopeTearConstraint.cs
// ---------------------------------------------
using System;
using UnityEngine;

[Serializable]
public class RopeTearConstraint
{
    [Header("Tearing Settings")]
    [Tooltip("Enable tearing logic")]
    public bool active = true;

    [Tooltip("Break when current length exceeds target * this ratio (e.g., 1.6 = 60% overstretch)")]
    [Min(1.01f)] public float tearStretchRatio = 1.6f;

    [Tooltip("Overstretch must persist this long (seconds) before a tear is committed")]
    [Min(0f)] public float minOverstretchTime = 0.05f;

    [Tooltip("If > 0, reduces stored overstretch when below threshold for stability")]
    [Min(0f)] public float recoverRate = 4.0f;

    [Header("Runtime (read-only)")]
    [SerializeField] private int segmentCount;
    [SerializeField] private bool[] connectMask;        // length = segments-1 (true = connected)
    [SerializeField] private float[] overstretchTimer;  // length = segments-1

    public event Action<int> OnSegmentTorn;

    public void Initialize(int segments)
    {
        segmentCount = Mathf.Max(2, segments);
        int edges = segmentCount - 1;
        connectMask = new bool[edges];
        overstretchTimer = new float[edges];
        for (int i = 0; i < edges; i++)
        {
            connectMask[i] = true;
            overstretchTimer[i] = 0f;
        }
    }

    /// <summary>Call once per simulation step BEFORE distance constraints are enforced.</summary>
    public void Step(Vector3[] points, float targetSegmentLength, float dt)
    {
        if (!active || points == null) return;
        if (connectMask == null || connectMask.Length != points.Length - 1) Initialize(points.Length);

        float thr = Mathf.Max(1.0001f, tearStretchRatio);
        int edges = points.Length - 1;

        for (int i = 0; i < edges; i++)
        {
            if (!connectMask[i]) { overstretchTimer[i] = 0f; continue; } // already torn

            float curLen = (points[i + 1] - points[i]).magnitude;
            float ratio = curLen / Mathf.Max(1e-6f, targetSegmentLength);

            if (ratio > thr)
            {
                overstretchTimer[i] += dt;
                if (overstretchTimer[i] >= minOverstretchTime)
                {
                    connectMask[i] = false; // tear!
                    overstretchTimer[i] = 0f;
                    OnSegmentTorn?.Invoke(i);
                }
            }
            else
            {
                // relax stored overstretch for stability
                if (recoverRate > 0f)
                    overstretchTimer[i] = Mathf.Max(0f, overstretchTimer[i] - recoverRate * dt);
                else
                    overstretchTimer[i] = 0f;
            }
        }
    }

    public bool IsConnected(int edgeIndex)
    {
        if (connectMask == null) return true;
        if (edgeIndex < 0 || edgeIndex >= connectMask.Length) return true;
        return connectMask[edgeIndex];
    }

    public bool[] GetConnectMask() => connectMask;

    public void ResetAll()
    {
        if (connectMask == null) return;
        for (int i = 0; i < connectMask.Length; i++)
        {
            connectMask[i] = true;
            overstretchTimer[i] = 0f;
        }
    }

    public void TearAt(int edgeIndex)
    {
        if (connectMask == null) return;
        if (edgeIndex < 0 || edgeIndex >= connectMask.Length) return;
        if (!connectMask[edgeIndex]) return;
        connectMask[edgeIndex] = false;
        overstretchTimer[edgeIndex] = 0f;
        OnSegmentTorn?.Invoke(edgeIndex);
    }

    public void RepairEdge(int edgeIndex)
    {
        if (connectMask == null) return;
        if (edgeIndex < 0 || edgeIndex >= connectMask.Length) return;
        connectMask[edgeIndex] = true;
        overstretchTimer[edgeIndex] = 0f;
    }
}
