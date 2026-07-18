using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;
using System.Collections.Generic;

public class PlaneAgent : Agent
{
    Rigidbody rBody;
    PlaneController planeController;
    [SerializeField] Transform runway;
    float throttleInput;
    float previousDistance;
    float rewardScale = 0.01f;
    bool takeoff;
    bool firstTargetOfEpisode;
    int targetsThisEpisode;
    bool firstEpisode;
    bool touchdownHandled;
    bool landing;
    bool wasGrounded;
    int waypointIndex;
    bool airborneRewardGiven;
    bool altitudeRewardGiven;
    bool outcomeRecorded;
    bool missionRecorded;
    bool wasChainedEpisode;
    int decisionCount;
    bool hitSpeed10;
    bool hitSpeed20;
    bool hitSpeed30;
    bool isChainedEpisode;
    float allowedRadius;
    float slope = Mathf.Tan(6f * Mathf.Deg2Rad);
    public override void Initialize()
    {
        rBody = GetComponent<Rigidbody>();
        planeController = GetComponent<PlaneController>();
        planeController.useKeyboardInput = false;
        firstEpisode = true;
        waypointIndex = 0;
        decisionCount = 0;
    }

    public Transform Target;
    public override void OnEpisodeBegin()
    {
        if (!firstEpisode)
        {
            Academy.Instance.StatsRecorder.Add("Custom/TargetsPerEpisode", targetsThisEpisode);

            if (wasChainedEpisode && !missionRecorded)
                Academy.Instance.StatsRecorder.Add("Custom/Mission/Completed", 0f);
        }
        if (!outcomeRecorded)
            RecordOutcome(timeout: true);

        outcomeRecorded = false;
        missionRecorded = false;

        firstEpisode = false;
        targetsThisEpisode = 0;
        touchdownHandled = false;
        waypointIndex = 0;
        airborneRewardGiven = false;
        altitudeRewardGiven = false;
        hitSpeed10 = false;
        hitSpeed20 = false;
        hitSpeed30 = false;
        decisionCount = 0;

        // Curriculum wrapper
        float loopProb = Academy.Instance.EnvironmentParameters.GetWithDefault("loop_prob", 0.8f);
        isChainedEpisode = Random.value < loopProb;
        wasChainedEpisode = isChainedEpisode;

        if (isChainedEpisode)
        {
            // Enforce continuous sequence
            takeoff = true;
            landing = false;

            // Always spawn on runway
            planeController.RespawnAt(new Vector3(0f, 0f, 0f), true, 0);
            wasGrounded = takeoff;

            // Set the first takeoff target
            Target.position = DeparturePoint(100f);
            firstTargetOfEpisode = false;
        }
        else
        {
            float groundedProb = Academy.Instance.EnvironmentParameters.GetWithDefault("grounded_prob", 0.3f);
            float landingProb = Academy.Instance.EnvironmentParameters.GetWithDefault("landing_prob", 0.4f);
            
            float r = Random.value;
            takeoff = r < groundedProb;
            landing = !takeoff && r < groundedProb + landingProb;

            if (takeoff)
            {
                planeController.RespawnAt(new Vector3(0f, 0f, 0f), true, 0);
            }
            else if (landing)
            {
                SpawnForLanding();
            }
            else
                planeController.RespawnAt(new Vector3(0f, 30f, 0f), false, 0);

            firstTargetOfEpisode = true;
            wasGrounded = takeoff;

            if (landing) Target.position = approach[0];
            else if (takeoff)
            {
                Target.position = DeparturePoint(100f);
                firstTargetOfEpisode = false;
            }
            else SpawnNewTarget();
        }

        previousDistance = Vector3.Distance(transform.localPosition, Target.localPosition);
        allowedRadius = previousDistance + 60f;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // Target and Agent positions
        Vector3 toTarget = Target.position - transform.position;
        Vector3 rwyLocal = runway.InverseTransformPoint(transform.position);
        sensor.AddObservation(transform.InverseTransformDirection(toTarget.normalized));        // 3: bearing in body frame
        sensor.AddObservation(toTarget.magnitude / 100f);                                       // 1: distance
        sensor.AddObservation(transform.InverseTransformDirection(rBody.linearVelocity) / 60f); // 3
        sensor.AddObservation(transform.forward.y);                                             // 1: climb/dive
        sensor.AddObservation(transform.right.y);                                               // 1: bank
        sensor.AddObservation(planeController.throttle);                                        // 1
        sensor.AddObservation(transform.position.y / 60f);                                      // 1: altitude
        sensor.AddObservation(transform.InverseTransformDirection(runway.forward));             // 3: runway heading in body frame
        sensor.AddObservation(rwyLocal.x / 20f);                                                // 1: cross-track offset
        sensor.AddObservation(rwyLocal.z / 100f);                                               // 1: distance along runway
        sensor.AddObservation(landing ? 1f : 0f);
        sensor.AddObservation(takeoff ? 1f : 0f);
        sensor.AddObservation(planeController.touchedDown ? 1f : 0f);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        decisionCount++;
        Debug.DrawLine(transform.position, Target.position, Color.green);
        float pitchInput = actions.ContinuousActions[0];
        float turnInput = actions.ContinuousActions[1];
        float throttleInput = actions.ContinuousActions[2];

        Vector3 flatfFwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
        Vector3 flatRwy = Vector3.ProjectOnPlane(runway.forward, Vector3.up).normalized;

        float alignment = Vector3.Dot(flatfFwd, flatRwy);
        float aligned = Mathf.Clamp01((alignment - 0.9f) / 0.1f);
        float offCenter = Mathf.Abs(runway.InverseTransformPoint(transform.position).x);
        float gentle = 1f - Mathf.Clamp01(planeController.touchdownImpact / 8f);

        planeController.AgentControl(pitchInput, turnInput, throttleInput);
        bool patternFix = landing && Mathf.Abs(runway.InverseTransformPoint(Target.position).x) > 1f;
        bool finalFix = Mathf.Abs(runway.InverseTransformPoint(Target.position).x) < 1f;

        float distanceToTarget = Vector3.Distance(transform.localPosition, Target.localPosition);
        if (planeController.agentCrashed)
        {
            SetReward(-1.0f);
            RecordOutcome(crash: true);
            LogEnd("CRASH");
            EndEpisode();
            return;
        }
        else if (landing && planeController.touchedDown && !touchdownHandled)
        {
            touchdownHandled = true;
            if (planeController.touchdownOnRunway)
            {
                LogEnd("RUNWAY TOUCHDOWN");
                AddReward(1.0f + 0.5f * gentle + 0.5f * aligned);
            }
            else
            {
                SetReward(-0.2f);
                RecordOutcome(grass: true);
                LogEnd("GRASS");
                EndEpisode();
                return;
            }
        }
        else if (landing && touchdownHandled && planeController.currentSpeed < 0.5f)
        {
            AddReward(1.0f + 0.5f * aligned);
            RecordOutcome(success: true);
            LogEnd("STOPPED - SUCCESS");
            EndEpisode();

            return;
        }
        else if (distanceToTarget < (patternFix ? 10f : 5f))
        {
            if (landing)
            {
                if (waypointIndex < approach.Count - 1)
                {
                    AddReward(finalFix ? 0.3f * (1f - Mathf.Clamp01(offCenter / 7f)) : 0.2f);
                    targetsThisEpisode++;
                    waypointIndex++;
                    Target.position = approach[waypointIndex];

                    // Reset new radius for new waypoint
                    previousDistance = Vector3.Distance(transform.localPosition, Target.localPosition);
                    allowedRadius = previousDistance + 60f;

                    Debug.Log($"waypoint {waypointIndex} reached");
                }
            }
            else
            {
                AddReward(1.0f);
                RecordOutcome(success: true);
                targetsThisEpisode++;
                LogEnd("SUCCESS");
                
                
                // Stage transition
                if (takeoff)
                {
                    if (isChainedEpisode)
                    {
                        // Takeoff -> Cruise
                        takeoff = false;
                        SpawnNewTarget();
                        Debug.Log("Transition to Cruise");
                    }
                    else
                    {
                        // Isolated run
                        EndEpisode();
                        return;
                    }
                }
                else if (isChainedEpisode && targetsThisEpisode >= 10)
                {
                    // Cruise -> Landing
                    landing = true;
                    BuildApproach(transform.position);
                    waypointIndex = 0;
                    Target.position = approach[waypointIndex];

                    previousDistance = Vector3.Distance(transform.localPosition, Target.localPosition);
                    allowedRadius = previousDistance + 60f;
                    Debug.Log("Transition to landing");
                }
                else
                {
                    SpawnNewTarget();
                }
            }
            return;
        }
        else if (distanceToTarget > allowedRadius)
        {
            SetReward(-1.0f);
            RecordOutcome(flyaway: true);
            LogEnd("FLYWAY");
            EndEpisode();
            return;
        }
        else if (transform.localPosition.y < -2f)
        {
            SetReward(-1.0f);
            RecordOutcome(crash: true);
            LogEnd("CRASH");
            EndEpisode();
            return;
        }
        else if (takeoff && !airborneRewardGiven && decisionCount > 500)
        {
            SetReward(-1.0f);
            RecordOutcome(timeout: true);
            LogEnd("TIMEOUT");
            EndEpisode();
            return;
        }
        if (takeoff)
        {
            AddReward(-0.005f * Mathf.Clamp01(offCenter / 5f));
            AddReward(-0.0002f); // Parking causes negative reward
            if (!hitSpeed10 && planeController.currentSpeed >= 10)
            {
                AddReward(0.1f);
                hitSpeed10 = true;
            }
            if (!hitSpeed20 && planeController.currentSpeed >= 20)
            {
                AddReward(0.1f);
                hitSpeed20 = true;
            }
            if (!hitSpeed30 && planeController.currentSpeed >= 30)
            {
                AddReward(0.1f);
                hitSpeed30 = true;
            }
            if (wasGrounded && !planeController.grounded && !airborneRewardGiven && planeController.currentSpeed > planeController.stallSpeed * 0.3)
            {
                AddReward(0.3f);
                airborneRewardGiven = true;
                Debug.Log("Lift off.");
            }
            if (!planeController.grounded && transform.position.y > 8f && !altitudeRewardGiven)
            {
                AddReward(0.5f);
                altitudeRewardGiven = true;
            }

            wasGrounded = planeController.grounded;
        }
        Vector3 toTarget = (Target.position - transform.position).normalized;
        if (!touchdownHandled)
        {
            AddReward(0.001f * Vector3.Dot(rBody.linearVelocity.normalized, toTarget.normalized));
            AddReward(rewardScale * (previousDistance - distanceToTarget));
        }
        previousDistance = distanceToTarget;
        
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var continuousActionsOut = actionsOut.ContinuousActions;
        continuousActionsOut[0] = Input.GetAxis("Vertical");
        continuousActionsOut[1] = Input.GetAxis("Horizontal");
        throttleInput = 0f;
        if (Input.GetKey(KeyCode.LeftShift))
        {
            throttleInput = 1f;
        }
        if (Input.GetKey(KeyCode.LeftControl))
        {
            throttleInput = -1f;
            
        }
        continuousActionsOut[2] = throttleInput;
    }

