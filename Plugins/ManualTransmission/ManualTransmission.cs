using System.IO.MemoryMappedFiles;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using ETS2LA.Backend.Events;
using ETS2LA.Game.Output;
using ETS2LA.Game.Telemetry;
using ETS2LA.Logging;
using ETS2LA.Overlay;
using ETS2LA.Shared;
using ETS2LA.State;
using Hexa.NET.ImGui;
using Godspeed.Diagnostics;
using Godspeed.Shared;

namespace Godspeed;

[SupportedOSPlatform("windows")]
public sealed class ManualTransmission : Plugin
{
    private static readonly GodspeedBuildVersion BuildVersion =
        GodspeedBuildVersion.Read(typeof(ManualTransmission).Assembly);
    private static readonly string VersionHeading =
        $"Manual Transmission v{BuildVersion.InformationalVersion.Split('+', 2)[0]}";
    private const string PluginId = "godspeed.manualtransmission";
    private const string StabilityCruiseControlChannelId = "godspeed.hsscc.throttle";
    private const string StockCruiseControlChannelId = "AdaptiveCruiseControl.Acceleration";
    private const string FlatShiftChannelId = $"{PluginId}.flatshift";
    private const float FlatShiftThrottle = .925f;
    private const float FlatShiftWeight = 1_000f;
    private const string ControllerMapName = @"Local\SCSControls";
    private const float ShiftPulseMilliseconds = 70f;
    private const float ShiftCooldownMilliseconds = 500f;
    private const float ConfirmedShiftCooldownMilliseconds = 180f;
    private const float StandstillSpeedMetresPerSecond = .15f;
    private const float TelemetryTimeoutMilliseconds = 1_000f;
    private const float AutomatedControlIntentTimeoutMilliseconds = 250f;
    private const float ConfigurationFullThrottle = .98f;
    private const float ConfigurationGearRatioTolerance = .05f;
    private const string CustomBrand = "Custom / Unspecified";
    private static readonly string[] EngineBrands =
        ["DAF", "Iveco", "MAN", "Mercedes", "Renault", "Scania", "Volvo", CustomBrand];
    public static IReadOnlyList<string> AvailableEngineBrands { get; } = Array.AsReadOnly(EngineBrands);
    // Raw zero flags from all 114 incomplete ETS2 engine records. These remain
    // separate from the playable fallback values used by the shifting engine.
    private readonly record struct IncompleteEngineRow(
        string Truck, string EngineId, int Horsepower, int Kw, int TorqueNm,
        int RpmLimit, int RpmRangeLow, int RpmRangeHigh);
    private static readonly IncompleteEngineRow[] IncompleteEngineRows =
    [
        new("iveco.hiway", "cur11_420.iveco.hiway.engine", 420, 309, 1900, 0, 1050, 1550),
        new("iveco.hiway", "cur11_460.iveco.hiway.engine", 460, 338, 2150, 0, 1050, 1500),
        new("iveco.hiway", "cur13_500.iveco.hiway.engine", 500, 368, 2300, 0, 1000, 1550),
        new("iveco.hiway", "cur13_560.iveco.hiway.engine", 560, 412, 2500, 0, 1000, 1575),
        new("iveco.hiway", "cur9_310.iveco.hiway.engine", 310, 228, 1300, 0, 1100, 1675),
        new("iveco.hiway", "cur9_330.iveco.hiway.engine", 330, 243, 1400, 0, 1100, 1675),
        new("iveco.hiway", "cur9_360.iveco.hiway.engine", 360, 265, 1650, 0, 1200, 1550),
        new("iveco.hiway", "cur9_400.iveco.hiway.engine", 400, 294, 1700, 0, 1200, 1650),
        new("iveco.stralis", "cur10_420.iveco.stralis.engine", 420, 309, 1900, 0, 1050, 1550),
        new("iveco.stralis", "cur10_450.iveco.stralis.engine", 450, 331, 2100, 0, 1050, 1550),
        new("iveco.stralis", "cur13_500.iveco.stralis.engine", 500, 368, 2300, 0, 1000, 1525),
        new("iveco.stralis", "cur13_560.iveco.stralis.engine", 560, 412, 2500, 0, 1000, 1575),
        new("iveco.stralis", "cur8_310.iveco.stralis.engine", 310, 228, 1300, 0, 1200, 1675),
        new("iveco.stralis", "cur8_330.iveco.stralis.engine", 330, 243, 1400, 0, 1080, 1655),
        new("iveco.stralis", "cur8_360.iveco.stralis.engine", 360, 265, 1500, 0, 1125, 1685),
        new("man.tgx", "d2066_235.man.tgx.engine", 320, 235, 1600, 0, 1000, 1400),
        new("man.tgx", "d2066_265.man.tgx.engine", 360, 265, 1800, 0, 1000, 1400),
        new("man.tgx", "d2066_294.man.tgx.engine", 400, 294, 1900, 0, 1000, 1400),
        new("man.tgx", "d2676_324.man.tgx.engine", 440, 324, 2100, 0, 1000, 1400),
        new("man.tgx", "d2676_353.man.tgx.engine", 480, 353, 2300, 0, 1000, 1400),
        new("man.tgx", "d2676_397.man.tgx.engine", 540, 397, 2500, 0, 1100, 1400),
        new("man.tgx", "d2868_500.man.tgx.engine", 680, 500, 3000, 0, 1100, 1400),
        new("man.tgx_euro6", "d1556_243.man.tgx_euro6.engine", 330, 243, 1600, 2100, 0, 0),
        new("man.tgx_euro6", "d1556_265.man.tgx_euro6.engine", 360, 265, 1700, 2000, 0, 0),
        new("man.tgx_euro6", "d1556_294.man.tgx_euro6.engine", 400, 294, 1800, 2000, 0, 0),
        new("man.tgx_euro6", "d2676_309.man.tgx_euro6.engine", 420, 309, 2100, 2100, 0, 0),
        new("man.tgx_euro6", "d2676_338.man.tgx_euro6.engine", 460, 343, 2300, 2100, 0, 0),
        new("man.tgx_euro6", "d2676_368.man.tgx_euro6.engine", 500, 373, 2500, 2100, 0, 0),
        new("man.tgx_euro6", "d3876_397.man.tgx_euro6.engine", 540, 403, 2700, 2100, 0, 0),
        new("man.tgx_euro6", "d3876_427.man.tgx_euro6.engine", 580, 427, 2900, 2100, 0, 0),
        new("man.tgx_euro6", "d3876_471a.man.tgx_euro6.engine", 640, 471, 3000, 2100, 0, 0),
        new("mercedes.actros", "1832ls.mercedes.actros.engine", 320, 235, 1650, 2000, 0, 0),
        new("mercedes.actros", "1836ls.mercedes.actros.engine", 360, 265, 1850, 2000, 0, 0),
        new("mercedes.actros", "1841ls.mercedes.actros.engine", 408, 300, 2000, 2000, 0, 0),
        new("mercedes.actros", "1844ls.mercedes.actros.engine", 435, 320, 2100, 2000, 0, 0),
        new("mercedes.actros", "1846ls.mercedes.actros.engine", 456, 335, 2200, 2000, 0, 0),
        new("mercedes.actros", "1848ls.mercedes.actros.engine", 476, 350, 2300, 2000, 0, 0),
        new("mercedes.actros", "1851ls.mercedes.actros.engine", 510, 375, 2400, 2000, 0, 0),
        new("mercedes.actros", "1855ls.mercedes.actros.engine", 551, 405, 2600, 2000, 0, 0),
        new("mercedes.actros", "1860ls.mercedes.actros.engine", 598, 440, 2800, 2000, 0, 0),
        new("mercedes.actros2014", "engine42.mercedes.actros2014.engine", 421, 310, 2100, 2300, 0, 0),
        new("mercedes.actros2014", "engine45.mercedes.actros2014.engine", 449, 330, 2200, 2300, 0, 0),
        new("mercedes.actros2014", "engine48.mercedes.actros2014.engine", 476, 350, 2300, 2300, 0, 0),
        new("mercedes.actros2014", "engine51.mercedes.actros2014.engine", 510, 375, 2500, 2300, 0, 0),
        new("mercedes.actros2014", "engine52.mercedes.actros2014.engine", 517, 380, 2600, 2300, 0, 0),
        new("mercedes.actros2014", "engine58.mercedes.actros2014.engine", 578, 425, 2800, 2300, 0, 0),
        new("mercedes.actros2014", "engine63.mercedes.actros2014.engine", 625, 460, 3000, 2300, 0, 0),
        new("renault.magnum", "dxi13_440.renault.magnum.engine", 440, 323, 2200, 0, 1020, 1400),
        new("renault.magnum", "dxi13_480.renault.magnum.engine", 480, 353, 2400, 0, 1030, 1400),
        new("renault.magnum", "dxi13_520.renault.magnum.engine", 520, 382, 2550, 0, 1050, 1430),
        new("renault.premium", "dxi11_380.renault.premium.engine", 380, 280, 1800, 0, 950, 1400),
        new("renault.premium", "dxi11_430.renault.premium.engine", 430, 316, 2040, 0, 950, 1400),
        new("renault.premium", "dxi11_460.renault.premium.engine", 460, 338, 2200, 0, 950, 1400),
        new("scania.r", "dc12_380.scania.r.engine", 380, 280, 1900, 0, 1100, 1400),
        new("scania.r", "dc12_420.scania.r.engine", 420, 309, 2100, 0, 1100, 1400),
        new("scania.r", "dc13_360.scania.r.engine", 360, 265, 1850, 0, 1000, 1300),
        new("scania.r", "dc13_400.scania.r.engine", 400, 294, 2100, 0, 1000, 1300),
        new("scania.r", "dc13_440.scania.r.engine", 440, 324, 2300, 0, 1000, 1300),
        new("scania.r", "dc13_440_2.scania.r.engine", 440, 324, 2300, 0, 1000, 1300),
        new("scania.r", "dc13_480.scania.r.engine", 480, 353, 2500, 0, 1000, 1300),
        new("scania.r", "dc13_480_2.scania.r.engine", 480, 353, 2500, 0, 1000, 1300),
        new("scania.r", "dc16_500.scania.r.engine", 500, 368, 2500, 0, 1000, 1350),
        new("scania.r", "dc16_560.scania.r.engine", 560, 412, 2700, 0, 1000, 1400),
        new("scania.r", "dc16_620.scania.r.engine", 620, 456, 3000, 0, 1000, 1400),
        new("scania.r", "dc16_730.scania.r.engine", 730, 537, 3500, 0, 1000, 1350),
        new("scania.r_2016", "dc13_370.scania.r_2016.engine", 370, 272, 1900, 0, 1000, 1300),
        new("scania.r_2016", "dc13_410.scania.r_2016.engine", 410, 302, 2150, 0, 1000, 1300),
        new("scania.r_2016", "dc13_420s.scania.r_2016.engine", 420, 309, 2300, 0, 900, 1280),
        new("scania.r_2016", "dc13_450.scania.r_2016.engine", 450, 331, 2350, 0, 1000, 1300),
        new("scania.r_2016", "dc13_460s.scania.r_2016.engine", 460, 338, 2500, 0, 900, 1290),
        new("scania.r_2016", "dc13_500.scania.r_2016.engine", 500, 368, 2550, 0, 1000, 1300),
        new("scania.r_2016", "dc13_500s.scania.r_2016.engine", 500, 368, 2650, 0, 900, 1320),
        new("scania.r_2016", "dc13_560s.scania.r_2016.engine", 560, 412, 2800, 0, 900, 1400),
        new("scania.r_2016", "dc16_520.scania.r_2016.engine", 520, 382, 2700, 0, 1000, 1300),
        new("scania.r_2016", "dc16_580.scania.r_2016.engine", 580, 427, 3000, 0, 950, 1350),
        new("scania.r_2016", "dc16_590.scania.r_2016.engine", 590, 434, 3050, 0, 925, 1350),
        new("scania.r_2016", "dc16_650.scania.r_2016.engine", 650, 478, 3300, 0, 950, 1350),
        new("scania.r_2016", "dc16_660.scania.r_2016.engine", 660, 485, 3300, 0, 950, 1400),
        new("scania.r_2016", "dc16_730.scania.r_2016.engine", 730, 537, 3500, 0, 1000, 1400),
        new("scania.r_2016", "dc16_770.scania.r_2016.engine", 770, 566, 3700, 0, 1000, 1450),
        new("scania.streamline", "dc12_380.scania.streamline.engine", 380, 280, 1900, 0, 1100, 1400),
        new("scania.streamline", "dc12_420.scania.streamline.engine", 420, 309, 2100, 0, 1100, 1400),
        new("scania.streamline", "dc13_360.scania.streamline.engine", 360, 265, 1850, 0, 1000, 1300),
        new("scania.streamline", "dc13_370.scania.streamline.engine", 370, 272, 1900, 0, 1000, 1300),
        new("scania.streamline", "dc13_400.scania.streamline.engine", 400, 294, 2100, 0, 1000, 1300),
        new("scania.streamline", "dc13_410.scania.streamline.engine", 410, 302, 2150, 0, 1000, 1300),
        new("scania.streamline", "dc13_440.scania.streamline.engine", 440, 324, 2300, 0, 1000, 1300),
        new("scania.streamline", "dc13_440_2.scania.streamline.engine", 440, 324, 2300, 0, 1000, 1300),
        new("scania.streamline", "dc13_450.scania.streamline.engine", 450, 331, 2350, 0, 1000, 1300),
        new("scania.streamline", "dc13_480.scania.streamline.engine", 480, 353, 2400, 0, 1000, 1300),
        new("scania.streamline", "dc13_480_2.scania.streamline.engine", 480, 353, 2500, 0, 1000, 1300),
        new("scania.streamline", "dc13_490.scania.streamline.engine", 490, 360, 2550, 0, 1000, 1300),
        new("scania.streamline", "dc16_500.scania.streamline.engine", 500, 368, 2500, 0, 1000, 1350),
        new("scania.streamline", "dc16_520.scania.streamline.engine", 520, 382, 2700, 0, 1000, 1300),
        new("scania.streamline", "dc16_560.scania.streamline.engine", 560, 412, 2700, 0, 1000, 1400),
        new("scania.streamline", "dc16_580.scania.streamline.engine", 580, 427, 2950, 0, 1000, 1300),
        new("scania.streamline", "dc16_620.scania.streamline.engine", 620, 456, 3000, 0, 1000, 1400),
        new("scania.streamline", "dc16_730.scania.streamline.engine", 730, 537, 3500, 0, 1000, 1350),
        new("scania.streamline", "dc16_730_2.scania.streamline.engine", 730, 537, 3500, 0, 1000, 1400),
        new("scania.s_2016", "dc13_370.scania.s_2016.engine", 370, 272, 1900, 0, 1000, 1300),
        new("scania.s_2016", "dc13_410.scania.s_2016.engine", 410, 302, 2150, 0, 1000, 1300),
        new("scania.s_2016", "dc13_420s.scania.s_2016.engine", 420, 309, 2300, 0, 900, 1280),
        new("scania.s_2016", "dc13_450.scania.s_2016.engine", 450, 331, 2350, 0, 1000, 1300),
        new("scania.s_2016", "dc13_460s.scania.s_2016.engine", 460, 338, 2500, 0, 900, 1290),
        new("scania.s_2016", "dc13_500.scania.s_2016.engine", 500, 368, 2550, 0, 1000, 1300),
        new("scania.s_2016", "dc13_500s.scania.s_2016.engine", 500, 368, 2650, 0, 900, 1320),
        new("scania.s_2016", "dc13_560s.scania.s_2016.engine", 560, 412, 2800, 0, 900, 1400),
        new("scania.s_2016", "dc16_520.scania.s_2016.engine", 520, 382, 2700, 0, 1000, 1300),
        new("scania.s_2016", "dc16_580.scania.s_2016.engine", 580, 427, 3000, 0, 950, 1350),
        new("scania.s_2016", "dc16_590.scania.s_2016.engine", 590, 434, 3050, 0, 925, 1350),
        new("scania.s_2016", "dc16_650.scania.s_2016.engine", 650, 478, 3300, 0, 950, 1350),
        new("scania.s_2016", "dc16_660.scania.s_2016.engine", 660, 485, 3300, 0, 950, 1400),
        new("scania.s_2016", "dc16_730.scania.s_2016.engine", 730, 537, 3500, 0, 1000, 1400),
        new("scania.s_2016", "dc16_770.scania.s_2016.engine", 770, 566, 3700, 0, 1000, 1450),
    ];
    private static readonly Dictionary<int, IncompleteEngineRow> IncompleteEnginesByProfile =
        BuildIncompleteEngineIndex();
    public static bool IsAiDrivenCatalogProfile(int index) =>
        IncompleteEnginesByProfile.ContainsKey(index);

