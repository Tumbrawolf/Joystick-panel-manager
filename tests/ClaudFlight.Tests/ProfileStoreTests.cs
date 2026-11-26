using ClaudFlight.Core.Models;
using ClaudFlight.Core.Profiles;
using Xunit;

namespace ClaudFlight.Tests;

public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ClaudFlightTests", Guid.NewGuid().ToString("N"));
    private readonly ProfileStore _store;

    public ProfileStoreTests() => _store = new ProfileStore(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Rename_moves_the_file_and_the_name_inside_it()
    {
        _store.Save(new Profile { Name = "Old", DeviceOrder = [Guid.NewGuid()] });

        _store.Rename("Old", "New");

        Assert.False(_store.Exists("Old"));
        var loaded = _store.Load("New");
        Assert.Equal("New", loaded.Name);
        Assert.Single(loaded.DeviceOrder);
    }

    [Fact]
    public void Rename_changing_only_capitals_keeps_the_profile()
    {
        _store.Save(new Profile { Name = "hotas" });

        _store.Rename("hotas", "HOTAS");

        Assert.Equal(["HOTAS"], _store.ListProfileNames().ToList());
        Assert.Equal("HOTAS", _store.Load("HOTAS").Name);
    }

    [Fact]
    public void UniqueName_skips_taken_names()
    {
        _store.Save(new Profile { Name = "Copy" });
        _store.Save(new Profile { Name = "Copy 2" });

        Assert.Equal("Copy 3", _store.UniqueName("Copy"));
        Assert.Equal("Other", _store.UniqueName("Other"));
    }
}
