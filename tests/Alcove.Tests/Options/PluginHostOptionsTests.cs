using System.Text.Json;
using Alcove.Cells;
using Alcove.Feeds;
using Alcove.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Alcove.Tests.Options;

public sealed class PluginHostOptionsTests
{
    [Fact]
    public void Defaults()
    {
        var options = new PluginHostOptions();

        Assert.Equal("", options.Root);
        Assert.Null(options.PluginPackageType);
        Assert.Empty(options.HostOwnedPackages);
        Assert.Empty(options.Feeds);
        Assert.Empty(options.Cells);
        Assert.False(options.AllowPrerelease);
        Assert.Equal(1, options.VersionsToKeep);
        Assert.False(options.FailFast);
        Assert.Null(options.LoggerFactory);
    }

    [Fact]
    public void AppDeveloperGuideSample_Compiles()
    {
        // docs/app-developer-guide.md §2 and §5
        var options = new PluginHostOptions
        {
            Root = Path.Combine(Path.GetTempPath(), "MyApp", "plugins"),
            PluginPackageType = "MyAppPlugin",
            HostOwnedPackages = { "MyApp.Contracts", "MyApp.Sdk" },
            Feeds = { new PackageFeed("https://nuget.example.com/v3/index.json") },
            LoggerFactory = NullLoggerFactory.Instance,
        };
        options.Cells.Add(new CellDefinition("Reporting")
        {
            Plugins = { "Contoso.Charts", "Contoso.Tables" },
        });

        Assert.Equal(["MyApp.Contracts", "MyApp.Sdk"], options.HostOwnedPackages);
        Assert.Equal(["Contoso.Charts", "Contoso.Tables"], Assert.Single(options.Cells).Plugins);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEverySerialisableSetting()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("config.json");
        var options = new PluginHostOptions
        {
            Root = temp.Combine("root"),
            PluginPackageType = "MyAppPlugin",
            HostOwnedPackages = { "MyApp.Contracts" },
            Feeds = { new PackageFeed("https://nuget.example.com/v3/index.json") { Name = "company" }, new PackageFeed("/srv/feed") },
            Cells = { new CellDefinition("Reporting") { Plugins = { "Contoso.Charts", "Contoso.Tables" } } },
            AllowPrerelease = true,
            VersionsToKeep = 3,
            FailFast = true,
            LoggerFactory = NullLoggerFactory.Instance,
        };

        options.Save(path);
        var loaded = PluginHostOptions.Load(path);

        Assert.Equal(options.Root, loaded.Root);
        Assert.Equal("MyAppPlugin", loaded.PluginPackageType);
        Assert.Equal(["MyApp.Contracts"], loaded.HostOwnedPackages);
        Assert.Equal(options.Feeds, loaded.Feeds);
        var cell = Assert.Single(loaded.Cells);
        Assert.Equal("Reporting", cell.Name);
        Assert.Equal(["Contoso.Charts", "Contoso.Tables"], cell.Plugins);
        Assert.True(loaded.AllowPrerelease);
        Assert.Equal(3, loaded.VersionsToKeep);
        Assert.True(loaded.FailFast);
        Assert.Null(loaded.LoggerFactory);
    }

    [Fact]
    public void Save_WritesCamelCaseJsonWithoutCodeOnlySettings()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("config.json");

        new PluginHostOptions { Root = temp.Path, LoggerFactory = NullLoggerFactory.Instance }.Save(path);

        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var names = json.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Contains("root", names);
        Assert.Contains("hostOwnedPackages", names);
        Assert.Contains("versionsToKeep", names);
        Assert.DoesNotContain("loggerFactory", names, StringComparer.OrdinalIgnoreCase);
        Assert.Single(Directory.GetFiles(temp.Path));   // no temporary file left behind
    }

    [Fact]
    public void Load_AcceptsAHandWrittenFileWithCommentsAndTrailingCommas()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile("config.json", """
            {
              // operators edit this file
              "root": "plugins",
              "pluginPackageType": "MyAppPlugin",
              "feeds": [ { "source": "https://nuget.example.com/v3/index.json" }, ],
              "cells": [ { "name": "Reporting", "plugins": [ "Contoso.Charts" ] } ],
              "trustPolicy": { "requireSignedPackages": true },
            }
            """);

        var options = PluginHostOptions.Load(path);

        Assert.Equal(temp.Combine("plugins"), options.Root);   // relative to the file
        Assert.Equal("https://nuget.example.com/v3/index.json", Assert.Single(options.Feeds).Source);
        Assert.Equal(["Contoso.Charts"], Assert.Single(options.Cells).Plugins);
        Assert.Empty(options.HostOwnedPackages);
        Assert.Equal(1, options.VersionsToKeep);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{ \"versionsToKeep\": \"many\" }")]
    [InlineData("{ \"feeds\": [ { \"name\": \"no source\" } ] }")]
    [InlineData("{ \"root\": ")]
    public void Load_RejectsInvalidFiles(string content)
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile("config.json", content);

        Assert.ThrowsAny<JsonException>(() => PluginHostOptions.Load(path));
    }

    [Fact]
    public void EnsureValid_RequiresAnAbsoluteRoot()
    {
        Assert.Throws<ArgumentException>(() => new PluginHostOptions().EnsureValid());
        Assert.Throws<ArgumentException>(() => new PluginHostOptions { Root = "relative/root" }.EnsureValid());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PluginHostOptions { Root = Path.GetTempPath(), VersionsToKeep = -1 }.EnsureValid());
        new PluginHostOptions { Root = Path.GetTempPath() }.EnsureValid();
    }

    [Fact]
    public void CellDefinition_RequiresAName() =>
        Assert.Throws<ArgumentException>(() => new CellDefinition(" "));
}
