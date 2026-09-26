using System.Reflection;
using ETS2LA.Game.Output;

namespace Godspeed.Shared;

/// <summary>Godspeed channel operations implemented only with the stock GameOutput API.</summary>
public static class GodspeedOutputCompatibility
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo? LegacyAccessor = typeof(GameOutput).GetField("legacyAccessor", PrivateInstance);
    private static readonly FieldInfo? ModernAccessor = typeof(GameOutput).GetField("modernAccessor", PrivateInstance);

    public static bool IsSCSControlsConnected(GameOutput? output)
    {
        if (output is null || LegacyAccessor is null || ModernAccessor is null)
            return false;
        try
        {
            return LegacyAccessor.GetValue(output) is not null
                && ModernAccessor.GetValue(output) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void RemoveChannel(GameOutput? output, string channelId)
    {
        if (output is null || string.IsNullOrEmpty(channelId))
            return;
        // Null payload is the stock host's documented channel withdrawal contract.
        // Both values must be null; its handler removes an existing channel.
        lock (output.Channels)
        {
            // Stock OnControlEvent inserts a null-valued ghost if the channel
            // does not already exist, so do not publish an absent withdrawal.
            if (!output.Channels.ContainsKey(channelId))
                return;
            output.OnControlEvent(new ControlEvent
            {
                ChannelDefinition = new ControlChannelDefinition { Id = channelId },
                Properties = null!,
                Variables = null!
            });
        }
    }

    public static bool IsBrakingOrRetarding(GameOutput? output, string? excludedChannelId = null)
        => RequestedBrakeDemand(output, excludedChannelId) > .001f;

    // The stock host exposes channel requests, not its final mixed pedal value.
    // Inspecting them here preserves brake-aware plugin safety without a host hook.
    public static float RequestedBrakeDemand(GameOutput? output, string? excludedChannelId = null)
    {
        if (output is null)
            return 1f;
        try
        {
            float requested = 0f;
            lock (output.Channels)
            {
                foreach (ControlChannel channel in output.Channels.Values)
                {
                    if (channel.Definition.Id == excludedChannelId
                        || channel.LastUpdate.Elapsed.TotalSeconds > channel.Definition.Timeout
                        || channel.Variables is null)
                        continue;
                    ControlVariables values = channel.Variables;
                    if (values.abackward is float brake && float.IsFinite(brake))
                        requested = Math.Max(requested, Math.Clamp(Math.Abs(brake), 0f, 1f));
                    if (values.aforward is float acceleration && float.IsFinite(acceleration)
                        && acceleration < 0f)
                        requested = Math.Max(requested, Math.Clamp(-acceleration, 0f, 1f));
                    if (values.retarderup == true || values.retarder1 == true
                        || values.retarder2 == true || values.retarder3 == true
                        || values.retarder4 == true || values.retarder5 == true
                        || values.motorbrake == true || values.trailerbrake == true
                        || values.embrake == true)
                        requested = 1f;
                }
            }
            return requested;
        }
        catch (InvalidOperationException)
        {
            // The stock mixer does not synchronize its dictionary writer.
            // Fail closed rather than allow a flat-shift throttle during a race.
            return 1f;
        }
    }
}
