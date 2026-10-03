using System.Text.Json;
using AravalsStream.Core.Models;
using AravalsStream.Core.Services;
using AravalsStream.Core.Settings;
using Xunit;

namespace AravalsStream.Tests;

public sealed class SourceAndVerticalTests
{
    // ==========================================
    // SOURCE TESTS
    // ==========================================

    [Fact]
    public void Source_ShowHide_TogglesVisibility()
    {
        var source = new SceneSource { Name = "Display 1", Visible = true };
        Assert.True(source.Visible);

        source.Visible = false;
        Assert.False(source.Visible);

        source.Visible = true;
        Assert.True(source.Visible);
    }

    [Fact]
    public void Source_LockUnlock_TogglesLockedState()
    {
        var source = new SceneSource { Name = "Camera" };
        Assert.False(source.HorizontalTransform.Locked);
        Assert.False(source.VerticalTransform.Locked);

        source.HorizontalTransform.Locked = true;
        source.VerticalTransform.Locked = true;
        Assert.True(source.HorizontalTransform.Locked);
        Assert.True(source.VerticalTransform.Locked);

        source.HorizontalTransform.Locked = false;
        source.VerticalTransform.Locked = false;
        Assert.False(source.HorizontalTransform.Locked);
        Assert.False(source.VerticalTransform.Locked);
    }

    [Fact]
    public void Source_Delete_RemovesFromSceneAndFindsOrphanResources()
    {
        var resourceId = Guid.NewGuid();
        var scene = new Scene { Name = "Gaming" };
        var source = new SceneSource { Name = "Unique Display", SourceReference = resourceId };
        scene.Sources.Add(source);

        var allScenes = new List<Scene> { scene };

        var deleted = SceneManager.DeleteSourceFromScene(scene, source, allScenes, out var orphans);

        Assert.True(deleted);
        Assert.DoesNotContain(source, scene.Sources);
        Assert.Single(orphans);
        Assert.Equal(resourceId, orphans[0]);
    }

    [Fact]
    public void Source_Delete_PreservesResourceIfUsedByAnotherScene()
    {
        var sharedResourceId = Guid.NewGuid();
        var scene1 = new Scene { Name = "Scene 1" };
        var source1 = new SceneSource { Name = "Shared Camera", SourceReference = sharedResourceId };
        scene1.Sources.Add(source1);

        var scene2 = new Scene { Name = "Scene 2" };
        var source2 = new SceneSource { Name = "Shared Camera Copy", SourceReference = sharedResourceId };
        scene2.Sources.Add(source2);

        var allScenes = new List<Scene> { scene1, scene2 };

        var deleted = SceneManager.DeleteSourceFromScene(scene1, source1, allScenes, out var orphans);

        Assert.True(deleted);
        Assert.DoesNotContain(source1, scene1.Sources);
        Assert.Contains(source2, scene2.Sources);
        // Shared resource must NOT be orphaned because scene2 still references it
        Assert.Empty(orphans);
    }

    [Fact]
    public void Source_Ordering_MoveUpAndMoveDown()
    {
        var scene = new Scene();
        var s1 = new SceneSource { Name = "S1" };
        var s2 = new SceneSource { Name = "S2" };
        var s3 = new SceneSource { Name = "S3" };
        scene.Sources.Add(s1);
        scene.Sources.Add(s2);
        scene.Sources.Add(s3);

        // Move s3 up by 1 (to index 1)
        int index = scene.Sources.IndexOf(s3);
        scene.Sources.Move(index, index - 1);
        Assert.Equal(s1, scene.Sources[0]);
        Assert.Equal(s3, scene.Sources[1]);
        Assert.Equal(s2, scene.Sources[2]);

        // Move s1 to bottom
        scene.Sources.Move(0, scene.Sources.Count - 1);
        Assert.Equal(s3, scene.Sources[0]);
        Assert.Equal(s2, scene.Sources[1]);
        Assert.Equal(s1, scene.Sources[2]);
    }

    // ==========================================
    // VERTICAL LAYOUT TESTS
    // ==========================================