    public static (bool RpmLimit, bool RpmRangeLow, bool RpmRangeHigh)
        GetMissingEngineFields(int index) =>
        IncompleteEnginesByProfile.TryGetValue(index, out IncompleteEngineRow row)
            ? (row.RpmLimit == 0, row.RpmRangeLow == 0, row.RpmRangeHigh == 0)
            : (false, false, false);

    private static Dictionary<int, IncompleteEngineRow> BuildIncompleteEngineIndex()
    {
        var index = new Dictionary<int, IncompleteEngineRow>();
        // Only the original 119 catalog rows correspond to this workbook.
        for (int profileIndex = 0;
             profileIndex < Math.Min(119, EngineCatalog.Profiles.Count); profileIndex++)
        {
            EngineSpec profile = EngineCatalog.Profiles[profileIndex];
            foreach (IncompleteEngineRow row in IncompleteEngineRows)
            {
                if (!row.Truck.StartsWith(profile.Brand + ".", StringComparison.OrdinalIgnoreCase)
                    || row.Horsepower != profile.Horsepower || row.Kw != profile.Kw
                    || row.TorqueNm != profile.TorqueNm
                    || (row.RpmLimit > 0 && row.RpmLimit != profile.RpmLimit)
                    || (row.RpmRangeLow > 0 && row.RpmRangeLow != profile.RpmRangeLow)
                    || (row.RpmRangeHigh > 0 && row.RpmRangeHigh != profile.RpmRangeHigh))
                    continue;
                index.Add(profileIndex, row);
                break;
            }
        }
        return index;
    }
    private static readonly TimeSpan WorkerInterval = TimeSpan.FromMilliseconds(20);
    private static readonly Func<object, float>? IdleRpmReader = CreateFloatReader(typeof(ConfigFloat), "engineRpmIdle", "rpmIdle");
    private static readonly Func<object, float>? EngineLoadReader = CreateFloatReader(typeof(TruckFloat), "engineLoad", "engineLoadPercent");
    private static readonly Func<object, float>? EngineTorqueReader = CreateFloatReader(typeof(ConfigFloat), "engineTorque", "engineTorqueMax", "engineTorqueMaximum");

    private readonly object lifecycleLock = new();
    private readonly object telemetryLock = new();
    private readonly object controlIntentLock = new();
    private readonly object flatShiftOutputLock = new();
    private readonly object memoryLock = new();
    private readonly object statusLock = new();
    private readonly object profileLock = new();
    private readonly object configurationLock = new();
    private readonly Dictionary<string, ConfigurationSession> configurationSessions = new();
    private ConfigurationSession? activeConfiguration;
    private string activeConfigurationKey = string.Empty;
    private readonly WindowDefinition statusWindow = new()
    {
        Title = "Manual Transmission",
        X = 15,
        Y = 520,
        Alpha = .85f,
        Open = true,
        Flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.AlwaysAutoResize
    };

    private CancellationTokenSource? workerCancellation;
    private Task? workerTask;
    private Task? pendingWorkerStop;
    private MemoryMappedFile? controllerMap;
    private MemoryMappedViewAccessor? controllerView;
    private TelemetrySnapshot latestTelemetry = TelemetrySnapshot.Empty;
    private float[] cachedForwardRatios = [];
    private AutomatedControlIntent latestAutomatedControlIntent = AutomatedControlIntent.Empty;
    private AutomatedControlIntent latestStockControlIntent = AutomatedControlIntent.Empty;
    private DateTime nextMapOpenAttempt = DateTime.MinValue;
    private int isEnabled;
    private long telemetrySequence;
    private long lastProcessedTelemetrySequence;
    private long lastDynamicsTelemetrySequence;
    private long shiftUpOffset = -1;
    private long shiftDownOffset = -1;
    private long shiftFirstOffset = -1;
    private int shiftTransitionActive;
    private int expectedGearAfterShift;
    private bool confirmedShiftCooldownApplied;
    private DateTime shiftTransitionStartedAt = DateTime.MinValue;
    private DateTime directFirstGearAttemptedAt = DateTime.MinValue;
    private float learnedIdleRpm;
    private float previousSpeed;
    private float latestSpeedRate;
    private double gradeBaseX, gradeBaseY, gradeBaseZ;
    private DateTime gradeBaseAt = DateTime.MinValue;
    private float latestRoadGrade;
    private bool roadGradeValid;
    private EngineSpec? cachedCustomEngine;
    private EngineSpec? cachedActiveEngineSpec;
    private DateTime previousDynamicsAt = DateTime.MinValue;
    private DateTime uphillReleaseEligibleSince = DateTime.MinValue;
    private DateTime shiftReleaseAt = DateTime.MinValue;
    private DateTime shiftCooldownUntil = DateTime.MinValue;
    private ShiftCommand activeShift;
    private volatile bool automaticShiftingEnabled = true;
    private volatile bool detailedDiagnosticMode;
    private bool uiProbeRenderLogged;
    private bool uiProbeLastDetailedMode;
    private string uiProbeLastBrand = string.Empty;
    private bool uphillFirstGearHoldActive;
    private bool uphillHoldReleasedForCurrentDrag;
    private bool flatShiftChannelActive;
    private bool shiftButtonsLatched;
    private float displayedPredictedRpm;
    private float displayedUpshiftRpm;
    private string controllerStatus = "Waiting for Local\\SCSControls";
    private string lastDecision = "Waiting for telemetry";
    private DecisionCode lastDecisionCode;
    private int lastDecisionFirst, lastDecisionSecond, lastDecisionThird;
    private string selectedBrand = CustomBrand;
    private int selectedHorsepower;
    private int selectedProfileIndex = -1;
    private int customHorsepower;
    private int customTorqueNm;
    private int customRpmLimit;
    private int customRpmRangeLow;
    private int customRpmRangeHigh;

    public override PluginInformation Info => new()
    {
        Name = "ManualTransmission",
        Id = PluginId,
        Description = "Telemetry-driven sequential shifting with selectable engine specifications",
        AuthorName = "the Godspeed",
        Version = BuildVersion.InformationalVersion,
        SupportedETS2LA = ">=2026.9.5026",
        Dependencies = ["godspeed.shared"],
        Tags = ["Transmission", "Telemetry"]
    };

    // ETS2LA owns a lightweight base tick. All transmission work runs in the
    // cancellation-controlled 50 Hz worker below.
    public override float TickRate => 1f;

    private bool IsEnabled => _IsRunning && Volatile.Read(ref isEnabled) != 0;

    public ManualTransmissionAdjustmentSettings GetAdjustmentSettings()
    {
        lock (profileLock)
            return new ManualTransmissionAdjustmentSettings(automaticShiftingEnabled,
                selectedBrand, selectedProfileIndex, customHorsepower, customTorqueNm,
                customRpmLimit, customRpmRangeLow, customRpmRangeHigh)
            { DetailedDiagnosticMode = detailedDiagnosticMode };
    }

    public ConfigurationModeSnapshot GetConfigurationModeSnapshot()
    {
        TelemetrySnapshot telemetry;
        lock (telemetryLock)
            telemetry = latestTelemetry;
        lock (configurationLock)
        {
            ConfigurationSession? session = activeConfiguration;
            bool needsLimit = session?.Raw.RpmLimit == 0 && session.MeasuredLimit is null;
            bool needsBand = session is not null
                && ((session.Raw.RpmRangeLow == 0 && session.MeasuredLow is null)
                    || (session.Raw.RpmRangeHigh == 0 && session.MeasuredHigh is null));
            float seconds = session is not null && session.RunningTest != ConfigurationTestKind.None
                ? Math.Max(0f, 10f - (Environment.TickCount64 - session.StartedAtTick) / 1000f)
                : 0f;
            bool telemetryReady = IsFreshConfigurationTelemetry(telemetry);
            bool stationaryReady = telemetryReady && IsStationaryTestSafe(telemetry);
            bool rollingReady = telemetryReady && IsRollingTestSafe(telemetry, out _);
            EngineSpec? effective = GetActiveEngineSpec();
            return new ConfigurationModeSnapshot(session is not null && (needsLimit || needsBand),
                needsLimit, needsBand, session?.RunningTest ?? ConfigurationTestKind.None,
                seconds, stationaryReady, rollingReady, session?.Message ?? "Select an engine profile.",
                effective?.RpmLimit ?? 0, effective?.RpmRangeLow ?? 0,
                effective?.RpmRangeHigh ?? 0,
                session?.MeasuredLimit is not null,
                session?.MeasuredLow is not null || session?.MeasuredHigh is not null);
        }
    }

    public bool TryStartConfigurationTest(ConfigurationTestKind kind)
    {
        if (!IsEnabled || kind is ConfigurationTestKind.None || IsShiftBusy(DateTime.UtcNow)
            || Volatile.Read(ref shiftTransitionActive) != 0)
            return false;
        TelemetrySnapshot telemetry;
        lock (telemetryLock)
            telemetry = latestTelemetry;
        if (!IsFreshConfigurationTelemetry(telemetry))
            return false;
        lock (configurationLock)
        {
            ConfigurationSession? session = activeConfiguration;
            if (session is null || session.RunningTest != ConfigurationTestKind.None)
                return false;
            if (kind == ConfigurationTestKind.StationaryLimit)
            {
                if (session.Raw.RpmLimit != 0 || session.MeasuredLimit is not null
                    || !IsStationaryTestSafe(telemetry))
                    return false;
            }
            else if (kind == ConfigurationTestKind.RollingPowerBand)
            {
                if ((session.Raw.RpmRangeLow != 0 || session.MeasuredLow is not null)
                    && (session.Raw.RpmRangeHigh != 0 || session.MeasuredHigh is not null))
                    return false;
                if (session.Raw.RpmLimit == 0 && session.MeasuredLimit is null)
                {
                    session.Message = "Complete the stationary RPM-limit test first.";
                    return false;
                }
                if (!IsRollingTestSafe(telemetry, out string reason))
                {
                    session.Message = reason;
                    return false;
                }
                session.LockedGear = telemetry.CurrentGear;
            }
            else
                return false;
            session.RunningTest = kind;
            session.StartedAtTick = Environment.TickCount64;
            session.LastSampleSequence = telemetry.Sequence;
            session.SawFullThrottle = false;
            session.MaximumObservedRpm = 0f;
            session.SmoothedAcceleration = 0f;
            session.LastSampleAt = DateTime.MinValue;
            session.Samples.Clear();
            session.Message = kind == ConfigurationTestKind.StationaryLimit
                ? "Stationary countdown started; hold full physical throttle."
                : $"Rolling countdown started in verified 1:1 gear {session.LockedGear}; hold full physical throttle.";
        }
        if (IsShiftBusy(DateTime.UtcNow) || Volatile.Read(ref shiftTransitionActive) != 0)
        {
            CancelConfigurationTest("A shift started during test setup; retry once the gearbox settles.");
            return false;
        }
        ReleaseShiftButtons();
        ReleaseFlatShiftThrottle();
        LogInfo("Configuration", $"Started {kind} diagnostic test");
        return true;
    }

    public void StopConfigurationMode()
        => CancelConfigurationTest("Configuration Mode closed; active test cancelled.");

    public void SetDetailedDiagnosticMode(bool enabled)
    {
        lock (profileLock)
            detailedDiagnosticMode = enabled;
        LogUiProbe("Setter.DetailedMode", $"requested={enabled}");
    }

    public void SetAutomaticShifting(bool enabled)
    {
        if (automaticShiftingEnabled == enabled)
            return;
        automaticShiftingEnabled = enabled;
        if (!enabled && IsEnabled)
        {
            ReleaseShiftButtons();
            ReleaseFlatShiftThrottle();
            SetDecision("Automatic shifting disabled from Adjustments");
        }
        else if (enabled && IsEnabled)
        {
            lock (memoryLock)
                shiftCooldownUntil = DateTime.UtcNow.AddMilliseconds(ShiftCooldownMilliseconds);
            SetDecision("Automatic shifting enabled");
        }
        SaveSettings();
    }

    public void SetSelectedBrand(string brand)
    {
        if (!EngineBrands.Contains(brand))
        {
            LogUiProbe("Setter.Brand.Rejected", $"requested={brand}");
            return;
        }
        lock (profileLock)
        {
            selectedBrand = brand;
            selectedHorsepower = 0;
            selectedProfileIndex = -1;
        }
        LogUiProbe("Setter.Brand", $"requested={brand}");
        RefreshEngineSpecCache();
    }

