using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;
using System.Collections.Generic;

public class PlaneAgent : Agent
{
    Rigidbody rBody;
    PlaneController planeController;
    ApproachPlanner planner;
    Phase phase;
    [SerializeField] Transform runway;

    float previousDistance;
    int targetsThisEpisode;
    int waypointIndex;
    int decisionCount;
    float allowedRadius;

    // Status flags
    bool firstTargetOfEpisode;
    bool firstEpisode;
    bool touchdownHandled;
    bool wasGrounded;
    bool isChainedEpisode;
    bool wasChainedEpisode;

    // Record flags
    bool outcomeRecorded;
    bool missionRecorded;

    // Reward flags
    bool airborneRewardGiven;
    bool altitudeRewardGiven;
    bool hitSpeed10;
    bool hitSpeed20;
    bool hitSpeed30;

    public override void Initialize()
    {
        rBody = GetComponent<Rigidbody>();
        planeController = GetComponent<PlaneController>();
        planeController.useKeyboardInput = false;
        firstEpisode = true;
        waypointIndex = 0;
        decisionCount = 0;
        planner = new ApproachPlanner(runway);
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
        if (!outcomeRecorded) RecordOutcome(timeout: true);

        targetsThisEpisode = 0;
        waypointIndex = 0;
        decisionCount = 0;
        
        // Status flags
        firstEpisode = false;
        touchdownHandled = false;

        // Record flags
        outcomeRecorded = false;
        missionRecorded = false;

        // Reward flags
        airborneRewardGiven = false;
        altitudeRewardGiven = false;
        hitSpeed10 = false;
        hitSpeed20 = false;
        hitSpeed30 = false;

        // Curriculum wrapper
        float loopProb = Academy.Instance.EnvironmentParameters.GetWithDefault("loop_prob", 0.8f);
        isChainedEpisode = Random.value < loopProb;
        wasChainedEpisode = isChainedEpisode;

        if (isChainedEpisode)
        {
            // Chained mode: Takeoff -> Cruise -> Landing
            // Enforce continuous sequence
            phase = Phase.Takeoff;

            // Always spawn on runway
            planeController.RespawnAt(new Vector3(0f, 0f, 0f), true, 0);
            wasGrounded = true;

            // Set the first takeoff target
            Target.position = planner.DeparturePoint(100f);
            firstTargetOfEpisode = false;
        }
        else
        {
            // Old prob mode
            float groundedProb = Academy.Instance.EnvironmentParameters.GetWithDefault("grounded_prob", 0.3f);
            float landingProb = Academy.Instance.EnvironmentParameters.GetWithDefault("landing_prob", 0.4f);
            
            float r = Random.value;
            if (r < groundedProb) phase = Phase.Takeoff;
            else if (r < groundedProb + landingProb) phase = Phase.Landing;
            else phase = Phase.Cruise;

            if (phase == Phase.Takeoff)
            {
                planeController.RespawnAt(new Vector3(0f, 0f, 0f), true, 0);
            }
            else if (phase == Phase.Landing)
            {
                SpawnForLanding();
            }
            else
                planeController.RespawnAt(new Vector3(0f, 30f, 0f), false, 0);

            firstTargetOfEpisode = true;
            wasGrounded = phase == Phase.Takeoff;

            if (phase == Phase.Landing) Target.position = approach[0];
            else if (phase == Phase.Takeoff)
            {
                Target.position = planner.DeparturePoint(100f);
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
        sensor.AddObservation(planeController.throttle);                                        // 1: throttle
        sensor.AddObservation(transform.position.y / 60f);                                      // 1: altitude
        sensor.AddObservation(transform.InverseTransformDirection(runway.forward));             // 3: runway heading in body frame
        sensor.AddObservation(rwyLocal.x / 20f);                                                // 1: cross-track offset
        sensor.AddObservation(rwyLocal.z / 100f);                                               // 1: distance along runway
        sensor.AddObservation(phase == Phase.Landing ? 1f : 0f);                                // 1: landing mode
        sensor.AddObservation(phase == Phase.Takeoff ? 1f : 0f);                                // 1: takeoff mode
        sensor.AddObservation(planeController.touchedDown ? 1f : 0f);                           // 1: touched down or not
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        float offCenter = Mathf.Abs(runway.InverseTransformPoint(transform.position).x);
        float distanceToTarget = Vector3.Distance(transform.localPosition, Target.localPosition);
        Debug.DrawLine(transform.position, Target.position, Color.green);

        decisionCount++;
        ApplyActions(actions);

        if (CheckTerminations(distanceToTarget, offCenter)) return;

        AddTakeoffShaping(offCenter);
        AddTrackingShaping(distanceToTarget);

        previousDistance = distanceToTarget;
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        float throttleInput = 0f;

        var continuousActionsOut = actionsOut.ContinuousActions;
        continuousActionsOut[0] = Input.GetAxis("Vertical");
        continuousActionsOut[1] = Input.GetAxis("Horizontal");

        if (Input.GetKey(KeyCode.LeftShift))
            throttleInput = 1f;
        if (Input.GetKey(KeyCode.LeftControl))
            throttleInput = -1f;

        continuousActionsOut[2] = throttleInput;
    }

    void SpawnNewTarget()
    {
        float minDist = 20f;
        float maxDist = 60f;

        float angleRange = Academy.Instance.EnvironmentParameters.GetWithDefault("angle_range", 180f);
        float heightRange = Academy.Instance.EnvironmentParameters.GetWithDefault("height_range", 60f);
        float angle, heightOffset;

        if (phase == Phase.Takeoff && firstTargetOfEpisode)
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
        string mode = phase.ToString();
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
            else if (success && phase == Phase.Landing)
            {
                stats.Add("Custom/Mission/Completed", 1f);
                missionRecorded = true;
            }
        }
        outcomeRecorded = true;
    }

    List<Vector3> approach = new List<Vector3>();

    void SpawnForLanding()
    {
        float coneDeg = Academy.Instance.EnvironmentParameters.GetWithDefault("spawn_cone", 45f);
        float distOut = Academy.Instance.EnvironmentParameters.GetWithDefault("spawn_dist", 200f);

        Vector3 threshold = runway.position - runway.forward * (runway.localScale.z / 2f); // The start of runway

        float ang = Random.Range(-coneDeg, coneDeg);
        Vector3 dir = Quaternion.AngleAxis(ang, Vector3.up) * -runway.forward; // Spawn direction (left / right)

        Vector3 pos = threshold + dir * distOut + Vector3.up * planner.GlideslopeAltitude(distOut) + Vector3.up * Random.Range(-15f, 15f); // Spawn position
        pos.y = Mathf.Clamp(pos.y, 10f, 37f); // 200 x tan(6°) = max 21.06 base height, ±15

        approach = planner.BuildApproach(pos);
        waypointIndex = 0;

        Vector3 toFirst = approach[0] - pos;
        float yawDeg = Mathf.Atan2(toFirst.x, toFirst.z) * Mathf.Rad2Deg;

        planeController.RespawnAt(transform.parent.InverseTransformPoint(pos), false, yawDeg);
    }

    void ApplyActions(ActionBuffers actions)
    {
        float pitchInput = actions.ContinuousActions[0];
        float turnInput = actions.ContinuousActions[1];
        float throttleInput = actions.ContinuousActions[2];
    
        planeController.AgentControl(pitchInput, turnInput, throttleInput);
    }

    /// <summary>
    /// Runs the ordered termination ladder: crash -> touchdown -> stopped ->
    /// waypoint -> flyaway -> underground -> takeoff timeout.
    /// The order is semantic — earlier outcomes suppress later ones.
    /// May pay terminal rewards, advance waypoints/phases, and end the episode
    /// (which synchronously runs OnEpisodeBegin).
    /// </summary>
    /// 
    /// <returns>
    /// True if this decision step must stop immediately: the episode ended or
    /// the target changed, so shaping rewards and the previousDistance update
    /// no longer apply to a valid state. False if flight continues normally.
    /// </returns>
    bool CheckTerminations(float distanceToTarget, float offCenter)
    {
        Vector3 flatfFwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
        Vector3 flatRwy = Vector3.ProjectOnPlane(runway.forward, Vector3.up).normalized;

        float alignment = Vector3.Dot(flatfFwd, flatRwy);
        float aligned = Mathf.Clamp01((alignment - 0.9f) / 0.1f);
        float gentle = 1f - Mathf.Clamp01(planeController.touchdownImpact / 8f);

        bool patternFix = phase == Phase.Landing && Mathf.Abs(runway.InverseTransformPoint(Target.position).x) > 1f;
        bool finalFix = Mathf.Abs(runway.InverseTransformPoint(Target.position).x) < 1f;

        // Crash
        if (planeController.agentCrashed)
        {
            SetReward(-1.0f);
            RecordOutcome(crash: true);
            LogEnd("CRASH");
            EndEpisode();
            return true;
        }
        // Touchdown
        else if (phase == Phase.Landing && planeController.touchedDown && !touchdownHandled)
        {
            touchdownHandled = true;
            // Runway
            if (planeController.touchdownOnRunway)
            {
                LogEnd("RUNWAY TOUCHDOWN");
                AddReward(1.0f + 0.5f * gentle + 0.5f * aligned);
                return false;
            }
            else
            // Grass
            {
                SetReward(-0.2f);
                RecordOutcome(grass: true);
                LogEnd("GRASS");
                EndEpisode();
                return true;
            }
        }
        // Stop
        else if (phase == Phase.Landing && touchdownHandled && planeController.currentSpeed < 0.5f)
        {
            AddReward(1.0f + 0.5f * aligned);
            RecordOutcome(success: true);
            LogEnd("STOPPED - SUCCESS");
            EndEpisode();

            return true;
        }
        // Waypoint
        else if (distanceToTarget < (patternFix ? 10f : 5f))
        {
            if (phase == Phase.Landing)
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
                
                OnWaypointReached();
            }
            return true;
        }
        // Flyaway
        else if (distanceToTarget > allowedRadius)
        {
            SetReward(-1.0f);
            RecordOutcome(flyaway: true);
            LogEnd("FLYWAY");
            EndEpisode();
            return true;
        }
        // Underground
        else if (transform.localPosition.y < -2f)
        {
            SetReward(-1.0f);
            RecordOutcome(crash: true);
            LogEnd("CRASH");
            EndEpisode();
            return true;
        }
        // Takeoff timeout
        else if (phase == Phase.Takeoff && !airborneRewardGiven && decisionCount > 500)
        {
            SetReward(-1.0f);
            RecordOutcome(timeout: true);
            LogEnd("TIMEOUT");
            EndEpisode();
            return true;
        }
        return false;
    }

    void OnWaypointReached()
    {
        // Phase transition
        if (phase == Phase.Takeoff)
        {
            if (isChainedEpisode)
            {
                // Takeoff -> Cruise
                phase = Phase.Cruise;
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
            phase = Phase.Landing;
            approach = planner.BuildApproach(transform.position);
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

    void AddTakeoffShaping(float offCenter)
    {
        if (phase == Phase.Takeoff)
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
    }

    void AddTrackingShaping(float distanceToTarget)
    {
        Vector3 toTarget = (Target.position - transform.position).normalized;
        if (!touchdownHandled)
        {
            AddReward(0.001f * Vector3.Dot(rBody.linearVelocity.normalized, toTarget.normalized));
            AddReward(0.01f * (previousDistance - distanceToTarget));
        }
    }

    void LogEnd(string outcome)
    {
        string mode = phase.ToString();
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