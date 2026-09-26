using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using Microsoft.Win32;

namespace Godspeed.Radar;

public readonly record struct RadarEntityGeometry(Vector3 RelativePosition,
    Quaternion Rotation, Vector3 BoundsMin, Vector3 BoundsMax);

public readonly record struct RadarEgoGeometry(double WorldX, double WorldY, double WorldZ,
    Quaternion Rotation, Vector3 BoundsMin, Vector3 BoundsMax, bool HasBounds);

public readonly record struct RadarVehicleSample(int PlayerId, string Name,
    float SignedDistanceMetres, float LateralDistanceMetres, float SpeedMetresPerSecond,
    float AbsoluteSpeedKmh, float HeadingAlignment, float SignedYawRadians,
    float RelativeHeightMetres, bool HasSignedYaw,
    RadarEntityGeometry? VehicleGeometry, bool TrailerPresent,
    RadarEntityGeometry? TrailerGeometry, RadarEgoGeometry? EgoGeometry, bool CanCollide,
    uint FrameSequence, DateTime ReceivedAt);

public sealed record RadarFrameSnapshot(uint Sequence, DateTime ReceivedAt,
    ImmutableArray<RadarVehicleSample> Vehicles);

// Compiled once into Godspeed.Shared in the default load context. Each plugin
// subscribes to the same receiver and immutable frame stream.
public sealed class GodspeedRadarFeed : IDisposable
{
    private static readonly object Sync = new();
    private static readonly object DeliverySync = new();
    private static readonly Dictionary<long, (Action<RadarFrameSnapshot> Frame,
        Action<Exception>? Error)> Subscribers = [];
    private static (Action<RadarFrameSnapshot> Frame, Action<Exception>? Error)[] subscriberSnapshot = [];
    private static SharedRadarReceiver? receiver;
    private static long nextSubscriberId;
    private static uint lastDeliveredSequence;
    private static DateTime lastDeliveredAt;
    private static bool haveDeliveredSequence;
    private readonly long subscriberId;
    private int disposed;

    public GodspeedRadarFeed(Action<RadarFrameSnapshot> onFrame,
        Action<Exception>? onError = null, CancellationToken lifecycle = default)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        if (lifecycle.IsCancellationRequested)
            throw new OperationCanceledException(lifecycle);
        lock (Sync)
        {
            receiver ??= new SharedRadarReceiver(Deliver, DeliverError);
            subscriberId = ++nextSubscriberId;
            Subscribers.Add(subscriberId, (onFrame, onError));
            subscriberSnapshot = Subscribers.Values.ToArray();
        }
    }

    public Task Completion
    {
        get
        {
            lock (Sync)
                return Subscribers.Count == 1 && Subscribers.ContainsKey(subscriberId)
                    ? receiver?.Completion ?? Task.CompletedTask : Task.CompletedTask;
        }
    }

    public int LocalPort
    {
        get { lock (Sync) return receiver?.LocalPort ?? 0; }
    }

    private static void Deliver(RadarFrameSnapshot frame)
    {
        lock (DeliverySync)
        {
            if (haveDeliveredSequence && frame.ReceivedAt - lastDeliveredAt <= TimeSpan.FromSeconds(2)
                && unchecked((int)(frame.Sequence - lastDeliveredSequence)) <= 0)
                return;
            haveDeliveredSequence = true;
            lastDeliveredSequence = frame.Sequence;
            lastDeliveredAt = frame.ReceivedAt;
            foreach (var subscriber in Volatile.Read(ref subscriberSnapshot))
            {
                try { subscriber.Frame(frame); }
                catch (Exception error)
                {
                    try { subscriber.Error?.Invoke(error); } catch { }
                }
            }
        }
    }

    private static void DeliverError(Exception error)
    {
        foreach (var subscriber in Volatile.Read(ref subscriberSnapshot))
            try { subscriber.Error?.Invoke(error); } catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        SharedRadarReceiver? stop = null;
        lock (Sync)
        {
            Subscribers.Remove(subscriberId);
            subscriberSnapshot = Subscribers.Values.ToArray();
            if (Subscribers.Count == 0)
            {
                stop = receiver;
                receiver = null;
            }
        }
        stop?.Dispose();
    }
}