    public void LogUiProbe(string stage, string detail, bool readSavedFile = false)
    {
        ManualTransmissionAdjustmentSettings current = GetAdjustmentSettings();
        string saved = "not_checked";
        if (readSavedFile)
        {
            try
            {
                string path = GetSettingsPath();
                EngineProfileSettings? disk = File.Exists(path)
                    ? JsonSerializer.Deserialize<EngineProfileSettings>(File.ReadAllText(path))
                    : null;
                saved = disk is null ? "missing_or_null"
                    : $"brand={disk.SelectedBrand};detailed={disk.DetailedDiagnosticMode}";
            }
            catch (Exception error)
            {
                saved = $"read_error={error.GetType().Name}";
            }
        }
        string message = $"stage={stage};plugin_instance={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this)};{detail};memory_brand={current.SelectedBrand};memory_detailed={current.DetailedDiagnosticMode};saved={saved}";
        Logger.Info($"ManualTransmission UI probe: {message}");
    }

    public void SetSelectedProfileIndex(int index)
    {
        if (index < 0 || index >= EngineCatalog.Profiles.Count)
            return;
        EngineSpec selected = EngineCatalog.Profiles[index];
        lock (profileLock)
        {
            if (selected.Brand != selectedBrand)
                return;
            selectedProfileIndex = index;
            selectedHorsepower = selected.Horsepower;
        }
        RefreshEngineSpecCache();
        SaveSettings();
    }

    public void SetCustomEngine(int horsepower, int torqueNm, int rpmLimit,
        int rpmRangeLow, int rpmRangeHigh)
    {
        lock (profileLock)
        {
            customHorsepower = Math.Clamp(horsepower, 0, 5000);
            customTorqueNm = Math.Clamp(torqueNm, 0, 20000);
            customRpmLimit = Math.Clamp(rpmLimit, 0, 10000);
            customRpmRangeLow = Math.Clamp(rpmRangeLow, 0, 10000);
            customRpmRangeHigh = Math.Clamp(rpmRangeHigh, 0, 10000);
        }
        RefreshEngineSpecCache();
        SaveSettings();
    }
    public bool IsOpenDriveline
    {
        get
        {
            lock (telemetryLock)
                return latestTelemetry.Sequence == 0 || !latestTelemetry.SdkActive
                    || latestTelemetry.Paused
                    || DateTime.UtcNow - latestTelemetry.ReceivedAt >= TimeSpan.FromMilliseconds(TelemetryTimeoutMilliseconds)
                    || latestTelemetry.CurrentGear <= 0 || latestTelemetry.Clutch > .05f
                    || Volatile.Read(ref shiftTransitionActive) != 0;
        }
    }

    public override void Init()
    {
        base.Init();
        LoadSettings();
        RefreshEngineSpecCache();
    }

    public override void OnEnable()
    {
        StartPluginLogic();
    }

    private void StartPluginLogic()
    {
        base.OnEnable();
        lock (lifecycleLock)
        {
            try
            {
                StopWorkerAndReleaseResources();
                if (pendingWorkerStop is { IsCompleted: false })
                    throw new InvalidOperationException("Previous transmission worker is still stopping.");
                LogInfo("Version", "Plugin build metadata resolved", BuildVersion.LogSnapshot);
                workerCancellation = new CancellationTokenSource();
                ResetRuntimeState();
                GodspeedIntentHub.PublishTransmission(true);
                Events.Current.Subscribe<GameTelemetryData>(GameTelemetry.Current.EventString, OnTelemetryReceived);
                Events.Current.Subscribe<ControlEvent>(GameOutput.Current.EventString, OnControlOutputReceived);
                OverlayHandler.Current.RegisterWindow(statusWindow, RenderStatusWindow);
                Volatile.Write(ref isEnabled, 1);
                workerTask = Task.Run(() => RunWorkerAsync(workerCancellation.Token));
                LogInfo("Lifecycle", "Plugin enabled; waiting for live powertrain telemetry");
            }
            catch (Exception error)
            {
                LogError("Lifecycle", "Plugin enable failed; rolling back partial registration", error.ToString());
                Volatile.Write(ref isEnabled, 0);
                StopWorkerAndReleaseResources();
                GodspeedIntentHub.ClearTransmission();
                Events.Current.Unsubscribe<GameTelemetryData>(GameTelemetry.Current.EventString, OnTelemetryReceived);
                Events.Current.Unsubscribe<ControlEvent>(GameOutput.Current.EventString, OnControlOutputReceived);
                OverlayHandler.Current.UnregisterWindow(statusWindow);
                base.OnDisable();
                throw;
            }
        }
    }

    public override void OnDisable()
    {
        if (!_IsRunning) return;
        DisablePluginLogic();
    }

    private void DisablePluginLogic()
    {
        base.OnDisable();
        lock (lifecycleLock)
        {
            Volatile.Write(ref isEnabled, 0);
            CancelConfigurationTest("Plugin disabled; configuration test cancelled.");
            Events.Current.Unsubscribe<GameTelemetryData>(GameTelemetry.Current.EventString, OnTelemetryReceived);
            Events.Current.Unsubscribe<ControlEvent>(GameOutput.Current.EventString, OnControlOutputReceived);
            OverlayHandler.Current.UnregisterWindow(statusWindow);
            StopWorkerAndReleaseResources();
            GodspeedIntentHub.ClearTransmission();
            LogInfo("Lifecycle", "Plugin disabled; shift buttons released and controller handles closed");
        }
    }

    public override void Shutdown()
    {
        if (!_IsRunning) return;
        ShutdownPluginLogic();
    }

    private void ShutdownPluginLogic()
    {
        base.Shutdown();
        lock (lifecycleLock)
        {
            Volatile.Write(ref isEnabled, 0);
            CancelConfigurationTest("Plugin shut down; configuration test cancelled.");
            Events.Current.Unsubscribe<GameTelemetryData>(GameTelemetry.Current.EventString, OnTelemetryReceived);
            Events.Current.Unsubscribe<ControlEvent>(GameOutput.Current.EventString, OnControlOutputReceived);
            OverlayHandler.Current.UnregisterWindow(statusWindow);
            StopWorkerAndReleaseResources();
            GodspeedIntentHub.ClearTransmission();
            LogInfo("Lifecycle", "Plugin shutdown completed");
        }
    }

    public override void Tick()
    {
        if (!IsEnabled)
            return;
    }

    private void OnControlOutputReceived(ControlEvent controlEvent)
    {
        if (!IsEnabled)
            return;

        bool stockCruise = string.Equals(controlEvent.ChannelDefinition.Id,
            StockCruiseControlChannelId, StringComparison.Ordinal);
        if (!stockCruise && !string.Equals(controlEvent.ChannelDefinition.Id,
                StabilityCruiseControlChannelId, StringComparison.Ordinal))
        {
            return;
        }

        ControlVariables? variables = controlEvent.Variables;
        if (variables is null
            || (!variables.aforward.HasValue && !variables.abackward.HasValue))
        {
            lock (controlIntentLock)
            {
                if (stockCruise) latestStockControlIntent = AutomatedControlIntent.Empty;
                else latestAutomatedControlIntent = AutomatedControlIntent.Empty;
            }
            ReleaseFlatShiftThrottle();
            return;
        }

        float throttle = variables.aforward.HasValue && float.IsFinite(variables.aforward.Value)
            ? Math.Clamp(variables.aforward.Value, 0f, 1f)
            : 0f;
        float brake = variables.abackward.HasValue && float.IsFinite(variables.abackward.Value)
            ? Math.Clamp(Math.Abs(variables.abackward.Value), 0f, 1f)
            : 0f;

        lock (controlIntentLock)
        {
            if (stockCruise) latestStockControlIntent = new AutomatedControlIntent(throttle, brake, DateTime.UtcNow);
            else latestAutomatedControlIntent = new AutomatedControlIntent(throttle, brake, DateTime.UtcNow);
        }
    }

    private AutomatedControlIntent GetFreshAutomatedControlIntent(DateTime now)
    {
        lock (controlIntentLock)
        {
            AutomatedControlIntent intent = latestAutomatedControlIntent;
            AutomatedControlIntent stock = latestStockControlIntent;
            bool customFresh = intent.ReceivedAt != DateTime.MinValue
                && (now - intent.ReceivedAt).TotalMilliseconds <= AutomatedControlIntentTimeoutMilliseconds;
            bool stockFresh = stock.ReceivedAt != DateTime.MinValue
                && (now - stock.ReceivedAt).TotalMilliseconds <= AutomatedControlIntentTimeoutMilliseconds;
            return new AutomatedControlIntent(
                Math.Max(customFresh ? intent.Throttle : 0f, stockFresh ? stock.Throttle : 0f),
                Math.Max(customFresh ? intent.Brake : 0f, stockFresh ? stock.Brake : 0f),
                customFresh || stockFresh ? now : DateTime.MinValue);
        }
    }

    private bool IsCruiseCoastingOrBraking(DateTime now)
    {
        lock (controlIntentLock)
        {
            AutomatedControlIntent custom = latestAutomatedControlIntent;
            AutomatedControlIntent stock = latestStockControlIntent;
            bool customFresh = custom.ReceivedAt != DateTime.MinValue
                && (now - custom.ReceivedAt).TotalMilliseconds <= AutomatedControlIntentTimeoutMilliseconds;
            bool stockFresh = stock.ReceivedAt != DateTime.MinValue
                && (now - stock.ReceivedAt).TotalMilliseconds <= AutomatedControlIntentTimeoutMilliseconds;
            return (customFresh && (custom.Brake > .01f || custom.Throttle <= .05f))
                || (stockFresh && (stock.Brake > .01f || stock.Throttle <= .05f));
        }
    }

    private void OnTelemetryReceived(GameTelemetryData data)
    {
        if (!IsEnabled)
            return;

        DateTime receivedAt = DateTime.UtcNow;
        int forwardGearCount = Math.Clamp(data.configUI.gears, 0, data.configFloat.gearRatiosForward.Length);
        float[] forwardRatios;

        AutomatedControlIntent automatedIntent = GetFreshAutomatedControlIntent(receivedAt);
        float physicalThrottle = Math.Clamp(
            Math.Max(Math.Abs(data.truckFloat.userThrottle), Math.Abs(data.truckFloat.gameThrottle)),
            0f,
            1f);
        bool trailerAttached = false;
        for (int trailerIndex = 0; trailerIndex < data.trailers.Length; trailerIndex++)
            trailerAttached |= data.trailers[trailerIndex].comBool.attached;
        float throttle = Math.Max(physicalThrottle, automatedIntent.Throttle);
        float clutch = Math.Clamp(Math.Max(Math.Abs(data.truckFloat.userClutch), Math.Abs(data.truckFloat.gameClutch)), 0f, 1f);
        float engineLoad = ReadOptionalFloat(EngineLoadReader, data.truckFloat);
        if (!float.IsFinite(engineLoad) || engineLoad < 0f)
            engineLoad = throttle;
        else if (engineLoad > 1f)
            engineLoad = Math.Clamp(engineLoad / 100f, 0f, 1f);
        engineLoad = Math.Max(engineLoad, throttle);

            float idleRpm = ReadOptionalFloat(IdleRpmReader, data.configFloat);
        float engineTorque = ReadOptionalFloat(EngineTorqueReader, data.configFloat);
        if (!float.IsFinite(engineTorque) || engineTorque <= 0f)
            engineTorque = Volatile.Read(ref cachedActiveEngineSpec)?.TorqueNm ?? 0f;
        float poweredRadiusSum = 0f;
        int poweredRadiusCount = 0;
        int wheelCount = Math.Clamp(data.configUI.truckWheelCount, 0, 16);
        for (int wheel = 0; wheel < wheelCount; wheel++)
        {
            float radius = data.configFloat.truckWheelRadius[wheel];
            if (data.configBool.truckWheelPowered[wheel] && float.IsFinite(radius)
                && radius is > .1f and < 2f)
            {
                poweredRadiusSum += radius;
                poweredRadiusCount++;
            }
        }
        float poweredWheelRadius = poweredRadiusCount > 0 ? poweredRadiusSum / poweredRadiusCount : 0f;
        lock (telemetryLock)
        {
            Vector3Double position = data.truckPlacement.coordinate;
            if (double.IsFinite(position.X) && double.IsFinite(position.Y) && double.IsFinite(position.Z))
            {
                if (gradeBaseAt != DateTime.MinValue
                    && (receivedAt - gradeBaseAt).TotalMilliseconds >= 600)
                {
                    double dx = position.X - gradeBaseX;
                    double dz = position.Z - gradeBaseZ;
                    double horizontal = Math.Sqrt(dx * dx + dz * dz);
                    double grade = horizontal > 1d && horizontal < 20d
                        ? (position.Y - gradeBaseY) / horizontal : double.NaN;
                    roadGradeValid = double.IsFinite(grade) && Math.Abs(grade) <= .30d;
                    if (roadGradeValid)
                        latestRoadGrade = (float)grade;
                    gradeBaseX = position.X;
                    gradeBaseY = position.Y;
                    gradeBaseZ = position.Z;
                    gradeBaseAt = receivedAt;
                }
                else if (gradeBaseAt == DateTime.MinValue)
                {
                    gradeBaseX = position.X;
                    gradeBaseY = position.Y;
                    gradeBaseZ = position.Z;
                    gradeBaseAt = receivedAt;
                }
            }
            bool ratiosChanged = cachedForwardRatios.Length != forwardGearCount;
            for (int index = 0; !ratiosChanged && index < forwardGearCount; index++)
                ratiosChanged = cachedForwardRatios[index] != data.configFloat.gearRatiosForward[index];
            if (ratiosChanged)
            {
                cachedForwardRatios = new float[forwardGearCount];
                Array.Copy(data.configFloat.gearRatiosForward, cachedForwardRatios, forwardGearCount);
            }
            forwardRatios = cachedForwardRatios;
            if ((!float.IsFinite(idleRpm) || idleRpm <= 0f)
                && data.truckBool.engineEnabled
                && data.truckFloat.engineRpm > 0f
                && throttle < .08f
                && Math.Abs(data.truckFloat.speed) < .5f)
            {
                learnedIdleRpm = learnedIdleRpm <= 0f
                    ? data.truckFloat.engineRpm
                    : learnedIdleRpm + (data.truckFloat.engineRpm - learnedIdleRpm) * .08f;
            }

            if (!float.IsFinite(idleRpm) || idleRpm <= 0f)
                idleRpm = learnedIdleRpm > 0f
                    ? learnedIdleRpm
                    : Math.Max(0f, data.configFloat.engineRpmMax * .30f);

            long sequence = Interlocked.Increment(ref telemetrySequence);
            latestTelemetry = new TelemetrySnapshot(
                sequence,
                receivedAt,
                data.sdkActive,
                data.paused,
                data.truckBool.engineEnabled,
                data.truckBool.parkingBrake,
                trailerAttached,
                float.IsFinite(data.configFloat.cargoMass)
                    ? Math.Max(0f, data.configFloat.cargoMass) : 0f,
                physicalThrottle,
                forwardGearCount,
                data.truckInt.gear,
                data.truckUI.shifterSlot,
                Math.Max(0f, data.truckFloat.engineRpm),
                Math.Max(0f, data.configFloat.engineRpmMax),
                Math.Max(0f, idleRpm),
                engineTorque,
                  Math.Max(0f, data.configFloat.gearDifferential),
                  poweredWheelRadius,
                  Math.Abs(data.truckFloat.speed),
                  float.IsFinite(data.truckVector.acceleration.Z) ? data.truckVector.acceleration.Z : 0f,
                  roadGradeValid,
                  latestRoadGrade,
                  throttle,
                automatedIntent.Throttle,
                automatedIntent.Brake,
                clutch,
                 Math.Clamp(engineLoad, 0f, 1f),
                 forwardRatios);
            if (Volatile.Read(ref shiftTransitionActive) != 0
                && data.truckInt.gear == Volatile.Read(ref expectedGearAfterShift)
                && clutch <= .05f)
                Volatile.Write(ref shiftTransitionActive, 0);
        }
    }