    void SpawnNewTarget()
    {
        float minDist = 20f;
        float maxDist = 60f;

        float angleRange = Academy.Instance.EnvironmentParameters.GetWithDefault("angle_range", 180f);
        float heightRange = Academy.Instance.EnvironmentParameters.GetWithDefault("height_range", 60f);
        float angle, heightOffset;

        if (takeoff && firstTargetOfEpisode)
        {
            // takeoff target
            angle = Random.Range(-30f, 30f) * Mathf.Deg2Rad;
            heightOffset = Random.Range(15f, 30f);
        }
        else
        {
            angle = Random.Range(-angleRange, angleRange) * Mathf.Deg2Rad;
            heightOffset = Random.Range(-heightRange, heightRange);
        }

        float dist = Random.Range(minDist, maxDist);

        Vector3 p = transform.localPosition + new Vector3(Mathf.Sin(angle) * dist, heightOffset, Mathf.Cos(angle) * dist);



        p.y = Mathf.Clamp(p.y, 10f, 90f);
        Target.localPosition = p;
        
        firstTargetOfEpisode = false;
        previousDistance = Vector3.Distance(transform.localPosition, Target.localPosition);

        // Reset radius
        allowedRadius = previousDistance + 60f;
    }

    void RecordOutcome(bool success = false, bool crash = false, bool flyaway = false, bool grass = false, bool timeout = false)
    {
        var stats = Academy.Instance.StatsRecorder;
        string mode = landing ? "Landing" : (takeoff ? "Takeoff" : "Cruise");
        stats.Add($"Custom/{mode}/Success", success ? 1f : 0f);
        stats.Add($"Custom/{mode}/Crash", crash ? 1f : 0f);
        stats.Add($"Custom/{mode}/Flyaway", flyaway ? 1f : 0f);
        stats.Add($"Custom/{mode}/Grass", grass ? 1f : 0f);
        stats.Add($"Custom/{mode}/Timeout", timeout ? 1f : 0f);

        if (isChainedEpisode)
        {
            bool isFailure = crash || flyaway || grass || timeout;
            
            if (isFailure)
            {
                stats.Add("Custom/Mission/Completed", 0f);
                missionRecorded = true;
            }
            else if (success && landing)
            {
                stats.Add("Custom/Mission/Completed", 1f);
                missionRecorded = true;
            }
        }
        outcomeRecorded = true;
    }

