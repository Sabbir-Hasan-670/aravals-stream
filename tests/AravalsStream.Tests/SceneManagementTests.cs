using System.Text.Json;
using AravalsStream.Core.Models;
using AravalsStream.Core.Services;
using AravalsStream.Core.Settings;
using Xunit;

namespace AravalsStream.Tests;

public sealed class SceneManagementTests
{
    [Fact]
    public void AddingSceneCreatesEnabledSceneWithUniqueId()
    {
        var scenes = new List<Scene>();
        var scene1 = new Scene { Name = "Main" };
        var scene2 = new Scene { Name = "Overlay" };

        scenes.Add(scene1);
        scenes.Add(scene2);

        Assert.Equal(2, scenes.Count);
        Assert.True(scene1.Enabled);
        Assert.True(scene2.Enabled);
        Assert.NotEqual(scene1.Id, scene2.Id);
        Assert.NotEqual(Guid.Empty, scene1.Id);
    }

    [Fact]
    public void EnablingDisablingTogglesState()
    {
        var scenes = new List<Scene>
        {
            new() { Name = "Scene 1", Enabled = true },
            new() { Name = "Scene 2", Enabled = true }
        };

        Scene? active = scenes[0];
        bool toggled = SceneManager.ToggleSceneEnabled(scenes, scenes[1], ref active);

        Assert.True(toggled);
        Assert.False(scenes[1].Enabled);

        toggled = SceneManager.ToggleSceneEnabled(scenes, scenes[1], ref active);
        Assert.True(toggled);
        Assert.True(scenes[1].Enabled);
    }

    [Fact]
    public void CannotDisableLastEnabledScene()
    {
        var scenes = new List<Scene>
        {
            new() { Name = "Scene 1", Enabled = true },
            new() { Name = "Scene 2", Enabled = false }
        };

        Scene? active = scenes[0];
        Assert.False(SceneManager.CanDisableScene(scenes, scenes[0]));

        bool toggled = SceneManager.ToggleSceneEnabled(scenes, scenes[0], ref active);
        Assert.False(toggled);
        Assert.True(scenes[0].Enabled);
        Assert.Equal(scenes[0], active);
    }

    [Fact]
    public void DeletingNormalSceneRemovesSceneAndIdentifiesOrphanResources()
    {
        var sharedRef = Guid.NewGuid();
        var uniqueRef = Guid.NewGuid();

        var scene1 = new Scene { Name = "Scene 1" };
        scene1.Sources.Add(new SceneSource { SourceReference = sharedRef });

        var scene2 = new Scene { Name = "Scene 2" };
        scene2.Sources.Add(new SceneSource { SourceReference = sharedRef });
        scene2.Sources.Add(new SceneSource { SourceReference = uniqueRef });

        var scenes = new List<Scene> { scene1, scene2 };
        Scene? active = scene1;

        var result = SceneManager.DeleteScene(scenes, scene2, active);

        Assert.True(result.Success);
        Assert.Single(scenes);
        Assert.Equal(scene1, scenes[0]);
        Assert.Equal(scene1, result.NextActiveScene);

        // uniqueRef is orphan because scene2 was deleted and no other scene uses it
        Assert.Contains(uniqueRef, result.OrphanResourceReferences);
        // sharedRef is still referenced by scene1, so it MUST NOT be orphaned
        Assert.DoesNotContain(sharedRef, result.OrphanResourceReferences);
    }

    [Fact]
    public void CannotDeleteFinalScene()
    {
        var scenes = new List<Scene>
        {
            new() { Name = "Only Scene" }
        };

        Assert.False(SceneManager.CanDeleteScene(scenes, scenes[0]));

        var result = SceneManager.DeleteScene(scenes, scenes[0], scenes[0]);
        Assert.False(result.Success);
        Assert.Single(scenes);
    }

    [Fact]
    public void DeletingActiveSceneSelectsFallback()
    {
        var scene1 = new Scene { Name = "Scene 1", Enabled = true };
        var scene2 = new Scene { Name = "Scene 2", Enabled = false };
        var scene3 = new Scene { Name = "Scene 3", Enabled = true };

        var scenes = new List<Scene> { scene1, scene2, scene3 };
        Scene? active = scene1;

        var result = SceneManager.DeleteScene(scenes, scene1, active);

        Assert.True(result.Success);
        Assert.Equal(2, scenes.Count);
        // Should select the next available enabled scene (Scene 3)
        Assert.Equal(scene3, result.NextActiveScene);
    }

    [Fact]
    public void RenamingPreservesId()
    {
        var scene = new Scene { Name = "Old Name" };
        var originalId = scene.Id;

        bool renamed = SceneManager.RenameScene(scene, "  New Name  ", out var error);

        Assert.True(renamed);
        Assert.Null(error);
        Assert.Equal("New Name", scene.Name);
        Assert.Equal(originalId, scene.Id);

        // Validation: cannot be empty
        bool invalid = SceneManager.RenameScene(scene, "   ", out error);
        Assert.False(invalid);
        Assert.NotNull(error);
        Assert.Equal("New Name", scene.Name);
    }

