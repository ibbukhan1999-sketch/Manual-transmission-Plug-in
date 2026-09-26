using ETS2LA.Shared;

namespace Godspeed.Shared;

/// <summary>Registers Godspeed's shared types in ETS2LA's main load context.</summary>
public sealed class GodspeedLibrary : LibraryPlugin
{
    public override PluginInformation Info => new()
    {
        Id = "godspeed.shared",
        Name = "Godspeed Shared",
        Description = "Shared radar and driving-intent data for Godspeed plugins",
        AuthorName = "the Godspeed",
        Version = typeof(GodspeedLibrary).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
        SupportedETS2LA = ">=2026.9.5026"
    };
}