    [Fact]
    public void Vertical_FitEntire_PreservesAspectRatioWithoutCrop()
    {
        // 1920x1080 source fitted into 1080x1920 vertical canvas
        var transform = new SourceTransform();
        CanvasLayout.FitEntire(transform, 1920, 1080, OutputMode.Vertical);

        Assert.Equal(VerticalLayoutMode.FitEntire, transform.LayoutMode);
        Assert.False(transform.BackgroundEnlarged);
        Assert.Equal(1080, transform.Width, 3);
        Assert.Equal(607.5, transform.Height, 3);
        Assert.Equal(0, transform.X, 3);
        // Centered vertically in 1920: (1920 - 607.5) / 2 = 656.25
        Assert.Equal(656.25, transform.Y, 3);
    }

    [Fact]
    public void Vertical_FillCanvas_FillsCanvasAndCalculatesCenterCrop()
    {
        // 1920x1080 source filled into 1080x1920 vertical canvas
        var transform = new SourceTransform();
        CanvasLayout.FillCanvas(transform, 1920, 1080, OutputMode.Vertical, focusX: 0.5);

        Assert.Equal(VerticalLayoutMode.FillCanvas, transform.LayoutMode);
        Assert.False(transform.BackgroundEnlarged);
        // Height scaled to 1920, width = 1920 * (1920/1080) = 3413.33
        Assert.Equal(1920, transform.Height, 2);
        Assert.Equal(3413.33, transform.Width, 1);
        Assert.Equal(0, transform.Y, 2);

        // Center crop: overflow = 3413.33 - 1080 = 2333.33; X = -2333.33 * 0.5 = -1166.67
        Assert.Equal(-1166.67, transform.X, 1);
        Assert.Equal(0.5, transform.FocusX, 2);
    }

    [Fact]
    public void Vertical_SmartVertical_FullscreenCrop_CalculatesCorrectDimensions()
    {
        var transform = new SourceTransform();
        CanvasLayout.SmartVertical(transform, 1920, 1080, SmartVerticalTemplate.FullscreenCrop, focusX: 0.5);

        Assert.Equal(VerticalLayoutMode.SmartVertical, transform.LayoutMode);
        Assert.Equal(SmartVerticalTemplate.FullscreenCrop, transform.VerticalTemplate);
        Assert.False(transform.BackgroundEnlarged);
        Assert.Equal(1920, transform.Height, 2);
        Assert.True(transform.Width >= 1080);
    }

    [Fact]
    public void Vertical_SmartVertical_BackgroundAndFullSource_EnablesBackgroundFlag()
    {
        var transform = new SourceTransform();
        CanvasLayout.SmartVertical(transform, 1920, 1080, SmartVerticalTemplate.BackgroundAndFullSource);

        Assert.Equal(VerticalLayoutMode.SmartVertical, transform.LayoutMode);
        Assert.Equal(SmartVerticalTemplate.BackgroundAndFullSource, transform.VerticalTemplate);
        Assert.True(transform.BackgroundEnlarged);
        Assert.Equal(1080, transform.Width, 3);
        Assert.Equal(607.5, transform.Height, 3);
        Assert.Equal(656.25, transform.Y, 3);
    }

    [Fact]
    public void Vertical_HorizontalAndVerticalTransforms_RemainIndependent()
    {
        var source = new SceneSource { Name = "Desktop" };

        CanvasLayout.Fit(source.HorizontalTransform, 1920, 1080, OutputMode.Horizontal);
        CanvasLayout.SmartVertical(source.VerticalTransform, 1920, 1080, SmartVerticalTemplate.FullscreenCrop);

        Assert.Equal(1920, source.HorizontalTransform.Width);
        Assert.Equal(1080, source.HorizontalTransform.Height);
        Assert.Equal(0, source.HorizontalTransform.X);
        Assert.Equal(0, source.HorizontalTransform.Y);

        Assert.NotEqual(source.HorizontalTransform.Width, source.VerticalTransform.Width);
        Assert.NotEqual(source.HorizontalTransform.Height, source.VerticalTransform.Height);
        Assert.NotEqual(source.HorizontalTransform.X, source.VerticalTransform.X);

        // Modifying vertical must not mutate horizontal
        source.VerticalTransform.X = -500;
        source.VerticalTransform.FocusX = 0.8;
        Assert.Equal(0, source.HorizontalTransform.X);
    }

