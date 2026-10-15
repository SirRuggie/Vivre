using Vivre.Core.Models;

namespace Vivre.Core.Tests.Models;

public sealed class CustomValueStoreTests
{
    [Fact]
    public void Remove_retires_case_variants_and_notifies_once_without_touching_other_columns()
    {
        var store = new CustomValueStore { ["Old"] = "payload", ["old"] = "older", ["Keep"] = "value" };
        int notifications = 0;
        store.PropertyChanged += (_, e) => { Assert.Equal("Item[]", e.PropertyName); notifications++; };
        Assert.True(store.Remove("OLD"));
        Assert.Null(store["Old"]);
        Assert.Null(store["old"]);
        Assert.Equal("value", store["Keep"]);
        Assert.Equal(1, notifications);
        Assert.False(store.Remove("OLD"));
        Assert.Equal(1, notifications);
    }
}
