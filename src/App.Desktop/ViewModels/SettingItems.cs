using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using App.Core.Settings;

namespace App.Desktop.ViewModels;

// Si vous avez déjà une ViewModelBase, remplacez ObservableBase par elle.
public abstract class ObservableBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; PropertyChanged?.Invoke(this, new(n)); return true;
    }
    protected void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}

public sealed class RelayCommand : ICommand
{
    private readonly Action _a;
    public RelayCommand(Action a) => _a = a;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) => _a();
}

public abstract class SettingItemViewModel : ObservableBase
{
    protected readonly ISettingsService Svc;
    public string Key { get; }
    public string Category { get; }
    public string Title { get; }
    public string Description { get; }
    public string DefaultValue { get; }
    public bool IsCommon { get; init; }
    public string Header => $"{Category}: {Title}";
    public bool IsModified => Raw != DefaultValue;
    public ICommand ResetCommand { get; }

    protected SettingItemViewModel(ISettingsService svc, string key, string category, string title, string desc, string def)
    {
        Svc = svc; Key = key; Category = category; Title = title; Description = desc; DefaultValue = def;
        ResetCommand = new RelayCommand(() => { Raw = DefaultValue; Raise("Value"); });
    }

    protected string Raw
    {
        get => Svc.Get(Key, DefaultValue);
        set { Svc.Set(Key, value); Raise(nameof(IsModified)); }
    }
}

public sealed class BoolSettingViewModel : SettingItemViewModel
{
    public BoolSettingViewModel(ISettingsService s, string k, string c, string t, string d, bool def)
        : base(s, k, c, t, d, def ? "true" : "false") { }
    public bool Value { get => Raw == "true"; set { Raw = value ? "true" : "false"; Raise(); } }
}

public sealed class TextSettingViewModel : SettingItemViewModel
{
    public TextSettingViewModel(ISettingsService s, string k, string c, string t, string d, string def)
        : base(s, k, c, t, d, def) { }
    public string Value { get => Raw; set { Raw = value ?? ""; Raise(); } }
}

public sealed class ChoiceSettingViewModel : SettingItemViewModel
{
    public IReadOnlyList<string> Options { get; }
    public ChoiceSettingViewModel(ISettingsService s, string k, string c, string t, string d, string def, params string[] options)
        : base(s, k, c, t, d, def) => Options = options;
    public string Value { get => Raw; set { if (value != null) { Raw = value; Raise(); } } }
}

public sealed class NumberSettingViewModel : SettingItemViewModel
{
    public decimal Minimum { get; }
    public decimal Maximum { get; }
    public NumberSettingViewModel(ISettingsService s, string k, string c, string t, string d, decimal def, decimal min, decimal max)
        : base(s, k, c, t, d, def.ToString(CultureInfo.InvariantCulture)) { Minimum = min; Maximum = max; }
    public decimal? Value
    {
        get => decimal.TryParse(Raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
        set { if (value != null) { Raw = value.Value.ToString(CultureInfo.InvariantCulture); Raise(); } }
    }
}
