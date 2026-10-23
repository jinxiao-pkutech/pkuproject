using System.Text.Json;
using ScreenFlow.Core.Config;
using Xunit;

namespace ScreenFlow.Core.Tests.ConfigTests;

/// <summary>
/// Checks the shipped configuration against 02 §4 and the loader's validation rules (T01 §2).
/// The bundled-file tests read <c>config/</c> from the repository, so a drift between the document
/// and what ships fails here rather than during an index build.
/// </summary>
public sealed class ConfigLoaderTests
{
    [Fact]
    [Trait("Task", "T01")]
    public void Config_LoadsBundledProfiles()
    {
        var profiles = ConfigLoader.LoadProfiles(RepoLayout.ProfilesPath);

        Assert.Equal("1", profiles.SchemaVersion);
        Assert.Equal(
            new[] { "android-onsite", "windows-editor", "fixture", "fixture-android" },
            profiles.Profiles.Select(p => p.Id).ToArray());

        var android = profiles.Find("android-onsite");
        Assert.NotNull(android);
        Assert.Equal("android", android.Platform);
        Assert.Equal("Library/Bee", android.Rsp.Root);
        Assert.Equal("android", android.Rsp.SelectPlatform);
        Assert.Equal(new[] { ".mvfrm." }, android.Rsp.ExcludeNamePatterns.ToArray());
        Assert.Empty(android.Defines.Add);
        Assert.Equal(
            new[] { "TeamUI", "TeamUIDummy", "APP_TEST", "UNITY_EDITOR", "UNITY_EDITOR_WIN", "UNITY_INCLUDE_TESTS" },
            android.Defines.Remove.ToArray());
        Assert.Equal(new[] { "Assembly-CSharp" }, android.Assemblies.Include.ToArray());
        Assert.Equal(new[] { "*Editor*", "*Tests*" }, android.Assemblies.Exclude.ToArray());
        Assert.False(android.AllowHostFrameworkReferences);

        var editor = profiles.Find("windows-editor");
        Assert.NotNull(editor);
        Assert.Equal("editor", editor.Platform);
        Assert.Equal("editor", editor.Rsp.SelectPlatform);
        Assert.Equal(new[] { "TeamUI", "TeamUIDummy" }, editor.Defines.Remove.ToArray());
        Assert.Empty(editor.Assemblies.Exclude);
        Assert.False(editor.AllowHostFrameworkReferences);

        var fixture = profiles.Find("fixture");
        Assert.NotNull(fixture);
        Assert.Equal("editor", fixture.Platform);
        Assert.Equal("any", fixture.Rsp.SelectPlatform);
        Assert.Equal(new[] { "*" }, fixture.Assemblies.Include.ToArray());
        Assert.True(fixture.AllowHostFrameworkReferences);

        var fixtureAndroid = profiles.Find("fixture-android");
        Assert.NotNull(fixtureAndroid);
        Assert.Equal("android", fixtureAndroid.Platform);
        Assert.Equal("android", fixtureAndroid.Rsp.SelectPlatform);
        Assert.Equal(
            new[] { "TeamUI", "TeamUIDummy", "APP_TEST", "UNITY_EDITOR" },
            fixtureAndroid.Defines.Remove.ToArray());
        Assert.True(fixtureAndroid.AllowHostFrameworkReferences);

        // Only a fixture may borrow the host runtime's references (02 §4.1).
        Assert.All(
            profiles.Profiles.Where(p => p.AllowHostFrameworkReferences),
            p => Assert.True(p.IsFixture, $"profile '{p.Id}' is not a fixture"));
    }

