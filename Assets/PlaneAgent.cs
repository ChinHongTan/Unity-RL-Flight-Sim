using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;

public class PlaneAgent : Agent
{
    Rigidbody rBody;
    PlaneController planeController;
    float throttleInput;
    float previousDistance;
    float rewardScale = 0.01f;
    bool startGrounded;
    bool firstTargetOfEpisode;
    int targetsThisEpisode;
    bool firstEpisode;
    public override void Initialize()
    {
        rBody = GetComponent<Rigidbody>();
        planeController = GetComponent<PlaneController>();
        planeController.useKeyboardInput = false;
        firstEpisode = true;
    }

    public Transform Target;
    public override void OnEpisodeBegin()
    {
        if (!firstEpisode)
        {
            Academy.Instance.StatsRecorder.Add("Custom/TargetsPerEpisode", targetsThisEpisode);
        }
        firstEpisode = false;
        
        targetsThisEpisode = 0;
        float groundedProb = Academy.Instance.EnvironmentParameters.GetWithDefault("grounded_prob", 1f);
        startGrounded = Random.value < groundedProb;

        if (startGrounded)
        {
            planeController.RespawnAt(new Vector3(0f, 0f, 0f), true);
        }
        else
        {
            planeController.RespawnAt(new Vector3(0f, 30f, 0f), false);
        }

        firstTargetOfEpisode = true;

        SpawnNewTarget();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // Target and Agent positions
        Vector3 toTarget = Target.position - transform.position;
        sensor.AddObservation(transform.InverseTransformDirection(toTarget.normalized));        // 3: bearing in body frame
        sensor.AddObservation(toTarget.magnitude / 100f);                                       // 1: distance
        sensor.AddObservation(transform.InverseTransformDirection(rBody.linearVelocity) / 60f); // 3
        sensor.AddObservation(transform.forward.y);                                             // 1: climb/dive
        sensor.AddObservation(transform.right.y);                                               // 1: bank
        sensor.AddObservation(planeController.throttle);                                        // 1
        sensor.AddObservation(transform.position.y / 60f);                                      // 1: altitude
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        float pitchInput = actions.ContinuousActions[0];
        float turnInput = actions.ContinuousActions[1];
        float throttleInput = actions.ContinuousActions[2];

        planeController.AgentControl(pitchInput, turnInput, throttleInput);

        float distanceToTarget = Vector3.Distance(this.transform.localPosition, Target.localPosition);
        if (planeController.agentCrashed)
        {
            SetReward(-1.0f);
            RecordOutcome(false, true, false);
            EndEpisode();
            return;
        }
        else if (distanceToTarget < 5f)
        {
            AddReward(1.0f);
            RecordOutcome(true, false, false);
            targetsThisEpisode++;
            SpawnNewTarget();
            return;
        }
        else if (distanceToTarget > 150f)
        {
            SetReward(-1.0f);
            RecordOutcome(false, false, true);
            EndEpisode();
            return;
        }
        else if (this.transform.localPosition.y < -2f)
        {
            SetReward(-1.0f);
            RecordOutcome(false, true, false);
            EndEpisode();
            return;
        }
        Vector3 toTarget = (Target.position - transform.position).normalized;
        AddReward(0.001f * Vector3.Dot(rBody.linearVelocity.normalized, toTarget.normalized));
        AddReward(rewardScale * (previousDistance - distanceToTarget));
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

        if (startGrounded && firstTargetOfEpisode)
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

    void RecordOutcome(bool success, bool crash, bool flyaway)
    {
        var stats = Academy.Instance.StatsRecorder;
        stats.Add("Custom/Success", success ? 1f : 0f);
        stats.Add("Custom/Crash", crash ? 1f : 0f);
        stats.Add("Custom/Flyaway", flyaway ? 1f : 0f);
    }
}