// -------------------------------------------------------------
// FILE 1: RopePerformance.cs
// -------------------------------------------------------------
using System;
using UnityEngine;


[Serializable]
public class RopePerformanceProfile
{
[Header("Adaptive Simulation")]
public bool adaptiveSubsteps = true;
[Min(1)] public int substepsMin = 1;
[Min(1)] public int substepsMax = 4;
[Tooltip("Increase substeps when max point speed exceeds this (m/s)")]
public float speedThreshold = 3.0f;
[Tooltip("Hysteresis band to avoid flicker")]
public float speedThresholdHysteresis = 0.5f;


public bool adaptiveIterations = true;
[Min(1)] public int iterationsMin = 2;
[Min(1)] public int iterationsMax = 8;
[Tooltip("Increase iterations if average segment stretch exceeds this fraction (e.g. 0.02 = 2%)")]
public float stretchTolerance = 0.02f;


[Header("Collision Broadphase")]
public bool useBroadphaseCache = true;
[Tooltip("Box padding added around rope AABB for collider query")] public float broadphasePadding = 0.25f;
[Tooltip("Max colliders collected per step (NonAlloc array)")] public int maxBroadphaseColliders = 128;


[Header("CCD Gating")]
[Tooltip("Skip expensive casts when motion is tiny")] public float ccdSpeedGate = 0.25f;


[Header("Renderer LOD")]
public bool dynamicRendererLOD = true;
[Tooltip("World-space distance at which the rope is 'near'")] public float nearDistance = 6f;
[Tooltip("World-space distance at which the rope is 'far'")] public float farDistance = 30f;
[Range(3,64)] public int radialSegmentsNear = 24;
[Range(3,64)] public int radialSegmentsFar = 8;
[Tooltip("Sample every Nth ring when far (render decimation only)")] public int ringStrideFar = 2;
[Tooltip("Rebuild mesh every N frames (1 = every frame)")] public int rebuildEveryNFrames = 1;


public static class RopePerfUtil
{
public static Bounds ComputeBounds(Vector3[] pts)
{
if (pts == null || pts.Length == 0)
return new Bounds(Vector3.zero, Vector3.zero);
var b = new Bounds(pts[0], Vector3.zero);
for (int i = 1; i < pts.Length; i++) b.Encapsulate(pts[i]);
return b;
}


public static float ComputeMaxSpeed(Vector3[] cur, Vector3[] prev, float dt)
{
    if (cur == null || prev == null || cur.Length != prev.Length || dt <= 0f) return 0f;
    float maxV = 0f;
    for (int i = 0; i < cur.Length; i++)
    {
        float v = (cur[i] - prev[i]).magnitude / dt;
        if (v > maxV) maxV = v;
    }
    
    return maxV;

}

public static float ComputeAvgStretch(Vector3[] pts, float targetLen)
{
    if (pts == null || pts.Length < 2 || targetLen <= 0f) return 0f;
    float acc = 0f; int n = 0;
    for (int i = 0; i < pts.Length - 1; i++) { acc += Mathf.Abs((pts[i+1]-pts[i]).magnitude - targetLen) / targetLen; n++; }
    return (n > 0) ? acc / n : 0f;
}
}
}