    private async Task RunWorkerAsync(CancellationToken cancellation)
    {
        using PeriodicTimer timer = new(WorkerInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false))
            {
                if (!IsEnabled)
                    return;

                try { RunWorkerTick(DateTime.UtcNow); }
                catch (Exception error)
                {
                    SetDecision($"Worker error: {error.Message}");
                    LogError("Worker", "Worker tick failed", error.ToString());
                    ReleaseShiftButtons();
                    ReleaseFlatShiftThrottle();
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            ReleaseShiftButtons();
            ReleaseFlatShiftThrottle();
            GodspeedIntentHub.ClearTransmission();
        }
    }

    private void RunWorkerTick(DateTime now)
    {
        if (!IsEnabled)
            return;

        TelemetrySnapshot engagementTelemetry;
        lock (telemetryLock)
            engagementTelemetry = latestTelemetry;
        lock (memoryLock)
        {
            if (Volatile.Read(ref shiftTransitionActive) != 0
                && activeShift == ShiftCommand.None
                && now - shiftTransitionStartedAt > TimeSpan.FromSeconds(2))
                Volatile.Write(ref shiftTransitionActive, 0);
        }
        bool openDriveline = IsOpenDriveline;
        bool atLaunchSpeed = engagementTelemetry.SpeedMetresPerSecond < (2f / 3.6f);
        bool gearEngaged = engagementTelemetry.Sequence != 0 && !openDriveline
            && (!atLaunchSpeed || engagementTelemetry.CurrentGear == 1);
        bool launchRequested = GodspeedIntentHub.Read().CruiseLaunchRequested;
        GodspeedIntentHub.PublishTransmission(openDriveline,
            launchRequested && (gearEngaged || Volatile.Read(ref shiftTransitionActive) != 0),
            gearEngaged);

        EnsureControllerMap(now);

        TelemetrySnapshot telemetry;
        lock (telemetryLock)
            telemetry = latestTelemetry;
        GodspeedIntentSnapshot ecosystem = GodspeedIntentHub.Read();
        UpdateShiftPulse(now, telemetry);
        ApplyConfirmedShiftCooldown(now);
        if (AdvanceConfigurationTest(telemetry))
        {
            ReleaseShiftButtons();
            ReleaseFlatShiftThrottle();
            GodspeedIntentHub.PublishTransmission(true);
            return;
        }
        if (ecosystem.TollActive || ecosystem.CruiseEmergency)
            ReleaseFlatShiftThrottle();
        else
            UpdateFlatShiftThrottle(telemetry, now);

        if (telemetry.Sequence == 0
            || (now - telemetry.ReceivedAt).TotalMilliseconds >= TelemetryTimeoutMilliseconds
            || !telemetry.SdkActive
            || telemetry.Paused
            || !telemetry.EngineEnabled)
        {
            ReleaseShiftButtons();
            ReleaseFlatShiftThrottle();
            SetDecision(telemetry.Sequence == 0 ? "Waiting for telemetry" : "Telemetry unavailable; shifting suspended");
            return;
        }

        UpdateVehicleDynamics(telemetry);
        if (!automaticShiftingEnabled || IsShiftBusy(now))
        {
            if (!automaticShiftingEnabled)
                ReleaseFlatShiftThrottle();
            return;
        }

        if (telemetry.Sequence == lastProcessedTelemetrySequence)
            return;
        lastProcessedTelemetrySequence = telemetry.Sequence;

        if (!HasUsableTransmission(telemetry))
        {
            SetDecision("Waiting for valid gearbox ratios");
            return;
        }

        EngineSpec? activeEngine = GetActiveEngineSpec();
        if (activeEngine is null)
        {
            SetDecision("Select an engine profile or enter complete custom specifications");
            return;
        }

        int currentGear = telemetry.CurrentGear;
        float rpmLimit = telemetry.EngineRpmMax > 0f
            ? Math.Min(activeEngine.RpmLimit, telemetry.EngineRpmMax)
            : activeEngine.RpmLimit;
        PowerBand powerBand = CalculatePowerBand(activeEngine, telemetry.Throttle, rpmLimit);
        bool highPowerMode = telemetry.Throttle > .80f;
        float upshiftRpm = powerBand.UpshiftRpm;
        float downshiftRpm = powerBand.DownshiftRpm;
        float minimumLandingRpm = powerBand.LowerRpm;
        float maximumCatalogDownshiftRpm = activeEngine.RpmLimit * .98f;

        if (currentGear == 0)
        {
            if (ecosystem.TollActive || ecosystem.CruiseEmergency)
            {
                SetDecision("Neutral: launch held while toll or emergency braking is active");
                return;
            }
            bool assistedLaunch = ApplicationState.Current.EnableAssists
                && ((float.IsFinite(ApplicationState.Current.DesiredSpeed)
                    && ApplicationState.Current.DesiredSpeed > 0f)
                    || ecosystem.CruiseLaunchRequested);
            if ((!assistedLaunch && telemetry.Throttle <= .05f)
                || telemetry.SpeedMetresPerSecond >= (2f / 3.6f))
            {
                SetDecision("Neutral: waiting for assisted launch or throttle below 2 km/h");
                return;
            }
            TriggerShift(
                ShiftCommand.Up,
                now,
                "Neutral->1: selecting first forward gear");
            return;
        }

        bool atStandstill = telemetry.SpeedMetresPerSecond <= StandstillSpeedMetresPerSecond;
        if (telemetry.Clutch > .5f && !atStandstill)
        {
            ReleaseFlatShiftThrottle();
            SetDecision("Clutch disengaged; automatic shift suspended");
            return;
        }

        float speedRate = latestSpeedRate;
        int targetGear = currentGear + 1;
        float predictedUpshiftRpm = targetGear <= telemetry.ForwardGearCount
            ? PredictLandingRpm(telemetry, currentGear, targetGear)
            : 0f;

        lock (statusLock)
        {
            displayedUpshiftRpm = upshiftRpm;
            displayedPredictedRpm = predictedUpshiftRpm;
        }

        bool automatedHeavyBraking = ecosystem.TollActive || ecosystem.CruiseEmergency
            || (Math.Max(telemetry.AutomatedBrake,
                    GodspeedOutputCompatibility.RequestedBrakeDemand(GameOutput.Current,
                        FlatShiftChannelId)) > .3f
                && latestSpeedRate < -1.5f);
        // At a stop the engine can be idling or free-revving behind an open
        // driveline. Its RPM is not proportional to wheel RPM, so multiplying
        // it by a crawler-gear ratio invents an impossible over-rev. Shift one
        // slot at a time; TriggerShift's pulse/cooldown still applies.
        if (currentGear > 1 && atStandstill)
        {
            // Ground speed can stay near zero during drive-wheel spin. A high
            // engine speed with throttle applied and the clutch coupled is
            // not proof that the driveline is stationary: wait for traction
            // or for the driver to release the throttle before changing gear.
            float spinGuardRpm = Math.Min(rpmLimit * .70f, telemetry.EngineRpmIdle + 400f);
            if (telemetry.Clutch <= .5f && telemetry.Throttle > .50f
                && telemetry.EngineRpm > spinGuardRpm)
            {
                SetDecision("Stopped-vehicle downshift waiting for stable driveline RPM");
                return;
            }
            if (directFirstGearAttemptedAt == DateTime.MinValue)
            {
                directFirstGearAttemptedAt = now;
                TriggerShift(ShiftCommand.FirstGear, now,
                    $"Stopped-vehicle direct first-gear selection from {currentGear}");
                return;
            }
            // Not all game gearbox modes honor gear1. After one verified
            // attempt, recover sequentially rather than repeating a blind key.
            int lowerGear = currentGear - 1;
            float landingRpm = PredictDownshiftLandingRpm(telemetry, currentGear, lowerGear);
            if (IsApprovedDownshift(landingRpm, activeEngine))
                TriggerShift(ShiftCommand.Down, now,
                    $"Stopped-vehicle downshift {currentGear}->{lowerGear}: landing {landingRpm:0} RPM",
                    predictedLandingRpm: landingRpm);
            else
                SetDecision(DecisionCode.StoppedDownshiftGuard, currentGear, lowerGear,
                    (int)MathF.Round(maximumCatalogDownshiftRpm));
            return;
        }
        if (!atStandstill || currentGear <= 1)
            directFirstGearAttemptedAt = DateTime.MinValue;
        if (currentGear > 1 && telemetry.SpeedMetresPerSecond < (5f / 3.6f)
            && telemetry.Throttle > .80f && !automatedHeavyBraking)
        {
            int lowerGear = currentGear - 1;
            float landingRpm = PredictDownshiftLandingRpm(telemetry, currentGear, lowerGear);
            if (IsApprovedDownshift(landingRpm, activeEngine))
                TriggerShift(ShiftCommand.Down, now,
                    $"Low-speed traction downshift {currentGear}->{lowerGear}: landing {landingRpm:0} RPM",
                    predictedLandingRpm: landingRpm);
            else
                SetDecision(DecisionCode.LowSpeedDownshiftGuard, currentGear, lowerGear,
                    (int)MathF.Round(maximumCatalogDownshiftRpm));
            return;
        }
        bool uphillDrag = telemetry.SpeedMetresPerSecond < (20f / 3.6f)
            && telemetry.Throttle > .80f
            && telemetry.RoadGradeValid && telemetry.RoadGrade > .02f
            && telemetry.LongitudinalAcceleration < .25f
            && speedRate < .10f;
        if (!uphillDrag || speedRate > .25f)
        {
            uphillHoldReleasedForCurrentDrag = false;
            if (speedRate > .25f)
                uphillFirstGearHoldActive = false;
        }
        if (uphillDrag && (currentGear > 1 || !uphillHoldReleasedForCurrentDrag))
            uphillFirstGearHoldActive = true;

        if (uphillFirstGearHoldActive)
        {
            if (currentGear > 1)
            {
                int uphillTargetGear = currentGear - 1;
                float predictedDownshiftRpm = PredictDownshiftLandingRpm(telemetry, currentGear, uphillTargetGear);
                if (!IsApprovedDownshift(predictedDownshiftRpm, activeEngine))
                {
                    SetDecision(DecisionCode.UphillDownshiftGuard, currentGear, uphillTargetGear,
                        (int)MathF.Round(maximumCatalogDownshiftRpm));
                    return;
                }
                TriggerShift(ShiftCommand.Down, now,
                    $"Uphill anti-stall downshift {currentGear}->{uphillTargetGear}: landing {predictedDownshiftRpm:0} RPM",
                    predictedLandingRpm: predictedDownshiftRpm);
                return;
            }

            bool releaseCondition = telemetry.EngineRpm > activeEngine.RpmRangeHigh;
            if (!releaseCondition)
                uphillReleaseEligibleSince = DateTime.MinValue;
            else if (uphillReleaseEligibleSince == DateTime.MinValue)
                uphillReleaseEligibleSince = now;
            else if ((now - uphillReleaseEligibleSince).TotalSeconds >= 1.0)
            {
                uphillFirstGearHoldActive = false;
                uphillHoldReleasedForCurrentDrag = true;
                uphillReleaseEligibleSince = DateTime.MinValue;
                SetDecision("Uphill first-gear hold released after 1.0 s above the selected powerband");
            }

            if (uphillFirstGearHoldActive)
            {
                SetDecision(DecisionCode.UphillFirstGearHold, activeEngine.RpmRangeHigh);
                return;
            }
        }

        if (currentGear > 1 && automatedHeavyBraking)
        {
            int downshiftTargetGear = currentGear - 1;
            float predictedDownshiftRpm = PredictDownshiftLandingRpm(telemetry, currentGear, downshiftTargetGear);
            if (!IsApprovedDownshift(predictedDownshiftRpm, activeEngine))
            {
                SetDecision(DecisionCode.CruiseDownshiftGuard, currentGear, downshiftTargetGear,
                    (int)MathF.Round(maximumCatalogDownshiftRpm));
                return;
            }

            TriggerShift(ShiftCommand.Down, now,
                $"Cruise braking downshift {currentGear}->{downshiftTargetGear}: landing {predictedDownshiftRpm:0} RPM",
                predictedLandingRpm: predictedDownshiftRpm);
            return;
        }

        if (currentGear > 1
            && telemetry.EngineRpm < downshiftRpm
            && (latestSpeedRate < -.05f || telemetry.LongitudinalAcceleration < -.25f))
        {
            int downshiftTargetGear = currentGear - 1;
            float predictedDownshiftRpm = PredictDownshiftLandingRpm(telemetry, currentGear, downshiftTargetGear);
            if (!IsApprovedDownshift(predictedDownshiftRpm, activeEngine))
            {
                SetDecision(DecisionCode.DownshiftGuard, currentGear, downshiftTargetGear,
                    (int)MathF.Round(maximumCatalogDownshiftRpm));
                return;
            }

            TriggerShift(ShiftCommand.Down, now,
                $"Downshift {currentGear}->{downshiftTargetGear}: landing {predictedDownshiftRpm:0} RPM, load {telemetry.EngineLoad:P0}",
                predictedLandingRpm: predictedDownshiftRpm);
            return;
        }

        float ratioStep = targetGear <= telemetry.ForwardGearCount
            ? CalculateGearStepRatio(telemetry, currentGear, targetGear)
            : float.NaN;
        bool wideRatioLaunchShift = currentGear == 1
            && ratioStep > 1.55f
            && telemetry.EngineRpm >= rpmLimit * .82f;
        if (automatedHeavyBraking)
            return;
        if (currentGear >= telemetry.ForwardGearCount
            || (telemetry.EngineRpm < upshiftRpm && !wideRatioLaunchShift)
            || telemetry.Throttle < .15f)
            return;

        if (!float.IsFinite(predictedUpshiftRpm)
            || predictedUpshiftRpm < minimumLandingRpm)
        {
            SetDecision(DecisionCode.UpshiftLandingGuard, currentGear,
                (int)MathF.Round(predictedUpshiftRpm), (int)MathF.Round(minimumLandingRpm));
            return;
        }

        TriggerShift(ShiftCommand.Up, now,
            $"Upshift {currentGear}->{targetGear}: predicted landing {predictedUpshiftRpm:0} RPM; {(wideRatioLaunchShift ? "wide-ratio launch" : highPowerMode ? "heavy load" : "economy")}",
            predictedLandingRpm: predictedUpshiftRpm);
    }