    List<Vector3> approach = new List<Vector3>();

    Vector3 Threshold() =>
        runway.position - runway.forward * (runway.transform.localScale.z / 2f);

    // distOut: metres before the threshold along the approach; lateral: metres right of centreline
    Vector3 FixAt(float distOut, float lateral)
    {
        float alt = (lateral != 0f || distOut < 0f)
            ? 25f                                // pattern legs: constant pattern altitude
            : Mathf.Max(distOut, 0f) * slope;    // final: on the glideslope
        return Threshold() - runway.forward * distOut
            + runway.right * lateral + Vector3.up * alt;
    }

    void BuildApproach(Vector3 fromPos)
    {
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
    }

    Vector3 ApproachPoint(float distOut)
    {
        Vector3 threshold = runway.position - runway.forward * (runway.transform.localScale.z / 2f);
        return threshold - runway.forward * distOut + Vector3.up * (distOut * slope);
    }

    Vector3 DeparturePoint(float distOut)
    {
        Vector3 departureEnd = runway.position + runway.forward * (runway.transform.localScale.z / 2f);
        return departureEnd + runway.forward * distOut + Vector3.up * (distOut * slope);
    }

    void SpawnForLanding()
    {
        float coneDeg = Academy.Instance.EnvironmentParameters.GetWithDefault("spawn_cone", 45f);
        float distOut = Academy.Instance.EnvironmentParameters.GetWithDefault("spawn_dist", 200f);

        Vector3 threshold = runway.position - runway.forward * (runway.localScale.z / 2f);

        float ang = Random.Range(-coneDeg, coneDeg);
        Vector3 dir = Quaternion.AngleAxis(ang, Vector3.up) * -runway.forward;

        Vector3 pos = threshold + dir * distOut + Vector3.up * (distOut * slope) + Vector3.up * Random.Range(-15f, 15f);

        BuildApproach(pos);
        waypointIndex = 0;

        Vector3 toFirst = approach[0] - pos;
        float yawDeg = Mathf.Atan2(toFirst.x, toFirst.z) * Mathf.Rad2Deg;

        planeController.RespawnAt(transform.parent.InverseTransformPoint(pos), false, yawDeg);
    }

    void LogEnd(string outcome)
    {
        string mode = landing ? "Landing" : (takeoff ? "Takeoff" : "Cruise");
        Debug.Log($"[{Time.time:F0}s] {mode} END: {outcome} | alt={transform.position.y:F2} " +
                $"spd={planeController.currentSpeed:F1} wp={waypointIndex} " +
                $"touchedDown={planeController.touchedDown} onRwy={planeController.touchdownOnRunway} " +
                $"R={GetCumulativeReward():F2}");
    }

    void OnGUI()
    {
        GUI.Label(new Rect(10, 10, 500, 120),
            $"alt {transform.position.y:F2}   spd {planeController.currentSpeed:F1}   thr {planeController.throttle:F2}\n" +
            $"touchedDown {planeController.touchedDown}   wp {waypointIndex}/{approach.Count - 1}\n" +
            $"distToTarget {Vector3.Distance(transform.position, Target.position):F1}   " +
            $"reward {GetCumulativeReward():F2}");
    }
}