    [Fact]
    [Trait("Task", "T01")]
    public void Config_LoadsBundledDomain()
    {
        var domain = ConfigLoader.LoadDomain(RepoLayout.DomainPath);

        Assert.Equal("1", domain.SchemaVersion);

        Assert.Equal("customer", domain.PeerOf("employee"));
        Assert.Equal("employee", domain.PeerOf("customer"));

        Assert.Equal(40, domain.Traversal.MaxChainDepth);
        Assert.Equal(12, domain.Traversal.MaxCandidatesPerDispatch);
        Assert.Equal(3, domain.Traversal.MaxChainsPerScreenEdge);
        Assert.Equal(2, domain.Traversal.MaxParamPropagationDepth);
        Assert.Contains("UnityEngine", domain.Traversal.DoNotDescendNamespaces);
        Assert.Contains("MQTTnet", domain.Traversal.DoNotDescendNamespaces);

        // The pattern uses .NET named groups, so it has to compile as a .NET regex.
        var markers = new System.Text.RegularExpressions.Regex(domain.ChangeMarkers.Pattern);
        var match = markers.Match("    // ▼ADD 2024/01/09 some change");
        Assert.True(match.Success);
        Assert.Equal("▼", match.Groups["dir"].Value);
        Assert.Equal("ADD", match.Groups["kind"].Value);

        var displayEventIds = domain.Tcp.GetProperty("displayEventIds");
        var displayPairs = displayEventIds.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString(), StringComparer.Ordinal);
        Assert.Equal(6, displayPairs.Count);
        Assert.Equal("screen", displayPairs["Common.Display"]);
        Assert.Equal("popup", displayPairs["Common.DisplayPopup"]);
        Assert.Equal("popup", displayPairs["Common.DisplayPopupAbove"]);
        Assert.Equal("popup", displayPairs["Common.DisplayPopupFirstCloseAfter"]);
        Assert.Equal("popup", displayPairs["Common.DisplayGuidancePopup"]);
        Assert.Equal("popup", displayPairs["Common.OpenFullSelfGuidance"]);