    private static PowerBand CalculatePowerBand(EngineSpec engine, float throttle, float liveRpmLimit)
    {
        float throttleIntent = Math.Clamp(throttle, 0f, 1f);
        // A zero lower bound in an exact catalog row means unspecified, not 0 RPM.
        float lowerRpm = engine.RpmRangeLow > 0
            ? engine.RpmRangeLow : Math.Max(700f, engine.RpmRangeHigh * .70f);
        float floorRpm = lowerRpm - 50f;
        float upshiftRpm = throttleIntent > .80f
            ? Math.Min(liveRpmLimit * .94f, engine.RpmRangeHigh + 250f)
            : floorRpm + (engine.RpmRangeHigh - floorRpm) * throttleIntent;
        upshiftRpm = Math.Min(upshiftRpm, liveRpmLimit * .94f);
        return new PowerBand(floorRpm, lowerRpm - 20f, upshiftRpm);
    }

    private void UpdateVehicleDynamics(TelemetrySnapshot telemetry)
    {
        if (telemetry.Sequence == lastDynamicsTelemetrySequence)
            return;
        lastDynamicsTelemetrySequence = telemetry.Sequence;

        if (previousDynamicsAt != DateTime.MinValue)
        {
            float dt = (float)(telemetry.ReceivedAt - previousDynamicsAt).TotalSeconds;
            if (dt is >= .005f and <= .5f)
            {
                float speedRate = (telemetry.SpeedMetresPerSecond - previousSpeed) / dt;
                latestSpeedRate = speedRate;
            }
        }

        previousSpeed = telemetry.SpeedMetresPerSecond;
        previousDynamicsAt = telemetry.ReceivedAt;
    }

    private static bool HasUsableTransmission(TelemetrySnapshot telemetry)
    {
        if (telemetry.ForwardGearCount <= 0
            || telemetry.CurrentGear < 0
            || telemetry.CurrentGear > telemetry.ForwardGearCount
            || telemetry.ForwardRatios.Length < telemetry.ForwardGearCount
            || telemetry.DifferentialRatio <= 0f)
            return false;

        if (telemetry.CurrentGear == 0)
            return telemetry.ForwardRatios.Length > 0 && telemetry.ForwardRatios[0] > 0f;

        return telemetry.ForwardRatios[telemetry.CurrentGear - 1] > 0f;
    }

    private static float PredictLandingRpm(TelemetrySnapshot telemetry, int currentGear, int targetGear)
    {
        if (currentGear < 1 || targetGear < 1
            || currentGear > telemetry.ForwardRatios.Length
            || targetGear > telemetry.ForwardRatios.Length)
            return float.NaN;

        float currentReduction = telemetry.ForwardRatios[currentGear - 1] * telemetry.DifferentialRatio;
        float targetReduction = telemetry.ForwardRatios[targetGear - 1] * telemetry.DifferentialRatio;
        if (!float.IsFinite(currentReduction) || !float.IsFinite(targetReduction)
            || currentReduction <= 0f || targetReduction <= 0f)
            return float.NaN;

        return telemetry.EngineRpm * (targetReduction / currentReduction);
    }

    private static float PredictDownshiftLandingRpm(TelemetrySnapshot telemetry, int currentGear, int targetGear)
    {
        if (currentGear < 1 || targetGear < 1
            || currentGear > telemetry.ForwardRatios.Length
            || targetGear > telemetry.ForwardRatios.Length)
            return float.NaN;

        float currentReduction = telemetry.ForwardRatios[currentGear - 1] * telemetry.DifferentialRatio;
        float targetReduction = telemetry.ForwardRatios[targetGear - 1] * telemetry.DifferentialRatio;
        if (!float.IsFinite(currentReduction) || !float.IsFinite(targetReduction)
            || currentReduction <= 0f || targetReduction <= 0f)
            return float.NaN;

        // Engine RPM is not proportional to stationary wheel RPM, particularly
        // behind an open clutch. At speed, use verified powered-wheel radius,
        // ground speed and the target reduction rather than inverting ratios.
        if (telemetry.SpeedMetresPerSecond <= StandstillSpeedMetresPerSecond)
            return Math.Max(0f, telemetry.EngineRpmIdle);
        if (!float.IsFinite(telemetry.PoweredWheelRadius) || telemetry.PoweredWheelRadius <= .1f)
            return float.NaN;
        float wheelRpm = telemetry.SpeedMetresPerSecond
            / (2f * MathF.PI * telemetry.PoweredWheelRadius) * 60f;
        return Math.Max(telemetry.EngineRpmIdle, wheelRpm * targetReduction);
    }

    private static bool IsApprovedDownshift(float predictedLandingRpm, EngineSpec activeEngine)
        => activeEngine.RpmLimit > 0
            && float.IsFinite(predictedLandingRpm)
            && predictedLandingRpm >= 0f
            && predictedLandingRpm <= activeEngine.RpmLimit * .98f;

    private static float CalculateGearStepRatio(TelemetrySnapshot telemetry, int currentGear, int targetGear)
    {
        if (currentGear < 1 || targetGear < 1
            || currentGear > telemetry.ForwardRatios.Length
            || targetGear > telemetry.ForwardRatios.Length)
            return float.NaN;
        float currentRatio = telemetry.ForwardRatios[currentGear - 1];
        float targetRatio = telemetry.ForwardRatios[targetGear - 1];
        return currentRatio > 0f && targetRatio > 0f
            ? currentRatio / targetRatio
            : float.NaN;
    }

    private void TriggerShift(ShiftCommand command, DateTime now, string decision,
        float predictedLandingRpm = float.NaN)
    {
        if (!IsEnabled)
            return;
        TelemetrySnapshot telemetry;
        lock (telemetryLock)
            telemetry = latestTelemetry;
        int expectedGear = command switch
        {
            ShiftCommand.Up => telemetry.CurrentGear + 1,
            ShiftCommand.Down => telemetry.CurrentGear - 1,
            ShiftCommand.FirstGear => 1,
            _ => telemetry.CurrentGear
        };
        lock (memoryLock)
        {
            lock (configurationLock)
                if (activeConfiguration is { RunningTest: not ConfigurationTestKind.None })
                    return;
            GodspeedIntentSnapshot currentIntent = GodspeedIntentHub.Read();
            if (!IsEnabled
                || (command == ShiftCommand.Up
                    && (currentIntent.TollActive || currentIntent.CruiseEmergency))
                || !WriteShiftButtonsUnsafe(command == ShiftCommand.Up, command == ShiftCommand.Down,
                    command == ShiftCommand.FirstGear))
                return;
            activeShift = command;
            shiftReleaseAt = now.AddMilliseconds(ShiftPulseMilliseconds);
            shiftCooldownUntil = shiftReleaseAt.AddMilliseconds(ShiftCooldownMilliseconds);
            confirmedShiftCooldownApplied = false;
            shiftTransitionStartedAt = now;
            Volatile.Write(ref expectedGearAfterShift, expectedGear);
            Volatile.Write(ref shiftTransitionActive, 1);
        }
        // Close the torque gate in the same synchronous call that wrote the
        // shift byte; waiting for the next 50 Hz publication is unsafe.
        GodspeedIntentHub.PublishTransmission(true, false, false);
        SetDecision(decision);
        UpdateFlatShiftThrottle(telemetry, now);
        LogInfo("ShiftDecision", decision,
            $"command={command};current_gear={telemetry.CurrentGear};current_rpm={telemetry.EngineRpm:0};predicted_landing_rpm={(float.IsFinite(predictedLandingRpm) ? predictedLandingRpm.ToString("0") : "n/a")};throttle_intent={telemetry.Throttle:0.000};telemetry_sequence={telemetry.Sequence}");
    }

    private void UpdateShiftPulse(DateTime now, TelemetrySnapshot telemetry)
    {
        lock (memoryLock)
        {
            if (activeShift == ShiftCommand.None)
                return;
            if (now < shiftReleaseAt)
            {
                if (!WriteShiftButtonsUnsafe(activeShift == ShiftCommand.Up, activeShift == ShiftCommand.Down,
                        activeShift == ShiftCommand.FirstGear))
                {
                    activeShift = ShiftCommand.None;
                    shiftReleaseAt = DateTime.MinValue;
                }
                return;
            }
            WriteShiftButtonsUnsafe(false, false);
            activeShift = ShiftCommand.None;
            shiftReleaseAt = DateTime.MinValue;
        }
    }

    private bool IsShiftBusy(DateTime now)
    {
        lock (memoryLock)
            return activeShift != ShiftCommand.None || now < shiftCooldownUntil;
    }

    private void ApplyConfirmedShiftCooldown(DateTime now)
    {
        lock (memoryLock)
        {
            // The 500 ms debounce remains in force when a keypress is not
            // confirmed. Once fresh telemetry shows the requested gear and
            // the 70 ms button pulse has been released, the next sequential
            // decision can proceed sooner without a blind double-shift.
            if (confirmedShiftCooldownApplied || activeShift != ShiftCommand.None
                || Volatile.Read(ref shiftTransitionActive) != 0)
                return;
            if (shiftCooldownUntil > now)
                shiftCooldownUntil = now.AddMilliseconds(ConfirmedShiftCooldownMilliseconds);
            confirmedShiftCooldownApplied = true;
        }
    }

    private bool EnsureControllerMap(DateTime now)
    {
        if (!IsEnabled)
            return false;

        lock (memoryLock)
        {
            if (controllerView is not null)
                return true;
            if (now < nextMapOpenAttempt)
                return false;
            nextMapOpenAttempt = now.AddSeconds(1);

            try
            {
                ControllerLayout layout = CalculateControllerLayout();
                MemoryMappedFile map = MemoryMappedFile.OpenExisting(ControllerMapName, MemoryMappedFileRights.ReadWrite);
                MemoryMappedViewAccessor view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
                long actualCapacity = view.Capacity;
                long requiredCapacity = Math.Max(layout.Capacity,
                    Math.Max(layout.FirstGearOffset, Math.Max(layout.ShiftUpOffset, layout.ShiftDownOffset)) + 1);
                if (actualCapacity < requiredCapacity)
                {
                    view.Dispose();
                    map.Dispose();
                    throw new InvalidDataException(
                        $"SCSControls capacity {actualCapacity} is smaller than the active ETS2LA layout ({requiredCapacity} bytes).");
                }

                controllerMap = map;
                controllerView = view;
                shiftUpOffset = layout.ShiftUpOffset;
                shiftDownOffset = layout.ShiftDownOffset;
                shiftFirstOffset = layout.FirstGearOffset;
                SetControllerStatus($"Connected ({view.Capacity} bytes; up={shiftUpOffset}, down={shiftDownOffset})");
                LogInfo("Controller", $"Connected to {ControllerMapName}",
                    $"capacity={view.Capacity};gearup_offset={shiftUpOffset};geardown_offset={shiftDownOffset}");
                return true;
            }
            catch (FileNotFoundException)
            {
                SetControllerStatus("Waiting for scs-sdk-controller");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidDataException)
            {
                CloseControllerMapUnsafe();
                SetControllerStatus($"Controller unavailable: {error.Message}");
            }
            return false;
        }
    }

    // memoryLock must be held by the caller.
    private bool WriteShiftButtonsUnsafe(bool shiftUp, bool shiftDown, bool selectFirst = false)
    {
        if (!IsEnabled)
            return false;

        if (controllerView is null || shiftUpOffset < 0 || shiftDownOffset < 0 || shiftFirstOffset < 0)
            return false;
        try
        {
            // Only these two one-byte booleans are touched. No analog offset
            // (steering, throttle, brake or clutch) is ever read or written.
            controllerView.Write(shiftUpOffset, shiftUp ? (byte)1 : (byte)0);
            controllerView.Write(shiftDownOffset, shiftDown ? (byte)1 : (byte)0);
            controllerView.Write(shiftFirstOffset, selectFirst ? (byte)1 : (byte)0);
            shiftButtonsLatched = shiftUp || shiftDown || selectFirst;
            return true;
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            CloseControllerMapUnsafe();
            SetControllerStatus("Controller connection lost");
            return false;
        }
    }

    private void ReleaseShiftButtons()
    {
        lock (memoryLock)
        {
            if (shiftButtonsLatched && controllerView is not null && shiftUpOffset >= 0 && shiftDownOffset >= 0)
            {
                try
                {
                    controllerView.Write(shiftUpOffset, (byte)0);
                    controllerView.Write(shiftDownOffset, (byte)0);
                    controllerView.Write(shiftFirstOffset, (byte)0);
                    shiftButtonsLatched = false;
                }
                catch { }
            }
            activeShift = ShiftCommand.None;
            shiftReleaseAt = DateTime.MinValue;
        }
    }