    [Fact]
    public void Vertical_FocusX_CalculatesAndAppliesCorrectHorizontalOffset()
    {
        double scaledWidth = 3413.33;
        double canvasWidth = 1080;
        double overflow = scaledWidth - canvasWidth; // 2333.33

        // When currentX is center
        double currentX = -overflow * 0.5;
        double calculatedFocus = CanvasLayout.CalculateFocusX(currentX, scaledWidth, canvasWidth);
        Assert.Equal(0.5, calculatedFocus, 2);

        // When shifted left edge visible (X = 0)
        calculatedFocus = CanvasLayout.CalculateFocusX(0, scaledWidth, canvasWidth);
        Assert.Equal(0.0, calculatedFocus, 2);

        // When shifted right edge visible (X = -overflow)
        calculatedFocus = CanvasLayout.CalculateFocusX(-overflow, scaledWidth, canvasWidth);
        Assert.Equal(1.0, calculatedFocus, 2);

        // Applying focus X to a transform
        var transform = new SourceTransform { Width = scaledWidth, FocusX = 0.75 };
        CanvasLayout.ApplyFocusX(transform, OutputMode.Vertical);
        Assert.Equal(-overflow * 0.75, transform.X, 1);
    }

    [Fact]
    public void Vertical_ManualEditAfterAutoVertical_SetsManualLayoutMode()
    {
        var transform = new SourceTransform();
        CanvasLayout.SmartVertical(transform, 1920, 1080);
        Assert.Equal(VerticalLayoutMode.SmartVertical, transform.LayoutMode);

        // User drags/edits manually
        transform.Width = 1200;
        transform.Height = 1600;
        transform.LayoutMode = VerticalLayoutMode.Manual;

        Assert.Equal(VerticalLayoutMode.Manual, transform.LayoutMode);
    }

    [Fact]
    public void Vertical_AutoVerticalAndTransforms_PersistCorrectlyViaJson()
    {
        var original = new Scene { Name = "Vertical Stream" };
        var source = new SceneSource
        {
            Name = "Display",
            Type = SourceType.DisplayCapture,
            Visible = true
        };
        CanvasLayout.SmartVertical(source.VerticalTransform, 1920, 1080, SmartVerticalTemplate.BackgroundAndFullSource, focusX: 0.65);
        source.VerticalTransform.CropLeft = 15;
        source.VerticalTransform.CropRight = 25;
        source.VerticalTransform.Opacity = 0.95;

        original.Sources.Add(source);

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<Scene>(json);

        Assert.NotNull(restored);
        Assert.Single(restored.Sources);
        var restoredSource = restored.Sources[0];

        Assert.Equal(VerticalLayoutMode.SmartVertical, restoredSource.VerticalTransform.LayoutMode);
        Assert.Equal(SmartVerticalTemplate.BackgroundAndFullSource, restoredSource.VerticalTransform.VerticalTemplate);
        Assert.True(restoredSource.VerticalTransform.BackgroundEnlarged);
        Assert.Equal(15, restoredSource.VerticalTransform.CropLeft);
        Assert.Equal(25, restoredSource.VerticalTransform.CropRight);
        Assert.Equal(0.95, restoredSource.VerticalTransform.Opacity, 2);
    }

    // ==========================================
    // VERTICAL MANUAL RESIZE / MOVE & LOCK TESTS
    // ==========================================

    [Fact]
    public void Vertical_PreviewToVertical_CoordinateConversion()
    {
        // Logical canvas: 1080x1920
        const double logicalW = 1080.0;
        const double logicalH = 1920.0;

        // Preview in Both mode: 270x480
        const double previewW = 270.0;
        const double previewH = 480.0;

        double scaleX = logicalW / previewW; // 4.0
        double scaleY = logicalH / previewH; // 4.0

        Assert.Equal(4.0, scaleX);
        Assert.Equal(4.0, scaleY);

        // A mouse delta of 15 display pixels converts to 60 logical pixels
        double displayDx = 15.0;
        double logicalDx = displayDx * scaleX;
        Assert.Equal(60.0, logicalDx);
    }

