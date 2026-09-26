using System;
using System.Collections.Generic;

namespace Godspeed.Transmission.Core;

/// <summary>Values supplied by a simulator adapter for one evaluation step.</summary>
/// <remarks>
/// Speed is in metres per second; RPM values are revolutions per minute; throttle,
/// brake and clutch are normalized to 0..1. ElapsedSeconds is monotonic time since
/// the previous evaluation, not a wall-clock timestamp.
/// </remarks>
public readonly record struct TelemetryInput(
    float SpeedMetresPerSecond,
    float EngineRpm,
    float ThrottleLoad,
    int CurrentGear,
    float Clutch,
    float ElapsedSeconds,
    float EngineRpmMax = 0f,
    float EngineRpmIdle = 0f,
    float PoweredWheelRadiusMetres = 0f,
    float LongitudinalAcceleration = 0f,
    float BrakeDemand = 0f,
    bool LaunchRequested = false);

public enum ShiftCommand
{
    None,
    Upshift,
    Downshift
}

/// <summary>Immutable, simulator-independent engine and gearbox configuration.</summary>
public sealed class EngineProfile
{
    private readonly float[] forwardGearRatios;
    private readonly IReadOnlyList<float> readonlyRatios;

    public EngineProfile(float rpmLimit, float idleRpm, float powerBandLowRpm,
        float powerBandHighRpm, float differentialRatio, IReadOnlyList<float> forwardGearRatios)
    {
        if (!float.IsFinite(rpmLimit) || rpmLimit <= 0f)
            throw new ArgumentOutOfRangeException(nameof(rpmLimit));
        if (!float.IsFinite(idleRpm) || idleRpm < 0f)
            throw new ArgumentOutOfRangeException(nameof(idleRpm));
        if (!float.IsFinite(powerBandLowRpm) || powerBandLowRpm < 0f)
            throw new ArgumentOutOfRangeException(nameof(powerBandLowRpm));
        if (!float.IsFinite(powerBandHighRpm) || powerBandHighRpm <= 0f
            || powerBandHighRpm < powerBandLowRpm)
            throw new ArgumentOutOfRangeException(nameof(powerBandHighRpm));
        if (!float.IsFinite(differentialRatio) || differentialRatio <= 0f)
            throw new ArgumentOutOfRangeException(nameof(differentialRatio));
        ArgumentNullException.ThrowIfNull(forwardGearRatios);
        if (forwardGearRatios.Count == 0)
            throw new ArgumentException("At least one forward ratio is required.", nameof(forwardGearRatios));

        this.forwardGearRatios = new float[forwardGearRatios.Count];
        for (int index = 0; index < forwardGearRatios.Count; index++)
        {
            float ratio = forwardGearRatios[index];
            if (!float.IsFinite(ratio) || ratio <= 0f)
                throw new ArgumentException($"Forward ratio {index + 1} must be positive and finite.",
                    nameof(forwardGearRatios));
            this.forwardGearRatios[index] = ratio;
        }

        readonlyRatios = Array.AsReadOnly(this.forwardGearRatios);
        RpmLimit = rpmLimit;
        IdleRpm = idleRpm;
        PowerBandLowRpm = powerBandLowRpm;
        PowerBandHighRpm = powerBandHighRpm;
        DifferentialRatio = differentialRatio;
    }

    public float RpmLimit { get; }
    public float IdleRpm { get; }
    public float PowerBandLowRpm { get; }
    public float PowerBandHighRpm { get; }
    public float DifferentialRatio { get; }
    public int ForwardGearCount => forwardGearRatios.Length;
    public IReadOnlyList<float> ForwardGearRatios => readonlyRatios;

    internal float ReductionForGear(int gear)
        => gear >= 1 && gear <= forwardGearRatios.Length
            ? forwardGearRatios[gear - 1] * DifferentialRatio : float.NaN;

    internal float RatioForGear(int gear)
        => gear >= 1 && gear <= forwardGearRatios.Length
            ? forwardGearRatios[gear - 1] : float.NaN;
}

public readonly record struct PowerBand(float LowerRpm, float DownshiftRpm, float UpshiftRpm);

/// <summary>Explicit state for the pure shift-evaluation overload.</summary>
public readonly record struct TransmissionState(
    float CooldownRemainingSeconds,
    int ExpectedGear,
    float ConfirmationRemainingSeconds);

/// <summary>
/// Deterministic shift policy. The static overload has no external effects; an
/// adapter decides how to execute the returned button pulse and confirms it via
/// the next telemetry samples. The instance overload only stores timing state.
/// </summary>
public sealed class TransmissionController
{
    private const float ShiftCooldownSeconds = .5f;
    private const float GearConfirmationSeconds = 2f;
    private const float StandstillSpeedMetresPerSecond = .15f;
    private const float NeutralLaunchSpeedMetresPerSecond = 2f / 3.6f;
    private const float LowSpeedTractionMetresPerSecond = 5f / 3.6f;
    private TransmissionState state;

