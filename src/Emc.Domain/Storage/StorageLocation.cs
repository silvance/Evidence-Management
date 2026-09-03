using Emc.Domain.Common;

namespace Emc.Domain.Storage;

/// <summary>
/// A physical place within an evidence room where an item may be stored.
///
/// Hierarchical: evidence room -> container/shelf -> bin. The kinds mirror the storage concepts
/// AR 195-5 actually names (4-1 evidence room, 4-1d depository, 4-3 temporary evidence facility,
/// 2-6f impound lot or warehouse, 2-13 long-term container).
///
/// A temporary release is NOT a storage location (LOC-005). "Released to USACIL" is a custody
/// state, not a place on a shelf. Conflating them is exactly the information loss the four-axis
/// state model exists to prevent (docs/domain-model.md §3).
/// </summary>
public class StorageLocation : Entity, IConcurrencyStamped
{
    private StorageLocation() { }

    public StorageLocation(
        int evidenceRoomId,
        string name,
        StorageLocationKind kind,
        StorageLocation? parent = null)
    {
        EvidenceRoomId = evidenceRoomId;
        Name = Guard.NotBlank(name, "LOC-004", "Storage location name");
        Kind = kind;

        if (parent is not null)
        {
            if (parent.EvidenceRoomId != evidenceRoomId)
            {
                throw new DomainRuleViolationException(
                    "LOC-004", "A storage location's parent must be in the same evidence room.");
            }

            ParentId = parent.Id;
            Parent = parent;
        }

        IsActive = true;
        ConcurrencyStamp = Guid.NewGuid();
    }

    public int EvidenceRoomId { get; private set; }
    public EvidenceRoom? EvidenceRoom { get; private set; }

    public string Name { get; private set; } = string.Empty;
    public StorageLocationKind Kind { get; private set; }

    public int? ParentId { get; private set; }
    public StorageLocation? Parent { get; private set; }

    public bool IsActive { get; private set; }
    public Guid ConcurrencyStamp { get; set; }

    /// <summary>Display path, e.g. "Evidence Room 3 / Shelf B / Bin 14".</summary>
    public string FullPath => Parent is null ? Name : $"{Parent.FullPath} / {Name}";

    public void Rename(string name) => Name = Guard.NotBlank(name, "LOC-004", "Storage location name");

    public void Deactivate() => IsActive = false;
}