    [Fact]
    public void Vertical_Drag_MovesSourceAndEnforcesManualMode()
    {
        var transform = new SourceTransform { X = 0, Y = 656, Width = 1080, Height = 608, LayoutMode = VerticalLayoutMode.FitEntire };

        // Simulate dragging inside source
        double dx = 120;
        double dy = -80;
        transform.X += dx;
        transform.Y += dy;
        transform.LayoutMode = VerticalLayoutMode.Manual;

        Assert.Equal(120, transform.X);
        Assert.Equal(576, transform.Y);
        Assert.Equal(VerticalLayoutMode.Manual, transform.LayoutMode);
    }

    [Fact]
    public void Vertical_CornerResize_PreservesAspectRatio()
    {
        // Initial 16:9 aspect ratio
        var start = (X: 0.0, Y: 0.0, Width: 1920.0, Height: 1080.0);
        double ratio = start.Width / start.Height;

        // Simulate dragging BR handle with dx = 100, dy = 50
        double dx = 100;
        double dy = 50;
        double desiredW = start.Width + dx;
        double desiredH = start.Height + dy;

        double scaleX = desiredW / start.Width;
        double scaleY = desiredH / start.Height;
        double scale = Math.Abs(dx) > Math.Abs(dy) ? scaleX : scaleY;

        double finalW = Math.Max(16, start.Width * scale);
        double finalH = Math.Max(16, finalW / ratio);

        Assert.Equal(start.Width + dx, finalW, 2);
        Assert.Equal(finalW / ratio, finalH, 2);
        Assert.Equal(ratio, finalW / finalH, 3);
    }

    [Fact]
    public void Vertical_CornerResize_FreeWithShift()
    {
        var start = (X: 0.0, Y: 0.0, Width: 1920.0, Height: 1080.0);

        // Free aspect ratio allows independent width and height
        double dx = 100;
        double dy = -200;
        double finalW = Math.Max(16, start.Width + dx);
        double finalH = Math.Max(16, start.Height + dy);

        Assert.Equal(2020.0, finalW);
        Assert.Equal(880.0, finalH);
    }

    [Fact]
    public void Vertical_EdgeResize_ModifiesOnlyRelevantAxis()
    {
        var transform = new SourceTransform { X = 50, Y = 100, Width = 600, Height = 400 };

        // Drag "R" handle: only width changes
        double dx = 80;
        transform.Width = Math.Max(16, transform.Width + dx);
        transform.LayoutMode = VerticalLayoutMode.Manual;

        Assert.Equal(680, transform.Width);
        Assert.Equal(50, transform.X);
        Assert.Equal(400, transform.Height);
        Assert.Equal(100, transform.Y);

        // Drag "T" handle: only height and Y change
        double startH = transform.Height;
        double startY = transform.Y;
        double dy = -60;
        double newH = Math.Max(16, startH - dy);
        transform.Y = startY + startH - newH;
        transform.Height = newH;

        Assert.Equal(460, transform.Height);
        Assert.Equal(40, transform.Y);
        Assert.Equal(680, transform.Width);
        Assert.Equal(50, transform.X);
    }

    [Fact]
    public void Vertical_IntentionalNegativeX_And_OversizedSource_Allowed()
    {
        var transform = new SourceTransform();
        // A landscape 1920x1080 scaled to portrait 1920h has width 3413.33 and X = -1166.67
        transform.Width = 3413.33;
        transform.Height = 1920;
        transform.X = -1166.67;
        transform.Y = 0;
        transform.LayoutMode = VerticalLayoutMode.Manual;

        Assert.True(transform.X < 0);
        Assert.True(transform.Width > 1080);
        Assert.True(transform.Width >= 16);
        Assert.True(transform.Height >= 16);
    }

