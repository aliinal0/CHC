using System.Windows;
using System.Windows.Controls;

namespace CHC.App.Controls;

/// <summary>A setting in the welcome guide: its name, its control, and a short note on what it does and why someone would want it.</summary>
public sealed class OptionCard : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(OptionCard));
    public static readonly DependencyProperty WhatProperty = DependencyProperty.Register(nameof(What), typeof(string), typeof(OptionCard));
    public static readonly DependencyProperty WhyProperty = DependencyProperty.Register(nameof(Why), typeof(string), typeof(OptionCard));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string What
    {
        get => (string)GetValue(WhatProperty);
        set => SetValue(WhatProperty, value);
    }

    public string Why
    {
        get => (string)GetValue(WhyProperty);
        set => SetValue(WhyProperty, value);
    }
}
