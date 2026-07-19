using System.Collections.Generic;
using UnityEngine;

//    Some terminology explaination for future reference
//                  ┌─────────┐
//                  │         │ ← departure end
//                  │ RUNWAY  │
//                  │         │        ● A   abeam fix, FixAt(-40, +100), alt 25 m
//                  ├─────────┤        │
//         threshold ● (0 m)           │  downwind leg
//                   ● 20 m            │  (flying opposite the
//                   ● 40 m            │   landing direction)
//                   ● 60 m            ▼
//    glideslope     │                 │
//    ladder         │                 │
//    (final)  F ●───●─────────────────● B   base corner, FixAt(150, +100), alt 25 m
//    FixAt(150,0)      ← base leg
//    alt ≈ 15.8 m

/// <summary>
/// Pure geometry for planning approaches around a runway.
/// Knows nothing about ML-Agents, rewards, or episodes.
/// </summary>
public class ApproachPlanner
{
    readonly Transform runway;
    readonly float slope = Mathf.Tan(6f * Mathf.Deg2Rad);

    public ApproachPlanner(Transform runway)
    {
        this.runway = runway;
    }

    public Vector3 Threshold() => 
        runway.position - runway.forward * (runway.localScale.z / 2f);

    public Vector3 DeparturePoint(float distOut)
    {
        Vector3 departureEnd = runway.position + runway.forward * (runway.localScale.z / 2f);
        return departureEnd + runway.forward * distOut + Vector3.up * (distOut * slope);
    }

    public float GlideslopeAltitude(float d) => Mathf.Max(d, 0f) * slope;

    // distOut: metres before the threshold along the approach; lateral: metres right of centreline
    Vector3 FixAt(float distOut, float lateral)
    {
        float alt = (lateral != 0f || distOut < 0f)
            ? 25f                                // pattern legs: constant pattern altitude
            : Mathf.Max(distOut, 0f) * slope;    // final: on the glideslope
        return Threshold() - runway.forward * distOut
            + runway.right * lateral + Vector3.up * alt;
    }

    public List<Vector3> BuildApproach(Vector3 fromPos)
    {
        var approach = new List<Vector3>();
        Vector3 rel  = fromPos - Threshold();
        float outDist = Vector3.Dot(rel, -runway.forward); // >0 = approach side of threshold
        float side    = Vector3.Dot(rel, runway.right);    // signed lateral offset
        float s       = Mathf.Sign(side);

        approach.Clear();

        if (outDist > 50f && Mathf.Abs(side) < outDist * 0.6f)
        {
            // already in a ~±30° cone in front: straight-in
            approach.Add(FixAt(150f, 0f));
        }
        else if (outDist > 50f)
        {
            // in front but off to the side: base entry on the plane's side
            approach.Add(FixAt(150f, s * 100f));   // base point
            approach.Add(FixAt(150f, 0f));         // turn final
        }
        else
        {
            // beside/behind the runway: downwind entry on the plane's side
            approach.Add(FixAt(-40f, s * 100f));   // abeam the runway
            approach.Add(FixAt(150f, s * 100f));   // downwind to base
            approach.Add(FixAt(150f, 0f));         // turn final
        }

        // glideslope chain, unchanged
        approach.Add(FixAt(60f, 0f));
        approach.Add(FixAt(40f, 0f));
        approach.Add(FixAt(20f, 0f));
        approach.Add(FixAt(0f, 0f));
        return approach;
    }
}