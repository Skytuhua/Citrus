using Citrus.Windows;
namespace Citrus.Core.Tests;

public class SettingsValidationTests
{
    [Theory]
    [InlineData(OperationMode.Convert, OperationMode.Tools, OperationMode.Actions)]
    [InlineData(OperationMode.Convert, OperationMode.Actions, OperationMode.Tools)]
    [InlineData(OperationMode.Tools, OperationMode.Convert, OperationMode.Actions)]
    [InlineData(OperationMode.Tools, OperationMode.Actions, OperationMode.Convert)]
    [InlineData(OperationMode.Actions, OperationMode.Convert, OperationMode.Tools)]
    [InlineData(OperationMode.Actions, OperationMode.Tools, OperationMode.Convert)]
    public void Each_permutation_of_modes_across_gesture_keys_is_valid_and_remaps_every_key(OperationMode shift, OperationMode alt, OperationMode control)
    {
        var settings = new CitrusSettings { ShiftMode = shift, AltMode = alt, ControlMode = control };
        settings.Validate();
        Assert.Equal(shift, settings.Remap(OperationMode.Convert));
        Assert.Equal(alt, settings.Remap(OperationMode.Tools));
        Assert.Equal(control, settings.Remap(OperationMode.Actions));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void Undefined_theme_is_rejected(int theme)
    {
        Assert.Throws<ArgumentException>(new CitrusSettings { Theme = (ThemeChoice)theme }.Validate);
    }

    [Theory]
    [InlineData("Shift", -1)]
    [InlineData("Shift", 3)]
    [InlineData("Alt", -1)]
    [InlineData("Alt", 3)]
    [InlineData("Control", -1)]
    [InlineData("Control", 3)]
    public void Undefined_mode_in_any_gesture_key_position_is_rejected(string key, int mode)
    {
        var undefined = (OperationMode)mode;
        var settings = key switch
        {
            "Shift" => new CitrusSettings { ShiftMode = undefined },
            "Alt" => new CitrusSettings { AltMode = undefined },
            _ => new CitrusSettings { ControlMode = undefined },
        };
        Assert.Throws<ArgumentException>(settings.Validate);
    }

    [Theory]
    // Shift and Alt share a mode.
    [InlineData(OperationMode.Convert, OperationMode.Convert, OperationMode.Actions)]
    [InlineData(OperationMode.Tools, OperationMode.Tools, OperationMode.Convert)]
    [InlineData(OperationMode.Actions, OperationMode.Actions, OperationMode.Tools)]
    // Shift and Control share a mode.
    [InlineData(OperationMode.Convert, OperationMode.Tools, OperationMode.Convert)]
    [InlineData(OperationMode.Tools, OperationMode.Actions, OperationMode.Tools)]
    [InlineData(OperationMode.Actions, OperationMode.Convert, OperationMode.Actions)]
    // Alt and Control share a mode.
    [InlineData(OperationMode.Tools, OperationMode.Convert, OperationMode.Convert)]
    [InlineData(OperationMode.Actions, OperationMode.Tools, OperationMode.Tools)]
    [InlineData(OperationMode.Convert, OperationMode.Actions, OperationMode.Actions)]
    public void Duplicate_pair_of_gesture_assignments_is_rejected(OperationMode shift, OperationMode alt, OperationMode control)
    {
        Assert.Throws<ArgumentException>(new CitrusSettings { ShiftMode = shift, AltMode = alt, ControlMode = control }.Validate);
    }

    [Theory]
    [InlineData(OperationMode.Convert)]
    [InlineData(OperationMode.Tools)]
    [InlineData(OperationMode.Actions)]
    public void Same_mode_on_every_gesture_key_is_rejected(OperationMode mode)
    {
        Assert.Throws<ArgumentException>(new CitrusSettings { ShiftMode = mode, AltMode = mode, ControlMode = mode }.Validate);
    }

    [Theory]
    [InlineData("valid remap")]
    [InlineData("undefined theme")]
    [InlineData("undefined mode")]
    [InlineData("duplicate assignment")]
    public void Validation_leaves_unrelated_preferences_unchanged(string scenario)
    {
        // Each preference differs from its default so a reset would be observable.
        var preferences = new CitrusSettings { SoundEnabled = false, ReduceMotion = true, ReduceTransparency = true, LaunchAtSignIn = true };
        var settings = scenario switch
        {
            "valid remap" => preferences with { ShiftMode = OperationMode.Actions, AltMode = OperationMode.Convert, ControlMode = OperationMode.Tools },
            "undefined theme" => preferences with { Theme = (ThemeChoice)99 },
            "undefined mode" => preferences with { AltMode = (OperationMode)99 },
            _ => preferences with { ShiftMode = OperationMode.Tools },
        };
        var before = settings with { };
        var failure = Record.Exception(settings.Validate);
        if (scenario == "valid remap") Assert.Null(failure);
        else Assert.IsType<ArgumentException>(failure);
        Assert.False(settings.SoundEnabled);
        Assert.True(settings.ReduceMotion);
        Assert.True(settings.ReduceTransparency);
        Assert.True(settings.LaunchAtSignIn);
        Assert.Equal(before, settings);
    }
}
