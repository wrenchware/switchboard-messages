namespace NativeBeeper.Protocol;

// The devices a host on the network has paired (SWITCHBOARD.md stage 4, the home-network transport). Only a host behind NativeBeeper.Host.Network serves
// them; every other host leaves ProtocolServices.Devices null and answers not_supported. None is journaled: a list changes nothing, a revoke that is
// repeated finds nothing left to remove and says so, a rename repeated gives the same name again, and a second new code simply replaces the first. Pairing
// itself (NetworkPairing.ClaimMethod) is not here: no dispatcher serves it, only the network host's door, to a connection that has no token yet and may do
// nothing else.
//
// A device token is never an admin's: any device may revoke or rename itself, and only the owner (the device paired first of those still paired, PairedDevice.Owner)
// may revoke or rename another device or open a code for a new one. Anyone else is answered forbidden.
public static partial class ProtocolMethods
{
    /// <summary>The paired devices, the one asking marked <see cref="PairedDevice.Current"/> and the owner <see cref="PairedDevice.Owner"/>.</summary>
    public const string DevicesList = "devices.list";
    /// <summary>Forgets a paired device: its token stops working at once, and its connections (the asker's own included, once answered) are closed.</summary>
    public const string DevicesRevoke = "devices.revoke";
    /// <summary>Opens a new pairing code (replacing any open one) for another device, without restarting the server; the owner only.</summary>
    public const string DevicesPairNew = "devices.pairNew";
    /// <summary>Gives a paired device a new name (protocol <see cref="ProtocolVersion.ServerAdmin"/>): a device may rename itself, and only the owner another.</summary>
    public const string DevicesRename = "devices.rename";
    /// <summary>The devices asking to join now, each with the code it shows (protocol <see cref="ProtocolVersion.JoinRequests"/>); the owner only.</summary>
    public const string DevicesJoinRequests = "devices.joinRequests";
    /// <summary>Lets a device that asked to join in: it is paired and handed its token on its own connection. The owner only.</summary>
    public const string DevicesApproveJoin = "devices.approveJoin";
    /// <summary>Turns a request to join away: the asking device is told so. The owner only.</summary>
    public const string DevicesDenyJoin = "devices.denyJoin";

    private static Entry[] DeviceEntries() =>
    [
        new(DevicesList, "IPairedDevices.ListAsync", null, typeof(IReadOnlyList<PairedDevice>)),
        new(DevicesRevoke, "IPairedDevices.RevokeAsync", typeof(DeviceIdParams), typeof(DeviceRevoked)),
        new(DevicesPairNew, "IPairedDevices.PairNewAsync", null, typeof(PairingCodeIssued)),
        new(DevicesRename, "IPairedDevices.RenameAsync", typeof(DeviceRenameParams), typeof(DeviceRenamed)),
        new(DevicesJoinRequests, "IPairedDevices.JoinRequestsAsync", null, typeof(IReadOnlyList<JoinRequest>)),
        new(DevicesApproveJoin, "IPairedDevices.ApproveJoinAsync", typeof(JoinIdParams), typeof(JoinApproved)),
        new(DevicesDenyJoin, "IPairedDevices.DenyJoinAsync", typeof(JoinIdParams), typeof(JoinDenied)),
    ];
}

/// <summary>
/// A device asking to join (<c>devices.joinRequests</c>): the name it gave, the network address it asked from (so a stranger stands out), when it asked and until when the
/// request holds, and <paramref name="Code"/>, which the asking device shows too (<see cref="JoinMatch"/>): approve only when the two match.
/// </summary>
public sealed record JoinRequest(string Id, string DeviceName, string Source, DateTimeOffset Asked, DateTimeOffset Expires, string Code);

public sealed record JoinIdParams(string Id);

/// <summary>Whether the device was let in and is now paired as <paramref name="DeviceId"/>: false when there was no such request any more (it was answered, ran out, or the device left).</summary>
/// <param name="FinishCode">For a device asking with protocol <see cref="ProtocolVersion.JoinFinish"/> or later: the code the owner tells its person to type there. The device is not
/// paired yet when this answers (<paramref name="DeviceId"/> is null); it is once that code was typed. Null for an older device, which this answers once it is paired or not.</param>
public sealed record JoinApproved(bool Approved, string? DeviceId = null, string? FinishCode = null);

/// <summary>Whether a request was turned away: false when there was no such request any more.</summary>
public sealed record JoinDenied(bool Denied);

/// <summary>One paired device as <c>devices.list</c> describes it. Its token never leaves the host, which keeps only a hash of it.</summary>
/// <param name="Name">What the device called itself when it paired.</param>
/// <param name="Current">The device asking.</param>
/// <param name="Owner">The device paired first of those still paired: the one that may revoke others and pair new ones.</param>
public sealed record PairedDevice(string Id, string Name, DateTimeOffset Created, DateTimeOffset? LastSeen = null, bool Current = false, bool Owner = false);

public sealed record DeviceIdParams(string Id);

/// <summary>Whether a device was removed: false when no device had that id (already revoked, or never paired).</summary>
public sealed record DeviceRevoked(bool Revoked);

/// <summary>A new pairing code, as the server's log also prints it, good once for <paramref name="ExpiresSeconds"/>.</summary>
public sealed record PairingCodeIssued(string Code, int ExpiresSeconds);

/// <summary>The device to rename and its new name, which the host cleans as it cleans a name given at pairing (trimmed, no control characters, at most <see cref="NetworkPairing.MaxDeviceName"/>).</summary>
public sealed record DeviceRenameParams(string Id, string Name);

/// <summary>Whether a device was renamed (false when no device had that id), and the name it now has.</summary>
public sealed record DeviceRenamed(bool Renamed, string Name);

/// <summary>What a host that pairs devices serves as <c>devices.*</c>, seen from one connection (which device is asking is the host's business).</summary>
public interface IPairedDevices
{
    Task<IReadOnlyList<PairedDevice>> ListAsync(CancellationToken cancellationToken = default);
    Task<DeviceRevoked> RevokeAsync(string id, CancellationToken cancellationToken = default);
    Task<PairingCodeIssued> PairNewAsync(CancellationToken cancellationToken = default);
    Task<DeviceRenamed> RenameAsync(string id, string name, CancellationToken cancellationToken = default);
    // A host without join requests (every one but the network host) offers none: these answer not supported there.
    Task<IReadOnlyList<JoinRequest>> JoinRequestsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<JoinApproved> ApproveJoinAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<JoinDenied> DenyJoinAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
