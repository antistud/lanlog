using Logrr.Storage;
using Logrr.Storage.Control;
using Xunit;

namespace Logrr.Tests;

public class SavedSearchStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "logrr-saved-" + Guid.NewGuid().ToString("N"));
    private readonly ControlDatabase _db;
    private readonly SavedSearchStore _store;
    private readonly DateTimeOffset _now = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    public SavedSearchStoreTests()
    {
        _db = new ControlDatabase(new StoragePaths(_root));
        _db.Initialize();
        _store = new SavedSearchStore(_db);
    }

    [Fact]
    public void Create_list_and_delete_are_scoped_per_app()
    {
        _store.Create(new SavedSearch("1", "billing", "Errors", "level=Error", "admin", _now));
        _store.Create(new SavedSearch("2", "billing", "Timeouts", "q=timeout", "admin", _now));
        _store.Create(new SavedSearch("3", "web", "Other", "range=7d", null, _now));

        var billing = _store.ListByApp("billing");
        Assert.Equal(2, billing.Count);
        Assert.Equal("Errors", billing[0].Name); // ordered by name
        Assert.Equal("level=Error", billing[0].Query);
        Assert.Single(_store.ListByApp("web"));

        _store.Delete("1");
        Assert.Single(_store.ListByApp("billing"));
        Assert.Equal("Timeouts", _store.ListByApp("billing")[0].Name);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