        var constraintMethods = domain.Ui.GetProperty("constraintMethods");
        var constraintPairs = constraintMethods.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString(), StringComparer.Ordinal);
        Assert.Equal(4, constraintPairs.Count);
        Assert.Equal("maxDigits", constraintPairs["SetInputMaxNumberOfCharacter"]);
        Assert.Equal("maxValue", constraintPairs["SetInputMaxValue"]);
        Assert.Equal("minValue", constraintPairs["SetInputMinValue"]);
        Assert.Equal("allowEmpty", constraintPairs["SetInputAllowEmptyString"]);
    }

    /// <summary>
    /// The shipped anchors must be well-formed in all five shapes of 02 §4.2, because an anchor the
    /// resolver cannot interpret disables an adapter feature rather than failing loudly later.
    /// </summary>
    [Fact]
    [Trait("Task", "T01")]
    public void Config_BundledAnchorsCoverAllFiveShapes()
    {
        var domain = ConfigLoader.LoadDomain(RepoLayout.DomainPath);

        var screenIdType = Anchor(domain.Screens, "screenIdType");
        Assert.Equal(AnchorKind.TypeInFile, screenIdType.Kind);
        Assert.Empty(screenIdType.Validate("screens.screenIdType"));

        var enumerationBase = Anchor(domain.Enumerations, "baseType");
        Assert.Equal(AnchorKind.TypeByFqName, enumerationBase.Kind);
        Assert.Empty(enumerationBase.Validate("enumerations.baseType"));

        var apiRequest = Anchor(domain.Api, "requestType");
        Assert.Equal(AnchorKind.TypeInFileGlob, apiRequest.Kind);
        Assert.Empty(apiRequest.Validate("api.requestType"));

        var commandInterfaces = Anchor(domain.Device, "commandInterfaces");
        Assert.Equal(AnchorKind.TypeSet, commandInterfaces.Kind);
        Assert.Equal("interface", commandInterfaces.TypeKind);
        Assert.Empty(commandInterfaces.Validate("device.commandInterfaces"));

        var enqueue = domain.Queue.GetProperty("enqueueMethods")[0]
            .Deserialize<AnchorSpec>(ConfigLoader.SectionOptions);
        Assert.NotNull(enqueue);
        Assert.Equal(AnchorKind.Member, enqueue.Kind);
        Assert.Equal(AnchorKind.AnyTypeInFileGlob, enqueue.TypePartKind);
        Assert.True(enqueue.MatchesAnyType);
        Assert.Equal("AddQueue", enqueue.Member);
        Assert.Empty(enqueue.Validate("queue.enqueueMethods[0]"));

        // A member anchor may name every member of a type, which the TcpPresenter entries use.
        var reflectionMethods = domain.Screens.GetProperty("domainHandledReflectionMethods")
            .EnumerateArray()
            .Select(e => e.Deserialize<AnchorSpec>(ConfigLoader.SectionOptions)!)
            .ToList();
        Assert.Equal(5, reflectionMethods.Count);
        Assert.All(reflectionMethods, a => Assert.Empty(a.Validate("screens.domainHandledReflectionMethods")));
        Assert.Equal(2, reflectionMethods.Count(a => a.MatchesAnyMember));
    }

    [Fact]
    [Trait("Task", "T01")]
    public void Config_RejectsHostRefsForNonFixture()
    {
        const string json = """
            {
              "schemaVersion": "1",
              "profiles": [
                {
                  "id": "android-onsite",
                  "platform": "android",
                  "rsp": { "root": "Library/Bee", "selectPlatform": "android", "excludeNamePatterns": [] },
                  "defines": { "add": [], "remove": [] },
                  "assemblies": { "include": ["Assembly-CSharp"], "exclude": [] },
                  "allowHostFrameworkReferences": true
                }
              ]
            }
            """;

        using var file = new TempConfigFile(json);
        var error = Assert.Throws<ConfigException>(() => ConfigLoader.LoadProfiles(file.Path));
        Assert.Contains(error.Problems, p => p.Contains("allowHostFrameworkReferences", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Task", "T01")]
    public void Config_RejectsUnknownField()
    {
        // 'selectPlatfrom' is a typo. Ignoring it would read as "no platform filter", which quietly
        // changes which response files are analysed.
        const string json = """
            {
              "schemaVersion": "1",
              "profiles": [
                {
                  "id": "fixture",
                  "platform": "editor",
                  "rsp": { "root": "Library/Bee", "selectPlatfrom": "any", "excludeNamePatterns": [] },
                  "defines": { "add": [], "remove": [] },
                  "assemblies": { "include": ["*"], "exclude": [] },
                  "allowHostFrameworkReferences": true
                }
              ]
            }
            """;

        using var file = new TempConfigFile(json);
        var error = Assert.Throws<ConfigException>(() => ConfigLoader.LoadProfiles(file.Path));
        Assert.Contains("selectPlatfrom", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Task", "T01")]
    public void Config_RejectsBadPeer()
    {
        // 'customer' points back at a third terminal, so the pairing is not mutual.
        const string json = """
            {
              "schemaVersion": "1",
              "terminals": [
                { "id": "employee", "peer": "customer" },
                { "id": "customer", "peer": "kitchen" },
                { "id": "kitchen", "peer": "kitchen" }
              ],
              "traversal": {
                "maxChainDepth": 40,
                "maxCandidatesPerDispatch": 12,
                "maxChainsPerScreenEdge": 3,
                "maxParamPropagationDepth": 2,
                "doNotDescendNamespaces": []
              },
              "changeMarkers": { "pattern": "^x$" }
            }
            """;

        using var file = new TempConfigFile(json);
        var error = Assert.Throws<ConfigException>(() => ConfigLoader.LoadDomain(file.Path));
        Assert.Contains(error.Problems, p => p.Contains("not mutual", StringComparison.Ordinal));
        Assert.Contains(error.Problems, p => p.Contains("its own peer", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Task", "T01")]
    public void Config_RejectsNonPositiveTraversalLimitsAndBadRegex()
    {
        const string json = """
            {
              "schemaVersion": "1",
              "terminals": [
                { "id": "employee", "peer": "customer" },
                { "id": "customer", "peer": "employee" }
              ],
              "traversal": {
                "maxChainDepth": 0,
                "maxCandidatesPerDispatch": -1,
                "maxChainsPerScreenEdge": 3,
                "maxParamPropagationDepth": 2,
                "doNotDescendNamespaces": []
              },
              "changeMarkers": { "pattern": "(unclosed" }
            }
            """;

        using var file = new TempConfigFile(json);
        var error = Assert.Throws<ConfigException>(() => ConfigLoader.LoadDomain(file.Path));
        Assert.Contains(error.Problems, p => p.Contains("maxChainDepth", StringComparison.Ordinal));
        Assert.Contains(error.Problems, p => p.Contains("maxCandidatesPerDispatch", StringComparison.Ordinal));
        Assert.Contains(error.Problems, p => p.Contains("changeMarkers.pattern", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Task", "T01")]
    public void Config_RejectsDuplicateProfileId()
    {
        const string json = """
            {
              "schemaVersion": "1",
              "profiles": [
                {
                  "id": "fixture",
                  "platform": "editor",
                  "rsp": { "root": "Library/Bee", "selectPlatform": "any", "excludeNamePatterns": [] },
                  "defines": { "add": [], "remove": [] },
                  "assemblies": { "include": ["*"], "exclude": [] },
                  "allowHostFrameworkReferences": true
                },
                {
                  "id": "fixture",
                  "platform": "android",
                  "rsp": { "root": "Library/Bee", "selectPlatform": "bogus", "excludeNamePatterns": [] },
                  "defines": { "add": [], "remove": [] },
                  "assemblies": { "include": ["*"], "exclude": [] },
                  "allowHostFrameworkReferences": true
                }
              ]
            }
            """;

        using var file = new TempConfigFile(json);
        var error = Assert.Throws<ConfigException>(() => ConfigLoader.LoadProfiles(file.Path));
        Assert.Contains(error.Problems, p => p.Contains("declared more than once", StringComparison.Ordinal));
        Assert.Contains(error.Problems, p => p.Contains("selectPlatform", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Task", "T01")]
    public void Config_AcceptsCommentsAndTrailingCommas()
    {
        const string json = """
            {
              // the fixture profile, with a trailing comma below
              "schemaVersion": "1",
              "profiles": [
                {
                  "id": "fixture",
                  "platform": "editor",
                  "rsp": { "root": "Library/Bee", "selectPlatform": "any", "excludeNamePatterns": [] },
                  "defines": { "add": [], "remove": [] },
                  "assemblies": { "include": ["*"], "exclude": [] },
                  "allowHostFrameworkReferences": true,
                },
              ]
            }
            """;

        using var file = new TempConfigFile(json);
        var profiles = ConfigLoader.LoadProfiles(file.Path);
        Assert.Single(profiles.Profiles);
    }

    [Fact]
    [Trait("Task", "T01")]
    public void Config_ResolvesBundledDirectoryFromCommandLine()
    {
        var resolved = ConfigLoader.ResolveConfigDirectory(RepoLayout.ConfigDirectory);
        Assert.True(File.Exists(Path.Combine(resolved, ConfigLoader.ProfilesFileName)));

        var (profiles, domain) = ConfigLoader.LoadAll(resolved);
        Assert.Equal(4, profiles.Profiles.Count);
        Assert.True(domain.HasTerminal("customer"));
    }

    [Fact]
    [Trait("Task", "T01")]
    public void Config_ResolveReportsProbedDirectoriesWhenNothingFound()
    {
        var empty = Path.Combine(Path.GetTempPath(), "screenflow-empty-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(empty);
        try
        {
            var error = Assert.Throws<ConfigException>(() => ConfigLoader.ResolveConfigDirectory(empty));
            Assert.Contains(ConfigLoader.ProfilesFileName, error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    private static AnchorSpec Anchor(JsonElement section, string propertyName)
    {
        var anchor = section.GetProperty(propertyName).Deserialize<AnchorSpec>(ConfigLoader.SectionOptions);
        Assert.NotNull(anchor);
        return anchor;
    }
}