    public TransmissionController(EngineProfile profile)
        => Profile = profile ?? throw new ArgumentNullException(nameof(profile));

    public EngineProfile Profile { get; }
    public TransmissionState State => state;

    public void Reset() => state = default;

    public ShiftCommand EvaluateShift(TelemetryInput currentTelemetry)
    {
        ShiftCommand command = EvaluateShift(currentTelemetry, Profile, state, out TransmissionState nextState);
        state = nextState;
        return command;
    }

    /// <summary>
    /// Pure core: identical input, profile and prior state produce identical
    /// command and next state. No clock, event bus, logger or controller I/O.
    /// </summary>
    public static ShiftCommand EvaluateShift(TelemetryInput telemetry, EngineProfile profile,
        TransmissionState priorState, out TransmissionState nextState)
    {
        ArgumentNullException.ThrowIfNull(profile);

        float elapsed = float.IsFinite(telemetry.ElapsedSeconds)
            ? Math.Max(0f, telemetry.ElapsedSeconds) : 0f;
        float cooldown = Math.Max(0f, priorState.CooldownRemainingSeconds - elapsed);
        float confirmation = Math.Max(0f, priorState.ConfirmationRemainingSeconds - elapsed);
        int expectedGear = priorState.ExpectedGear;
        if (expectedGear != 0 && (telemetry.CurrentGear == expectedGear || confirmation <= 0f))
        {
            expectedGear = 0;
            confirmation = 0f;
        }
        nextState = new TransmissionState(cooldown, expectedGear, confirmation);

        if (expectedGear != 0 || cooldown > 0f
            || !float.IsFinite(telemetry.SpeedMetresPerSecond)
            || !float.IsFinite(telemetry.EngineRpm)
            || !float.IsFinite(telemetry.ThrottleLoad)
            || !float.IsFinite(telemetry.Clutch)
            || telemetry.EngineRpm <= 0f
            || telemetry.CurrentGear < 0
            || telemetry.CurrentGear > profile.ForwardGearCount)
            return ShiftCommand.None;

        float speed = Math.Abs(telemetry.SpeedMetresPerSecond);
        float throttle = Math.Clamp(telemetry.ThrottleLoad, 0f, 1f);
        float clutch = Math.Clamp(telemetry.Clutch, 0f, 1f);
        float brake = float.IsFinite(telemetry.BrakeDemand)
            ? Math.Clamp(telemetry.BrakeDemand, 0f, 1f) : 0f;
        float acceleration = float.IsFinite(telemetry.LongitudinalAcceleration)
            ? telemetry.LongitudinalAcceleration : 0f;
        float liveLimit = float.IsFinite(telemetry.EngineRpmMax) && telemetry.EngineRpmMax > 0f
            ? Math.Min(profile.RpmLimit, telemetry.EngineRpmMax) : profile.RpmLimit;
        float idleRpm = float.IsFinite(telemetry.EngineRpmIdle) && telemetry.EngineRpmIdle > 0f
            ? telemetry.EngineRpmIdle : profile.IdleRpm;

        if (telemetry.CurrentGear == 0)
        {
            if (speed < NeutralLaunchSpeedMetresPerSecond
                && (throttle > .05f || telemetry.LaunchRequested) && brake <= .3f)
                return Issue(ShiftCommand.Upshift, 1, out nextState);
            return ShiftCommand.None;
        }

        if (clutch > .5f && speed > StandstillSpeedMetresPerSecond)
            return ShiftCommand.None;

        int gear = telemetry.CurrentGear;
        if (gear > 1 && speed <= StandstillSpeedMetresPerSecond)
        {
            float spinGuardRpm = Math.Min(liveLimit * .70f, idleRpm + 400f);
            if (clutch <= .5f && throttle > .50f && telemetry.EngineRpm > spinGuardRpm)
                return ShiftCommand.None;

            float landing = PredictDownshiftLandingRpm(telemetry, profile, gear, gear - 1);
            if (IsApprovedDownshift(landing, profile))
                return Issue(ShiftCommand.Downshift, gear - 1, out nextState);
            return ShiftCommand.None;
        }

        bool heavyBraking = brake > .3f && acceleration < -1.5f;
        if (gear > 1 && speed < LowSpeedTractionMetresPerSecond
            && throttle > .80f && !heavyBraking)
        {
            float landing = PredictDownshiftLandingRpm(telemetry, profile, gear, gear - 1);
            if (IsApprovedDownshift(landing, profile))
                return Issue(ShiftCommand.Downshift, gear - 1, out nextState);
            return ShiftCommand.None;
        }

        PowerBand band = CalculatePowerBand(profile, throttle, liveLimit);
        if (gear > 1 && (heavyBraking
            || (telemetry.EngineRpm < band.DownshiftRpm && acceleration < -.25f)))
        {
            float landing = PredictDownshiftLandingRpm(telemetry, profile, gear, gear - 1);
            if (IsApprovedDownshift(landing, profile))
                return Issue(ShiftCommand.Downshift, gear - 1, out nextState);
            return ShiftCommand.None;
        }

        if (heavyBraking || gear >= profile.ForwardGearCount || throttle < .15f)
            return ShiftCommand.None;

        float ratioStep = profile.RatioForGear(gear) / profile.RatioForGear(gear + 1);
        bool wideRatioLaunchShift = gear == 1 && ratioStep > 1.55f
            && telemetry.EngineRpm >= liveLimit * .82f;
        if (telemetry.EngineRpm < band.UpshiftRpm && !wideRatioLaunchShift)
            return ShiftCommand.None;

        float predictedUpshiftRpm = PredictUpshiftLandingRpm(telemetry, profile, gear, gear + 1);
        if (!float.IsFinite(predictedUpshiftRpm) || predictedUpshiftRpm < band.LowerRpm)
            return ShiftCommand.None;
        return Issue(ShiftCommand.Upshift, gear + 1, out nextState);
    }

