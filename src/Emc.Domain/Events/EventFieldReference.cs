namespace Emc.Domain.Events;

/// <summary>
/// What a correctable field actually points at.
///
/// Most correctable fields are free text - a purpose of change of custody, a reason, a note. Two
/// are not: an item's storage location and a custody party are ROWS, and the event stores both
/// their identifier and the display text as it read at the time.
///
/// DESIGN. AR 195-5 says nothing about identifiers; it is a paper form on which a location is
/// written in pencil (2-4e) and a custody party is written in a block (2-3f). The reason EMC must
/// nonetheless keep the identifier through a correction is that its own projections are built on
/// it: an item's current location is a StorageLocation, not a string, and inventory and
/// discrepancy work (3-2, 3-3a) depend on being able to ask which items a given bin holds.
/// </summary>
public enum CorrectableFieldReference
{
    /// <summary>Free text. The correction carries no identifier.</summary>
    None = 0,

    /// <summary>A <see cref="Emc.Domain.Storage.StorageLocation"/> row.</summary>
    StorageLocation = 1,

    /// <summary>A <see cref="CustodyParty"/> row.</summary>
    CustodyParty = 2
}

/// <summary>
/// The identifier behind a correctable field, and what kind of row it names.
/// </summary>
public sealed record EventFieldReference(CorrectableFieldReference Kind, int Id);