    private void UpdateFlatShiftThrottle(TelemetrySnapshot telemetry, DateTime now)
    {
        GodspeedIntentSnapshot ecosystem = GodspeedIntentHub.Read();
        bool shifting;
        lock (memoryLock)
            shifting = activeShift != ShiftCommand.None && now < shiftReleaseAt;

        bool safeToHold = IsEnabled && ApplicationState.Current.EnableAssists
            && automaticShiftingEnabled && shifting
            && telemetry.Sequence != 0
            && now - telemetry.ReceivedAt < TimeSpan.FromMilliseconds(TelemetryTimeoutMilliseconds)
            && telemetry.SdkActive && !telemetry.Paused && telemetry.EngineEnabled
            && telemetry.CurrentGear > 0
            && telemetry.SpeedMetresPerSecond > StandstillSpeedMetresPerSecond
            && telemetry.Clutch <= .5f
            && !ecosystem.TollActive && !ecosystem.CruiseEmergency
            && !IsCruiseCoastingOrBraking(now)
            && !GodspeedOutputCompatibility.IsBrakingOrRetarding(GameOutput.Current, FlatShiftChannelId)
            && GetActiveEngineSpec() is not null;

        if (!safeToHold)
        {
            ReleaseFlatShiftThrottle();
            return;
        }

        lock (flatShiftOutputLock)
        {
            if (!IsEnabled || !ApplicationState.Current.EnableAssists)
                return;

            ecosystem = GodspeedIntentHub.Read();
            if (ecosystem.TollActive || ecosystem.CruiseEmergency
                || IsCruiseCoastingOrBraking(now)
                || GodspeedOutputCompatibility.IsBrakingOrRetarding(GameOutput.Current, FlatShiftChannelId))
            {
                GodspeedOutputCompatibility.RemoveChannel(GameOutput.Current, FlatShiftChannelId);
                flatShiftChannelActive = false;
                return;
            }

            Events.Current.Publish(GameOutput.Current.EventString, new ControlEvent
            {
                ChannelDefinition = new ControlChannelDefinition { Id = FlatShiftChannelId, Timeout = .07f },
                Properties = new ControlProperties { Weight = FlatShiftWeight },
                Variables = new ControlVariables { aforward = FlatShiftThrottle }
            });
            flatShiftChannelActive = true;
        }
    }

    private void ReleaseFlatShiftThrottle()
    {
        lock (flatShiftOutputLock)
        {
            if (!flatShiftChannelActive)
                return;
            GodspeedOutputCompatibility.RemoveChannel(GameOutput.Current, FlatShiftChannelId);
            flatShiftChannelActive = false;
        }
    }

    private void StopWorkerAndReleaseResources()
    {
        CancellationTokenSource? cancellation = workerCancellation;
        Task? task = workerTask;
        workerCancellation = null;
        workerTask = null;
        cancellation?.Cancel();
        if (task is not null)
            pendingWorkerStop = task;
        if (task is { IsCompleted: false })
            _ = task.ContinueWith(_ => cancellation?.Dispose(), TaskScheduler.Default);
        else
            cancellation?.Dispose();
        ReleaseShiftButtons();
        ReleaseFlatShiftThrottle();
        Volatile.Write(ref shiftTransitionActive, 0);
        Volatile.Write(ref expectedGearAfterShift, 0);
        lock (memoryLock)
            CloseControllerMapUnsafe();
    }

    private void CloseControllerMapUnsafe()
    {
        controllerView?.Dispose();
        controllerMap?.Dispose();
        controllerView = null;
        controllerMap = null;
        shiftButtonsLatched = false;
        shiftUpOffset = -1;
        shiftDownOffset = -1;
        shiftFirstOffset = -1;
    }

    private void ResetRuntimeState()
    {
        lock (telemetryLock)
        {
            latestTelemetry = TelemetrySnapshot.Empty;
            cachedForwardRatios = [];
        }
        lock (controlIntentLock)
        {
            latestAutomatedControlIntent = AutomatedControlIntent.Empty;
            latestStockControlIntent = AutomatedControlIntent.Empty;
        }
        lastProcessedTelemetrySequence = 0;
        lastDynamicsTelemetrySequence = 0;
        previousSpeed = 0f;
        latestSpeedRate = 0f;
        previousDynamicsAt = DateTime.MinValue;
        gradeBaseAt = DateTime.MinValue;
        latestRoadGrade = 0f;
        roadGradeValid = false;
        uphillReleaseEligibleSince = DateTime.MinValue;
        shiftReleaseAt = DateTime.MinValue;
        shiftCooldownUntil = DateTime.MinValue;
        confirmedShiftCooldownApplied = false;
        activeShift = ShiftCommand.None;
        shiftTransitionStartedAt = DateTime.MinValue;
        directFirstGearAttemptedAt = DateTime.MinValue;
        Volatile.Write(ref shiftTransitionActive, 0);
        Volatile.Write(ref expectedGearAfterShift, 0);
        uphillFirstGearHoldActive = false;
        uphillHoldReleasedForCurrentDrag = false;
        displayedPredictedRpm = displayedUpshiftRpm = 0f;
        nextMapOpenAttempt = DateTime.MinValue;
        SetControllerStatus("Waiting for Local\\SCSControls");
        SetDecision("Waiting for telemetry");
    }

