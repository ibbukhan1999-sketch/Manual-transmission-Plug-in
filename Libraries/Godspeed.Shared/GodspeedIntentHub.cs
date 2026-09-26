using System.Collections.Immutable;
using System.Numerics;

namespace Godspeed.Shared;

public readonly record struct GodspeedBreadcrumbSnapshot(
    int LeaderPlayerId, ImmutableArray<Vector3> Points, long PublishedAtTick);

public readonly record struct GodspeedIntentSnapshot(
    bool TollActive,
    float TollBrake,
    bool CruiseEmergency,
    float CruiseBrake,
    bool CruiseLaunchRequested,
    bool DrivelineOpen)
{
    // Additive properties preserve the original six-argument constructor for
    // already compiled plugins while exposing the new handshake to rebuilt ones.
    public bool TransmissionFresh { get; init; }
    public bool LaunchAccepted { get; init; }
    public bool GearEngaged { get; init; }
    public float FinalArbitratedBrake { get; init; }
    public bool TollEmergency { get; init; }
}

// Loaded once in the default assembly context from the top-level Libraries folder.
public static class GodspeedIntentHub
{
    private static readonly object Sync = new();
    private static long tollExpiresAt, cruiseExpiresAt, transmissionExpiresAt;
    private static bool tollActive, tollEmergency, cruiseEmergency, cruiseLaunchRequested;
    private static bool drivelineOpen, transmissionParticipating, launchAccepted, gearEngaged;
    private static float tollBrake, cruiseBrake, finalArbitratedBrake;
    private static int breadcrumbLeaderPlayerId;
    private static long breadcrumbsExpireAt, breadcrumbsPublishedAt;
    private static ImmutableArray<Vector3> breadcrumbs = ImmutableArray<Vector3>.Empty;
    private static long ftlSteeringExpiresAt;

    public static void PublishFtlSteering() 
    {
        lock (Sync)
            ftlSteeringExpiresAt = Environment.TickCount64 + 250;
    }

    public static bool FtlSteeringActive
    {
        get
        {
            lock (Sync)
                return ftlSteeringExpiresAt > Environment.TickCount64;
        }
    }

    // Primitive-returning mixer queries permit the host to bind optional
    // delegates without taking a compile-time dependency on this assembly.
    public static bool InhibitCruiseAcceleration
    {
        get
        {
            GodspeedIntentSnapshot intent = Read();
            return intent.DrivelineOpen || (intent.TransmissionFresh && !intent.GearEngaged);
        }
    }

    public static void ClearFtlSteering()
    {
        lock (Sync)
            ftlSteeringExpiresAt = 0;
    }

    public static void PublishBreadcrumbs(int leaderPlayerId, ReadOnlySpan<Vector3> points)
    {
        if (leaderPlayerId <= 0 || points.Length < 2)
        {
            ClearBreadcrumbs();
            return;
        }
        ImmutableArray<Vector3> snapshot = ImmutableArray.CreateRange(points.ToArray());
        lock (Sync)
        {
            breadcrumbLeaderPlayerId = leaderPlayerId;
            breadcrumbs = snapshot;
            breadcrumbsPublishedAt = Environment.TickCount64;
            breadcrumbsExpireAt = breadcrumbsPublishedAt + 250;
        }
    }

    public static GodspeedBreadcrumbSnapshot ReadBreadcrumbs()
    {
        lock (Sync)
            return breadcrumbsExpireAt > Environment.TickCount64
                ? new GodspeedBreadcrumbSnapshot(breadcrumbLeaderPlayerId,
                    breadcrumbs, breadcrumbsPublishedAt)
                : new GodspeedBreadcrumbSnapshot(0, ImmutableArray<Vector3>.Empty, 0);
    }

    public static void ClearBreadcrumbs()
    {
        lock (Sync)
        {
            breadcrumbLeaderPlayerId = 0;
            breadcrumbs = ImmutableArray<Vector3>.Empty;
            breadcrumbsExpireAt = breadcrumbsPublishedAt = 0;
        }
    }

    public static void PublishToll(bool active, float brake)
        => PublishToll(active, brake, false);

    public static void PublishToll(bool active, float brake, bool isEmergency)
    {
        lock (Sync)
        {
            tollActive = active;
            tollBrake = float.IsFinite(brake) ? Math.Clamp(brake, 0f, 1f) : 0f;
            tollEmergency = active && isEmergency;
            tollExpiresAt = Environment.TickCount64 + 250;
        }
    }

    public static void PublishCruise(float brake, bool emergency, bool launchRequested)
    {
        lock (Sync)
        {
            cruiseBrake = float.IsFinite(brake) ? Math.Clamp(brake, 0f, 1f) : 0f;
            cruiseEmergency = emergency;
            cruiseLaunchRequested = launchRequested;
            cruiseExpiresAt = Environment.TickCount64 + 250;
        }
    }

    public static void PublishTransmission(bool openDriveline)
        => PublishTransmission(openDriveline, false, false);

    public static void PublishTransmission(bool openDriveline, bool acceptedLaunch, bool confirmedGearEngaged)
    {
        lock (Sync)
        {
            transmissionParticipating = true;
            drivelineOpen = openDriveline;
            launchAccepted = acceptedLaunch;
            gearEngaged = confirmedGearEngaged && !openDriveline;
            transmissionExpiresAt = Environment.TickCount64 + 250;
        }
    }

    // The mixer is the sole writer of the final pedal demand. This is
    // diagnostic feedback, not a second controller channel.
    public static void PublishFinalArbitratedBrake(float brake)
    {
        lock (Sync)
            finalArbitratedBrake = float.IsFinite(brake) ? Math.Clamp(brake, 0f, 1f) : 0f;
    }

    public static GodspeedIntentSnapshot Read()
    {
        lock (Sync)
        {
            long now = Environment.TickCount64;
            bool tollFresh = tollExpiresAt > now;
            bool cruiseFresh = cruiseExpiresAt > now;
            bool transmissionFresh = transmissionExpiresAt > now;
            return new GodspeedIntentSnapshot(
                tollFresh && tollActive,
                tollFresh ? tollBrake : 0f,
                cruiseFresh && cruiseEmergency,
                cruiseFresh ? cruiseBrake : 0f,
                cruiseFresh && cruiseLaunchRequested,
                transmissionParticipating && (!transmissionFresh || drivelineOpen))
            {
                TransmissionFresh = transmissionFresh,
                LaunchAccepted = transmissionFresh && launchAccepted,
                GearEngaged = transmissionFresh && gearEngaged,
                FinalArbitratedBrake = finalArbitratedBrake,
                TollEmergency = tollFresh && tollEmergency
            };
        }
    }

    public static void ClearToll()
    {
        lock (Sync) { tollExpiresAt = 0; tollActive = false; tollBrake = 0f; tollEmergency = false; }
    }

    public static void ClearCruise()
    {
        lock (Sync)
        {
            cruiseExpiresAt = 0;
            cruiseEmergency = false;
            cruiseBrake = 0f;
            cruiseLaunchRequested = false;
        }
    }

    public static void ClearTransmission()
    {
        lock (Sync)
        {
            transmissionExpiresAt = 0;
            transmissionParticipating = false;
            drivelineOpen = true;
            launchAccepted = false;
            gearEngaged = false;
        }
    }
}