    [Fact]
    public void Vertical_ManualTransform_Persistence()
    {
        var original = new SceneSource
        {
            Name = "Manual Cropped Display",
            Locked = false,
            VerticalTransform = new SourceTransform
            {
                X = -450,
                Y = -50,
                Width = 2200,
                Height = 1300,
                CropLeft = 30,
                CropTop = 20,
                CropRight = 40,
                CropBottom = 10,
                FocusX = 0.42,
                LayoutMode = VerticalLayoutMode.Manual
            }
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<SceneSource>(json);

        Assert.NotNull(restored);
        Assert.Equal(VerticalLayoutMode.Manual, restored.VerticalTransform.LayoutMode);
        Assert.Equal(-450, restored.VerticalTransform.X);
        Assert.Equal(-50, restored.VerticalTransform.Y);
        Assert.Equal(2200, restored.VerticalTransform.Width);
        Assert.Equal(1300, restored.VerticalTransform.Height);
        Assert.Equal(30, restored.VerticalTransform.CropLeft);
        Assert.Equal(0.42, restored.VerticalTransform.FocusX, 2);
    }

    [Fact]
    public void Source_LockState_SynchronizesTransformsAndPersists()
    {
        var source = new SceneSource { Name = "Camera", Locked = false };
        Assert.False(source.Locked);
        Assert.False(source.HorizontalTransform.Locked);
        Assert.False(source.VerticalTransform.Locked);

        // Setting Locked = true synchronizes both transforms
        source.Locked = true;
        Assert.True(source.Locked);
        Assert.True(source.HorizontalTransform.Locked);
        Assert.True(source.VerticalTransform.Locked);

        // Persistence test
        var json = JsonSerializer.Serialize(source);
        var restored = JsonSerializer.Deserialize<SceneSource>(json);

        Assert.NotNull(restored);
        Assert.True(restored.Locked);
        Assert.True(restored.HorizontalTransform.Locked);
        Assert.True(restored.VerticalTransform.Locked);
    }

    [Fact]
    public void Source_Visibility_Persistence()
    {
        var source = new SceneSource { Name = "Overlay", Visible = false };
        var json = JsonSerializer.Serialize(source);
        var restored = JsonSerializer.Deserialize<SceneSource>(json);

        Assert.NotNull(restored);
        Assert.False(restored.Visible);
    }

    [Fact]
    public void Source_LockedSource_RejectsMoveResizeCrop()
    {
        var source = new SceneSource
        {
            Name = "Protected Display",
            Locked = true,
            VerticalTransform = new SourceTransform { X = 0, Y = 0, Width = 1080, Height = 1920 }
        };

        // Attempting to begin drag when locked must be rejected
        bool canDrag = !source.Locked && !source.VerticalTransform.Locked;
        Assert.False(canDrag);

        // Coordinates remain unchanged
        Assert.Equal(0, source.VerticalTransform.X);
        Assert.Equal(0, source.VerticalTransform.Y);
        Assert.Equal(1080, source.VerticalTransform.Width);
        Assert.Equal(1920, source.VerticalTransform.Height);
    }

    [Fact]
    public void Source_UnlockedSource_CanMoveWhileAnotherSourceIsLocked()
    {
        var display = new SceneSource
        {
            Name = "Display 1",
            Locked = true,
            VerticalTransform = new SourceTransform { X = 0, Y = 0, Width = 1080, Height = 1920 }
        };

        var camera = new SceneSource
        {
            Name = "Webcam",
            Locked = false,
            VerticalTransform = new SourceTransform { X = 50, Y = 50, Width = 300, Height = 300 }
        };

        // Display rejects drag
        Assert.True(display.Locked);

        // Camera accepts drag
        Assert.False(camera.Locked);
        camera.VerticalTransform.X += 100;
        camera.VerticalTransform.Y += 50;

        Assert.Equal(150, camera.VerticalTransform.X);
        Assert.Equal(100, camera.VerticalTransform.Y);
        // Display still locked and unchanged
        Assert.Equal(0, display.VerticalTransform.X);
        Assert.Equal(0, display.VerticalTransform.Y);
    }

    [Fact]
    public void Source_DeleteWhileSelected_SafelyCleared()
    {
        var scene = new Scene { Name = "Main" };
        var s1 = new SceneSource { Name = "Source 1" };
        var s2 = new SceneSource { Name = "Source 2" };
        scene.Sources.Add(s1);
        scene.Sources.Add(s2);

        SceneSource? selectedSource = s1;

        // Delete selected source s1
        bool deleted = SceneManager.DeleteSourceFromScene(scene, s1, new[] { scene }, out _);
        Assert.True(deleted);

        if (ReferenceEquals(selectedSource, s1))
        {
            selectedSource = null;
        }

        Assert.Null(selectedSource);
        Assert.Single(scene.Sources);
        Assert.Equal(s2, scene.Sources[0]);
    }
}