    public static PowerBand CalculatePowerBand(EngineProfile profile, float throttle,
        float liveRpmLimit = 0f)
    {
        ArgumentNullException.ThrowIfNull(profile);
        float limit = float.IsFinite(liveRpmLimit) && liveRpmLimit > 0f
            ? Math.Min(profile.RpmLimit, liveRpmLimit) : profile.RpmLimit;
        float throttleIntent = float.IsFinite(throttle) ? Math.Clamp(throttle, 0f, 1f) : 0f;
        float lowerRpm = profile.PowerBandLowRpm > 0f
            ? profile.PowerBandLowRpm : Math.Max(700f, profile.PowerBandHighRpm * .70f);
        float floorRpm = lowerRpm - 50f;
        float upshiftRpm = throttleIntent > .80f
            ? Math.Min(limit * .94f, profile.PowerBandHighRpm + 250f)
            : floorRpm + (profile.PowerBandHighRpm - floorRpm) * throttleIntent;
        upshiftRpm = Math.Min(upshiftRpm, limit * .94f);
        return new PowerBand(floorRpm, lowerRpm - 20f, upshiftRpm);
    }

    public static float PredictUpshiftLandingRpm(TelemetryInput telemetry,
        EngineProfile profile, int currentGear, int targetGear)
    {
        ArgumentNullException.ThrowIfNull(profile);
        float currentReduction = profile.ReductionForGear(currentGear);
        float targetReduction = profile.ReductionForGear(targetGear);
        if (!float.IsFinite(currentReduction) || !float.IsFinite(targetReduction)
            || currentReduction <= 0f || targetReduction <= 0f
            || !float.IsFinite(telemetry.EngineRpm))
            return float.NaN;
        return telemetry.EngineRpm * (targetReduction / currentReduction);
    }

    public static float PredictDownshiftLandingRpm(TelemetryInput telemetry,
        EngineProfile profile, int currentGear, int targetGear)
    {
        ArgumentNullException.ThrowIfNull(profile);
        float currentReduction = profile.ReductionForGear(currentGear);
        float targetReduction = profile.ReductionForGear(targetGear);
        if (!float.IsFinite(currentReduction) || !float.IsFinite(targetReduction)
            || currentReduction <= 0f || targetReduction <= 0f
            || !float.IsFinite(telemetry.SpeedMetresPerSecond))
            return float.NaN;

        float speed = Math.Abs(telemetry.SpeedMetresPerSecond);
        float idleRpm = float.IsFinite(telemetry.EngineRpmIdle) && telemetry.EngineRpmIdle > 0f
            ? telemetry.EngineRpmIdle : profile.IdleRpm;
        if (speed <= StandstillSpeedMetresPerSecond)
            return Math.Max(0f, idleRpm);
        if (!float.IsFinite(telemetry.PoweredWheelRadiusMetres)
            || telemetry.PoweredWheelRadiusMetres <= .1f)
            return float.NaN;

        float wheelRpm = speed / (2f * MathF.PI * telemetry.PoweredWheelRadiusMetres) * 60f;
        return Math.Max(idleRpm, wheelRpm * targetReduction);
    }

    private static bool IsApprovedDownshift(float predictedLandingRpm, EngineProfile profile)
        => float.IsFinite(predictedLandingRpm) && predictedLandingRpm >= 0f
            && predictedLandingRpm <= profile.RpmLimit * .98f;

    private static ShiftCommand Issue(ShiftCommand command, int expectedGear,
        out TransmissionState nextState)
    {
        nextState = new TransmissionState(ShiftCooldownSeconds, expectedGear,
            GearConfirmationSeconds);
        return command;
    }
}
