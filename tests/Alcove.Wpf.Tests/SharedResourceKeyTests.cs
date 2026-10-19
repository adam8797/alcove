namespace Alcove.Wpf.Tests;

public sealed class SharedResourceKeyTests
{
    [Fact]
    public void KeysWithTheSameIdAndType_AreEqual_SoTheyFindTheSameResource()
    {
        var resources = new Dictionary<object, object> { [new SharedResourceKey<string>("App.Title")] = "Alcove" };

        Assert.Equal(new SharedResourceKey<string>("App.Title"), new SharedResourceKey<string>("App.Title"));
        Assert.Equal("Alcove", resources[new SharedResourceKey<string>("App.Title")]);
    }

    [Fact]
    public void KeysDiffer_ByIdOrValueType()
    {
        object asString = new SharedResourceKey<string>("App.Accent");

        Assert.NotEqual(asString, new SharedResourceKey<string>("App.Other"));
        Assert.NotEqual(asString, new SharedResourceKey<int>("App.Accent"));
        Assert.NotEqual(asString, "App.Accent");
    }

    [Fact]
    public void AKeyDescribesItself()
    {
        var key = new SharedResourceKey<double>("App.Spacing");

        Assert.Equal(typeof(double), key.ValueType);
        Assert.Equal("App.Spacing (Double)", key.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void AKeyNeedsAnId(string id) => Assert.Throws<ArgumentException>(() => new SharedResourceKey<string>(id));
}
