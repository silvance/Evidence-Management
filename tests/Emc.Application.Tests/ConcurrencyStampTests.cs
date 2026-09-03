using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>SEC-007. Optimistic concurrency is enforced at the database, not in the UI.</summary>
public class ConcurrencyStampTests : IDisposable
{
    private readonly SliceTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task AStaleUpdateIsRejectedAtTheDatabase()
    {
        // Two requests load the same row; the first saves; the second, holding the old stamp,
        // must fail rather than overwrite. SaveChanges rotates the stamp on every update.
        using var second = _harness.CreateSecondContext();

        var a = await _harness.Db.StorageLocations.FirstAsync(l => l.Id == _harness.ShelfBBin14Id);
        var b = await second.StorageLocations.FirstAsync(l => l.Id == _harness.ShelfBBin14Id);
        var originalStamp = a.ConcurrencyStamp;

        a.Rename("Bin 14 (relabelled)");
        await _harness.Db.SaveChangesAsync();
        Assert.NotEqual(originalStamp, a.ConcurrencyStamp);

        b.Rename("Bin 14 (conflicting)");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        var stored = await _harness.CreateSecondContext().StorageLocations.AsNoTracking().FirstAsync(l => l.Id == _harness.ShelfBBin14Id);
        Assert.Equal("Bin 14 (relabelled)", stored.Name);
    }
}
