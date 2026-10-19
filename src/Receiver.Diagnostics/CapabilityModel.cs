using System.Text.Json.Serialization;

namespace Receiver.Diagnostics;

/// <summary>
/// Evidence level of a single capability, per ARCHITEKTUR.md section 5.3.
/// The distinction matters: a driver capability bit is not a radio proof.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CapState
{
    /// <summary>Not probed, or the probe itself could not run.</summary>
    Unknown,
    /// <summary>The driver/stack explicitly denies the capability.</summary>
    Unsupported,
    /// <summary>The driver/stack claims the capability, but we did not exercise it.</summary>
    Reported,
    /// <summary>We exercised the capability and observed the expected real-world effect.</summary>
    Verified,
}

public sealed class Capability
{
    public required string Name { get; init; }
    public CapState State { get; set; } = CapState.Unknown;

    /// <summary>Short human-readable justification for exactly this state.</summary>
    public string? Rationale { get; set; }

    /// <summary>Ids of evidence files written alongside capabilities.json.</summary>
    public List<string> EvidenceIds { get; } = new();

    /// <summary>Known limits that hold even when State is Reported or Verified.</summary>
    public List<string> Limitations { get; } = new();

    public Capability Set(CapState state, string rationale)
    {
        State = state;
        Rationale = rationale;
        return this;
    }
}

public sealed class CapabilityReport
{
    public string SchemaVersion { get; init; } = "capabilities-v1";
    public DateTimeOffset ObservedOn { get; init; } = DateTimeOffset.Now;
    public string RunId { get; init; } = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    public string? MachineName { get; init; }
    public bool ElevatedProcess { get; init; }

    public Dictionary<string, object?> Host { get; } = new();
    public List<Dictionary<string, object?>> Adapters { get; } = new();
    public Dictionary<string, object?> WlanDriver { get; } = new();
    public Dictionary<string, object?> Npcap { get; } = new();

    /// <summary>Keyed by capability name; see <see cref="Capabilities"/> factory.</summary>
    public Dictionary<string, Capability> Capabilities { get; } = new();

    /// <summary>Files written next to this report that back the states above.</summary>
    public List<string> EvidenceFiles { get; } = new();

    public Capability Cap(string name)
    {
        if (!Capabilities.TryGetValue(name, out var c))
        {
            c = new Capability { Name = name };
            Capabilities[name] = c;
        }
        return c;
    }
}