    private void RenderStatusWindow()
    {
        if (!IsEnabled)
            return;

        Vector4 labelColor = new(.7f, .7f, .7f, 1f);
        Vector4 valueColor = new(1f, 1f, 1f, 1f);
        Vector4 inactiveColor = new(1f, .4f, .4f, 1f);

        TelemetrySnapshot telemetry;
        lock (telemetryLock)
            telemetry = latestTelemetry;
        string mapStatus;
        string decision;
        float predictedRpm;
        float upshiftRpm;
        lock (statusLock)
        {
            mapStatus = controllerStatus;
            decision = lastDecision;
            predictedRpm = displayedPredictedRpm;
            upshiftRpm = displayedUpshiftRpm;
        }

        ManualTransmissionAdjustmentSettings currentSettings = GetAdjustmentSettings();
        if (!uiProbeRenderLogged || uiProbeLastDetailedMode != currentSettings.DetailedDiagnosticMode
            || !string.Equals(uiProbeLastBrand, currentSettings.SelectedBrand, StringComparison.Ordinal))
        {
            uiProbeRenderLogged = true;
            uiProbeLastDetailedMode = currentSettings.DetailedDiagnosticMode;
            uiProbeLastBrand = currentSettings.SelectedBrand;
            LogUiProbe("ImGui.Render", "state_change");
        }
        if (!currentSettings.DetailedDiagnosticMode)
        {
            EngineSpec? activeEngine = GetActiveEngineSpec();
            bool liveTelemetry = telemetry.Sequence > 0
                && (DateTime.UtcNow - telemetry.ReceivedAt).TotalMilliseconds < TelemetryTimeoutMilliseconds
                && telemetry.EngineRpmMax > 0f;
            bool engineVerified = activeEngine is not null && liveTelemetry
                && Math.Abs(telemetry.EngineRpmMax - activeEngine.RpmLimit) <= 25f;
            float pulse = .5f + .5f * MathF.Sin((float)ImGui.GetTime() * 2f);
            Vector4 statusColor = !automaticShiftingEnabled
                ? new Vector4(1f, .4f, .4f, 1f)
                : engineVerified
                    ? new Vector4(.3f, 1f, .4f, 1f)
                    : new Vector4(1f, .85f, .2f, 1f);

            const string header = "Automatic Manual Transmission";
            const float circleWidth = 22f;
            float contentWidth = ImGui.CalcTextSize(header).X + ImGui.GetStyle().ItemSpacing.X + circleWidth;
            ImGui.TextColored(valueColor, header);
            ImGui.SameLine();
            Vector2 circleCenter = ImGui.GetCursorScreenPos()
                + new Vector2(circleWidth * .5f, ImGui.GetTextLineHeight() * .5f);
            Vector4 outerGlow = statusColor;
            outerGlow.W = .04f + .08f * pulse;
            Vector4 innerGlow = statusColor;
            innerGlow.W = .08f + .17f * pulse;
            Vector4 lightColor = statusColor;
            float brightness = .4f + .6f * pulse;
            lightColor.X *= brightness;
            lightColor.Y *= brightness;
            lightColor.Z *= brightness;
            ImDrawListPtr drawList = ImGui.GetWindowDrawList();
            drawList.AddCircleFilled(circleCenter, 10f, ImGui.ColorConvertFloat4ToU32(outerGlow));
            drawList.AddCircleFilled(circleCenter, 7.5f, ImGui.ColorConvertFloat4ToU32(innerGlow));
            drawList.AddCircleFilled(circleCenter, 5.5f, ImGui.ColorConvertFloat4ToU32(lightColor));
            ImGui.Dummy(new Vector2(circleWidth, ImGui.GetTextLineHeight()));

            string gear = telemetry.CurrentGear < 0 ? "R"
                : telemetry.CurrentGear == 0 ? "N" : telemetry.CurrentGear.ToString();
            ImGui.TextColored(labelColor, "Gears:"); ImGui.SameLine();
            ImGui.TextColored(valueColor, $"{gear} / {telemetry.ForwardGearCount}");

            float maxRpm = float.IsFinite(telemetry.EngineRpmMax) && telemetry.EngineRpmMax > 0f
                ? telemetry.EngineRpmMax : 1f;
            float currentRpm = float.IsFinite(telemetry.EngineRpm)
                ? Math.Clamp(telemetry.EngineRpm, 0f, maxRpm) : 0f;
            Vector4 blue = new(.2f, .55f, 1f, 1f);
            Vector4 yellow = new(1f, .85f, .2f, 1f);
            Vector4 green = new(.3f, 1f, .4f, 1f);
            Vector4 red = new(1f, .4f, .4f, 1f);
            Vector4 rpmColor = blue;
            if (float.IsFinite(upshiftRpm) && upshiftRpm > 0f)
            {
                float blueEnd = Math.Clamp(upshiftRpm - 200f, 0f, maxRpm);
                float yellowEnd = Math.Clamp(upshiftRpm, blueEnd, maxRpm);
                float greenEnd = Math.Clamp(upshiftRpm + 200f, yellowEnd, maxRpm);
                if (currentRpm > blueEnd && currentRpm <= yellowEnd)
                    rpmColor = Vector4.Lerp(blue, yellow,
                        (currentRpm - blueEnd) / Math.Max(1f, yellowEnd - blueEnd));
                else if (currentRpm > yellowEnd && currentRpm <= greenEnd)
                    rpmColor = Vector4.Lerp(yellow, green,
                        (currentRpm - yellowEnd) / Math.Max(1f, greenEnd - yellowEnd));
                else if (currentRpm > greenEnd)
                    rpmColor = Vector4.Lerp(green, red,
                        (currentRpm - greenEnd) / Math.Max(1f, maxRpm - greenEnd));
            }
            ImGui.TextColored(labelColor, "Engine RPM:");
            DrawGlowingProgressBar(currentRpm / maxRpm, contentWidth, rpmColor);

            float throttle = float.IsFinite(telemetry.Throttle)
                ? Math.Clamp(telemetry.Throttle, 0f, 1f) : 0f;
            ImGui.TextColored(labelColor, "Throttle:");
            DrawGlowingProgressBar(throttle, contentWidth, blue);
        }
        else
        {
            ImGui.TextColored(valueColor, VersionHeading);
            ImGui.Separator();
            if (!ImGui.BeginTabBar("ManualTransmissionTabs"))
                return;
            if (ImGui.BeginTabItem("Live Telemetry"))
            {
                ImGui.TextColored(labelColor, "Gear:"); ImGui.SameLine();
                ImGui.TextColored(valueColor, $"{telemetry.CurrentGear} / {telemetry.ForwardGearCount}");
                ImGui.TextColored(labelColor, "Speed:"); ImGui.SameLine();
                ImGui.TextColored(valueColor, $"{telemetry.SpeedMetresPerSecond * 3.6f:0.0} km/h");
                ImGui.TextColored(labelColor, "Engine RPM:"); ImGui.SameLine();
                ImGui.TextColored(valueColor, $"{telemetry.EngineRpm:0} / Idle {telemetry.EngineRpmIdle:0} / Max {telemetry.EngineRpmMax:0}");
                float currentLoadPercent = float.IsFinite(telemetry.EngineLoad)
                    ? Math.Clamp(telemetry.EngineLoad, 0f, 1f) * 100f : 0f;
                float cruiseIntentThrottle = float.IsFinite(telemetry.AutomatedThrottle)
                    ? Math.Clamp(telemetry.AutomatedThrottle, 0f, 1f) * 100f : 0f;
                ImGui.TextColored(labelColor, "Throttle Load:"); ImGui.SameLine();
                ImGui.TextColored(valueColor, $"{currentLoadPercent:F0}% / 100% (Cmd: {cruiseIntentThrottle:F0}%)");
                ImGui.TextColored(labelColor, "Cruise intent:"); ImGui.SameLine();
                ImGui.TextColored(valueColor, $"throttle {telemetry.AutomatedThrottle:P0} / brake {telemetry.AutomatedBrake:P0}");
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Powertrain"))
            {
                ImGui.TextColored(labelColor, "Engine torque:"); ImGui.SameLine();
                ImGui.TextColored(telemetry.EngineTorque > 0f ? valueColor : inactiveColor,
                    telemetry.EngineTorque > 0f
                        ? $"{telemetry.EngineTorque:0} Nm (selected profile)"
                        : "Select an engine profile in Adjustments");
                ImGui.TextColored(labelColor, "Differential ratio:"); ImGui.SameLine();
                ImGui.TextColored(valueColor, $"{telemetry.DifferentialRatio:0.000}");
                ImGui.TextColored(labelColor, "Upshift trigger:"); ImGui.SameLine();
                ImGui.TextColored(valueColor, $"{upshiftRpm:0} RPM");
                ImGui.TextColored(labelColor, "Predicted landing:"); ImGui.SameLine();
                ImGui.TextColored(valueColor, $"{predictedRpm:0} RPM");
                ImGui.TextColored(labelColor, "Uphill first-gear hold:"); ImGui.SameLine();
                ImGui.TextColored(uphillFirstGearHoldActive ? valueColor : inactiveColor,
                    uphillFirstGearHoldActive ? "ACTIVE" : "Ready");
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Diagnostics"))
            {
                RenderEngineVerification(telemetry);
                ImGui.TextColored(labelColor, "Controller:"); ImGui.SameLine();
                ImGui.TextColored(mapStatus.StartsWith("Connected", StringComparison.Ordinal)
                    ? valueColor : inactiveColor, mapStatus);
                bool telemetryConnected = telemetry.Sequence > 0
                    && (DateTime.UtcNow - telemetry.ReceivedAt).TotalMilliseconds < TelemetryTimeoutMilliseconds;
                ImGui.TextColored(labelColor, "Telemetry:"); ImGui.SameLine();
                ImGui.TextColored(telemetryConnected ? valueColor : inactiveColor,
                    telemetryConnected ? "Connected" : "Waiting");
                ImGui.Separator();
                ImGui.TextColored(labelColor, "Decision:"); ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, valueColor);
                ImGui.TextWrapped(decision);
                ImGui.PopStyleColor();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Configuration"))
            {
                ConfigurationModeSnapshot configuration = GetConfigurationModeSnapshot();
                if (!configuration.NeedsConfiguration
                    && !configuration.LimitMeasured && !configuration.PowerBandMeasured
                    && configuration.RunningTest == ConfigurationTestKind.None)
                {
                    ImGui.TextColored(labelColor, "No engine calibration is needed.");
                }
                else
                {
                    DrawConfigurationTestRow("RPM limit", ConfigurationTestKind.StationaryLimit,
                        configuration.NeedsLimitTest,
                        configuration.StationaryConditionsMet, configuration.RunningTest);
                    DrawConfigurationTestRow("RPM range", ConfigurationTestKind.RollingPowerBand,
                        configuration.NeedsPowerBandTest,
                        configuration.RollingConditionsMet && !configuration.NeedsLimitTest,
                        configuration.RunningTest);
                    if (configuration.RunningTest != ConfigurationTestKind.None)
                        ImGui.TextColored(valueColor,
                            $"{configuration.RunningTest}: {configuration.SecondsRemaining:0.0}s");
                }
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private void DrawConfigurationTestRow(string label, ConfigurationTestKind kind,
        bool pending, bool conditionsMet, ConfigurationTestKind runningTest)
    {
        bool running = runningTest == kind;
        Vector2 position = ImGui.GetCursorScreenPos();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        if (pending && !running)
        {
            Vector4 warning = new(1f, .4f, .4f, 1f);
            drawList.AddTriangleFilled(position + new Vector2(8f, 0f),
                position + new Vector2(0f, 16f), position + new Vector2(16f, 16f),
                ImGui.ColorConvertFloat4ToU32(warning));
            drawList.AddText(position + new Vector2(6f, 3f), 0xFF202020, "!");
        }
        else
        {
            float pulse = running ? .5f + .5f * MathF.Sin((float)ImGui.GetTime() * 2.6f) : 1f;
            Vector4 green = new(.3f, 1f, .4f, .4f + .6f * pulse);
            drawList.AddCircleFilled(position + new Vector2(8f, 8f), 5f,
                ImGui.ColorConvertFloat4ToU32(green));
        }
        ImGui.Dummy(new Vector2(18f, 18f));
        ImGui.SameLine();
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        bool canRun = pending && conditionsMet && runningTest == ConfigurationTestKind.None;
        ImGui.PushStyleColor(ImGuiCol.Button, canRun
            ? new Vector4(1f, 1f, 1f, 1f) : new Vector4(.5f, .5f, .5f, .5f));
        ImGui.PushStyleColor(ImGuiCol.Text, canRun
            ? new Vector4(.1f, .1f, .1f, 1f) : new Vector4(1f, 1f, 1f, .5f));
        ImGui.BeginDisabled(!canRun);
        if (ImGui.Button($"Run##{kind}"))
            TryStartConfigurationTest(kind);
        ImGui.EndDisabled();
        ImGui.PopStyleColor(2);
    }

    private static void DrawGlowingProgressBar(float value, float width, Vector4 color)
    {
        float level = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
        const float height = 14f;
        Vector2 start = ImGui.GetCursorScreenPos();
        Vector2 end = start + new Vector2(width, height);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        if (level > 0f)
        {
            Vector2 filledEnd = start + new Vector2(width * level, height);
            Vector4 outerGlow = color;
            outerGlow.W = .02f + .16f * level;
            Vector4 innerGlow = color;
            innerGlow.W = .05f + .22f * level;
            drawList.AddRectFilled(start - new Vector2(3f, 3f),
                filledEnd + new Vector2(3f, 3f), ImGui.ColorConvertFloat4ToU32(outerGlow), 6f);
            drawList.AddRectFilled(start - new Vector2(1.5f, 1.5f),
                filledEnd + new Vector2(1.5f, 1.5f), ImGui.ColorConvertFloat4ToU32(innerGlow), 5f);
        }

        drawList.AddRectFilled(start, end,
            ImGui.ColorConvertFloat4ToU32(new Vector4(.12f, .12f, .12f, .65f)), 4f);
        if (level > 0f)
        {
            float brightness = .35f + .65f * level;
            Vector4 fillColor = new(color.X * brightness, color.Y * brightness,
                color.Z * brightness, .3f + .7f * level);
            drawList.AddRectFilled(start, start + new Vector2(width * level, height),
                ImGui.ColorConvertFloat4ToU32(fillColor), 4f);
        }
        ImGui.Dummy(new Vector2(width, height));
    }

    private void RenderEngineVerification(TelemetrySnapshot telemetry)
    {
        EngineSpec? active = GetActiveEngineSpec();
        if (active is null)
        {
            ImGui.TextColored(new Vector4(1f, .75f, .2f, 1f), "Select a complete engine specification to enable shifting");
            return;
        }

        ImGui.Text($"Selected curve: {active.Brand} {active.Horsepower} HP / {active.TorqueNm} Nm / {active.RpmLimit} RPM");
        bool liveTelemetry = telemetry.Sequence > 0
            && (DateTime.UtcNow - telemetry.ReceivedAt).TotalMilliseconds < TelemetryTimeoutMilliseconds
            && telemetry.EngineRpmMax > 0f;
        if (!liveTelemetry)
        {
            ImGui.Text("[ENGINE VERIFICATION: AWAITING TELEMETRY]");
        }
        else if (Math.Abs(telemetry.EngineRpmMax - active.RpmLimit) <= 25f)
        {
            ImGui.TextColored(new Vector4(.3f, 1f, .4f, 1f), "[ENGINE VERIFIED: OK]");
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, .85f, .2f, 1f), "[ENGINE OVERRIDE: ACTIVE]");
        }
    }

    private EngineSpec? GetActiveEngineSpec() => Volatile.Read(ref cachedActiveEngineSpec);

    private void RefreshEngineSpecCache()
    {
        EngineSpec? raw = ResolveActiveEngineSpec();
        string key;
        lock (profileLock)
            key = selectedBrand == CustomBrand
                ? $"custom:{customHorsepower}:{customTorqueNm}:{customRpmLimit}:{customRpmRangeLow}:{customRpmRangeHigh}"
                : $"catalog:{selectedProfileIndex}";
        lock (configurationLock)
        {
            if (activeConfigurationKey != key)
            {
                if (activeConfiguration is { RunningTest: not ConfigurationTestKind.None } prior)
                {
                    prior.RunningTest = ConfigurationTestKind.None;
                    prior.Message = "Profile changed; configuration test cancelled.";
                }
                activeConfigurationKey = key;
                if (raw is not null && !configurationSessions.TryGetValue(key, out activeConfiguration))
                {
                    activeConfiguration = new ConfigurationSession(raw);
                    configurationSessions.Add(key, activeConfiguration);
                }
            }
            if (raw is null)
                activeConfiguration = null;
            else if (activeConfiguration is null)
            {
                activeConfiguration = new ConfigurationSession(raw);
                configurationSessions[key] = activeConfiguration;
            }
            Volatile.Write(ref cachedActiveEngineSpec, raw is null ? null
                : BuildPlayableEngineSpec(raw, activeConfiguration!));
        }
    }

    private EngineSpec? ResolveActiveEngineSpec()
    {
        lock (profileLock)
        {
            if (selectedBrand != CustomBrand)
            {
                if (selectedProfileIndex < 0 || selectedProfileIndex >= EngineCatalog.Profiles.Count)
                    return null;
                EngineSpec selected = EngineCatalog.Profiles[selectedProfileIndex];
                if (selected.Brand != selectedBrand || selected.Horsepower != selectedHorsepower)
                    return null;
                if (!IncompleteEnginesByProfile.TryGetValue(selectedProfileIndex, out var incomplete))
                    return selected;
                // Read the raw missing-field flags before playable fallbacks are applied.
                return selected with
                {
                    RpmLimit = incomplete.RpmLimit == 0 ? 0 : selected.RpmLimit,
                    RpmRangeLow = incomplete.RpmRangeLow == 0 ? 0 : selected.RpmRangeLow,
                    RpmRangeHigh = incomplete.RpmRangeHigh == 0 ? 0 : selected.RpmRangeHigh
                };
            }

            if (customHorsepower <= 0 || customTorqueNm <= 0
                || (customRpmRangeLow is > 0 and <= 50)
                || (customRpmRangeLow > 0 && customRpmRangeHigh > 0
                    && customRpmRangeHigh < customRpmRangeLow)
                || (customRpmLimit > 0 && customRpmRangeHigh > customRpmLimit))
                return null;
            int kw = (int)Math.Round(customHorsepower * .73549875d);
            if (cachedCustomEngine is not { } cached
                || cached.Horsepower != customHorsepower || cached.Kw != kw
                || cached.TorqueNm != customTorqueNm || cached.RpmLimit != customRpmLimit
                || cached.RpmRangeLow != customRpmRangeLow || cached.RpmRangeHigh != customRpmRangeHigh)
                cachedCustomEngine = new EngineSpec("Custom", customHorsepower, kw, customTorqueNm,
                    customRpmLimit, customRpmRangeLow, customRpmRangeHigh);
            return cachedCustomEngine;
        }
    }

    private static EngineSpec BuildPlayableEngineSpec(EngineSpec raw, ConfigurationSession session)
    {
        int limit = raw.RpmLimit > 0 ? raw.RpmLimit
            : session.MeasuredLimit ?? Math.Max(1800,
                Math.Max(raw.RpmRangeHigh + 400, raw.RpmRangeLow + 500));
        int low = raw.RpmRangeLow > 0 ? raw.RpmRangeLow
            : session.MeasuredLow ?? Math.Max(700,
                raw.RpmRangeHigh > 0 ? (int)MathF.Round(raw.RpmRangeHigh * .70f) : 900);
        int high = raw.RpmRangeHigh > 0 ? raw.RpmRangeHigh
            : session.MeasuredHigh ?? Math.Min(limit - 150, Math.Max(1400, low + 300));
        if (raw.RpmRangeLow == 0)
            low = Math.Min(low, Math.Max(600, high - 150));
        if (raw.RpmRangeHigh == 0)
            high = Math.Clamp(high, low + 150, Math.Max(low + 150, limit - 100));
        return raw with { RpmLimit = limit, RpmRangeLow = low, RpmRangeHigh = high };
    }

    private static bool IsFreshConfigurationTelemetry(TelemetrySnapshot telemetry)
        => telemetry.Sequence != 0 && telemetry.SdkActive && !telemetry.Paused
            && telemetry.EngineEnabled
            && DateTime.UtcNow - telemetry.ReceivedAt < TimeSpan.FromMilliseconds(TelemetryTimeoutMilliseconds)
            && float.IsFinite(telemetry.EngineRpm)
            && float.IsFinite(telemetry.SpeedMetresPerSecond);

    private static bool IsStationaryTestSafe(TelemetrySnapshot telemetry)
        => telemetry.SpeedMetresPerSecond <= .05f
            && telemetry.CurrentGear == 0 && telemetry.ParkingBrake;

    private static bool IsRollingTestSafe(TelemetrySnapshot telemetry, out string reason)
    {
        if (!telemetry.TrailerAttached || telemetry.CargoMassKilograms < 15_000f)
        {
            reason = "Attach a loaded trailer with at least 15 t of SDK-reported cargo.";
            return false;
        }
        if (!telemetry.RoadGradeValid || Math.Abs(telemetry.RoadGrade) > .01f)
        {
            reason = "Move to a level road; measured grade must stay within 1%.";
            return false;
        }
        if (telemetry.ParkingBrake)
        {
            reason = "Release the parking brake before the rolling test.";
            return false;
        }
        if (telemetry.CurrentGear <= 0 || telemetry.CurrentGear > telemetry.ForwardRatios.Length
            || telemetry.Clutch > .05f)
        {
            reason = "Engage a direct-drive gear and release the clutch.";
            return false;
        }
        float ratio = telemetry.ForwardRatios[telemetry.CurrentGear - 1];
        if (!float.IsFinite(ratio) || Math.Abs(ratio - 1f) > ConfigurationGearRatioTolerance)
        {
            reason = "The engaged gear must have a verified ratio within 0.05 of 1:1.";
            return false;
        }
        if (telemetry.SpeedMetresPerSecond < 2f
            || telemetry.EngineRpm < telemetry.EngineRpmIdle + 100f)
        {
            reason = "Begin a low-RPM roll above 7 km/h before starting the test.";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private void CancelConfigurationTest(string message)
    {
        lock (configurationLock)
        {
            if (activeConfiguration is not { RunningTest: not ConfigurationTestKind.None } session)
                return;
            session.RunningTest = ConfigurationTestKind.None;
            session.Message = message;
            session.Samples.Clear();
        }
        LogWarn("Configuration", message);
    }

    // Returns true while calibration owns this plugin's shift scheduler. Driver
    // inputs cannot be locked by ETS2LA; a changed gear aborts the test instead.
    private bool AdvanceConfigurationTest(TelemetrySnapshot telemetry)
    {
        bool completed = false;
        string? outcome = null;
        lock (configurationLock)
        {
            ConfigurationSession? session = activeConfiguration;
            if (session is null || session.RunningTest == ConfigurationTestKind.None)
                return false;
            ConfigurationTestKind test = session.RunningTest;
            if (!IsFreshConfigurationTelemetry(telemetry))
                outcome = "Telemetry disconnected; configuration test cancelled.";
            else if (test == ConfigurationTestKind.StationaryLimit
                && !IsStationaryTestSafe(telemetry))
                outcome = "Truck moved, left neutral, or parking brake released; test cancelled.";
            else if (test == ConfigurationTestKind.RollingPowerBand
                && (telemetry.CurrentGear != session.LockedGear
                    || !IsRollingTestSafe(telemetry, out _)))
                outcome = "Gear, trailer load, or road conditions changed; test cancelled.";
            if (outcome is not null)
            {
                session.RunningTest = ConfigurationTestKind.None;
                session.Message = outcome;
                session.Samples.Clear();
            }
            else
            {
                long elapsed = Environment.TickCount64 - session.StartedAtTick;
                if (elapsed < 5_000)
                {
                    if (telemetry.PhysicalThrottle >= ConfigurationFullThrottle)
                        session.SawFullThrottle = true;
                }
                else if (!session.SawFullThrottle)
                {
                    outcome = "Full physical throttle was not reached during the preparation window.";
                    session.RunningTest = ConfigurationTestKind.None;
                    session.Message = outcome;
                }
                else if (telemetry.PhysicalThrottle < .95f)
                {
                    outcome = "Physical throttle fell below 95% during measurement; test cancelled.";
                    session.RunningTest = ConfigurationTestKind.None;
                    session.Message = outcome;
                }
                else
                {
                    if (telemetry.Sequence != session.LastSampleSequence)
                    {
                        session.LastSampleSequence = telemetry.Sequence;
                        if (test == ConfigurationTestKind.StationaryLimit)
                            session.MaximumObservedRpm = Math.Max(session.MaximumObservedRpm,
                                telemetry.EngineRpm);
                        else
                            RecordRollingSample(session, telemetry);
                    }
                    if (elapsed >= 10_000)
                    {
                        if (test == ConfigurationTestKind.StationaryLimit)
                        {
                            int measured = (int)MathF.Round(session.MaximumObservedRpm);
                            if (measured > telemetry.EngineRpmIdle + 300f
                                && measured is >= 800 and <= 6000
                                && measured >= Math.Max(session.Raw.RpmRangeHigh,
                                    session.Raw.RpmRangeLow) + 100
                                && (telemetry.EngineRpmMax <= 0f
                                    || measured <= telemetry.EngineRpmMax * 1.10f))
                            {
                                session.MeasuredLimit = measured;
                                completed = true;
                                outcome = $"Measured RPM ceiling: {measured} RPM (session only).";
                            }
                            else
                                outcome = "RPM ceiling was not observed reliably; fallback retained.";
                        }
                        else if (TryCalculateMeasuredPowerBand(session, out int low, out int high)
                            && (session.Raw.RpmRangeLow == 0 || high > session.Raw.RpmRangeLow + 150)
                            && (session.Raw.RpmRangeHigh == 0 || low < session.Raw.RpmRangeHigh - 150)
                            && high < (session.Raw.RpmLimit > 0
                                ? session.Raw.RpmLimit : session.MeasuredLimit ?? 2000) - 100)
                        {
                            if (session.Raw.RpmRangeLow == 0)
                                session.MeasuredLow = low;
                            if (session.Raw.RpmRangeHigh == 0)
                                session.MeasuredHigh = high;
                            completed = true;
                            outcome = $"Estimated acceleration-derived band: {low}–{high} RPM (session only).";
                        }
                        else
                            outcome = "No reliable power onset and drop-off were observed; fallback retained.";
                        session.RunningTest = ConfigurationTestKind.None;
                        session.Message = outcome;
                        session.Samples.Clear();
                    }
                }
            }
        }
        if (completed)
            RefreshEngineSpecCache();
        if (outcome is not null)
            LogInfo("Configuration", outcome);
        return true;
    }

    private static void RecordRollingSample(ConfigurationSession session, TelemetrySnapshot telemetry)
    {
        if (session.LastSampleAt != DateTime.MinValue)
        {
            float dt = (float)(telemetry.ReceivedAt - session.LastSampleAt).TotalSeconds;
            if (dt is >= .01f and <= .2f)
            {
                float acceleration = (telemetry.SpeedMetresPerSecond - session.LastSpeed) / dt;
                session.SmoothedAcceleration = session.Samples.Count == 0
                    ? acceleration : session.SmoothedAcceleration * .75f + acceleration * .25f;
                float powerProxy = Math.Max(0f,
                    session.SmoothedAcceleration * telemetry.SpeedMetresPerSecond);
                if (float.IsFinite(powerProxy) && telemetry.EngineRpm > telemetry.EngineRpmIdle)
                    session.Samples.Add(new CalibrationSample(telemetry.EngineRpm, powerProxy));
            }
        }
        session.LastSpeed = telemetry.SpeedMetresPerSecond;
        session.LastSampleAt = telemetry.ReceivedAt;
    }

    private static bool TryCalculateMeasuredPowerBand(ConfigurationSession session,
        out int low, out int high)
    {
        low = high = 0;
        List<CalibrationSample> samples = session.Samples;
        if (samples.Count < 30)
            return false;
        int peakIndex = 0;
        for (int i = 1; i < samples.Count; i++)
            if (samples[i].PowerProxy > samples[peakIndex].PowerProxy)
                peakIndex = i;
        float peak = samples[peakIndex].PowerProxy;
        if (peak < .2f || peakIndex < 5 || peakIndex > samples.Count - 6)
            return false;
        float threshold = peak * .85f;
        bool sawLower = false, sawUpper = false;
        float lower = float.MaxValue, upper = 0f;
        for (int i = 0; i < samples.Count; i++)
        {
            CalibrationSample sample = samples[i];
            if (sample.PowerProxy >= threshold)
            {
                lower = Math.Min(lower, sample.Rpm);
                upper = Math.Max(upper, sample.Rpm);
            }
            else if (i < peakIndex)
                sawLower = true;
            else if (i > peakIndex)
                sawUpper = true;
        }
        if (!sawLower || !sawUpper || upper - lower < 150f
            || lower < 500f || upper > 6000f)
            return false;
        low = (int)(MathF.Round(lower / 25f) * 25f);
        high = (int)(MathF.Round(upper / 25f) * 25f);
        return high > low;
    }

    private static string GetSettingsPath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "ETS2LA", "current", "Plugins",
            "Godspeed.ManualTransmission.settings.json");
    }

    private void LoadSettings()
    {
        try
        {
            string path = GetSettingsPath();
            if (!File.Exists(path))
                return;
            EngineProfileSettings? settings = JsonSerializer.Deserialize<EngineProfileSettings>(
                File.ReadAllText(path));
            if (settings is null || !EngineBrands.Contains(settings.SelectedBrand))
                return;
            automaticShiftingEnabled = settings.AutomaticShiftingEnabled;
            lock (profileLock)
            {
                detailedDiagnosticMode = settings.DetailedDiagnosticMode;
                selectedBrand = settings.SelectedBrand;
                selectedProfileIndex = settings.SelectedProfileIndex >= 0
                    && settings.SelectedProfileIndex < EngineCatalog.Profiles.Count
                    && EngineCatalog.Profiles[settings.SelectedProfileIndex].Brand == selectedBrand
                    ? settings.SelectedProfileIndex : -1;
                selectedHorsepower = selectedProfileIndex >= 0
                    ? EngineCatalog.Profiles[selectedProfileIndex].Horsepower : 0;
                customHorsepower = settings.CustomHorsepower;
                customTorqueNm = settings.CustomTorqueNm;
                customRpmLimit = settings.CustomRpmLimit;
                customRpmRangeLow = settings.CustomRpmRangeLow;
                customRpmRangeHigh = settings.CustomRpmRangeHigh;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            LogWarn("Settings", "Engine profile settings could not be loaded", error.Message);
        }
    }

    public void SaveSettings()
    {
        RefreshEngineSpecCache();
        try
        {
            string path = GetSettingsPath();
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            EngineProfileSettings settings;
            lock (profileLock)
                settings = new EngineProfileSettings(selectedBrand, selectedProfileIndex,
                    customHorsepower, customTorqueNm, customRpmLimit,
                    customRpmRangeLow, customRpmRangeHigh, automaticShiftingEnabled,
                    detailedDiagnosticMode);
            string json = JsonSerializer.Serialize(settings,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            LogWarn("Settings", "Engine profile settings could not be saved", error.Message);
        }
    }

    private void SetControllerStatus(string value)
    {
        lock (statusLock)
            controllerStatus = value;
    }

    private void SetDecision(string value)
    {
        bool changed;
        lock (statusLock)
        {
            changed = !string.Equals(lastDecision, value, StringComparison.Ordinal);
            lastDecision = value;
            lastDecisionCode = DecisionCode.None;
        }
        if (changed)
            LogInfo("Decision", value);
    }

    private enum DecisionCode
    {
        None,
        StoppedDownshiftGuard,
        LowSpeedDownshiftGuard,
        UphillDownshiftGuard,
        UphillFirstGearHold,
        CruiseDownshiftGuard,
        DownshiftGuard,
        UpshiftLandingGuard
    }

    // Compare a small value-state before creating text. The string is cached
    // for ImGui and logged only when the driver's decision actually changes.
    private void SetDecision(DecisionCode code, int first, int second = 0, int third = 0)
    {
        string value;
        lock (statusLock)
        {
            if (lastDecisionCode == code && lastDecisionFirst == first
                && lastDecisionSecond == second && lastDecisionThird == third)
                return;
            lastDecisionCode = code;
            lastDecisionFirst = first;
            lastDecisionSecond = second;
            lastDecisionThird = third;
            value = code switch
            {
                DecisionCode.StoppedDownshiftGuard => $"Stopped-vehicle downshift {first}->{second} waiting for valid ratio and catalog limit {third} RPM",
                DecisionCode.LowSpeedDownshiftGuard => $"Low-speed downshift {first}->{second} waiting for valid ratio and catalog limit {third} RPM",
                DecisionCode.UphillDownshiftGuard => $"Uphill downshift {first}->{second} waiting for valid ratio and catalog limit {third} RPM",
                DecisionCode.UphillFirstGearHold => $"Uphill hold: retaining gear 1 until RPM > {first} for 1.0 s",
                DecisionCode.CruiseDownshiftGuard => $"Cruise downshift {first}->{second} waiting for valid ratio and catalog limit {third} RPM",
                DecisionCode.DownshiftGuard => $"Downshift {first}->{second} waiting for valid ratio and catalog limit {third} RPM",
                DecisionCode.UpshiftLandingGuard => $"Holding gear {first}: predicted landing {second} RPM below {third}",
                _ => "Waiting for telemetry"
            };
            lastDecision = value;
        }
        LogInfo("Decision", value);
    }

    private void LogInfo(string component, string message, string? telemetrySnapshot = null)
    {
        Logger.Info($"ManualTransmission/{component}: {message}"
            + (telemetrySnapshot is null ? string.Empty : $"; {telemetrySnapshot}"));
    }

    private void LogWarn(string component, string message, string? telemetrySnapshot = null)
    {
        Logger.Warn($"ManualTransmission/{component}: {message}"
            + (telemetrySnapshot is null ? string.Empty : $"; {telemetrySnapshot}"));
    }

    private void LogError(string component, string message, string? telemetrySnapshot = null)
    {
        Logger.Error($"ManualTransmission/{component}: {message}"
            + (telemetrySnapshot is null ? string.Empty : $"; {telemetrySnapshot}"));
    }

    private static ControllerLayout CalculateControllerLayout()
    {
        // The host owns the versioned SCSControls ABI; no local reflection-order
        // reconstruction may silently move a shift command to another byte.
        _ = SCSControlsLayout.Offsets;
        return new ControllerLayout(SCSControlsLayout.Size, SCSControlsLayout.GearUpOffset,
            SCSControlsLayout.GearDownOffset, SCSControlsLayout.Gear1Offset);
    }

    private static Func<object, float>? CreateFloatReader(Type type, params string[] names)
    {
        foreach (string name in names)
        {
            FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (field?.FieldType == typeof(float))
            {
                ParameterExpression source = Expression.Parameter(typeof(object), "source");
                return Expression.Lambda<Func<object, float>>(
                    Expression.Field(Expression.Convert(source, type), field), source).Compile();
            }
        }
        return null;
    }

    private static float ReadOptionalFloat(Func<object, float>? reader, object source)
    {
        if (reader is null)
            return float.NaN;
        return reader(source);
    }

    private enum ShiftCommand { None, Up, Down, FirstGear }

    private readonly record struct ControllerLayout(long Capacity, long ShiftUpOffset,
        long ShiftDownOffset, long FirstGearOffset);
    private readonly record struct PowerBand(float LowerRpm, float DownshiftRpm, float UpshiftRpm);
    private sealed record EngineProfileSettings(string SelectedBrand, int SelectedProfileIndex,
        int CustomHorsepower, int CustomTorqueNm, int CustomRpmLimit,
        int CustomRpmRangeLow, int CustomRpmRangeHigh, bool AutomaticShiftingEnabled = true,
        bool DetailedDiagnosticMode = false);
    private readonly record struct CalibrationSample(float Rpm, float PowerProxy);
    private sealed class ConfigurationSession(EngineSpec raw)
    {
        public EngineSpec Raw { get; } = raw;
        public int? MeasuredLimit, MeasuredLow, MeasuredHigh;
        public ConfigurationTestKind RunningTest;
        public long StartedAtTick, LastSampleSequence;
        public int LockedGear;
        public bool SawFullThrottle;
        public float MaximumObservedRpm, SmoothedAcceleration, LastSpeed;
        public DateTime LastSampleAt = DateTime.MinValue;
        public List<CalibrationSample> Samples { get; } = new(256);
        public string Message = raw.RpmLimit == 0 || raw.RpmRangeLow == 0 || raw.RpmRangeHigh == 0
            ? "Missing engine fields are using safe session fallbacks."
            : "Engine profile is complete; no calibration is required.";
    }
    private readonly record struct AutomatedControlIntent(float Throttle, float Brake, DateTime ReceivedAt)
    {
        public static readonly AutomatedControlIntent Empty = new(0f, 0f, DateTime.MinValue);
    }

    private sealed record TelemetrySnapshot(
        long Sequence,
        DateTime ReceivedAt,
        bool SdkActive,
        bool Paused,
        bool EngineEnabled,
        bool ParkingBrake,
        bool TrailerAttached,
        float CargoMassKilograms,
        float PhysicalThrottle,
        int ForwardGearCount,
        int CurrentGear,
        int ShifterSlot,
        float EngineRpm,
        float EngineRpmMax,
        float EngineRpmIdle,
        float EngineTorque,
        float DifferentialRatio,
        float PoweredWheelRadius,
        float SpeedMetresPerSecond,
        float LongitudinalAcceleration,
        bool RoadGradeValid,
        float RoadGrade,
        float Throttle,
        float AutomatedThrottle,
        float AutomatedBrake,
        float Clutch,
        float EngineLoad,
        float[] ForwardRatios)
    {
        public static readonly TelemetrySnapshot Empty = new(
            0, DateTime.MinValue, false, false, false, false, false, 0f, 0f, 0, 0, 0,
            0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, false, 0f, 0f, 0f, 0f, 0f, 0f, []);
    }
}

public sealed record ManualTransmissionAdjustmentSettings(
    bool AutomaticShiftingEnabled,
    string SelectedBrand,
    int SelectedProfileIndex,
    int CustomHorsepower,
    int CustomTorqueNm,
    int CustomRpmLimit,
    int CustomRpmRangeLow,
    int CustomRpmRangeHigh)
{
    public bool DetailedDiagnosticMode { get; init; }
}

public enum ConfigurationTestKind { None, StationaryLimit, RollingPowerBand }

public sealed record ConfigurationModeSnapshot(
    bool NeedsConfiguration,
    bool NeedsLimitTest,
    bool NeedsPowerBandTest,
    ConfigurationTestKind RunningTest,
    float SecondsRemaining,
    bool StationaryConditionsMet,
    bool RollingConditionsMet,
    string Message,
    int EffectiveRpmLimit,
    int EffectiveRpmRangeLow,
    int EffectiveRpmRangeHigh,
    bool LimitMeasured,
    bool PowerBandMeasured);
