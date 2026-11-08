using System.Text.Json.Serialization;

namespace PcMieru.Wire;

/// <summary>Where a startup entry is registered. Shared because the Helper accepts the HKLM ones.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<StartupProvider>))]
public enum StartupProvider
{
    RunHkcu,
    RunHklm,
    RunHklm32,
    StartupFolderUser,
    StartupFolderCommon,
    ScheduledTask,
    PackagedApp,
}

/// <summary>Priority classes a user may set. Realtime is deliberately absent (SPEC 5.2).</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<SettablePriority>))]
public enum SettablePriority
{
    Idle,
    BelowNormal,
    Normal,
    AboveNormal,
    High,
}
