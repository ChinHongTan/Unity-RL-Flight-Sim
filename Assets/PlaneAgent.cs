using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;

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
    int decisionCount;
    bool hitSpeed10;
    bool hitSpeed20;
    bool hitSpeed30;
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
            Academy.Instance.StatsRecorder.Add("Custom/TargetsPerEpisode", targetsThisEpisode);
        if (!outcomeRecorded)
            RecordOutcome(timeout: true);

        outcomeRecorded = false;
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
        
        float groundedProb = Academy.Instance.EnvironmentParameters.GetWithDefault("grounded_prob", 0.3f);
        float landingProb = Academy.Instance.EnvironmentParameters.GetWithDefault("landing_prob", 0.4f);
        
        float r = Random.value;
        takeoff = r < groundedProb;
        landing = !takeoff && r < groundedProb + landingProb;

        if (takeoff)
            planeController.RespawnAt(new Vector3(0f, 0f, 0f), true, 0);
        else if (landing)
            SpawnForLanding();
        else
            planeController.RespawnAt(new Vector3(0f, 30f, 0f), false, 0);

        firstTargetOfEpisode = true;
        wasGrounded = takeoff;

        if (landing)
        {
            Target.position = ApproachPoint(80f);
            previousDistance = Vector3.Distance(transform.localPosition, Target.localPosition);
        }
        else if (takeoff)
        {
            Target.position = DeparturePoint(100f);
            previousDistance = Vector3.Distance(transform.localPosition, Target.localPosition);
            firstTargetOfEpisode = false;
        }
        else
            SpawnNewTarget();
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

        float distanceToTarget = Vector3.Distance(this.transform.localPosition, Target.localPosition);
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
        else if (distanceToTarget < 5f)
        {
            if (landing)
            {
                if (waypointIndex < 4)
                {
                    AddReward(0.3f * (1f - Mathf.Clamp01(offCenter / 7f)));
                    targetsThisEpisode++;
                    waypointIndex++;
                    Target.position = ApproachPoint((4 - waypointIndex) * 20f);
                    previousDistance = Vector3.Distance(transform.localPosition, Target.localPosition);
                    Debug.Log($"waypoint {waypointIndex} reached");
                }
            }
            else
            {
                AddReward(1.0f);
                RecordOutcome(success: true);
                targetsThisEpisode++;
                LogEnd("SUCCESS");
                SpawnNewTarget();
            }
            return;
        }
        else if (distanceToTarget > 200f)
        {
            SetReward(-1.0f);
            RecordOutcome(flyaway: true);
            LogEnd("FLYWAY");
            EndEpisode();
            return;
        }
        else if (this.transform.localPosition.y < -2f)
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
        outcomeRecorded = true;
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

        Vector3 pos = threshold + dir * distOut + Vector3.up * (distOut * slope);

        Vector3 toFirst = ApproachPoint(80f) - pos;
        float yawDeg = Mathf.Atan2(toFirst.x, toFirst.z) * Mathf.Rad2Deg;

        planeController.RespawnAt(transform.parent.InverseTransformPoint(pos), false, yawDeg);
    }

    void GetStage()
    {
        if (!firstTargetOfEpisode)
        {
            takeoff = false;
            return;
        }
        if (targetsThisEpisode >= 10)
        {
            landing = true;
            return;
        }
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
            $"touchedDown {planeController.touchedDown}   wp {waypointIndex}/4\n" +
            $"distToTarget {Vector3.Distance(transform.position, Target.position):F1}   " +
            $"reward {GetCumulativeReward():F2}");
    }
}