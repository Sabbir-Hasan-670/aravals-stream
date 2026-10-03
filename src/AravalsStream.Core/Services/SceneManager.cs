using AravalsStream.Core.Models;

namespace AravalsStream.Core.Services;

public sealed record SceneDeletionResult(bool Success, Scene? NextActiveScene, IReadOnlyList<Guid> OrphanResourceReferences);

public static class SceneManager
{
    public const int MaxSceneNameLength = 50;

    public static string GenerateDuplicateName(string originalName, IEnumerable<string> existingNames)
    {
        var existing = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseName = originalName.Trim();
        var candidate = $"{baseName} Copy";
        if (!existing.Contains(candidate)) return candidate;

        int counter = 2;
        while (existing.Contains($"{baseName} Copy {counter}"))
        {
            counter++;
        }
        return $"{baseName} Copy {counter}";
    }

    public static bool CanDisableScene(IList<Scene> allScenes, Scene scene)
    {
        if (!scene.Enabled) return true;
        return allScenes.Count(s => s.Enabled) > 1;
    }

    public static bool ToggleSceneEnabled(IList<Scene> allScenes, Scene scene, ref Scene? activeScene)
    {
        if (scene.Enabled)
        {
            if (!CanDisableScene(allScenes, scene)) return false;
            scene.Enabled = false;
            if (activeScene == scene)
            {
                activeScene = allScenes.FirstOrDefault(s => s != scene && s.Enabled);
            }
            return true;
        }

        scene.Enabled = true;
        return true;
    }

    public static bool CanDeleteScene(IList<Scene> allScenes, Scene? scene)
    {
        if (scene is null) return false;
        return allScenes.Count > 1 && allScenes.Contains(scene);
    }

    public static SceneDeletionResult DeleteScene(IList<Scene> allScenes, Scene sceneToDelete, Scene? currentActiveScene)
    {
        if (!CanDeleteScene(allScenes, sceneToDelete))
            return new SceneDeletionResult(false, currentActiveScene, Array.Empty<Guid>());

        var deletedReferences = sceneToDelete.Sources
            .Select(s => s.SourceReference)
            .Distinct()
            .ToList();

        allScenes.Remove(sceneToDelete);

        Scene? nextActive = currentActiveScene;
        if (currentActiveScene == sceneToDelete)
        {
            nextActive = allScenes.FirstOrDefault(s => s.Enabled) ?? allScenes.FirstOrDefault();
        }

        var remainingReferences = allScenes.SelectMany(s => s.Sources)
            .Select(s => s.SourceReference)
            .ToHashSet();

        var orphans = deletedReferences.Where(r => !remainingReferences.Contains(r)).ToList();

        return new SceneDeletionResult(true, nextActive, orphans);
    }

    public static bool RenameScene(Scene scene, string newName, out string? error)
    {
        var trimmed = newName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            error = "Scene name cannot be empty.";
            return false;
        }

        if (trimmed.Length > MaxSceneNameLength)
        {
            error = $"Scene name cannot exceed {MaxSceneNameLength} characters.";
            return false;
        }

        scene.Name = trimmed;
        error = null;
        return true;
    }

    public static Scene DuplicateScene(Scene original, IList<Scene> allScenes, string? customName = null)
    {
        var newName = customName ?? GenerateDuplicateName(original.Name, allScenes.Select(s => s.Name));
        var duplicate = original.Clone(newName);

        int index = allScenes.IndexOf(original);
        if (index >= 0 && index < allScenes.Count)
        {
            allScenes.Insert(index + 1, duplicate);
        }
        else
        {
            allScenes.Add(duplicate);
        }

        return duplicate;
    }

    public static Scene? ResolveActiveScene(IList<Scene> allScenes, string? savedActiveSceneId)
    {
        if (allScenes.Count == 0) return null;

        if (savedActiveSceneId is { } idStr && Guid.TryParse(idStr, out var id))
        {
            var match = allScenes.FirstOrDefault(s => s.Id == id);
            if (match is not null && match.Enabled)
            {
                return match;
            }
        }

        return allScenes.FirstOrDefault(s => s.Enabled) ?? allScenes.FirstOrDefault();
    }

    public static bool DeleteSourceFromScene(Scene scene, SceneSource source, IEnumerable<Scene> allScenes, out List<Guid> orphanResources)
    {
        orphanResources = [];
        if (!scene.Sources.Contains(source)) return false;

        var refId = source.SourceReference;
        scene.Sources.Remove(source);

        bool stillUsed = allScenes.SelectMany(s => s.Sources).Any(s => s.SourceReference == refId);
        if (!stillUsed)
        {
            orphanResources.Add(refId);
        }

        return true;
    }
}