internal sealed class SharedRadarReceiver : IDisposable
{
    private const int RegistrationPort = 48150;
    private const int LegacyPacketSize = 218;
    private const int GeometryPacketSize = 320;
    private const int EgoPacketSize = 388;
    private const uint PacketMagic = 0x36524450;
    private static readonly byte[] RegisterMessage = "GSR6"u8.ToArray();
    private static readonly byte[] UnregisterMessage = "GSR0"u8.ToArray();
    private static readonly IPEndPoint Server = new(IPAddress.Loopback, RegistrationPort);
    private readonly Action<RadarFrameSnapshot> onFrame;
    private readonly Action<Exception>? onError;
    private readonly Dictionary<uint, PendingFrame> pending = [];
    private readonly object frameLock = new();
    private readonly UdpClient client;
    private readonly CancellationTokenSource cancellation;
    private readonly Task receiverTask;
    private readonly Task registrationTask;
    private readonly Task maintenanceTask;
    private uint lastSequence;
    private bool haveSequence;
    private DateTime lastCommittedAt;
    private int disposed;

    public SharedRadarReceiver(Action<RadarFrameSnapshot> onFrame,
        Action<Exception>? onError = null, CancellationToken lifecycle = default)
    {
        this.onFrame = onFrame ?? throw new ArgumentNullException(nameof(onFrame));
        this.onError = onError;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifecycle);
        try
        {
            client = new UdpClient(AddressFamily.InterNetwork);
            client.ExclusiveAddressUse = false;
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        }
        catch
        {
            cancellation.Dispose();
            throw;
        }
        receiverTask = ReceiveLoop(cancellation.Token);
        registrationTask = RegisterLoop(cancellation.Token);
        maintenanceTask = MaintenanceLoop(cancellation.Token);
    }

    public Task Completion => Task.WhenAll(receiverTask, registrationTask, maintenanceTask);
    public int LocalPort => ((IPEndPoint)client.Client.LocalEndPoint!).Port;

    private async Task RegisterLoop(CancellationToken token)
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
            do { await client.SendAsync(RegisterMessage, Server, token).ConfigureAwait(false); }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception error) { onError?.Invoke(error); }
    }

    private async Task ReceiveLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                UdpReceiveResult packet = await client.ReceiveAsync(token).ConfigureAwait(false);
                if (!IPAddress.Loopback.Equals(packet.RemoteEndPoint.Address)
                    || packet.RemoteEndPoint.Port != RegistrationPort)
                    continue;
                try { ProcessPacket(packet.Buffer); }
                catch (Exception error) { Report(error); }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) when (token.IsCancellationRequested) { }
        catch (Exception error) { onError?.Invoke(error); }
    }

    private async Task MaintenanceLoop(CancellationToken token)
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(20));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                RadarFrameSnapshot? ready;
                lock (frameLock) ready = CommitReady(DateTime.UtcNow);
                if (ready is not null) Dispatch(ready);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { onError?.Invoke(error); }
    }

    private void ProcessPacket(byte[] bytes)
    {
        if (bytes.Length < LegacyPacketSize || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != PacketMagic)
            return;
        byte version = bytes[4];
        int expectedSize = version == 9 ? EgoPacketSize
            : version == 8 ? GeometryPacketSize
            : version is 6 or 7 ? LegacyPacketSize : 0;
        if (expectedSize == 0 || bytes.Length != expectedSize
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)) != expectedSize)
            return;
        bool hasSignedYaw = version >= 7;
        byte type = bytes[5];
        if (type is not (1 or 2)) return;
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        DateTime now = DateTime.UtcNow;
        RadarFrameSnapshot? ready;
        lock (frameLock)
        {
            if (haveSequence && now - lastCommittedAt > TimeSpan.FromSeconds(2))
            {
                // TruckersMP can restart independently of ETS2LA. Its native
                // frame counter then starts over; do not reject the new run.
                haveSequence = false;
                pending.Clear();
                lastCommittedAt = now;
            }
            if (haveSequence && !IsNewer(sequence, lastSequence)) return;
            if (!pending.TryGetValue(sequence, out PendingFrame? frame))
            {
                if (pending.Count >= 32) pending.Clear();
                frame = new PendingFrame(sequence, now);
                pending.Add(sequence, frame);
            }
            if (type == 1)
            {
                uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));
                if (count > 2048) { pending.Remove(sequence); return; }
                frame.ExpectedCount = count;
                frame.HeartbeatAt = now;
            }
            else
            {
                float distance = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(16));
                float lateral = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(20));
                float speed = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(24));
                float absolute = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(28));
                float heading = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(32));
                float signedYaw = hasSignedYaw
                    ? BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(36)) : 0f;
                float relativeHeight = hasSignedYaw
                    ? BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(40)) : 0f;
                if (!float.IsFinite(distance) || !float.IsFinite(lateral)
                    || !float.IsFinite(speed) || !float.IsFinite(absolute)
                    || !float.IsFinite(heading) || !float.IsFinite(signedYaw)
                    || !float.IsFinite(relativeHeight) || speed < 0 || absolute < 0) return;
                int id = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
                RadarEntityGeometry? vehicleGeometry = null;
                RadarEntityGeometry? trailerGeometry = null;
                RadarEgoGeometry? egoGeometry = null;
                bool trailerPresent = false;
                if (version >= 8)
                {
                    if (bytes[45] != 0 && TryReadGeometry(bytes, 48, out RadarEntityGeometry vehicle))
                        vehicleGeometry = vehicle;
                    trailerPresent = bytes[46] != 0;
                    if (trailerPresent && bytes[47] != 0
                        && TryReadGeometry(bytes, 100, out RadarEntityGeometry trailer))
                        trailerGeometry = trailer;
                }
                if (version == 9 && TryReadEgoGeometry(bytes, out RadarEgoGeometry ego))
                    egoGeometry = ego;
                ReadOnlySpan<byte> nameBytes = version >= 8 ? bytes.AsSpan(152, 168)
                    : version == 7 ? bytes.AsSpan(45, 173) : bytes.AsSpan(37, 181);
                int end = nameBytes.IndexOf((byte)0);
                if (end >= 0) nameBytes = nameBytes[..end];
                string name = Encoding.UTF8.GetString(nameBytes);
                frame.Vehicles[id] = new RadarVehicleSample(id,
                    string.IsNullOrWhiteSpace(name) ? "Unknown" : name,
                    distance, lateral, speed, absolute, heading, signedYaw,
                    relativeHeight, hasSignedYaw, vehicleGeometry, trailerPresent,
                    trailerGeometry, egoGeometry, bytes[hasSignedYaw ? 44 : 36] != 0,
                    sequence, now);
            }
            ready = CommitReady(now);
        }
        if (ready is not null) Dispatch(ready);
    }

    private static bool TryReadGeometry(ReadOnlySpan<byte> bytes, int offset,
        out RadarEntityGeometry geometry)
    {
        Vector3 relativePosition = new(
            BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 0)..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 4)..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 8)..]));
        Quaternion rotation = new(
            BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 16)..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 20)..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 24)..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 12)..]));
        Vector3 boundsMin = new(
            BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 28)..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 32)..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 36)..]));
        Vector3 boundsMax = new(
            BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 40)..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 44)..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 48)..]));
        float rotationLength = rotation.LengthSquared();
        if (!IsFinite(relativePosition) || !IsFinite(boundsMin)
            || !IsFinite(boundsMax) || !float.IsFinite(rotationLength)
            || rotationLength < .000001f || boundsMax.X <= boundsMin.X
            || boundsMax.Y <= boundsMin.Y || boundsMax.Z <= boundsMin.Z)
        {
            geometry = default;
            return false;
        }
        geometry = new RadarEntityGeometry(relativePosition, Quaternion.Normalize(rotation),
            boundsMin, boundsMax);
        return true;
    }

    private static bool TryReadEgoGeometry(ReadOnlySpan<byte> bytes,
        out RadarEgoGeometry geometry)
    {
        double x = BitConverter.Int64BitsToDouble(
            BinaryPrimitives.ReadInt64LittleEndian(bytes[320..]));
        double y = BitConverter.Int64BitsToDouble(
            BinaryPrimitives.ReadInt64LittleEndian(bytes[328..]));
        double z = BitConverter.Int64BitsToDouble(
            BinaryPrimitives.ReadInt64LittleEndian(bytes[336..]));
        Quaternion rotation = new(
            BinaryPrimitives.ReadSingleLittleEndian(bytes[348..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[352..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[356..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[344..]));
        Vector3 min = new(
            BinaryPrimitives.ReadSingleLittleEndian(bytes[360..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[364..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[368..]));
        Vector3 max = new(
            BinaryPrimitives.ReadSingleLittleEndian(bytes[372..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[376..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[380..]));
        bool hasBounds = bytes[384] != 0;
        float rotationLength = rotation.LengthSquared();
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)
            || !float.IsFinite((float)x) || !float.IsFinite((float)y)
            || !float.IsFinite((float)z) || !float.IsFinite(rotationLength)
            || rotationLength < .000001f
            || (hasBounds && (!IsFinite(min) || !IsFinite(max)
                || max.X <= min.X || max.Y <= min.Y || max.Z <= min.Z)))
        {
            geometry = default;
            return false;
        }
        geometry = new RadarEgoGeometry(x, y, z, Quaternion.Normalize(rotation),
            min, max, hasBounds);
        return true;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private void Dispatch(RadarFrameSnapshot frame)
    {
        try { onFrame(frame); }
        catch (Exception error) { Report(error); }
    }

    private void Report(Exception error)
    {
        try { onError?.Invoke(error); } catch { }
    }

    private RadarFrameSnapshot? CommitReady(DateTime now)
    {
        PendingFrame? newest = null;
        foreach (PendingFrame frame in pending.Values)
        {
            if (now - frame.FirstAt > TimeSpan.FromMilliseconds(500)) continue;
            if (frame.HeartbeatAt is not DateTime heartbeat) continue;
            if (frame.Vehicles.Count < frame.ExpectedCount
                && now - heartbeat < TimeSpan.FromMilliseconds(50)) continue;
            if (newest is null || IsNewer(frame.Sequence, newest.Sequence)) newest = frame;
        }
        if (newest is null)
        {
            foreach (uint stale in pending.Where(pair => now - pair.Value.FirstAt > TimeSpan.FromMilliseconds(500))
                         .Select(pair => pair.Key).ToArray()) pending.Remove(stale);
            return null;
        }
        lastSequence = newest.Sequence;
        haveSequence = true;
        lastCommittedAt = now;
        RadarFrameSnapshot snapshot = new(newest.Sequence, now,
            ImmutableArray.CreateRange(newest.Vehicles.Values));
        foreach (uint old in pending.Keys.Where(key => !IsNewer(key, lastSequence)).ToArray()) pending.Remove(old);
        return snapshot;
    }

    private static bool IsNewer(uint candidate, uint current)
        => unchecked((int)(candidate - current)) > 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { client.Send(UnregisterMessage, Server); } catch { }
        cancellation.Cancel();
        client.Dispose();
        _ = Completion.ContinueWith(_ => cancellation.Dispose(), TaskScheduler.Default);
    }

    private sealed class PendingFrame(uint sequence, DateTime firstAt)
    {
        public uint Sequence { get; } = sequence;
        public DateTime FirstAt { get; } = firstAt;
        public DateTime? HeartbeatAt { get; set; }
        public uint ExpectedCount { get; set; }
        public Dictionary<int, RadarVehicleSample> Vehicles { get; } = [];
    }
}

public static class RadarGeometryMath
{
    private const float FallbackVehicleLengthMetres = 16.5f;
    private const float PrimaryLaneHalfWidthMetres = 1.75f;

    public static bool TryGetPhysicalClearance(in RadarVehicleSample sample,
        out float clearanceMetres)
        => TryGetClearance(sample, false, out clearanceMetres, out _);

    public static bool TryGetPhysicalClearance(in RadarVehicleSample sample,
        out float clearanceMetres, out bool usedFallback)
        => TryGetClearance(sample, false, out clearanceMetres, out usedFallback);

    public static bool TryGetLaneClearance(in RadarVehicleSample sample,
        out float clearanceMetres, out bool usedFallback)
        => TryGetClearance(sample, true, out clearanceMetres, out usedFallback);

    private static bool TryGetClearance(in RadarVehicleSample sample, bool primaryLaneOnly,
        out float clearanceMetres, out bool usedFallback)
    {
        clearanceMetres = float.NaN;
        usedFallback = false;
        // A V9 ego pose is required even for the degraded placement fallback.
        if (sample.EgoGeometry is not RadarEgoGeometry ego)
            return false;

        if (!ego.HasBounds || sample.VehicleGeometry is null
            || (sample.TrailerPresent && sample.TrailerGeometry is null))
        {
            if (!float.IsFinite(sample.SignedDistanceMetres)) return false;
            clearanceMetres = sample.SignedDistanceMetres - FallbackVehicleLengthMetres;
            usedFallback = true;
            return float.IsFinite(clearanceMetres);
        }

        RadarEntityGeometry vehicle = sample.VehicleGeometry.Value;

        Vector3 forward = Vector3.Transform(-Vector3.UnitZ, ego.Rotation);
        forward.Y = 0f;
        if (!IsFinite(forward) || forward.LengthSquared() < .0001f)
            return false;
        forward = Vector3.Normalize(forward);

        float egoFront = float.NegativeInfinity;
        float targetRear = float.PositiveInfinity;
        ProjectBounds(ego.BoundsMin, ego.BoundsMax, ego.Rotation,
            Vector3.Zero, forward, ref egoFront, ref targetRear, projectFront: true);
        Vector3 right = new(-forward.Z, 0f, forward.X);
        if (primaryLaneOnly)
            ProjectInLane(vehicle, forward, right, ref targetRear);
        else
            ProjectBounds(vehicle.BoundsMin, vehicle.BoundsMax, vehicle.Rotation,
                vehicle.RelativePosition, forward, ref egoFront, ref targetRear,
                projectFront: false);
        if (sample.TrailerGeometry is RadarEntityGeometry trailer)
        {
            if (primaryLaneOnly)
                ProjectInLane(trailer, forward, right, ref targetRear);
            else
                ProjectBounds(trailer.BoundsMin, trailer.BoundsMax, trailer.Rotation,
                    trailer.RelativePosition, forward, ref egoFront, ref targetRear,
                    projectFront: false);
        }

        clearanceMetres = targetRear - egoFront;
        return float.IsFinite(clearanceMetres);
    }

    private static void ProjectInLane(in RadarEntityGeometry entity, Vector3 forward,
        Vector3 right, ref float nearestLongitudinal)
    {
        Span<float> lateral = stackalloc float[8];
        Span<float> longitudinal = stackalloc float[8];
        for (int index = 0; index < 8; index++)
        {
            Vector3 corner = new(
                (index & 1) == 0 ? entity.BoundsMin.X : entity.BoundsMax.X,
                (index & 2) == 0 ? entity.BoundsMin.Y : entity.BoundsMax.Y,
                (index & 4) == 0 ? entity.BoundsMin.Z : entity.BoundsMax.Z);
            Vector3 relative = Vector3.Transform(corner, entity.Rotation)
                + entity.RelativePosition;
            lateral[index] = Vector3.Dot(relative, right);
            longitudinal[index] = Vector3.Dot(relative, forward);
            if (MathF.Abs(lateral[index]) <= PrimaryLaneHalfWidthMetres)
                nearestLongitudinal = Math.Min(nearestLongitudinal, longitudinal[index]);
        }

        // Clip box edges against both lane borders. A rotated box can span the
        // lane without any original corner lying inside it.
        ReadOnlySpan<(int Start, int End)> edges =
        [
            (0, 1), (0, 2), (0, 4), (1, 3), (1, 5), (2, 3),
            (2, 6), (3, 7), (4, 5), (4, 6), (5, 7), (6, 7)
        ];
        ReadOnlySpan<float> boundaries =
            stackalloc float[2] { -PrimaryLaneHalfWidthMetres, PrimaryLaneHalfWidthMetres };
        foreach ((int start, int end) in edges)
        {
            float span = lateral[end] - lateral[start];
            if (MathF.Abs(span) < .000001f) continue;
            foreach (float boundary in boundaries)
            {
                float t = (boundary - lateral[start]) / span;
                if (t >= 0f && t <= 1f)
                    nearestLongitudinal = Math.Min(nearestLongitudinal,
                        longitudinal[start] + t * (longitudinal[end] - longitudinal[start]));
            }
        }
    }

    public static bool TryGetWorldOrigin(in RadarEgoGeometry ego,
        in RadarEntityGeometry entity, out Vector3 origin)
    {
        origin = new Vector3(
            (float)(ego.WorldX + entity.RelativePosition.X),
            (float)(ego.WorldY + entity.RelativePosition.Y),
            (float)(ego.WorldZ + entity.RelativePosition.Z));
        return IsFinite(origin);
    }

    private static void ProjectBounds(Vector3 min, Vector3 max, Quaternion rotation,
        Vector3 relativePosition, Vector3 forward, ref float egoFront,
        ref float targetRear, bool projectFront)
    {
        for (int index = 0; index < 8; index++)
        {
            Vector3 corner = new(
                (index & 1) == 0 ? min.X : max.X,
                (index & 2) == 0 ? min.Y : max.Y,
                (index & 4) == 0 ? min.Z : max.Z);
            float longitudinal = Vector3.Dot(
                Vector3.Transform(corner, rotation) + relativePosition, forward);
            if (projectFront)
                egoFront = Math.Max(egoFront, longitudinal);
            else
                targetRear = Math.Min(targetRear, longitudinal);
        }
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

public static class GodspeedRadarDeployment
{
    private const string ResourceName = "Godspeed.Native.PlayerRadar.dll";
    private const string MutexName = @"Global\GodspeedRadarDeployMutex";
    private const string GameFolder = "Euro Truck Simulator 2";

    public static void EnsureDeployed(Action<string, bool> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        try
        {
            using Stream? resource = typeof(GodspeedRadarDeployment).Assembly
                .GetManifestResourceStream(ResourceName);
            if (resource is null)
            {
                report("Embedded PlayerRadar.dll resource is unavailable; keeping the current radar hook.", true);
                return;
            }
            using MemoryStream payloadStream = new();
            resource.CopyTo(payloadStream);
            byte[] payload = payloadStream.ToArray();
            string expectedHash = Convert.ToHexString(SHA256.HashData(payload));

            using Mutex mutex = new(false, MutexName);
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                report("PlayerRadar deployment mutex timed out; keeping the current radar hook.", true);
                return;
            }
            try
            {
                string? pluginsDirectory = FindEts2PluginDirectory();
                if (pluginsDirectory is null)
                {
                    report("ETS2 installation could not be resolved from Steam or standard paths; radar deployment skipped.", true);
                    return;
                }
                string destination = Path.Combine(pluginsDirectory, "PlayerRadar.dll");
                if (File.Exists(destination) && HashFile(destination) == expectedHash)
                {
                    report($"PlayerRadar.dll already matches the embedded build at {destination}; no copy needed.", false);
                    return;
                }

                Directory.CreateDirectory(pluginsDirectory);
                string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (FileStream output = new(temporary, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None))
                    {
                        output.Write(payload);
                        output.Flush(true);
                    }
                    if (HashFile(temporary) != expectedHash)
                        throw new IOException("Extracted PlayerRadar.dll failed hash verification.");
                    File.Move(temporary, destination, overwrite: true);
                    report($"PlayerRadar.dll deployed to {destination}; restart ETS2 to load the new native hook.", false);
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (IOException error)
        {
            report($"PlayerRadar.dll could not be replaced (possibly in use by ETS2): {error.Message}; keeping the running hook.", true);
        }
        catch (UnauthorizedAccessException error)
        {
            report($"PlayerRadar.dll deployment lacks file or mutex permission: {error.Message}; keeping the running hook.", true);
        }
        catch (Exception error)
        {
            report($"PlayerRadar.dll deployment failed: {error.Message}; keeping the running hook.", true);
        }
    }

    private static string HashFile(string path)
    {
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static string? FindEts2PluginDirectory()
    {
        if (!OperatingSystem.IsWindows()) return null;
        HashSet<string> steamRoots = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam",
                    "SteamPath", null) is string steamPath)
                steamRoots.Add(steamPath);
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam",
                    "SteamExe", null) is string steamExe)
                steamRoots.Add(Path.GetDirectoryName(steamExe) ?? string.Empty);
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }

        string programFilesX86 = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            steamRoots.Add(Path.Combine(programFilesX86, "Steam"));
        string programFiles = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            steamRoots.Add(Path.Combine(programFiles, "Steam"));

        foreach (string root in steamRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            HashSet<string> libraries = new(StringComparer.OrdinalIgnoreCase) { root };
            string libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            try
            {
                if (File.Exists(libraryFile))
                {
                    string contents = File.ReadAllText(libraryFile);
                    foreach (System.Text.RegularExpressions.Match match in
                        System.Text.RegularExpressions.Regex.Matches(contents,
                            "\"path\"\\s*\"([^\"]+)\"",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            foreach (string library in libraries)
            {
                string gameRoot = Path.Combine(library, "steamapps", "common", GameFolder);
                if (File.Exists(Path.Combine(gameRoot, "bin", "win_x64", "eurotrucks2.exe")))
                    return Path.Combine(gameRoot, "bin", "win_x64", "plugins");
            }
        }
        return null;
    }
}