    [Fact]
    public void DuplicatingGeneratesNewId()
    {
        var original = new Scene { Name = "Gaming" };
        var source = new SceneSource { Name = "Camera 1", SourceReference = Guid.NewGuid() };
        original.Sources.Add(source);

        var scenes = new List<Scene> { original };
        var duplicate = SceneManager.DuplicateScene(original, scenes);

        Assert.Equal(2, scenes.Count);
        Assert.Equal("Gaming Copy", duplicate.Name);
        Assert.NotEqual(original.Id, duplicate.Id);
        Assert.NotEqual(source.Id, duplicate.Sources[0].Id);
    }

    [Fact]
    public void DuplicatedTransformsAreIndependent()
    {
        var original = new Scene { Name = "Original" };
        var source = new SceneSource
        {
            Name = "Screen",
            HorizontalTransform = new SourceTransform { X = 100, Y = 50, Width = 800, Height = 600, Locked = false },
            VerticalTransform = new SourceTransform { X = 20, Y = 40, Width = 400, Height = 300, Locked = true }
        };
        original.Sources.Add(source);

        var scenes = new List<Scene> { original };
        var duplicate = SceneManager.DuplicateScene(original, scenes);

        var dupSource = duplicate.Sources[0];

        // Mutate the duplicate's transforms
        dupSource.HorizontalTransform.X = 999;
        dupSource.HorizontalTransform.Locked = true;
        dupSource.VerticalTransform.Y = 888;

        // Original remains completely unchanged
        Assert.Equal(100, source.HorizontalTransform.X);
        Assert.False(source.HorizontalTransform.Locked);
        Assert.Equal(40, source.VerticalTransform.Y);
        Assert.True(source.VerticalTransform.Locked);

        // Duplicate has new mutated values
        Assert.Equal(999, dupSource.HorizontalTransform.X);
        Assert.True(dupSource.HorizontalTransform.Locked);
        Assert.Equal(888, dupSource.VerticalTransform.Y);
    }

    [Fact]
    public void DuplicatedSceneReferencesSharedResourceCorrectly()
    {
        var captureResourceId = Guid.NewGuid();
        var original = new Scene { Name = "Scene A" };
        original.Sources.Add(new SceneSource
        {
            Name = "Display 1",
            Type = SourceType.DisplayCapture,
            DisplayId = @"\\.\DISPLAY1",
            SourceReference = captureResourceId
        });

        var scenes = new List<Scene> { original };
        var duplicate = SceneManager.DuplicateScene(original, scenes);

        // Must reference the exact same underlying shared physical capture resource
        Assert.Equal(captureResourceId, duplicate.Sources[0].SourceReference);
        Assert.Equal(original.Sources[0].SourceReference, duplicate.Sources[0].SourceReference);
        Assert.Equal(@"\\.\DISPLAY1", duplicate.Sources[0].DisplayId);
    }

    [Fact]
    public void SceneOrderingPersistence()
    {
        var s1 = new Scene { Name = "Scene 1" };
        var s2 = new Scene { Name = "Scene 2" };
        var s3 = new Scene { Name = "Scene 3" };

        var originalOrder = new List<Scene> { s1, s2, s3 };

        // Reorder: Move s3 to first position
        var reordered = new List<Scene> { s3, s1, s2 };

        var settings = new AppSettings { Scenes = reordered };
        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal(3, restored.Scenes.Count);
        Assert.Equal(s3.Id, restored.Scenes[0].Id);
        Assert.Equal(s1.Id, restored.Scenes[1].Id);
        Assert.Equal(s2.Id, restored.Scenes[2].Id);
        Assert.Equal("Scene 3", restored.Scenes[0].Name);
    }

    [Fact]
    public void ActiveScenePersistence()
    {
        var s1 = new Scene { Name = "Scene 1", Enabled = false };
        var s2 = new Scene { Name = "Scene 2", Enabled = true };
        var s3 = new Scene { Name = "Scene 3", Enabled = true };

        var scenes = new List<Scene> { s1, s2, s3 };

        // Case 1: Valid enabled scene is saved
        var restoredActive1 = SceneManager.ResolveActiveScene(scenes, s3.Id.ToString());
        Assert.Equal(s3, restoredActive1);

        // Case 2: Saved active scene is disabled -> falls back to first enabled scene (s2)
        var restoredActive2 = SceneManager.ResolveActiveScene(scenes, s1.Id.ToString());
        Assert.Equal(s2, restoredActive2);

        // Case 3: Saved active scene is missing/null -> falls back to first enabled scene (s2)
        var restoredActive3 = SceneManager.ResolveActiveScene(scenes, Guid.NewGuid().ToString());
        Assert.Equal(s2, restoredActive3);
    }
}
