using System;

namespace Screenbox.Core.Helpers;

/// <summary>The range of the subtitle size setting, as a percentage of the original size.</summary>
public static class SubtitleScaleRange
{
    /// <summary>The original size. At this value the embedded track plays untouched.</summary>
    public const int Default = 100;

    /// <summary>The smallest size.</summary>
    public const int Minimum = 50;

    /// <summary>The largest size.</summary>
    public const int Maximum = 250;

    /// <summary>The slider step.</summary>
    public const int Step = 10;

    /// <summary>Limits a value to the supported range.</summary>
    public static int Clamp(int value) => Math.Clamp(value, Minimum, Maximum);
}
