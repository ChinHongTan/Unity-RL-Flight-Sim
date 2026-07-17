using System.Collections;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class PlaneController : MonoBehaviour
{
    private Rigidbody rb;
    float pitch, yaw, roll;
    public float pitchSpeed = 60f;
    public float bankSpeed = 2f;
    public float gravity = 10f;
    public float maxBank = 60f;
    public float turnRate = 1.5f;
    public float currentSpeed;
    public float stallSpeed = 30f;
    public float throttleAccel = 1f;      // was 0.5 — full throttle in 1s instead of 2s
    public float enginePower = 6f;
    public float drag = 0.1f;
    public float throttle = 0f;
    public float stallPitchMultiplier = 15f;
    public float brakeStrength = 15f;
    public float rollingFriction = 1.5f;  // slow roll-to-a-stop when engine is off

    [Header("Inputs (keyboard by default)")]
    public bool useKeyboardInput = true;
    [HideInInspector] public float pitchInput;    // -1..1
    [HideInInspector] public float turnInput;     // -1..1
    [HideInInspector] public float throttleInput; // -1..1 (rate of throttle change)
    [HideInInspector] public bool touchedDown;
    [HideInInspector] public bool touchdownOnRunway;
    [HideInInspector] public float touchdownImpact;

    Vector3 startPos;
    float startYaw;
    public bool grounded;

    public GameObject explosionPrefab;
    public float respawnDelay = 2f;
    bool crashed;
    public bool agentCrashed;
    Renderer[] renderers;
    [SerializeField] LayerMask groundMask = ~0;

    float spawnGraceUntil;

    Vector3 groundNormal = Vector3.up;

    float currentSink;


    void Start()
    {
        rb = GetComponent<Rigidbody>();
        yaw = transform.eulerAngles.y;
        currentSpeed = 0f;
        startPos = transform.position;
        startYaw = transform.eulerAngles.y;

        renderers = GetComponentsInChildren<Renderer>();
    }

    // Read the keyboard in Update.
    void Update()
    {
        if (!useKeyboardInput || crashed) return;

        pitchInput = Input.GetAxis("Vertical");
        turnInput = Input.GetAxis("Horizontal");

        throttleInput = 0f;
        if (Input.GetKey(KeyCode.LeftShift)) throttleInput += 1f;
        if (Input.GetKey(KeyCode.LeftControl)) throttleInput -= 1f;
    }

    void CheckGrounded()
    {
        // look ahead one frame of fall so fast descents can't skip the window
        float lookahead = Mathf.Max(0.05f, -rb.linearVelocity.y * Time.fixedDeltaTime);
        Vector3 origin = transform.position + Vector3.up * 1.0f;

        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 1.0f + lookahead,
                            groundMask, QueryTriggerInteraction.Ignore)
            && !hit.collider.transform.IsChildOf(transform))
        {
            if (!grounded) HandleTouchdown(hit);   // rising edge = the moment of touchdown
            grounded = true;
            groundNormal = hit.normal;
        }
        else
        {
            grounded = false;
            groundNormal = Vector3.up;
        }
        Debug.DrawRay(origin, Vector3.down * (1.0f + lookahead), grounded ? Color.red : Color.yellow);
    }

    void HandleTouchdown(RaycastHit hit)
    {
        if (crashed || Time.time < spawnGraceUntil) return;

        float sinkSpeed = Mathf.Max(0f, -rb.linearVelocity.y);  // last frame's applied velocity
        float upright   = Vector3.Dot(transform.up, Vector3.up);
        bool  noseFirst = transform.forward.y < -0.5f;           // same -30° the old contact check implied

        bool safe = upright > 0.7f && sinkSpeed < 8f && !noseFirst;
        if (safe)
        {
            if (!touchedDown)
            {
                touchedDown = true;
                touchdownOnRunway = hit.collider.CompareTag("Runway");
                touchdownImpact = sinkSpeed;
            }
        }
        else
        {
            if (useKeyboardInput) StartCoroutine(CrashSequence(hit.point));
            else agentCrashed = true;
        }
    }

    void FixedUpdate()
    {
        if (crashed) return;

        CheckGrounded();

        // --- Stall: nose drops when too slow in the air ---
        float stallEffect = 0f;
        if (currentSpeed / stallSpeed < 1f && !grounded)
        {
            stallEffect = (1f - (currentSpeed / stallSpeed)) * stallPitchMultiplier * Time.deltaTime;
        }

        float controlAuthority = Mathf.Clamp01(currentSpeed / stallSpeed);

        // --- Rotation ---
        pitch += (pitchInput * controlAuthority * pitchSpeed * Time.deltaTime) + stallEffect;
        pitch = Mathf.Clamp(pitch, -80f, 80f);

        float targetRoll = -turnInput * maxBank;
        roll = Mathf.LerpAngle(roll, targetRoll, Time.deltaTime * bankSpeed);
        yaw += -roll * turnRate * Time.deltaTime;

        rb.MoveRotation(Quaternion.Euler(pitch, yaw, roll));

        // --- Gravity along the flight path ---
        // Only when airborne: on the ground the wheels carry the weight,
        // so pitching the nose up during the takeoff roll no longer bleeds speed.
        float slopeY = grounded ? 0f : transform.forward.y;
        currentSpeed += -gravity * slopeY * Time.deltaTime;
        currentSpeed = Mathf.Max(currentSpeed, 0f);

        // --- Throttle and engine ---
        float minThrottle = grounded ? -1f : 0f;
        throttle = Mathf.Clamp(throttle + throttleInput * throttleAccel * Time.deltaTime, minThrottle, 1.0f);
        currentSpeed += (throttle * enginePower - drag * currentSpeed) * Time.deltaTime;

        // --- Ground handling ---
        if (grounded)
        {
            pitch = Mathf.Clamp(pitch, -15f, 2f); // -15 = allowed rotation for takeoff, 2 = level

            float brakeDecel = throttle < 0f ? -throttle * brakeStrength : 0f;

            if (brakeDecel > 0f)
                currentSpeed -= brakeDecel * Time.deltaTime;
            else if (throttle <= 0.01f)
                currentSpeed -= rollingFriction * Time.deltaTime;

            currentSpeed = Mathf.Max(currentSpeed, 0f);
        }

        // --- Movement direction ---
        Vector3 moveDir = transform.forward;

        // The runway is a one-way barrier: block movement into the surface,
        // never movement away from it.
        if (grounded && Vector3.Dot(moveDir, groundNormal) < 0f)
        {
            moveDir = Vector3.ProjectOnPlane(moveDir, groundNormal);
            if (moveDir.sqrMagnitude > 0.001f) moveDir.Normalize();
            else moveDir = transform.forward; // pointing straight up
        }

        // --- Sink: insufficient lift below stall speed ---
        float sinkFactor = Mathf.Clamp01(1f - (currentSpeed / stallSpeed));
        float targetSink = grounded ? 0f : gravity * sinkFactor;
        currentSink = Mathf.MoveTowards(currentSink, targetSink, gravity * 2f * Time.deltaTime);

        Vector3 sinkVelocity = Vector3.down * currentSink;

        rb.linearVelocity = (moveDir * currentSpeed) + sinkVelocity;
    }

    void OnCollisionEnter(Collision collision)
    {
        if (crashed) return;
        if (Time.time < spawnGraceUntil) return;

        ContactPoint contact = collision.GetContact(0);
        float impactSpeed = Mathf.Abs(Vector3.Dot(collision.relativeVelocity, contact.normal));
        float upright = Vector3.Dot(transform.up, Vector3.up);
        bool noseFirst = Vector3.Dot(transform.forward, -contact.normal) > 0.5f;

        bool safeLanding = upright > 0.7f && impactSpeed < 8f && !noseFirst;
        if (safeLanding)
        {
            if (!touchedDown)
            {
                touchedDown = true;
                touchdownOnRunway = collision.gameObject.CompareTag("Runway");
                touchdownImpact = impactSpeed;
            }
            return;
        }

        if (useKeyboardInput)
        {
            StartCoroutine(CrashSequence(contact.point));
        }
        else
        {
            agentCrashed = true;
            return;
        }
    }

    IEnumerator CrashSequence(Vector3 impactPoint)
    {
        crashed = true;

        if (explosionPrefab)
        {
            GameObject fx = Instantiate(explosionPrefab, impactPoint, Quaternion.identity);
            Destroy(fx, 2f);
        }

        rb.linearVelocity = Vector3.zero;
        rb.isKinematic = true;
        foreach (var r in renderers) r.enabled = false;

        yield return new WaitForSeconds(respawnDelay);

        Respawn();

        foreach (var r in renderers) r.enabled = true;
        rb.isKinematic = false;
        crashed = false;
    }

    void Respawn()
    {
        pitch = 0f; roll = 0f; yaw = startYaw;
        currentSpeed = 0f;
        rb.position = startPos;
        rb.rotation = Quaternion.Euler(0f, startYaw, 0f);
        rb.linearVelocity = Vector3.zero;
        grounded = true;
        spawnGraceUntil = Time.time + 0.5f;
        throttle = 0f;

        pitchInput = 0f; turnInput = 0f; throttleInput = 0f;
    }

    public void RespawnAt(Vector3 position, bool startGrounded, float yawDeg)
    {
        pitch = 0f; roll = 0f; yaw = 0f;
        rb.transform.localPosition = position;
        rb.rotation = Quaternion.Euler(0f, yawDeg, 0f);
        this.yaw = yawDeg;
        rb.linearVelocity = Vector3.zero;
        spawnGraceUntil = Time.time + 0.5f;
        pitchInput = 0f; turnInput = 0f; throttleInput = 0f;
        agentCrashed = false;
        currentSink = 0f;
        crashed = false;
        this.grounded = startGrounded;
        currentSpeed = startGrounded ? 0f : 40f;
        throttle = startGrounded ? 0f : 0.7f;
        touchedDown = false;
        touchdownOnRunway = false;
        touchdownImpact = 0f;
    }

    public void AgentControl(float pitchInput, float turnInput, float throttleInput)
    {
        this.pitchInput = pitchInput;
        this.turnInput = turnInput;
        this.throttleInput = throttleInput;
    }
}